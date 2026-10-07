using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using MCPPOC.Workspace;
using ModelContextProtocol;

namespace MCPPOC.Analysis;

/// <summary>
/// Wraps ILSpy (ICSharpCode.Decompiler). When an assembly has been modified in the workspace (patched IL,
/// renamed members) it is serialized to memory first so the decompiled output reflects the current state.
/// </summary>
public sealed class DecompilerService
{
    private sealed record Cached(int Version, PEFile File, UniversalAssemblyResolver Resolver);

    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    public static DecompilerSettings CreateSettings(bool showIlOffsets = false) => new(LanguageVersion.Latest)
    {
        ThrowOnAssemblyResolveErrors = false,
        ShowXmlDocumentation = true,
        RemoveDeadCode = false,
        RemoveDeadStores = false,
        UseSdkStyleProjectFormat = true,
        LoadInMemory = true,
    };

    public string DecompileMember(LoadedAssembly asm, IMemberDef member, CancellationToken ct = default)
    {
        var handle = MetadataTokens.EntityHandle(member.MDToken.ToInt32());
        return Run(asm, (file, resolver) =>
        {
            var decompiler = new CSharpDecompiler(file, resolver, CreateSettings()) { CancellationToken = ct };
            return decompiler.DecompileAsString(handle);
        });
    }

    public string DecompileModule(LoadedAssembly asm, CancellationToken ct = default) =>
        Run(asm, (file, resolver) =>
        {
            var decompiler = new CSharpDecompiler(file, resolver, CreateSettings()) { CancellationToken = ct };
            return decompiler.DecompileWholeModuleAsString();
        });

    public string DecompileAssemblyAttributes(LoadedAssembly asm) =>
        Run(asm, (file, resolver) => new CSharpDecompiler(file, resolver, CreateSettings()).DecompileModuleAndAssemblyAttributesToString());

    /// <summary>ILSpy-style IL disassembly (like ildasm) of a method, type, or the whole module when <paramref name="member"/> is null.</summary>
    public string Disassemble(LoadedAssembly asm, IMemberDef? member, CancellationToken ct = default) =>
        Run(asm, (file, _) =>
        {
            var output = new PlainTextOutput { IndentationString = "  " };
            var disassembler = new ReflectionDisassembler(output, ct)
            {
                DetectControlStructure = true,
                ShowSequencePoints = false,
                ShowMetadataTokens = true,
                ShowMetadataTokensInBase10 = false,
                ExpandMemberDefinitions = true,
            };
            var token = member?.MDToken.ToInt32() ?? 0;
            switch (member)
            {
                case null:
                    disassembler.WriteModuleContents(file);
                    break;
                case MethodDef:
                    disassembler.DisassembleMethod(file, (MethodDefinitionHandle)MetadataTokens.EntityHandle(token));
                    break;
                case TypeDef:
                    disassembler.DisassembleType(file, (TypeDefinitionHandle)MetadataTokens.EntityHandle(token));
                    break;
                case FieldDef:
                    disassembler.DisassembleField(file, (FieldDefinitionHandle)MetadataTokens.EntityHandle(token));
                    break;
                case PropertyDef:
                    disassembler.DisassembleProperty(file, (PropertyDefinitionHandle)MetadataTokens.EntityHandle(token));
                    break;
                case EventDef:
                    disassembler.DisassembleEvent(file, (EventDefinitionHandle)MetadataTokens.EntityHandle(token));
                    break;
            }
            return output.ToString();
        });

    /// <summary>Writes a compilable SDK-style C# project for the whole assembly to <paramref name="outputDirectory"/>.</summary>
    public (string ProjectFile, int FileCount) ExportProject(LoadedAssembly asm, string outputDirectory, IProgress<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new McpException($"Output directory '{outputDirectory}' is not empty.");

        return Run(asm, (file, resolver) =>
        {
            var decompiler = new WholeProjectDecompiler(CreateSettings(), resolver, null, null, null);
            var count = 0;
            decompiler.ProgressIndicator = new Progress<DecompilationProgress>(p => progress?.Report(Interlocked.Increment(ref count)));
            decompiler.DecompileProject(file, outputDirectory, ct);
            var project = Directory.EnumerateFiles(outputDirectory, "*.csproj").FirstOrDefault() ?? outputDirectory;
            var files = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).Count();
            return (project, files);
        });
    }

    public void Invalidate(string assemblyId) => _cache.TryRemove(assemblyId, out _);

    private T Run<T>(LoadedAssembly asm, Func<PEFile, UniversalAssemblyResolver, T> action)
    {
        var gate = _locks.GetOrAdd(asm.Id, _ => new object());
        lock (gate)
        {
            var cached = asm.Read(a =>
            {
                if (_cache.TryGetValue(a.Id, out var c) && c.Version == a.Version)
                    return c;
                var fresh = Create(a);
                _cache[a.Id] = fresh;
                return fresh;
            });
            try
            {
                return action(cached.File, cached.Resolver);
            }
            catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
            {
                throw new McpException($"Decompilation failed: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }
    }

    private static Cached Create(LoadedAssembly asm)
    {
        Stream stream;
        if (asm.IsModified)
        {
            var ms = new MemoryStream();
            ModuleWriting.Write(asm.Module, ms, out _);
            ms.Position = 0;
            stream = ms;
        }
        else
        {
            stream = new MemoryStream(File.ReadAllBytes(asm.Path));
        }

        var file = new PEFile(asm.Path, stream, PEStreamOptions.PrefetchEntireImage);
        var resolver = new UniversalAssemblyResolver(asm.Path, false, file.DetectTargetFrameworkId(),
            file.DetectRuntimePack(), PEStreamOptions.PrefetchMetadata, MetadataReaderOptions.ApplyWindowsRuntimeProjections);
        return new Cached(asm.Version, file, resolver);
    }
}

/// <summary>Shared logic for serializing a dnlib module (used by save and by the decompiler for modified modules).</summary>
public static class ModuleWriting
{
    private sealed class CollectingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public void Log(object sender, LoggerEvent loggerEvent, string format, params object[] args)
        {
            if (loggerEvent <= LoggerEvent.Warning)
                Messages.Add($"{loggerEvent}: {string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args)}");
        }

        public bool IgnoresEvent(LoggerEvent loggerEvent) => loggerEvent > LoggerEvent.Warning;
    }

    /// <returns>Whether the module was written as a mixed-mode (native) image.</returns>
    public static bool Write(ModuleDefMD module, Stream destination, out IReadOnlyList<string> warnings, bool? forceNative = null)
    {
        var logger = new CollectingLogger();
        var native = forceNative ?? !module.IsILOnly;
        if (native)
        {
            var options = new NativeModuleWriterOptions(module, optimizeImageSize: false)
            {
                Logger = logger,
                AddCheckSum = true,
            };
            options.MetadataOptions.Flags |= MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack;
            module.NativeWrite(destination, options);
        }
        else
        {
            var options = new ModuleWriterOptions(module)
            {
                Logger = logger,
                AddCheckSum = true,
            };
            // Keep tokens stable so tokens shown before a patch remain valid afterwards. Patched bodies get a
            // freshly computed MaxStack in MethodPatcher, so keeping old values elsewhere is safe and avoids
            // failures on obfuscated methods whose stack cannot be computed.
            options.MetadataOptions.Flags |= MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack;
            module.Write(destination, options);
        }
        warnings = logger.Messages;
        return native;
    }
}
