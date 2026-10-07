using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace MCPPOC.Analysis;

/// <summary>
/// Placeholder operand for a branch target that is resolved by <see cref="MethodPatcher"/>.
/// <c>IL_XXXX</c> refers to an instruction of the original body; <c>#N</c> refers to the N-th (0-based)
/// instruction of the snippet being inserted.
/// </summary>
public sealed record BranchLabel(string Target);

/// <summary>
/// Parses textual IL (ilasm/ILSpy syntax or the format dnlib prints, e.g. as returned by 'get_method_il')
/// into dnlib <see cref="Instruction"/>s whose operands are real metadata references.
/// </summary>
public sealed partial class InstructionParser
{
    private static readonly Dictionary<string, OpCode> OpCodeMap = BuildOpCodeMap();

    private static readonly Dictionary<string, string> TypeKeywords = new(StringComparer.Ordinal)
    {
        ["void"] = "System.Void", ["bool"] = "System.Boolean", ["char"] = "System.Char",
        ["int8"] = "System.SByte", ["sbyte"] = "System.SByte", ["uint8"] = "System.Byte", ["byte"] = "System.Byte",
        ["int16"] = "System.Int16", ["short"] = "System.Int16", ["uint16"] = "System.UInt16", ["ushort"] = "System.UInt16",
        ["int32"] = "System.Int32", ["int"] = "System.Int32", ["uint32"] = "System.UInt32", ["uint"] = "System.UInt32",
        ["int64"] = "System.Int64", ["long"] = "System.Int64", ["uint64"] = "System.UInt64", ["ulong"] = "System.UInt64",
        ["float32"] = "System.Single", ["float"] = "System.Single", ["single"] = "System.Single",
        ["float64"] = "System.Double", ["double"] = "System.Double", ["decimal"] = "System.Decimal",
        ["string"] = "System.String", ["object"] = "System.Object",
        ["nint"] = "System.IntPtr", ["nuint"] = "System.UIntPtr", ["typedref"] = "System.TypedReference",
    };

    private readonly ModuleDef _module;

    private Dictionary<string, IMethod>? _methodIndex;
    private Dictionary<string, IField>? _fieldIndex;
    private Dictionary<string, TypeSpec>? _typeSpecIndex;

    public InstructionParser(ModuleDef module) => _module = module;

    // Normalized full name -> existing member, built once per parser (a parser is discarded when the module changes).
    private Dictionary<string, IMethod> MethodIndex => _methodIndex ??= BuildIndex(
        _module.GetTypes().SelectMany(t => t.Methods).Cast<IMethod>()
            .Concat(_module.GetMemberRefs().Where(r => r.IsMethodRef))
            .Concat(EnumerateMethodSpecs()));

    private Dictionary<string, IField> FieldIndex => _fieldIndex ??= BuildIndex(
        _module.GetTypes().SelectMany(t => t.Fields).Cast<IField>()
            .Concat(_module.GetMemberRefs().Where(r => r.IsFieldRef)));

    private Dictionary<string, TypeSpec> TypeSpecIndex => _typeSpecIndex ??= BuildIndex(EnumerateTypeSpecs());

