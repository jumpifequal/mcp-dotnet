using System.Collections.Concurrent;
using dnlib.DotNet;
using ModelContextProtocol;

namespace MCPPOC.Workspace;

/// <summary>
/// Holds every assembly loaded during the session. Tools address assemblies by id; when no id is
/// given the most recently loaded (or explicitly activated) assembly is used.
/// </summary>
public sealed class AssemblyWorkspace : IDisposable
{
    private readonly ConcurrentDictionary<string, LoadedAssembly> _assemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string? _activeId;

    public string? ActiveId => _activeId;

    public IReadOnlyCollection<LoadedAssembly> All => _assemblies.Values.OrderBy(a => a.LoadedAt).ToList();

    public LoadedAssembly Load(string path, string? alias = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new McpException("An assembly path is required.");

        var fullPath = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (!File.Exists(fullPath))
            throw new McpException($"File not found: '{fullPath}'.");

        lock (_gate)
        {
            var existing = _assemblies.Values.FirstOrDefault(a =>
                string.Equals(a.Path, fullPath, StringComparison.OrdinalIgnoreCase) && !a.IsModified);
            if (existing != null && (alias == null || string.Equals(alias, existing.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _activeId = existing.Id;
                return existing;
            }

            var resolver = new AssemblyResolver { EnableTypeDefCache = true, FindExactMatch = false };
            var directory = System.IO.Path.GetDirectoryName(fullPath);
            if (directory != null)
                resolver.PreSearchPaths.Add(directory);
            var context = new ModuleContext(resolver);
            resolver.DefaultModuleContext = context;

            ModuleDefMD module;
            try
            {
                // Load from a byte array so the file on disk is not locked (allows saving patches over it).
                module = ModuleDefMD.Load(File.ReadAllBytes(fullPath), new ModuleCreationOptions(context));
            }
            catch (BadImageFormatException ex)
            {
                throw new McpException($"'{fullPath}' is not a valid .NET assembly: {ex.Message}");
            }

            module.Location = fullPath;
            resolver.AddToCache(module);

            var id = MakeUniqueId(alias ?? System.IO.Path.GetFileNameWithoutExtension(fullPath));
            var loaded = new LoadedAssembly(id, fullPath, module, resolver);
            _assemblies[id] = loaded;
            _activeId = id;
            return loaded;
        }
    }

    public LoadedAssembly Get(string? id = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            var active = _activeId;
            if (active != null && _assemblies.TryGetValue(active, out var current))
                return current;
            throw new McpException("No assembly loaded. Call 'load_assembly' with the path of a .NET assembly first.");
        }

        if (_assemblies.TryGetValue(id, out var assembly))
            return assembly;

        var known = _assemblies.Keys.ToList();
        throw new McpException(known.Count == 0
            ? "No assembly loaded. Call 'load_assembly' first."
            : $"Unknown assembly id '{id}'. Loaded: {string.Join(", ", known)}.");
    }

    public LoadedAssembly SetActive(string id)
    {
        var assembly = Get(id);
        _activeId = assembly.Id;
        return assembly;
    }

    public bool Unload(string id)
    {
        lock (_gate)
        {
            if (!_assemblies.TryRemove(id, out var assembly))
                return false;
            if (string.Equals(_activeId, assembly.Id, StringComparison.OrdinalIgnoreCase))
                _activeId = _assemblies.Values.OrderByDescending(a => a.LoadedAt).FirstOrDefault()?.Id;
            assembly.Dispose();
            return true;
        }
    }

    private string MakeUniqueId(string baseId)
    {
        var id = baseId;
        for (var i = 2; _assemblies.ContainsKey(id); i++)
            id = $"{baseId}#{i}";
        return id;
    }

    public void Dispose()
    {
        foreach (var assembly in _assemblies.Values)
            assembly.Dispose();
        _assemblies.Clear();
    }
}
