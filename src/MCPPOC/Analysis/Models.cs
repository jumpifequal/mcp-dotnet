using dnlib.DotNet;

namespace MCPPOC.Analysis;

// Result shapes returned by tools. Tokens are rendered as hex strings ("0x06000012") because that is how
// every .NET tool (ILSpy, dnSpy, ildasm) displays them and they can be passed back to any tool.

public sealed record AssemblySummary(
    string Id,
    string Path,
    string Name,
    string? Version,
    bool Active,
    bool Modified,
    int TypeCount,
    int MethodCount);

public sealed record AssemblyInfo(
    string Id,
    string Path,
    string Name,
    string FullName,
    string? Version,
    string? Culture,
    string? PublicKeyToken,
    string? TargetFramework,
    string RuntimeVersion,
    string ModuleKind,
    string Machine,
    bool IlOnly,
    bool MixedMode,
    bool StrongNameSigned,
    string Mvid,
    MemberLink? EntryPoint,
    MemberLink? ModuleInitializer,
    int TypeCount,
    int MethodCount,
    int ResourceCount,
    IReadOnlyList<string> AssemblyReferences,
    IReadOnlyList<string> NativeModuleReferences,
    IReadOnlyList<string> AssemblyAttributes,
    IReadOnlyList<string> ObfuscationHints,
    bool Modified);

/// <summary>Compact reference to any metadata member.</summary>
public sealed record MemberLink(string Token, string Kind, string Name, string FullName);

public sealed record TypeSummary(
    string Token,
    string FullName,
    string Namespace,
    string Name,
    string Kind,
    string Visibility,
    string? BaseType,
    string? DeclaringType,
    int MethodCount,
    int FieldCount);

public sealed record TypeDetails(
    string Token,
    string FullName,
    string Kind,
    string Visibility,
    string Attributes,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> CustomAttributes,
    string? DeclaringType,
    IReadOnlyList<MemberLink> NestedTypes,
    IReadOnlyList<FieldSummary> Fields,
    IReadOnlyList<MethodSummary> Methods,
    IReadOnlyList<MemberLink> Properties,
    IReadOnlyList<MemberLink> Events);

public sealed record MethodSummary(
    string Token,
    string Name,
    string FullName,
    string DeclaringType,
    string Signature,
    bool IsStatic,
    bool IsVirtual,
    bool IsAbstract,
    string Visibility,
    bool HasBody,
    int? CodeSize);

public sealed record MethodDetails(
    string Token,
    string FullName,
    string DeclaringType,
    string ReturnType,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string> GenericParameters,
    string Attributes,
    string ImplAttributes,
    IReadOnlyList<string> CustomAttributes,
    string? Rva,
    string? FileOffset,
    MethodBodyInfo? Body,
    PInvokeInfo? PInvoke,
    IReadOnlyList<string> Overrides);

public sealed record MethodBodyInfo(
    int CodeSize,
    int InstructionCount,
    int MaxStack,
    bool InitLocals,
    IReadOnlyList<string> Locals,
    IReadOnlyList<string> ExceptionHandlers);

public sealed record PInvokeInfo(string Module, string EntryPoint, string Attributes);

public sealed record FieldSummary(
    string Token,
    string Name,
    string FieldType,
    bool IsStatic,
    bool IsLiteral,
    string Visibility,
    string? Constant,
    string? InitialValueHex);

public sealed record ResourceSummary(
    string Name,
    string Kind,
    string Visibility,
    long? Size,
    string? Detected);

public sealed record ResourceContent(
    string Name,
    long Size,
    string? Detected,
    string? SavedTo,
    string? Text,
    string? Base64,
    string? HexPreview,
    IReadOnlyList<ResourceEntry>? Entries);

public sealed record ResourceEntry(string Name, string Type, string? Value);

public sealed record StringLiteral(string Value, MemberLink Method, string Offset);

public sealed record CodeReference(MemberLink Method, string Offset, string OpCode, string Detail);

public sealed record IlInstruction(string Offset, string? FileOffset, string? Bytes, string OpCode, string? Operand);

public sealed record MethodIl(
    string Token,
    string FullName,
    int MaxStack,
    IReadOnlyList<string> Locals,
    IReadOnlyList<string> ExceptionHandlers,
    IReadOnlyList<IlInstruction> Instructions);

public sealed record BasicBlock(
    int Index,
    string Start,
    string End,
    int InstructionCount,
    string Terminator,
    IReadOnlyList<int> Successors,
    IReadOnlyList<string> Instructions);

public sealed record ControlFlowGraph(string Method, int BlockCount, int EdgeCount, IReadOnlyList<BasicBlock> Blocks, string Mermaid);

public sealed record ApiFinding(string Category, string Severity, string Api, MemberLink? Caller, string? Offset);

public sealed record ApiScanResult(
    IReadOnlyDictionary<string, int> CountsByCategory,
    Page<ApiFinding> Findings);

public sealed record PatchResult(string Method, string Message, int InstructionCount, IReadOnlyList<IlInstruction> Window);

public sealed record SaveResult(string Path, long Size, bool MixedMode, IReadOnlyList<string> Warnings);

public sealed record HexDump(string Start, int Size, string Dump);

