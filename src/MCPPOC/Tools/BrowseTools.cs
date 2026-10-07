using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MCPPOC.Analysis;
using MCPPOC.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MCPPOC.Tools;

public sealed record NamespaceInfo(string Namespace, int TypeCount);

public sealed record MemberMatch(string Token, string Kind, string Name, string FullName, string DeclaringType);

public sealed record EntryPointInfo(string Role, MemberLink Method);

[McpServerToolType]
public sealed class BrowseTools(AssemblyWorkspace workspace)
{
    [McpServerTool(Name = "list_namespaces", Title = "List namespaces", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List namespaces with their type counts. A quick map of an unfamiliar assembly.")]
    public IReadOnlyList<NamespaceInfo> ListNamespaces([Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a => a.AllTypes
            .GroupBy(t => GetNamespace(t))
            .Select(g => new NamespaceInfo(g.Key.Length == 0 ? "<global>" : g.Key, g.Count()))
            .OrderBy(n => n.Namespace, StringComparer.Ordinal)
            .ToList());

    [McpServerTool(Name = "list_types", Title = "List/search types", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List types (including nested and compiler-generated ones) filtered by a regex on the full name, a namespace and/or a kind. Returns tokens usable by every other tool.")]
    public Page<TypeSummary> ListTypes(
        [Description("Optional regex matched against the type full name (case-insensitive), e.g. 'Crypt|Decode'.")] string? pattern = null,
        [Description("Optional exact namespace filter ('<global>' for the global namespace).")] string? @namespace = null,
        [Description("Optional kind filter: class, static class, interface, struct, enum, delegate.")] string? kind = null,
        [Description("Include compiler-generated types such as '<>c' or '<Method>d__0' (default true).")] bool includeCompilerGenerated = true,
        [Description(P.Offset)] int offset = 0,
        [Description(P.Limit)] int limit = Paging.DefaultLimit,
        [Description(P.Assembly)] string? assembly = null)
    {
        var regex = string.IsNullOrEmpty(pattern) ? null : MemberResolver.CreateRegex(pattern, ignoreCase: true);
        var ns = @namespace == "<global>" ? "" : @namespace;
        return workspace.Get(assembly).Read(a => Paging.Paginate(a.AllTypes
            .Where(t => regex == null || regex.IsMatch(t.FullName))
            .Where(t => ns == null || GetNamespace(t) == ns)
            .Where(t => kind == null || string.Equals(ModelFactory.TypeKind(t), kind, StringComparison.OrdinalIgnoreCase))
            .Where(t => includeCompilerGenerated || !IsCompilerGenerated(t))
            .Select(ModelFactory.Summary), offset, limit));
    }

    [McpServerTool(Name = "get_type", Title = "Type details", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Full description of a type: base type, interfaces, attributes, generic parameters, nested types and all fields, methods, properties and events with their tokens.")]
    public TypeDetails GetType(
        [Description(P.Type)] string type,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            var t = MemberResolver.ResolveType(a, type);
            return new TypeDetails(
                ModelFactory.Hex(t.MDToken), t.FullName, ModelFactory.TypeKind(t), ModelFactory.Visibility(t), t.Attributes.ToString(),
                t.BaseType?.FullName,
                t.Interfaces.Select(i => i.Interface.FullName).ToList(),
                t.GenericParameters.Select(g => g.Name.String).ToList(),
                t.CustomAttributes.Select(WorkspaceTools.FormatAttribute).ToList(),
                t.DeclaringType?.FullName,
                t.NestedTypes.Select(n => ModelFactory.Ref(n)).ToList(),
                t.Fields.Select(ModelFactory.Summary).ToList(),
                t.Methods.Select(ModelFactory.Summary).ToList(),
                t.Properties.Select(p => ModelFactory.Ref(p)).ToList(),
                t.Events.Select(e => ModelFactory.Ref(e)).ToList());
        });

    [McpServerTool(Name = "find_methods", Title = "Find methods", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find methods whose name (or full signature when 'matchFullName' is true) matches a regex, across all types.")]
    public Page<MethodSummary> FindMethods(
        [Description("Regex, e.g. '^Decrypt' or 'Password'. Case-insensitive.")] string pattern,
        [Description("Match against the full signature (return type, declaring type, parameters) instead of the bare name.")] bool matchFullName = false,
        [Description("Only methods with an IL body.")] bool withBodyOnly = false,
        [Description(P.Offset)] int offset = 0,
        [Description(P.Limit)] int limit = Paging.DefaultLimit,
        [Description(P.Assembly)] string? assembly = null)
    {
        var regex = MemberResolver.CreateRegex(pattern, ignoreCase: true);
        return workspace.Get(assembly).Read(a => Paging.Paginate(a.AllTypes
            .SelectMany(t => t.Methods)
            .Where(m => !withBodyOnly || m.HasBody)
            .Where(m => regex.IsMatch(matchFullName ? m.FullName : m.Name.String))
            .Select(ModelFactory.Summary), offset, limit));
    }

    [McpServerTool(Name = "find_members", Title = "Find fields/properties/events", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search fields, properties and/or events by a regex on their name across the whole assembly.")]
    public Page<MemberMatch> FindMembers(
        [Description("Regex on the member name (case-insensitive). Use '.' to list everything.")] string pattern,
        [Description("Kinds to include: 'field', 'property', 'event' or 'all' (default).")] string kind = "all",
        [Description(P.Offset)] int offset = 0,
        [Description(P.Limit)] int limit = Paging.DefaultLimit,
        [Description(P.Assembly)] string? assembly = null)
    {
        var regex = MemberResolver.CreateRegex(pattern, ignoreCase: true);
        var k = kind.ToLowerInvariant();
        if (k is not ("all" or "field" or "property" or "event"))
            throw new McpException("kind must be one of: all, field, property, event.");
        return workspace.Get(assembly).Read(a =>
        {
            var members = new List<IMemberDef>();
            foreach (var t in a.AllTypes)
            {
                if (k is "all" or "field") members.AddRange(t.Fields);
                if (k is "all" or "property") members.AddRange(t.Properties);
                if (k is "all" or "event") members.AddRange(t.Events);
            }
            return Paging.Paginate(members.Where(m => regex.IsMatch(m.Name))
                .Select(m => new MemberMatch(ModelFactory.Hex(m.MDToken), ModelFactory.Ref(m).Kind, m.Name, m.FullName, m.DeclaringType?.FullName ?? "")),
                offset, limit);
        });
    }

    [McpServerTool(Name = "get_method", Title = "Method details", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Signature, attributes, RVA/file offset, body statistics (locals, exception handlers, max stack), P/Invoke info and overrides of a method.")]
    public MethodDetails GetMethod(
        [Description(P.Method)] string method,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            var m = MemberResolver.ResolveMethod(a, method);
            MethodBodyInfo? body = null;
            if (m.HasBody)
            {
                var b = m.Body;
                body = new MethodBodyInfo(
                    b.Instructions.Sum(i => i.GetSize()), b.Instructions.Count, b.MaxStack, b.InitLocals,
                    b.Variables.Select(v => $"V_{v.Index}{(string.IsNullOrEmpty(v.Name) ? "" : $" ({v.Name})")}: {v.Type.FullName}").ToList(),
                    b.ExceptionHandlers.Select(FormatHandler).ToList());
            }
            string? fileOffset = null;
            if (m.RVA != 0)
            {
                try { fileOffset = ModelFactory.Hex((uint)a.Module.Metadata.PEImage.ToFileOffset(m.RVA)); }
                catch { /* RVA outside image */ }
            }
            return new MethodDetails(
                ModelFactory.Hex(m.MDToken), m.FullName, m.DeclaringType.FullName, m.ReturnType.FullName,
                m.Parameters.Where(p => !p.IsHiddenThisParameter).Select(p => $"{p.Type.FullName} {p.Name}").ToList(),
                m.GenericParameters.Select(g => g.Name.String).ToList(),
                m.Attributes.ToString(), m.ImplAttributes.ToString(),
                m.CustomAttributes.Select(WorkspaceTools.FormatAttribute).ToList(),
                m.RVA != 0 ? ModelFactory.Hex((uint)m.RVA) : null, fileOffset, body,
                m.ImplMap is { } map ? new PInvokeInfo(map.Module?.Name ?? "?", map.Name, map.Attributes.ToString()) : null,
                m.Overrides.Select(o => o.MethodDeclaration.FullName).ToList());
        });

    [McpServerTool(Name = "get_entry_points", Title = "Entry points", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Code that runs first: the managed entry point, the module initializer (<Module>::.cctor, abused by packers), static constructors, and DllMain-like exports. Start triage here.")]
    public IReadOnlyList<EntryPointInfo> GetEntryPoints([Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            var result = new List<EntryPointInfo>();
            if (a.Module.EntryPoint is { } ep)
            {
                result.Add(new("Entry point", ModelFactory.Ref(ep)));
                if (ep.DeclaringType.FindStaticConstructor() is { } epCctor)
                    result.Add(new("Static constructor of entry point type (runs before Main)", ModelFactory.Ref(epCctor)));
            }
            if (a.Module.GlobalType?.FindStaticConstructor() is { } moduleInit)
                result.Add(new("Module initializer (<Module>::.cctor, runs on assembly load)", ModelFactory.Ref(moduleInit)));
            foreach (var m in a.AllTypes.SelectMany(t => t.Methods)
                         .Where(m => m.CustomAttributes.Any(c => c.TypeFullName == "System.Runtime.CompilerServices.ModuleInitializerAttribute")))
                result.Add(new("[ModuleInitializer] method", ModelFactory.Ref(m)));
            foreach (var m in a.AllTypes.SelectMany(t => t.Methods).Where(m => m.ExportInfo != null))
                result.Add(new($"Unmanaged export '{m.ExportInfo!.Name ?? m.Name}'", ModelFactory.Ref(m)));
            foreach (var cctor in a.AllTypes.Where(t => !t.IsGlobalModuleType).Select(t => t.FindStaticConstructor()).OfType<MethodDef>()
                         .Where(c => c.HasBody && c.Body.Instructions.Count > 0))
                if (result.All(r => r.Method.Token != ModelFactory.Hex(cctor.MDToken)))
                    result.Add(new("Static constructor", ModelFactory.Ref(cctor)));
            return result;
        });

    [McpServerTool(Name = "list_pinvokes", Title = "Native imports (P/Invoke)", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List all P/Invoke (DllImport) declarations: native DLL, entry point and the managed method that wraps it.")]
    public IReadOnlyList<MethodDetails> ListPInvokes([Description(P.Assembly)] string? assembly = null)
    {
        var asm = workspace.Get(assembly);
        var tokens = asm.Read(a => a.AllTypes.SelectMany(t => t.Methods).Where(m => m.ImplMap != null)
            .Select(m => ModelFactory.Hex(m.MDToken)).ToList());
        return tokens.Select(t => GetMethod(t, asm.Id)).ToList();
    }

    [McpServerTool(Name = "get_static_field_values", Title = "Static field values", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Static fields of a type with their constant values, raw initial data (FieldRVA, e.g. byte[] initializers / encryption keys) and the value assigned in the static constructor when it is a simple constant.")]
    public IReadOnlyList<StaticFieldValue> GetStaticFieldValues(
        [Description(P.Type)] string type,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            var t = MemberResolver.ResolveType(a, type);
            var cctor = t.FindStaticConstructor();
            var assigned = new Dictionary<FieldDef, string>();
            if (cctor?.HasBody == true)
            {
                var instrs = cctor.Body.Instructions;
                for (var i = 1; i < instrs.Count; i++)
                {
                    if (instrs[i].OpCode.Code == Code.Stsfld && instrs[i].Operand is FieldDef f && f.DeclaringType == t)
                    {
                        var prev = instrs[i - 1];
                        var value = prev.OpCode.Code == Code.Ldstr ? ModelFactory.FormatConstant(prev.Operand)
                            : prev.IsLdcI4() ? prev.GetLdcI4Value().ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : prev.OpCode.Code is Code.Ldc_I8 or Code.Ldc_R4 or Code.Ldc_R8 ? ModelFactory.FormatConstant(prev.Operand)
                            : $"<computed: ... {prev.OpCode.Name} {ModelFactory.FormatOperand(prev)}>".Replace(" >", ">", StringComparison.Ordinal);
                        assigned[f] = value!;
                    }
                }
            }
            return t.Fields.Where(f => f.IsStatic).Select(f => new StaticFieldValue(
                ModelFactory.Hex(f.MDToken), f.Name, f.FieldType.FullName, f.Attributes.ToString(),
                f.HasConstant ? ModelFactory.FormatConstant(f.Constant.Value) : null,
                f.HasFieldRVA && f.InitialValue is { Length: > 0 } iv ? ModelFactory.HexBytes(iv, 256) : null,
                assigned.GetValueOrDefault(f))).ToList();
        });

    internal static string FormatHandler(ExceptionHandler eh) =>
        $"{eh.HandlerType} try {Off(eh.TryStart)}-{Off(eh.TryEnd)} handler {Off(eh.HandlerStart)}-{Off(eh.HandlerEnd)}" +
        (eh.CatchType != null ? $" catch {eh.CatchType.FullName}" : "") +
        (eh.FilterStart != null ? $" filter {Off(eh.FilterStart)}" : "");

    private static string Off(Instruction? i) => i == null ? "end" : ModelFactory.Il(i.Offset);

    private static string GetNamespace(TypeDef t)
    {
        while (t.DeclaringType != null)
            t = t.DeclaringType;
        return t.Namespace;
    }

    private static bool IsCompilerGenerated(TypeDef t) =>
        t.Name.String.Contains('<') ||
        t.CustomAttributes.Any(c => c.TypeFullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
}
