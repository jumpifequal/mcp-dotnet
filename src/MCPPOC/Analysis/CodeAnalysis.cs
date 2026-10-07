using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Resources;
using dnlib.IO;

namespace MCPPOC.Analysis;

public static class CodeAnalysis
{
    // ------------------------------------------------------------------ control flow

    public static ControlFlowGraph BuildCfg(MethodDef method)
    {
        var body = method.Body;
        var instrs = body.Instructions;
        body.UpdateInstructionOffsets();

        var leaders = new SortedSet<int> { 0 };
        var index = new Dictionary<Instruction, int>();
        for (var i = 0; i < instrs.Count; i++)
            index[instrs[i]] = i;

        for (var i = 0; i < instrs.Count; i++)
        {
            var ins = instrs[i];
            foreach (var target in BranchTargets(ins))
                leaders.Add(index[target]);
            if (EndsBlock(ins) && i + 1 < instrs.Count)
                leaders.Add(i + 1);
        }
        foreach (var eh in body.ExceptionHandlers)
            foreach (var ins in new[] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart })
                if (ins != null && index.TryGetValue(ins, out var li))
                    leaders.Add(li);

        var starts = leaders.Where(l => l < instrs.Count).ToList();
        var blockOf = new int[instrs.Count];
        for (var b = 0; b < starts.Count; b++)
        {
            var end = b + 1 < starts.Count ? starts[b + 1] : instrs.Count;
            for (var i = starts[b]; i < end; i++)
                blockOf[i] = b;
        }

        var blocks = new List<BasicBlock>();
        var edges = 0;
        var mermaid = new StringBuilder("flowchart TD\n");
        for (var b = 0; b < starts.Count; b++)
        {
            var first = starts[b];
            var last = (b + 1 < starts.Count ? starts[b + 1] : instrs.Count) - 1;
            var term = instrs[last];
            var succ = new SortedSet<int>();
            foreach (var target in BranchTargets(term))
                succ.Add(blockOf[index[target]]);
            var flow = term.OpCode.FlowControl;
            if (flow is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw) && term.OpCode.Code != Code.Leave && term.OpCode.Code != Code.Leave_S && last + 1 < instrs.Count)
                succ.Add(b + 1);
            edges += succ.Count;

            var lines = Enumerable.Range(first, last - first + 1)
                .Select(i => $"{ModelFactory.Il(instrs[i].Offset)}: {instrs[i].OpCode.Name} {ModelFactory.FormatOperand(instrs[i])}".TrimEnd())
                .ToList();
            blocks.Add(new BasicBlock(b, ModelFactory.Il(instrs[first].Offset), ModelFactory.Il(term.Offset), lines.Count,
                term.OpCode.Name, succ.ToList(), lines));

