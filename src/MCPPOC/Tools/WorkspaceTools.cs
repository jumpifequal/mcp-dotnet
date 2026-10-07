using System.ComponentModel;
using dnlib.DotNet;
using MCPPOC.Analysis;
using MCPPOC.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MCPPOC.Tools;

internal static class P
{
    public const string Assembly = "Id of a loaded assembly (see 'list_assemblies'). Omit to use the active assembly.";
    public const string Offset = "Number of items to skip (pagination). Start at 0, then use 'nextOffset' from the previous result.";
    public const string Limit = "Maximum number of items to return (default 100, 0 = maximum of 2000).";
    public const string Method = "Method token (e.g. 0x06000012), or a name like 'Namespace.Type::Method' (add '(int32,string)' to pick an overload).";
    public const string Type = "Type full name ('Namespace.Type', nested as 'Outer/Inner' or 'Outer+Inner') or token (0x02xxxxxx).";
    public const string Member = "Token of any type/method/field/property/event, or a name ('Namespace.Type' or 'Namespace.Type::Member').";
}

public sealed record AssemblyReferenceInfo(string Name, string? Version, string? PublicKeyToken, string? ResolvedPath);

[McpServerToolType]
public sealed class WorkspaceTools(AssemblyWorkspace workspace)
{
    [McpServerTool(Name = "load_assembly", Title = "Load .NET assembly", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Load a .NET assembly (EXE/DLL, managed or mixed-mode) from disk into the workspace and make it the active assembly. Several assemblies can be loaded at once. Returns an overview: version, target framework, entry point, references, and obfuscation hints. This is the first call of every analysis.")]
    public AssemblyInfo LoadAssembly(
        [Description("Absolute path to the .NET assembly.")] string path,
        [Description("Optional short id to refer to this assembly in other tools (defaults to the file name).")] string? alias = null)
    {
        var asm = workspace.Load(path, alias);
        return asm.Read(a => BuildInfo(a, workspace));
    }

    [McpServerTool(Name = "list_assemblies", Title = "List loaded assemblies", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List all assemblies loaded in the workspace and show which one is active.")]
    public IReadOnlyList<AssemblySummary> ListAssemblies() =>
        workspace.All.Select(a => a.Read(x => new AssemblySummary(
            x.Id, x.Path, x.Module.Assembly?.Name ?? x.Module.Name, x.Module.Assembly?.Version?.ToString(),
            string.Equals(x.Id, workspace.ActiveId, StringComparison.OrdinalIgnoreCase), x.IsModified,
            x.AllTypes.Count, x.AllTypes.Sum(t => t.Methods.Count)))).ToList();

    [McpServerTool(Name = "set_active_assembly", Title = "Set active assembly", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Make a loaded assembly the default target for tools called without an 'assembly' argument.")]
    public string SetActiveAssembly([Description("Assembly id from 'list_assemblies'.")] string assembly) =>
        $"Active assembly is now '{workspace.SetActive(assembly).Id}'.";

    [McpServerTool(Name = "unload_assembly", Title = "Unload assembly", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Remove an assembly from the workspace. Unsaved patches are lost.")]
    public string UnloadAssembly([Description("Assembly id from 'list_assemblies'.")] string assembly) =>
        workspace.Unload(assembly) ? $"Unloaded '{assembly}'." : throw new McpException($"Unknown assembly id '{assembly}'.");

    [McpServerTool(Name = "get_assembly_info", Title = "Assembly overview", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Overview of an assembly: identity, target framework, runtime, PE kind, IL-only/mixed-mode, strong-name, entry point and module initializer, counts, references, native imports, assembly attributes and obfuscator detection hints.")]
    public AssemblyInfo GetAssemblyInfo([Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a => BuildInfo(a, workspace));

    [McpServerTool(Name = "list_assembly_references", Title = "Assembly references", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List referenced assemblies and whether each can be resolved on this machine (next to the assembly, GAC or .NET shared runtime).")]
    public IReadOnlyList<AssemblyReferenceInfo> ListAssemblyReferences([Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a => a.Module.GetAssemblyRefs().Select(r =>
        {
            string? resolved = null;
            try { resolved = a.Resolver.Resolve(r, a.Module)?.ManifestModule?.Location; }
            catch { /* unresolvable reference */ }
            return new AssemblyReferenceInfo(r.Name, r.Version?.ToString(),
                r.PublicKeyOrToken is { IsNullOrEmpty: false } pk ? pk.Token.ToString() : null, resolved);
        }).ToList());

    internal static AssemblyInfo BuildInfo(LoadedAssembly a, AssemblyWorkspace workspace)
    {
        var m = a.Module;
        var asm = m.Assembly;
        var targetFramework = asm?.CustomAttributes
            .FirstOrDefault(c => c.TypeFullName == "System.Runtime.Versioning.TargetFrameworkAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value?.ToString();
        var cctor = m.GlobalType?.FindStaticConstructor();

        return new AssemblyInfo(
            a.Id,
            a.Path,
            asm?.Name ?? m.Name,
            asm?.FullName ?? m.Name,
            asm?.Version?.ToString(),
            asm is { Culture.Length: > 0 } ? asm.Culture.String : null,
            asm?.PublicKey is { IsNullOrEmpty: false } pk ? pk.Token.ToString() : null,
            targetFramework,
            m.RuntimeVersion,
            m.Kind.ToString(),
            m.Machine.ToString(),
            m.IsILOnly,
            !m.IsILOnly,
            m.IsStrongNameSigned,
            m.Mvid?.ToString() ?? "",
            m.EntryPoint != null ? ModelFactory.Ref(m.EntryPoint) : null,
            cctor != null ? ModelFactory.Ref(cctor) : null,
            a.AllTypes.Count,
            a.AllTypes.Sum(t => t.Methods.Count),
            m.Resources.Count,
            m.GetAssemblyRefs().Select(r => r.FullName.ToString()).ToList(),
            m.GetModuleRefs().Select(r => r.Name.String).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            (asm?.CustomAttributes ?? new CustomAttributeCollection()).Select(FormatAttribute).ToList(),
            CodeAnalysis.ObfuscationHints(m, a.AllTypes),
            a.IsModified);
    }

    internal static string FormatAttribute(CustomAttribute ca)
    {
        var args = ca.ConstructorArguments.Select(arg => ModelFactory.FormatConstant(arg.Value is UTF8String u ? u.String : arg.Value))
            .Concat(ca.NamedArguments.Select(n => $"{n.Name} = {ModelFactory.FormatConstant(n.Argument.Value is UTF8String u ? u.String : n.Argument.Value)}"));
        return $"{ca.TypeFullName}({string.Join(", ", args)})";
    }
}
