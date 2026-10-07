using System.Globalization;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.MD;
using MCPPOC.Workspace;
using ModelContextProtocol;

namespace MCPPOC.Analysis;

/// <summary>
/// Turns the identifiers an LLM is likely to pass (hex tokens, decimal tokens, method RIDs, full names,
/// "Type::Method" shorthands, reflection-style nested names) into dnlib definitions.
/// </summary>
public static class MemberResolver
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public static Regex CreateRegex(string pattern, bool ignoreCase = false)
    {
        try
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            return new Regex(pattern, options, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new McpException($"Invalid regular expression '{pattern}': {ex.Message}");
        }
    }

    public static bool TryParseToken(string text, out uint token)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out token);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out token);
    }

    public static IMDTokenProvider ResolveToken(LoadedAssembly asm, string text)
    {
        if (!TryParseToken(text, out var raw))
            throw new McpException($"'{text}' is not a metadata token (expected e.g. 0x06000012).");
        var member = asm.Module.ResolveToken(raw);
        return member ?? throw new McpException($"Token {ModelFactory.Hex(raw)} does not resolve to anything in '{asm.Id}'.");
    }

    public static TypeDef ResolveType(LoadedAssembly asm, string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new McpException("A type name or token is required.");
        type = type.Trim();

        if (TryParseToken(type, out var raw) && (raw >> 24) == (uint)Table.TypeDef)
            return asm.Module.ResolveToken(raw) as TypeDef
                   ?? throw new McpException($"Type token {ModelFactory.Hex(raw)} not found.");

        // Accept both dnlib ("Outer/Inner") and reflection ("Outer+Inner") nested type syntax.
        var normalized = type.Replace('+', '/');
        var match = asm.AllTypes.FirstOrDefault(t => t.FullName == normalized)
                    ?? asm.AllTypes.FirstOrDefault(t => string.Equals(t.FullName, normalized, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            return match;

        var byName = asm.AllTypes.Where(t => t.Name == type || t.Name == normalized.Split('/').Last()).ToList();
        if (byName.Count == 1)
            return byName[0];
        if (byName.Count > 1)
            throw new McpException($"Type name '{type}' is ambiguous: " +
                                   string.Join(", ", byName.Take(10).Select(t => $"{t.FullName} ({ModelFactory.Hex(t.MDToken)})")));

        var suggestions = asm.AllTypes
            .Where(t => t.FullName.Contains(normalized.Split('/', '.').Last(), StringComparison.OrdinalIgnoreCase))
            .Take(8).Select(t => t.FullName).ToList();
        throw new McpException($"Type '{type}' not found in '{asm.Id}'." +
                               (suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : " Use 'list_types' to search."));
    }

    /// <summary>
    /// Resolves a method from a token (0x06xxxxxx), a method RID (small integer, kept for backwards
    /// compatibility), a full dnlib name, or "Namespace.Type::Method" (must be unambiguous).
    /// </summary>
    public static MethodDef ResolveMethod(LoadedAssembly asm, string method)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new McpException("A method token or name is required.");
        method = method.Trim();

        if (TryParseToken(method, out var raw))
        {
            if (raw >> 24 == 0)
                raw |= (uint)Table.Method << 24; // bare RID
            if (asm.Module.ResolveToken(raw) is MethodDef byToken)
                return byToken;
            throw new McpException($"Method token {ModelFactory.Hex(raw)} not found.");
        }

        var all = asm.AllTypes.SelectMany(t => t.Methods).ToList();
        var exact = all.FirstOrDefault(m => m.FullName == method);
        if (exact != null)
            return exact;

        var sep = method.LastIndexOf("::", StringComparison.Ordinal);
        List<MethodDef> candidates;
        if (sep > 0)
        {
            var typeName = method[..sep].Replace('+', '/');
            var rest = method[(sep + 2)..];
            var name = rest.Split('(')[0].Trim();
            candidates = asm.AllTypes
                .Where(t => t.FullName == typeName || t.Name == typeName)
                .SelectMany(t => t.Methods)
                .Where(m => m.Name == name)
                .ToList();
            if (rest.Contains('(') && candidates.Count > 1)
            {
                var wanted = NormalizeParams(rest[rest.IndexOf('(')..]);
                var bySig = candidates.Where(m => NormalizeParams(ParamList(m)) == wanted).ToList();
                if (bySig.Count > 0)
                    candidates = bySig;
            }
        }
        else
        {
            candidates = all.Where(m => m.Name == method).ToList();
        }

        if (candidates.Count == 1)
            return candidates[0];
        if (candidates.Count > 1)
            throw new McpException($"Method '{method}' is ambiguous; pass a token instead: " +
                                   string.Join("; ", candidates.Take(15).Select(m => $"{ModelFactory.Hex(m.MDToken)} {m.FullName}")));
        throw new McpException($"Method '{method}' not found in '{asm.Id}'. Use 'find_methods' to search, then pass the token.");
    }

    public static FieldDef ResolveField(LoadedAssembly asm, string field)
    {
        field = field.Trim();
        if (TryParseToken(field, out var raw))
            return asm.Module.ResolveToken(raw) as FieldDef ?? throw new McpException($"Field token {ModelFactory.Hex(raw)} not found.");

        var fields = asm.AllTypes.SelectMany(t => t.Fields).ToList();
        var exact = fields.FirstOrDefault(f => f.FullName == field);
        if (exact != null)
            return exact;
        var sep = field.LastIndexOf("::", StringComparison.Ordinal);
        var candidates = sep > 0
            ? fields.Where(f => f.Name == field[(sep + 2)..] && (f.DeclaringType.FullName == field[..sep].Replace('+', '/') || f.DeclaringType.Name == field[..sep])).ToList()
            : fields.Where(f => f.Name == field).ToList();
        if (candidates.Count == 1)
            return candidates[0];
        if (candidates.Count > 1)
            throw new McpException($"Field '{field}' is ambiguous: " +
                                   string.Join("; ", candidates.Take(15).Select(f => $"{ModelFactory.Hex(f.MDToken)} {f.FullName}")));
        throw new McpException($"Field '{field}' not found in '{asm.Id}'.");
    }

    /// <summary>Resolves any definition (type, method, field, property, event) from a token or name.</summary>
    public static IMemberDef ResolveMember(LoadedAssembly asm, string member)
    {
        if (TryParseToken(member, out _))
            return ResolveToken(asm, member) as IMemberDef
                   ?? throw new McpException($"Token '{member}' is not a type, method, field, property or event definition.");
        if (!member.Contains("::"))
        {
            try { return ResolveType(asm, member); }
            catch (McpException) when (asm.AllTypes.SelectMany(t => t.Methods).Any(m => m.Name == member))
            {
                // A bare name may also be a unique method name ("Decrypt", "<Main>$").
                return ResolveMethod(asm, member);
            }
        }
        try { return ResolveMethod(asm, member); }
        catch (McpException) { return ResolveField(asm, member); }
    }

    private static string ParamList(MethodDef m) =>
        "(" + string.Join(",", m.MethodSig.Params.Select(p => p.FullName)) + ")";

    private static string NormalizeParams(string s) =>
        Regex.Replace(s, @"\s+", "").Replace("System.", "", StringComparison.Ordinal).ToLowerInvariant();
}