            mermaid.Append($"  B{b}[\"B{b} {ModelFactory.Il(instrs[first].Offset)}..{ModelFactory.Il(term.Offset)}\\n{Escape(term.OpCode.Name)}\"]\n");
            foreach (var s in succ)
                mermaid.Append($"  B{b} --> B{s}\n");
        }

        return new ControlFlowGraph(method.FullName, blocks.Count, edges, blocks, mermaid.ToString());
    }

    private static string Escape(string s) => s.Replace("\"", "'", StringComparison.Ordinal);

    private static IEnumerable<Instruction> BranchTargets(Instruction ins) => ins.Operand switch
    {
        Instruction t => [t],
        IList<Instruction> ts => ts,
        _ => [],
    };

    private static bool EndsBlock(Instruction ins) =>
        ins.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw
        || ins.OpCode.Code is Code.Leave or Code.Leave_S or Code.Endfinally or Code.Endfilter or Code.Switch;

    // ------------------------------------------------------------------ obfuscation

    private static readonly (string Marker, string Name)[] ObfuscatorMarkers =
    [
        ("ConfusedByAttribute", "ConfuserEx / Confuser"),
        ("DotfuscatorAttribute", "Dotfuscator"),
        ("BabelObfuscatorAttribute", "Babel.NET"),
        ("BabelAttribute", "Babel.NET"),
        ("SmartAssembly.Attributes", "SmartAssembly"),
        ("PoweredByAttribute", "SmartAssembly"),
        ("CryptoObfuscator", "Crypto Obfuscator"),
        ("ObfuscatedByGoliath", "Goliath.NET"),
        ("Xenocode.Client.Attributes", "Xenocode"),
        ("YanoAttribute", "Yano"),
        ("NETGuard", ".NETGuard"),
        ("NineRays.Obfuscator", "Spices.Net"),
        ("ZYXDNGuarder", "DNGuard"),
        ("EMyPID_8234_", "Agile.NET / CliSecure"),
        ("AgileDotNetRT", "Agile.NET"),
        ("SecureTeam.Attributes", "Agile.NET"),
        ("ObfuscatorAttribute", "Unknown obfuscator attribute"),
        ("ProcessedByXenocode", "Xenocode"),
        ("MaxtoCode", "MaxtoCode"),
        ("Eazfuscator", "Eazfuscator.NET"),
        ("CodeWallTrialVersion", "CodeWall"),
        ("VMProtect", "VMProtect"),
        ("Themida", "Themida/WinLicense"),
        ("de4dot", "de4dot (already deobfuscated)"),
    ];

    public static List<string> ObfuscationHints(ModuleDefMD module, IReadOnlyList<TypeDef> types)
    {
        var hints = new List<string>();
        var names = types.Select(t => t.FullName)
            .Concat(module.Assembly?.CustomAttributes.Select(a => a.TypeFullName) ?? [])
            .Concat(module.CustomAttributes.Select(a => a.TypeFullName))
            .ToList();
        foreach (var (marker, name) in ObfuscatorMarkers)
            if (names.Any(n => n.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                hints.Add($"Marker for {name} ('{marker}')");

        var members = types.SelectMany(t => t.Methods.Select(m => m.Name.String).Concat(t.Fields.Select(f => f.Name.String)).Append(t.Name.String))
            .Where(n => !n.StartsWith('<') && n != ".ctor" && n != ".cctor")
            .ToList();
        if (members.Count > 20)
        {
            var weird = members.Count(IsUnreadableName);
            var ratio = (double)weird / members.Count;
            if (ratio > 0.15)
                hints.Add($"{ratio:P0} of member names are non-ASCII/unprintable or random-looking (renaming obfuscation likely)");
        }

        var moduleType = module.GlobalType;
        if (moduleType?.FindStaticConstructor() is { HasBody: true } cctor && cctor.Body.Instructions.Count > 5)
            hints.Add($"<Module>::.cctor contains {cctor.Body.Instructions.Count} instructions (module initializer often used by protectors for anti-tamper/decryption)");

        var invalidSwitch = types.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .Count(m => m.Body.Instructions.Count(i => i.OpCode.Code == Code.Switch) > 0 && m.Body.Instructions.Count(i => i.OpCode.FlowControl == FlowControl.Branch) > 15);
        if (invalidSwitch > 10)
            hints.Add($"{invalidSwitch} methods with switch-based dispatch and many unconditional branches (control-flow flattening likely)");

        if (types.SelectMany(t => t.Methods).Count(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode.Code == Code.Calli)) > 5)
            hints.Add("Multiple methods use 'calli' (often a sign of call obfuscation/proxying)");

        var resources = module.Resources.OfType<EmbeddedResource>().ToList();
        if (resources.Any(r => r.Name.String.Length > 0 && r.Name.String.All(c => !char.IsAsciiLetterOrDigit(c) && c != '.')))
            hints.Add("Embedded resources with unreadable names (encrypted payload/strings likely)");

        return hints;
    }

    private static bool IsUnreadableName(string name)
    {
        if (name.Length == 0)
            return true;
        if (name.Any(c => c > 0x7E || char.IsControl(c)))
            return true;
        // ConfuserEx-like random identifiers: long, no vowels pattern, mixed case without word structure.
        return name.Length >= 20 && name.Count(char.IsUpper) > name.Length / 3 && !name.Contains('_');
    }

    // ------------------------------------------------------------------ resources / data

    public static string? DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 'M' && data[1] == 'Z') return "PE executable (MZ)";
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4) return "ZIP archive";
        if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B) return "GZip";
        if (data.Length >= 4 && data[0] == 0xCE && data[1] == 0xCA && data[2] == 0xEF && data[3] == 0xBE) return ".NET resources (.resources)";
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return "PNG image";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "JPEG image";
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0) return "ICO icon";
        if (data.Length >= 3 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F') return "GIF image";
        if (data.Length >= 4 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F') return "PDF";
        if (data.Length >= 6 && data[0] == '7' && data[1] == 'z' && data[2] == 0xBC && data[3] == 0xAF) return "7-Zip archive";
        if (data.Length >= 5 && data[0] == '<' && (data[1] == '?' || char.IsAsciiLetter((char)data[1]))) return "XML/markup text";
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return "UTF-8 text (BOM)";
        if (data.Length > 0 && LooksLikeText(data)) return "Text";
        if (data.Length >= 64 && Entropy(data) > 7.5) return $"High-entropy data ({Entropy(data):F2} bits/byte; encrypted or compressed)";
        return null;
    }

    public static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        var sample = data.Length > 4096 ? data[..4096] : data;
        var printable = 0;
        foreach (var b in sample)
            if (b is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\r' or (byte)'\t')
                printable++;
        return printable >= sample.Length * 0.95;
    }

    public static double Entropy(ReadOnlySpan<byte> data)
    {
        Span<int> counts = stackalloc int[256];
        foreach (var b in data)
            counts[b]++;
        double entropy = 0;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            var p = (double)c / data.Length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    public static List<ResourceEntry>? ReadResourceSet(ModuleDefMD module, byte[] data)
    {
        if (!ResourceReader.CouldBeResourcesFile(ByteArrayDataReaderFactory.CreateReader(data)))
            return null;
        var set = ResourceReader.Read(module, ByteArrayDataReaderFactory.CreateReader(data));
        return set.ResourceElements.Select(e => new ResourceEntry(
            e.Name,
            e.ResourceData.Code.ToString(),
            e.ResourceData switch
            {
                BuiltInResourceData b when b.Data is string s => s.Length > 500 ? s[..500] + "..." : s,
                BuiltInResourceData b when b.Data is byte[] bytes => $"{bytes.Length} bytes" + (DetectFormat(bytes) is { } f ? $" [{f}]" : ""),
                BuiltInResourceData b => ModelFactory.FormatConstant(b.Data),
                BinaryResourceData bin => $"{bin.Data.Length} bytes serialized ({bin.TypeName})",
                _ => e.ResourceData.ToString(),
            })).ToList();
    }

    /// <summary>Classic hex dump: offset, 16 bytes, ASCII column.</summary>
    public static string FormatHexDump(ReadOnlySpan<byte> data, ulong baseAddress)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i += 16)
        {
            var line = data.Slice(i, Math.Min(16, data.Length - i));
            sb.Append((baseAddress + (ulong)i).ToString("X8")).Append("  ");
            for (var j = 0; j < 16; j++)
            {
                sb.Append(j < line.Length ? line[j].ToString("X2") + " " : "   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(" |");
            foreach (var b in line)
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            sb.Append("|\n");
        }
        return sb.ToString();
    }
}