    private static Dictionary<string, T> BuildIndex<T>(IEnumerable<T> members) where T : IFullName
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var m in members)
            index.TryAdd(Key(m.FullName), m); // first wins: definitions before references
        return index;
    }

    public List<Instruction> ParseInstructions(string input, MethodDef? method = null)
    {
        var result = new List<Instruction>();
        var lineNo = 0;
        foreach (var rawLine in input.Split('\n'))
        {
            lineNo++;
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;
            try
            {
                var instr = ParseSingleInstruction(line, method);
                if (instr != null)
                    result.Add(instr);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Line {lineNo} ('{line}'): {ex.Message}", ex);
            }
        }
        return result;
    }

    public Instruction? ParseSingleInstruction(string line, MethodDef? method = null)
    {
        line = StripComment(line).Trim();
        if (line.Length == 0)
            return null;

        // Accept pasted listings: "IL_0012: ldstr "x"" or "IL_0012 ldstr ..." or "0012: ..."
        line = LabelPrefix().Replace(line, "");

        var space = line.IndexOfAny([' ', '\t', '(']);
        var opName = space < 0 ? line : line[..space];
        var operandText = space < 0 ? "" : line[space..].Trim();

        if (!OpCodeMap.TryGetValue(opName, out var opcode) &&
            !OpCodeMap.TryGetValue(opName.Replace(".", "", StringComparison.Ordinal), out opcode))
            throw new ArgumentException($"Unknown opcode '{opName}'.");

        var operand = ParseOperand(opcode, operandText, method);
        return new Instruction(opcode, operand);
    }

    internal object? ParseOperand(OpCode opcode, string text, MethodDef? method = null)
    {
        switch (opcode.OperandType)
        {
            case OperandType.InlineNone:
                if (text.Length > 0)
                    throw new ArgumentException($"Opcode '{opcode.Name}' does not take an operand.");
                return null;

            case OperandType.InlineString:
                return ParseStringLiteral(text);

            case OperandType.ShortInlineI:
                // ldc.i4.s takes a signed byte; 'unaligned.' takes an unsigned byte.
                return opcode.Code == Code.Ldc_I4_S ? (object)checked((sbyte)ParseInteger(text)) : checked((byte)ParseInteger(text));

            case OperandType.InlineI:
                return unchecked((int)ParseInteger(text));

            case OperandType.InlineI8:
                return ParseInteger(text.TrimEnd('L', 'l'));

            case OperandType.InlineR:
                return double.Parse(text.TrimEnd('d', 'D'), NumberStyles.Float, CultureInfo.InvariantCulture);

            case OperandType.ShortInlineR:
                return float.Parse(text.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture);

            case OperandType.InlineBrTarget:
            case OperandType.ShortInlineBrTarget:
                return ParseBranchLabel(text);

            case OperandType.InlineSwitch:
            {
                var inner = text.Trim().TrimStart('(').TrimEnd(')');
                return inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseBranchLabel).ToArray();
            }

            case OperandType.InlineVar:
            case OperandType.ShortInlineVar:
                return ResolveVariable(opcode, text, method);

            case OperandType.InlineMethod:
                return ResolveMethod(text);

            case OperandType.InlineField:
                return ResolveField(text);

            case OperandType.InlineType:
                return ResolveType(text);

            case OperandType.InlineTok:
            {
                var t = text.Trim();
                if (t.StartsWith("method ", StringComparison.Ordinal))
                    return ResolveMethod(t[7..]);
                if (t.StartsWith("field ", StringComparison.Ordinal))
                    return ResolveField(t[6..]);
                if (t.Contains("::", StringComparison.Ordinal))
                    return t.Contains('(') ? ResolveMethod(t) : ResolveField(t);
                return ResolveType(t);
            }

            default:
                throw new ArgumentException($"Operand type {opcode.OperandType} ('{opcode.Name}') is not supported by the IL parser.");
        }
    }

    /// <summary>
    /// Resolves 'ldloc'/'stloc'/'ldloca' operands to <see cref="Local"/>s and 'ldarg'/'starg'/'ldarga' operands
    /// to <see cref="Parameter"/>s of <paramref name="method"/>. Accepts an index, V_n / A_n, or a name.
    /// </summary>
    public object ResolveVariable(OpCode opcode, string text, MethodDef? method)
    {
        text = text.Trim();
        var isLocal = opcode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloca or Code.Ldloca_S or Code.Stloc or Code.Stloc_S;
        if (method == null)
            throw new ArgumentException($"'{opcode.Name}' needs a method context to resolve '{text}'.");

        int? index = null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            index = n;
        else if (text.Length > 2 && (text.StartsWith("V_", StringComparison.OrdinalIgnoreCase) || text.StartsWith("A_", StringComparison.OrdinalIgnoreCase))
                 && int.TryParse(text.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            index = n;

        if (isLocal)
        {
            var locals = method.Body?.Variables ?? throw new ArgumentException($"Method {method.FullName} has no body/locals.");
            if (index is { } i)
                return i >= 0 && i < locals.Count ? locals[i] : throw new ArgumentException($"Local index {i} out of range (method has {locals.Count} locals).");
            return locals.FirstOrDefault(l => l.Name == text) ?? throw new ArgumentException($"Local '{text}' not found.");
        }

        // method.Parameters includes the hidden 'this' parameter at index 0 for instance methods, which matches IL arg numbering.
        if (index is { } a)
            return a >= 0 && a < method.Parameters.Count ? method.Parameters[a] : throw new ArgumentException($"Argument index {a} out of range (method has {method.Parameters.Count} args).");
        if (text == "this" && method.HasThis)
            return method.Parameters[0];
        return method.Parameters.FirstOrDefault(p => p.Name == text) ?? throw new ArgumentException($"Parameter '{text}' not found.");
    }

    // ---------------------------------------------------------------- members

    public IMethod ResolveMethod(string text)
    {
        var spec = ParseMemberSpec(text, isMethod: true);
        var key = spec.ComparisonKey;

        // 1. Reuse an existing definition/reference with identical signature: guaranteed valid metadata.
        if (MethodIndex.TryGetValue(key, out var known))
            return known;

        // 2. Build a reference and try to bind it to the real definition (fixes signature and scope).
        var declaringType = ResolveTypeDefOrRef(spec.DeclaringType, spec.AssemblyScope);
        var retType = ParseTypeSig(spec.ReturnType);
        var paramSigs = spec.Parameters.Select(ParseTypeSig).ToArray();

        if (declaringType.ResolveTypeDef() is { } typeDef)
        {
            var candidates = typeDef.Methods.Where(m => m.Name == spec.Name && m.MethodSig.Params.Count == paramSigs.Length).ToList();
            var comparer = new SigComparer();
            var exact = candidates.FirstOrDefault(m => m.MethodSig.Params.Select((p, i) => comparer.Equals(p, paramSigs[i])).All(x => x));
            var chosen = exact ?? (candidates.Count == 1 ? candidates[0] : null);
            if (chosen != null)
                return chosen.Module == _module ? chosen : _module.Import(chosen);
        }

        var sig = spec.IsInstance ? MethodSig.CreateInstance(retType, paramSigs) : MethodSig.CreateStatic(retType, paramSigs);
        return new MemberRefUser(_module, spec.Name, sig, declaringType);
    }

    public IField ResolveField(string text)
    {
        var spec = ParseMemberSpec(text, isMethod: false);
        var key = spec.ComparisonKey;

        if (FieldIndex.TryGetValue(key, out var known))
            return known;

        var declaringType = ResolveTypeDefOrRef(spec.DeclaringType, spec.AssemblyScope);
        if (declaringType.ResolveTypeDef() is { } typeDef && typeDef.Fields.FirstOrDefault(f => f.Name == spec.Name) is { } fieldDef)
            return fieldDef.Module == _module ? fieldDef : _module.Import(fieldDef);

        return new MemberRefUser(_module, spec.Name, new FieldSig(ParseTypeSig(spec.ReturnType)), declaringType);
    }

    public ITypeDefOrRef ResolveType(string text)
    {
        var cleaned = text.Trim();
        foreach (var prefix in new[] { "class ", "valuetype " })
            if (cleaned.StartsWith(prefix, StringComparison.Ordinal))
                cleaned = cleaned[prefix.Length..].Trim();

        // Arrays, pointers, generic instances → TypeSpec.
        if (cleaned.EndsWith(']') && !cleaned.StartsWith('[') || cleaned.EndsWith('*') || cleaned.EndsWith('&') || cleaned.Contains('<') || cleaned.StartsWith('!'))
        {
            if (TypeSpecIndex.TryGetValue(Key(NormalizeTypeText(cleaned)), out var known))
                return known;
            return new TypeSpecUser(ParseTypeSig(cleaned));
        }

        var (scope, name) = SplitScope(cleaned);
        return ResolveTypeDefOrRef(name, scope);
    }

    private ITypeDefOrRef ResolveTypeDefOrRef(string name, string? assemblyScope)
    {
        name = NormalizeTypeText(name).Replace('+', '/');

        if (assemblyScope == null)
        {
            var def = _module.Find(name, isReflectionName: false);
            if (def != null)
                return def;
        }

        var existing = _module.GetTypeRefs().FirstOrDefault(r => r.FullName == name &&
            (assemblyScope == null || string.Equals(r.DefinitionAssembly?.Name, assemblyScope, StringComparison.OrdinalIgnoreCase)));
        if (existing != null)
            return existing;

        SplitNamespace(name, out var ns, out var simpleName);

        if (assemblyScope == null)
        {
            var corlib = _module.CorLibTypes.GetCorLibTypeSig(ns, simpleName, _module.CorLibTypes.AssemblyRef);
            if (corlib != null)
                return corlib.TypeDefOrRef;
            // Assume the core library for System.* types, which is right for the vast majority of BCL types.
            return new TypeRefUser(_module, ns, simpleName, _module.CorLibTypes.AssemblyRef);
        }

        var asmRef = _module.GetAssemblyRefs().FirstOrDefault(a => string.Equals(a.Name, assemblyScope, StringComparison.OrdinalIgnoreCase))
                     ?? (IResolutionScope)new AssemblyRefUser(assemblyScope);
        return new TypeRefUser(_module, ns, simpleName, asmRef);
    }

    public TypeSig ParseTypeSig(string text)
    {
        var t = text.Trim();
        foreach (var prefix in new[] { "class ", "valuetype " })
            if (t.StartsWith(prefix, StringComparison.Ordinal))
                t = t[prefix.Length..].Trim();

        if (t.EndsWith("[]", StringComparison.Ordinal))
            return new SZArraySig(ParseTypeSig(t[..^2]));
        if (t.EndsWith('&'))
            return new ByRefSig(ParseTypeSig(t[..^1]));
        if (t.EndsWith('*'))
            return new PtrSig(ParseTypeSig(t[..^1]));
        if (t.StartsWith("!!", StringComparison.Ordinal) && uint.TryParse(t.AsSpan(2), out var mvar))
            return new GenericMVar(mvar);
        if (t.StartsWith('!') && uint.TryParse(t.AsSpan(1), out var tvar))
            return new GenericVar(tvar);

        var lt = t.IndexOf('<');
        if (lt > 0 && t.EndsWith('>'))
        {
            var genericType = ResolveTypeDefOrRef(SplitScope(t[..lt]).Name, SplitScope(t[..lt]).Scope);
            var args = SplitTopLevel(t[(lt + 1)..^1]).Select(ParseTypeSig).ToList();
            var genericSig = genericType.ToTypeSig() as ClassOrValueTypeSig ?? new ClassSig(genericType);
            return new GenericInstSig(genericSig, args);
        }

        var normalized = NormalizeTypeText(t);
        SplitNamespace(normalized, out var ns, out var name);
        if (ns == "System" && !normalized.Contains('[') && _module.CorLibTypes.GetCorLibTypeSig(ns, name, _module.CorLibTypes.AssemblyRef) is { } corlib)
            return corlib;

        var (scope, typeName) = SplitScope(t);
        return ResolveTypeDefOrRef(typeName, scope).ToTypeSig();
    }

    // ---------------------------------------------------------------- helpers

    private sealed record MemberSpec(bool IsInstance, string ReturnType, string? AssemblyScope, string DeclaringType, string Name, IReadOnlyList<string> Parameters, bool IsMethod)
    {
        public string ComparisonKey => Key(IsMethod
            ? $"{NormalizeTypeText(ReturnType)} {NormalizeTypeText(DeclaringType)}::{Name}({string.Join(",", Parameters.Select(NormalizeTypeText))})"
            : $"{NormalizeTypeText(ReturnType)} {NormalizeTypeText(DeclaringType)}::{Name}");
    }

    private static MemberSpec ParseMemberSpec(string text, bool isMethod)
    {
        var t = text.Trim();
        var isInstance = false;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var mod in new[] { "instance ", "static ", "explicit ", "default ", "vararg " })
            {
                if (t.StartsWith(mod, StringComparison.Ordinal))
                {
                    isInstance |= mod == "instance ";
                    t = t[mod.Length..].TrimStart();
                    changed = true;
                }
            }
        }

        var sep = t.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0)
            throw new ArgumentException($"Expected '<type> <DeclaringType>::<name>{(isMethod ? "(<params>)" : "")}', got '{text}'.");

        var head = t[..sep].TrimEnd();
        var tail = t[(sep + 2)..].Trim();

        // head = "<return type> <declaring type>" — split at the last top-level space.
        var split = LastTopLevelSpace(head);
        if (split < 0)
            throw new ArgumentException($"Missing return/field type in '{text}'.");
        var retType = head[..split].Trim();
        var declaring = head[(split + 1)..].Trim();
        var (scope, declaringName) = SplitScope(declaring);

        string name;
        IReadOnlyList<string> parameters = [];
        if (isMethod)
        {
            var paren = tail.IndexOf('(');
            if (paren < 0 || !tail.EndsWith(')'))
                throw new ArgumentException($"Expected a parameter list in '{text}'.");
            name = tail[..paren].Trim();
            parameters = SplitTopLevel(tail[(paren + 1)..^1]);
        }
        else
        {
            name = tail;
        }

        return new MemberSpec(isInstance, retType, scope, declaringName, name, parameters, isMethod);
    }

    private static (string? Scope, string Name) SplitScope(string text)
    {
        var t = text.Trim();
        foreach (var prefix in new[] { "class ", "valuetype " })
            if (t.StartsWith(prefix, StringComparison.Ordinal))
                t = t[prefix.Length..].Trim();
        if (t.StartsWith('['))
        {
            var close = t.IndexOf(']');
            if (close > 0)
                return (t[1..close].Trim(), t[(close + 1)..].Trim());
        }
        return (null, t);
    }

    /// <summary>Converts ilasm/C# keywords to System.* names and drops [assembly] scopes and class/valuetype prefixes.</summary>
    internal static string NormalizeTypeText(string text)
    {
        var t = text.Replace("native unsigned int", "nuint", StringComparison.Ordinal)
            .Replace("native int", "nint", StringComparison.Ordinal)
            .Replace("unsigned int", "uint", StringComparison.Ordinal);
        t = ScopePrefix().Replace(t, "");
        t = ClassPrefix().Replace(t, "");
        return IdentifierToken().Replace(t, m => TypeKeywords.TryGetValue(m.Value, out var full) ? full : m.Value);
    }

    private static string Key(string fullName) => Whitespace().Replace(NormalizeTypeText(fullName), "");

    private static int LastTopLevelSpace(string s)
    {
        var depth = 0;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            switch (s[i])
            {
                case '>' or ']' or ')': depth++; break;
                case '<' or '[' or '(': depth--; break;
                case ' ' when depth == 0: return i;
            }
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '<' or '[' or '(': depth++; break;
                case '>' or ']' or ')': depth--; break;
                case ',' when depth == 0:
                    parts.Add(s[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        var last = s[start..].Trim();
        if (last.Length > 0)
            parts.Add(last);
        return parts;
    }

    private static void SplitNamespace(string fullName, out string ns, out string name)
    {
        var nestedSep = fullName.IndexOf('/');
        var outer = nestedSep >= 0 ? fullName[..nestedSep] : fullName;
        var dot = outer.LastIndexOf('.');
        ns = dot > 0 ? outer[..dot] : "";
        name = (dot > 0 ? outer[(dot + 1)..] : outer) + (nestedSep >= 0 ? fullName[nestedSep..] : "");
    }

    private static BranchLabel ParseBranchLabel(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("IL_", StringComparison.OrdinalIgnoreCase) || t.StartsWith('#'))
            return new BranchLabel(t);
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return new BranchLabel("IL_" + t[2..]);
        throw new ArgumentException($"Invalid branch target '{text}' (use IL_XXXX for an existing instruction or #N for the N-th new instruction).");
    }

    private static long ParseInteger(string text)
    {
        var t = text.Trim();
        var negative = t.StartsWith('-');
        if (negative)
            t = t[1..];
        long value;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = unchecked((long)ulong.Parse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        else if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            throw new ArgumentException($"Invalid integer '{text}'.");
        return negative ? -value : value;
    }

    internal static string ParseStringLiteral(string text)
    {
        var t = text.Trim();
        if (t.Length < 2 || t[0] != '"' || t[^1] != '"')
            throw new ArgumentException($"String operand must be quoted: {text}");
        var sb = new StringBuilder(t.Length);
        for (var i = 1; i < t.Length - 1; i++)
        {
            var c = t[i];
            if (c != '\\' || i + 1 >= t.Length - 1)
            {
                sb.Append(c);
                continue;
            }
            var e = t[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case 'u' when i + 4 < t.Length:
                    sb.Append((char)Convert.ToUInt16(t.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                case 'x' when i + 2 < t.Length:
                    sb.Append((char)Convert.ToByte(t.Substring(i + 1, 2), 16));
                    i += 2;
                    break;
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();
    }

    private static string StripComment(string line)
    {
        // Strip "// ..." comments that are not inside a string literal.
        var inString = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            else if (!inString && line[i] == '/' && line[i + 1] == '/')
                return line[..i];
        }
        return line;
    }

    private IEnumerable<MethodSpec> EnumerateMethodSpecs()
    {
        if (_module is not ModuleDefMD md)
            yield break;
        var rows = md.TablesStream.MethodSpecTable.Rows;
        for (uint rid = 1; rid <= rows; rid++)
            if (md.ResolveMethodSpec(rid) is { } ms)
                yield return ms;
    }

    private IEnumerable<TypeSpec> EnumerateTypeSpecs()
    {
        if (_module is not ModuleDefMD md)
            yield break;
        var rows = md.TablesStream.TypeSpecTable.Rows;
        for (uint rid = 1; rid <= rows; rid++)
            if (md.ResolveTypeSpec(rid) is { } ts)
                yield return ts;
    }

    private static Dictionary<string, OpCode> BuildOpCodeMap()
    {
        var map = new Dictionary<string, OpCode>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in OpCodes.OneByteOpCodes.Concat(OpCodes.TwoByteOpCodes))
        {
            if (op.Code == Code.UNKNOWN1 || op.Code == Code.UNKNOWN2)
                continue;
            map.TryAdd(op.Name, op);
            map.TryAdd(op.Name.TrimEnd('.'), op); // "tail." / "volatile." prefixes
        }
        foreach (var op in OpCodes.OneByteOpCodes.Concat(OpCodes.TwoByteOpCodes))
            if (op.Code != Code.UNKNOWN1 && op.Code != Code.UNKNOWN2)
                map.TryAdd(op.Name.Replace(".", "", StringComparison.Ordinal), op);
        return map;
    }

    [GeneratedRegex(@"^(IL_)?[0-9A-Fa-f]{4,8}:?\s+(?=[A-Za-z])")]
    private static partial Regex LabelPrefix();

    [GeneratedRegex(@"\[[^\]\s]+\]")]
    private static partial Regex ScopePrefix();

    [GeneratedRegex(@"\b(class|valuetype)\s+")]
    private static partial Regex ClassPrefix();

    [GeneratedRegex(@"(?<![\w.])[A-Za-z_][A-Za-z0-9_]*(?![\w.])")]
    private static partial Regex IdentifierToken();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
