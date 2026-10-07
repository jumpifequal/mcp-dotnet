using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MCPPOC.Analysis;
using MCPPOC.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MCPPOC.Tools;

[McpServerToolType]
public sealed class CodeTools(AssemblyWorkspace workspace, DecompilerService decompiler)
{
    private const int MaxDecompiledChars = 200_000;

    [McpServerTool(Name = "decompile", Title = "Decompile to C# (ILSpy)", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Decompile a type, method, field, property or event to C# using the ILSpy engine. Reflects any patches/renames made in this session. Prefer decompiling single methods for very large types.")]
    public string Decompile(
        [Description(P.Member)] string member,
        [Description(P.Assembly)] string? assembly = null,
        CancellationToken cancellationToken = default)
    {
        var asm = workspace.Get(assembly);
        var def = asm.Read(a => MemberResolver.ResolveMember(a, member));
        return Truncate(decompiler.DecompileMember(asm, def, cancellationToken));
    }

    [McpServerTool(Name = "decompile_assembly_attributes", Title = "Decompile assembly attributes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Decompile assembly- and module-level attributes (version info, InternalsVisibleTo, TargetFramework, obfuscator markers...).")]
    public string DecompileAssemblyAttributes([Description(P.Assembly)] string? assembly = null) =>
        decompiler.DecompileAssemblyAttributes(workspace.Get(assembly));

    [McpServerTool(Name = "disassemble", Title = "Disassemble IL (ILSpy)", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("ildasm-style IL disassembly of a method, type, field, property or event produced by ILSpy's ReflectionDisassembler, with control-structure detection (try/catch/loops) and metadata tokens.")]
    public string Disassemble(
        [Description(P.Member)] string member,
        [Description(P.Assembly)] string? assembly = null,
        CancellationToken cancellationToken = default)
    {
        var asm = workspace.Get(assembly);
        var def = asm.Read(a => MemberResolver.ResolveMember(a, member));
        return Truncate(decompiler.Disassemble(asm, def, cancellationToken));
    }

    [McpServerTool(Name = "get_method_il", Title = "Method IL with offsets and bytes", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Instruction listing of a method with IL offsets, raw bytes and file offsets (for hex patching), plus locals and exception handlers. Offsets are what 'patch_method_il' expects; the listing format can be pasted back as patch input.")]
    public MethodIl GetMethodIl(
        [Description(P.Method)] string method,
        [Description("Include raw instruction bytes and file offsets (only available for unmodified methods).")] bool includeBytes = true,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            var m = MemberResolver.ResolveMethod(a, method);
            if (!m.HasBody)
                throw new McpException($"Method {m.FullName} has no IL body ({(m.IsAbstract ? "abstract" : m.IsPinvokeImpl ? "P/Invoke" : "runtime/native")}).");
            var raw = includeBytes && !a.IsModified ? ReadRawBody(a.Module, m) : null;
            var instrs = m.Body.Instructions;
            var list = new List<IlInstruction>(instrs.Count);
            for (var i = 0; i < instrs.Count; i++)
            {
                var ins = instrs[i];
                string? bytes = null, fileOffset = null;
                if (raw != null)
                {
                    var size = ins.GetSize();
                    if (ins.Offset + size <= raw.Value.Code.Length)
                        bytes = ModelFactory.HexBytes(raw.Value.Code.AsSpan((int)ins.Offset, size));
                    fileOffset = ModelFactory.Hex(raw.Value.CodeFileOffset + ins.Offset);
                }
                list.Add(new IlInstruction(ModelFactory.Il(ins.Offset), fileOffset, bytes, ins.OpCode.Name, ModelFactory.FormatOperand(ins)));
            }
            return new MethodIl(ModelFactory.Hex(m.MDToken), m.FullName, m.Body.MaxStack,
                m.Body.Variables.Select(v => $"V_{v.Index}: {v.Type.FullName}").ToList(),
                m.Body.ExceptionHandlers.Select(BrowseTools.FormatHandler).ToList(), list);
        });

    [McpServerTool(Name = "get_control_flow_graph", Title = "Control-flow graph", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Split a method into basic blocks with successor edges (branches, switches, fall-through, exception-handler boundaries). Includes a Mermaid flowchart. Useful to understand control-flow-flattened or obfuscated code.")]
    public ControlFlowGraph GetControlFlowGraph(
        [Description(P.Method)] string method,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            var m = MemberResolver.ResolveMethod(a, method);
            if (!m.HasBody)
                throw new McpException($"Method {m.FullName} has no IL body.");
            return CodeAnalysis.BuildCfg(m);
        });

    [McpServerTool(Name = "read_bytes", Title = "Read raw bytes", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Hex dump of raw bytes from the PE image, addressed by RVA or file offset (max 64 KB).")]
    public HexDump ReadBytes(
        [Description("Address (hex like 0x2050 or decimal).")] string address,
        [Description("Number of bytes to read (max 65536).")] int size = 256,
        [Description("Interpret 'address' as an RVA (true, default) or as a file offset (false).")] bool isRva = true,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            if (!MemberResolver.TryParseToken(address, out var addr))
                throw new McpException($"Invalid address '{address}'.");
            size = Math.Clamp(size, 1, 65536);
            var image = a.Module.Metadata.PEImage;
            var reader = image.CreateReader();
            var fileOffset = isRva ? (uint)image.ToFileOffset((dnlib.PE.RVA)addr) : addr;
            if (fileOffset == 0 && addr != 0 || fileOffset >= reader.Length)
                throw new McpException($"Address {ModelFactory.Hex(addr)} is outside the image.");
            reader.Position = fileOffset;
            var count = (int)Math.Min((uint)size, reader.Length - fileOffset);
            var data = reader.ReadBytes(count);
            return new HexDump(ModelFactory.Hex(addr), data.Length, CodeAnalysis.FormatHexDump(data, addr));
        });

    [McpServerTool(Name = "export_project", Title = "Export decompiled C# project", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Decompile the whole assembly into a compilable SDK-style C# project (one file per type, resources extracted) in an empty directory, using ILSpy's WholeProjectDecompiler. Use this for large assemblies, then grep the output.")]
    public string ExportProject(
        [Description("Absolute path of an empty (or non-existent) output directory.")] string outputDirectory,
        [Description(P.Assembly)] string? assembly = null,
        IProgress<ModelContextProtocol.ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var asm = workspace.Get(assembly);
        var total = asm.Read(a => a.AllTypes.Count);
        var dir = Path.GetFullPath(outputDirectory);
        var (project, files) = decompiler.ExportProject(asm, dir,
            new Progress<int>(n => progress?.Report(new() { Progress = n, Total = total, Message = $"Decompiled {n} file(s)" })),
            cancellationToken);
        return $"Exported {files} file(s) to '{dir}'. Project: {project}";
    }

    private static string Truncate(string text) =>
        text.Length <= MaxDecompiledChars
            ? text
            : text[..MaxDecompiledChars] + $"\n\n// ... output truncated ({text.Length:N0} chars). Decompile individual members or use 'export_project'.";

    private readonly record struct RawBody(byte[] Code, uint CodeFileOffset);

    private static RawBody? ReadRawBody(ModuleDefMD module, MethodDef method)
    {
        if (method.RVA == 0)
            return null;
        try
        {
            var image = module.Metadata.PEImage;
            var reader = image.CreateReader();
            var start = (uint)image.ToFileOffset(method.RVA);
            reader.Position = start;
            var flags = reader.ReadByte();
            uint headerSize, codeSize;
            if ((flags & 0x03) == 0x02)
            {
                headerSize = 1;
                codeSize = (uint)(flags >> 2);
            }
            else if ((flags & 0x03) == 0x03)
            {
                reader.Position = start;
                var fatFlags = reader.ReadUInt16();
                headerSize = (uint)((fatFlags >> 12) * 4);
                reader.ReadUInt16(); // max stack
                codeSize = reader.ReadUInt32();
            }
            else
            {
                return null;
            }
            reader.Position = start + headerSize;
            return new RawBody(reader.ReadBytes((int)codeSize), start + headerSize);
        }
        catch
        {
            return null;
        }
    }
}
