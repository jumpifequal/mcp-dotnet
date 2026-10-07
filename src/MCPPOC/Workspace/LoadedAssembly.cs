using dnlib.DotNet;
using MCPPOC.Analysis;

namespace MCPPOC.Workspace;

/// <summary>
/// One assembly loaded in the workspace. All access to <see cref="Module"/> must go through
/// <see cref="Read{T}"/> or <see cref="Write{T}"/> because dnlib object graphs are not safe for
/// concurrent mutation and MCP requests may be served in parallel.
/// </summary>
public sealed class LoadedAssembly : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    private IReadOnlyList<TypeDef>? _allTypes;
    private InstructionParser? _parser;

    internal LoadedAssembly(string id, string path, ModuleDefMD module, AssemblyResolver resolver)
    {
        Id = id;
        Path = path;
        Module = module;
        Resolver = resolver;
        LoadedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    /// <summary>Full path of the file the assembly was loaded from.</summary>
    public string Path { get; }

    public ModuleDefMD Module { get; }

    public AssemblyResolver Resolver { get; }

    public DateTimeOffset LoadedAt { get; }

    /// <summary>Incremented on every modification; used to invalidate caches (decompiler, type list).</summary>
    public int Version { get; private set; }

    public bool IsModified => Version > 0;

    /// <summary>All types including nested ones (dnlib's <c>Module.Types</c> only returns top-level types).</summary>
    public IReadOnlyList<TypeDef> AllTypes => _allTypes ??= Module.GetTypes().ToList();

    public InstructionParser Parser => _parser ??= new InstructionParser(Module);

    public T Read<T>(Func<LoadedAssembly, T> func)
    {
        _lock.EnterReadLock();
        try { return func(this); }
        finally { _lock.ExitReadLock(); }
    }

    public T Write<T>(Func<LoadedAssembly, T> func)
    {
        _lock.EnterWriteLock();
        try
        {
            var result = func(this);
            Version++;
            _allTypes = null;
            _parser = null;
            return result;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void Dispose()
    {
        Module.Dispose();
        _lock.Dispose();
    }
}