public sealed record StaticFieldValue(
    string Token,
    string Name,
    string FieldType,
    string Attributes,
    string? Constant,
    string? InitialValueHex,
    string? InitializerInCctor);

internal static class ModelFactory
{
    public static string Hex(MDToken token) => $"0x{token.Raw:X8}";

    public static string Hex(uint value) => $"0x{value:X8}";

    public static string Il(uint offset) => $"IL_{offset:X4}";

    public static MemberLink Ref(IMDTokenProvider member) => member switch
    {
        TypeDef t => new MemberLink(Hex(t.MDToken), "type", t.Name, t.FullName),
        MethodDef m => new MemberLink(Hex(m.MDToken), "method", m.Name, m.FullName),
        FieldDef f => new MemberLink(Hex(f.MDToken), "field", f.Name, f.FullName),
        PropertyDef p => new MemberLink(Hex(p.MDToken), "property", p.Name, p.FullName),
        EventDef e => new MemberLink(Hex(e.MDToken), "event", e.Name, e.FullName),
        IFullName n => new MemberLink(Hex(member.MDToken), member.MDToken.Table.ToString(), n.Name, n.FullName),
        _ => new MemberLink(Hex(member.MDToken), member.MDToken.Table.ToString(), member.ToString() ?? "", member.ToString() ?? ""),
    };

    public static string TypeKind(TypeDef t) =>
        t.IsInterface ? "interface" :
        t.IsEnum ? "enum" :
        t.IsDelegate ? "delegate" :
        t.IsValueType ? "struct" :
        t.IsAbstract && t.IsSealed ? "static class" :
        "class";

    public static string Visibility(TypeDef t) => t.Visibility switch
    {
        TypeAttributes.Public or TypeAttributes.NestedPublic => "public",
        TypeAttributes.NestedFamily => "protected",
        TypeAttributes.NestedFamORAssem => "protected internal",
        TypeAttributes.NestedFamANDAssem => "private protected",
        TypeAttributes.NestedPrivate => "private",
        _ => "internal",
    };

    public static string Visibility(MethodDef m) => m.Access switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Family => "protected",
        MethodAttributes.FamORAssem => "protected internal",
        MethodAttributes.FamANDAssem => "private protected",
        MethodAttributes.Assembly => "internal",
        MethodAttributes.Private => "private",
        _ => "compilercontrolled",
    };

    public static string Visibility(FieldDef f) => f.Access switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Family => "protected",
        FieldAttributes.FamORAssem => "protected internal",
        FieldAttributes.FamANDAssem => "private protected",
        FieldAttributes.Assembly => "internal",
        FieldAttributes.Private => "private",
        _ => "compilercontrolled",
    };

    public static TypeSummary Summary(TypeDef t) => new(
        Hex(t.MDToken), t.FullName, t.Namespace, t.Name, TypeKind(t), Visibility(t),
        t.BaseType?.FullName, t.DeclaringType?.FullName, t.Methods.Count, t.Fields.Count);

    public static MethodSummary Summary(MethodDef m) => new(
        Hex(m.MDToken), m.Name, m.FullName, m.DeclaringType?.FullName ?? "", m.MethodSig?.ToString() ?? "",
        m.IsStatic, m.IsVirtual, m.IsAbstract, Visibility(m), m.HasBody,
        m.HasBody ? (int?)m.Body.Instructions.Sum(i => i.GetSize()) : null);

    public static FieldSummary Summary(FieldDef f) => new(
        Hex(f.MDToken), f.Name, f.FieldType?.FullName ?? "?", f.IsStatic, f.IsLiteral, Visibility(f),
        f.HasConstant ? FormatConstant(f.Constant.Value) : null,
        f.HasFieldRVA && f.InitialValue is { Length: > 0 } iv ? HexBytes(iv, 64) : null);

    public static string? FormatConstant(object? value) => value switch
    {
        null => "null",
        string s => System.Text.Json.JsonSerializer.Serialize(s),
        char c => System.Text.Json.JsonSerializer.Serialize(c.ToString()),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    public static string HexBytes(ReadOnlySpan<byte> data, int max = int.MaxValue)
    {
        var slice = data.Length > max ? data[..max] : data;
        var hex = Convert.ToHexString(slice);
        var spaced = string.Join(' ', Enumerable.Range(0, slice.Length).Select(i => hex.Substring(i * 2, 2)));
        return data.Length > max ? $"{spaced} ... ({data.Length} bytes)" : spaced;
    }

    public static string FormatOperand(dnlib.DotNet.Emit.Instruction instr) => instr.Operand switch
    {
        null => "",
        dnlib.DotNet.Emit.Instruction target => Il(target.Offset),
        IList<dnlib.DotNet.Emit.Instruction> targets => "(" + string.Join(", ", targets.Select(t => Il(t.Offset))) + ")",
        string s => System.Text.Json.JsonSerializer.Serialize(s),
        dnlib.DotNet.Emit.Local l => string.IsNullOrEmpty(l.Name) ? $"V_{l.Index}" : l.Name,
        Parameter p => string.IsNullOrEmpty(p.Name) ? $"A_{p.Index}" : p.Name,
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => instr.Operand.ToString() ?? "",
    };
}
