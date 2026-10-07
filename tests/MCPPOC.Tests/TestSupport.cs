using System.Reflection;
using System.Runtime.Loader;
using MCPPOC.Analysis;
using MCPPOC.Tools;
using MCPPOC.Workspace;

namespace MCPPOC.Tests;

/// <summary>A fresh workspace with TestTarget.dll loaded, plus every tool class wired to it.</summary>
public sealed class Fixture : IDisposable
{
    public static string TargetPath => typeof(TestTarget.Calculator).Assembly.Location;

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public Fixture(string? path = null)
    {
        Workspace = new AssemblyWorkspace();
        Decompiler = new DecompilerService();
        Info = new WorkspaceTools(Workspace).LoadAssembly(path ?? TargetPath);
        Assembly = Workspace.Get();
    }

    public AssemblyWorkspace Workspace { get; }
    public DecompilerService Decompiler { get; }
    public AssemblyInfo Info { get; }
    public LoadedAssembly Assembly { get; }

    public WorkspaceTools WorkspaceTools => new(Workspace);
    public BrowseTools Browse => new(Workspace);
    public CodeTools Code => new(Workspace, Decompiler);
    public SearchTools Search => new(Workspace);
    public ResourceTools Resources => new(Workspace);
    public PatchTools Patch => new(Workspace, Decompiler);

    public string Token(string methodName) =>
        Assembly.Read(a => ModelFactory.Hex(MemberResolver.ResolveMethod(a, methodName).MDToken));

    public void Dispose() => Workspace.Dispose();
}

/// <summary>Loads a saved (patched) assembly in a collectible context and invokes a static method.</summary>
public static class Runner
{
    public static object? Invoke(string assemblyPath, string typeName, string methodName, params object?[] args)
    {
        var alc = new AssemblyLoadContext("patched-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            var asm = alc.LoadFromStream(stream);
            var method = asm.GetType(typeName, throwOnError: true)!.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            return method.Invoke(null, args);
        }
        finally
        {
            alc.Unload();
        }
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-dotnet-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
