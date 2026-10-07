using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MCPPOC.Analysis;
using MCPPOC.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MCPPOC.Tools;

[McpServerToolType]
public sealed class SearchTools(AssemblyWorkspace workspace)
{
    private static readonly SigComparer Comparer = new(SigComparerOptions.DontCompareTypeScope | SigComparerOptions.CompareMethodFieldDeclaringType | SigComparerOptions.PrivateScopeIsComparable);

    [McpServerTool(Name = "find_strings", Title = "Find string literals", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List string literals loaded by code (ldstr) together with the method and IL offset that uses them. Filter with a substring or regex to hunt for URLs, paths, keys, SQL, commands, etc.")]
    public Page<StringLiteral> FindStrings(
        [Description("Optional filter. Treated as a case-insensitive substring unless 'isRegex' is true.")] string? filter = null,
        [Description("Interpret 'filter' as a regular expression.")] bool isRegex = false,
        [Description("Return each distinct string only once (first occurrence).")] bool distinct = false,
        [Description(P.Offset)] int offset = 0,
        [Description(P.Limit)] int limit = Paging.DefaultLimit,
        [Description(P.Assembly)] string? assembly = null)
    {
        var regex = isRegex && !string.IsNullOrEmpty(filter) ? MemberResolver.CreateRegex(filter, ignoreCase: true) : null;
        return workspace.Get(assembly).Read(a =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var items = Instructions(a)
                .Where(x => x.Instr.OpCode.Code == Code.Ldstr && x.Instr.Operand is string)
                .Select(x => (x.Method, x.Instr, Value: (string)x.Instr.Operand))
                .Where(x => filter == null || (regex != null ? regex.IsMatch(x.Value) : x.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .Where(x => !distinct || seen.Add(x.Value))
                .Select(x => new StringLiteral(x.Value, ModelFactory.Ref(x.Method), ModelFactory.Il(x.Instr.Offset)));
            return Paging.Paginate(items, offset, limit);
        });
    }

    [McpServerTool(Name = "find_callers", Title = "Find callers of a method", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find every call site (call, callvirt, newobj, ldftn, ldvirtftn) of a method in the assembly, including calls through generic instantiations and member references. Also works for external methods given as a full name, e.g. 'System.Void System.Console::WriteLine(System.String)' or 'System.Diagnostics.Process::Start'.")]
    public Page<CodeReference> FindCallers(
        [Description(P.Method + " For external APIs pass 'Namespace.Type::Method' to match all overloads.")] string method,
        [Description(P.Offset)] int offset = 0,
        [Description(P.Limit)] int limit = Paging.DefaultLimit,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            Func<IMethod, bool> matches;
            MethodDef? def = null;
            try { def = MemberResolver.ResolveMethod(a, method); }
            catch (McpException) when (method.Contains("::")) { /* external */ }

            if (def != null)
            {
                matches = m => m is MethodDef d ? d == def : Comparer.Equals(Underlying(m), def) || Underlying(m).ResolveMethodDef() == def;
            }
            else
            {
                var key = method.Trim();
                matches = m =>
                {
                    var u = Underlying(m);
                    var shortName = $"{u.DeclaringType?.FullName}::{u.Name}";
                    return u.FullName == key || shortName == key || shortName.EndsWith("." + key, StringComparison.Ordinal);
                };
            }

            var refs = Instructions(a)
                .Where(x => x.Instr.Operand is IMethod target && IsCallLike(x.Instr.OpCode.Code) && matches(target))
                .Select(x => new CodeReference(ModelFactory.Ref(x.Method), ModelFactory.Il(x.Instr.Offset), x.Instr.OpCode.Name, ((IMethod)x.Instr.Operand).FullName));
            return Paging.Paginate(refs, offset, limit);
        });

    [McpServerTool(Name = "find_callees", Title = "Methods called by a method", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List what a method calls, reads and writes (methods, fields, types and strings) in IL order: a quick behavioural summary without decompiling.")]
    public IReadOnlyList<CodeReference> FindCallees(
        [Description(P.Method)] string method,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            var m = MemberResolver.ResolveMethod(a, method);
            if (!m.HasBody)
                throw new McpException($"Method {m.FullName} has no IL body.");
            var owner = ModelFactory.Ref(m);
            return m.Body.Instructions
                .Where(i => i.Operand is IMethod or IField or ITypeDefOrRef or string)
                .Select(i => new CodeReference(owner, ModelFactory.Il(i.Offset), i.OpCode.Name, i.Operand switch
                {
                    IMethod im => im.FullName,
                    IField f => f.FullName,
                    ITypeDefOrRef t => t.FullName,
                    string s => System.Text.Json.JsonSerializer.Serialize(s),
                    _ => "",
                }))
                .ToList();
        });

    [McpServerTool(Name = "find_field_references", Title = "Find field reads/writes", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find where a field is read (ldfld/ldsfld), written (stfld/stsfld) or has its address taken.")]
    public Page<CodeReference> FindFieldReferences(
        [Description("Field token (0x04xxxxxx), 'Namespace.Type::field', full dnlib name, or a bare field name.")] string field,
        [Description(P.Offset)] int offset = 0,
        [Description(P.Limit)] int limit = Paging.DefaultLimit,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            FieldDef? def = null;
            try { def = MemberResolver.ResolveField(a, field); }
            catch (McpException) when (field.Contains("::")) { /* external field */ }

            var refs = Instructions(a)
                .Where(x => x.Instr.Operand is IField f && (def != null ? f == def || f.ResolveFieldDef() == def : f.FullName.EndsWith(field, StringComparison.Ordinal)))
                .Select(x => new CodeReference(ModelFactory.Ref(x.Method), ModelFactory.Il(x.Instr.Offset), x.Instr.OpCode.Name,
                    x.Instr.OpCode.Code switch
                    {
                        Code.Stfld or Code.Stsfld => "write",
                        Code.Ldflda or Code.Ldsflda => "address",
                        Code.Ldtoken => "token",
                        _ => "read",
                    }));
            return Paging.Paginate(refs, offset, limit);
        });

    [McpServerTool(Name = "find_type_usages", Title = "Find usages of a type", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find where a type is used: instantiation, casts, member access, field/parameter/return/local types, base types and interface implementations.")]
    public Page<CodeReference> FindTypeUsages(
        [Description(P.Type + " External types can be given by full name, e.g. 'System.Net.WebClient'.")] string type,
        [Description(P.Offset)] int offset = 0,
        [Description(P.Limit)] int limit = Paging.DefaultLimit,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            string fullName;
            try { fullName = MemberResolver.ResolveType(a, type).FullName; }
            catch (McpException) { fullName = type.Trim().Replace('+', '/'); }

            bool Is(ITypeDefOrRef? t) => t != null && (t.FullName == fullName || t.ScopeType?.FullName == fullName);
            bool SigUses(TypeSig? s) => s != null && (s.FullName == fullName || s.FullName.Contains(fullName + "<", StringComparison.Ordinal) ||
                                                      s.FullName.StartsWith(fullName + "[", StringComparison.Ordinal) || s.ScopeType?.FullName == fullName);

            var results = new List<CodeReference>();
            foreach (var t in a.AllTypes)
            {
                var tRef = ModelFactory.Ref(t);
                if (Is(t.BaseType)) results.Add(new(tRef, "", "inherits", t.FullName));
                foreach (var i in t.Interfaces.Where(i => Is(i.Interface)))
                    results.Add(new(tRef, "", "implements", t.FullName));
                foreach (var f in t.Fields.Where(f => SigUses(f.FieldType)))
                    results.Add(new(ModelFactory.Ref(f), "", "field type", f.FullName));
                foreach (var m in t.Methods)
                {
                    var mRef = ModelFactory.Ref(m);
                    if (SigUses(m.ReturnType) || m.MethodSig.Params.Any(SigUses))
                        results.Add(new(mRef, "", "signature", m.FullName));
                    if (!m.HasBody) continue;
                    if (m.Body.Variables.Any(v => SigUses(v.Type)))
                        results.Add(new(mRef, "", "local variable", m.FullName));
                    foreach (var ins in m.Body.Instructions)
                    {
                        var hit = ins.Operand switch
                        {
                            ITypeDefOrRef tr => Is(tr),
                            IMethod im => Is(im.DeclaringType),
                            IField fi => Is(fi.DeclaringType),
                            _ => false,
                        };
                        if (hit)
                            results.Add(new(mRef, ModelFactory.Il(ins.Offset), ins.OpCode.Name, ins.Operand!.ToString() ?? ""));
                    }
                }
            }
            return Paging.Paginate(results, offset, limit);
        });

    [McpServerTool(Name = "find_derived_types", Title = "Find derived types / implementations", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find all types in the assembly that derive from a class (transitively) or implement an interface.")]
    public IReadOnlyList<TypeSummary> FindDerivedTypes(
        [Description(P.Type + " External base types can be given by full name, e.g. 'System.IO.Stream'.")] string type,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            string fullName;
            try { fullName = MemberResolver.ResolveType(a, type).FullName; }
            catch (McpException) { fullName = type.Trim(); }

            bool Derives(TypeDef t, int depth)
            {
                if (depth > 64) return false;
                if (t.Interfaces.Any(i => i.Interface.FullName == fullName || i.Interface.ScopeType?.FullName == fullName))
                    return true;
                var bt = t.BaseType;
                if (bt == null) return false;
                if (bt.FullName == fullName || bt.ScopeType?.FullName == fullName) return true;
                return bt.ResolveTypeDef() is { } btDef && btDef.Module == t.Module && Derives(btDef, depth + 1);
            }

            return a.AllTypes.Where(t => t.FullName != fullName && Derives(t, 0)).Select(ModelFactory.Summary).ToList();
        });

    [McpServerTool(Name = "scan_suspicious_apis", Title = "Scan for security-relevant APIs", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Security/malware triage: flag call sites of security-relevant APIs (dynamic assembly loading, Reflection.Emit, process execution, injection-related P/Invokes like VirtualAlloc/WriteProcessMemory, network, crypto, registry, unsafe deserialization, anti-debugging, keylogging...). Results are sorted by severity with per-category counts.")]
    public ApiScanResult ScanSuspiciousApis(
        [Description("Optional comma-separated categories to include, e.g. 'DynamicCode,Injection,Network'. Omit for all.")] string? categories = null,
        [Description(P.Offset)] int offset = 0,
        [Description(P.Limit)] int limit = Paging.DefaultLimit,
        [Description(P.Assembly)] string? assembly = null)
    {
        HashSet<string>? wanted = null;
        if (!string.IsNullOrWhiteSpace(categories))
        {
            wanted = new HashSet<string>(categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
            var unknown = wanted.Where(c => !ApiScanner.Categories.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
                throw new McpException($"Unknown categories: {string.Join(", ", unknown)}. Valid: {string.Join(", ", ApiScanner.Categories)}.");
            wanted = new HashSet<string>(ApiScanner.Categories.Where(c => wanted.Contains(c)), StringComparer.Ordinal);
        }
        return workspace.Get(assembly).Read(a =>
        {
            var findings = ApiScanner.Scan(a.AllTypes, wanted);
            var counts = findings.GroupBy(f => f.Category).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count());
            return new ApiScanResult(counts, Paging.Paginate(findings, offset, limit));
        });
    }

    private static IMethod Underlying(IMethod m) => m is MethodSpec ms ? ms.Method : m;

    private static bool IsCallLike(Code code) => code is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn or Code.Jmp or Code.Ldtoken;

    private static IEnumerable<(MethodDef Method, Instruction Instr)> Instructions(LoadedAssembly a)
    {
        foreach (var type in a.AllTypes)
            foreach (var method in type.Methods)
                if (method.HasBody)
                    foreach (var instr in method.Body.Instructions)
                        yield return (method, instr);
    }
}
