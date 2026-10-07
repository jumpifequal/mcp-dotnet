using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MCPPOC.Analysis;
using Xunit;

namespace MCPPOC.Tests;

public class InstructionParserTests(ITestOutputHelper output)
{
    /// <summary>
    /// Round-trip: every instruction of a real-world assembly (dnlib.dll) printed by dnlib must parse back to the
    /// same opcode and an equivalent operand. Guards the "paste get_method_il output into patch_method_il" workflow.
    /// </summary>
    [Fact]
    public void RoundTrip_DnlibPrintedInstructions()
    {
        using var module = ModuleDefMD.Load(Fixture.FixturePath("dnlib.dll"));
        var parser = new InstructionParser(module);
        int total = 0, ok = 0;
        var failures = new List<string>();

        foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.OpCode.OperandType == OperandType.InlineSig)
                    continue; // calli signatures are not supported by the text format
                total++;
                var text = $"{instr.OpCode.Name} {ModelFactory.FormatOperand(instr)}".Trim();
                if (instr.Operand is IFullName fn)
                    text = $"{instr.OpCode.Name} {fn.FullName}";
                try
                {
                    var parsed = parser.ParseSingleInstruction(text, method)!;
                    Assert.Equal(instr.OpCode, parsed.OpCode);
                    if (Equivalent(instr.Operand, parsed.Operand))
                        ok++;
                    else if (failures.Count < 25)
                        failures.Add($"operand mismatch: {text} -> {parsed.Operand}");
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                {
                    if (failures.Count < 25)
                        failures.Add($"{text}: {ex.Message}");
                }
            }
        }

        foreach (var f in failures)
            output.WriteLine(f);
        var ratio = (double)ok / total;
        output.WriteLine($"{ok}/{total} ({ratio:P2}) instructions round-tripped");
        Assert.True(ratio > 0.99, $"Only {ratio:P2} of instructions round-tripped");
    }

    private static bool Equivalent(object? original, object? parsed) => (original, parsed) switch
    {
        (null, null) => true,
        (Instruction o, BranchLabel l) => l.Target == ModelFactory.Il(o.Offset),
        (Instruction[] o, BranchLabel[] l) => o.Select(i => ModelFactory.Il(i.Offset)).SequenceEqual(l.Select(x => x.Target)),
        (Local o, Local p) => o == p,
        (Parameter o, Parameter p) => o == p,
        (IFullName o, IFullName p) => o.FullName == p.FullName,
        _ => Equals(original, parsed),
    };

    [Theory]
    [InlineData("ldc.i4.s -5", Code.Ldc_I4_S, (sbyte)-5)]
    [InlineData("ldc.i4 0x10", Code.Ldc_I4, 16)]
    [InlineData("ldc.i4 -0x10", Code.Ldc_I4, -16)]
    [InlineData("ldc.i8 42L", Code.Ldc_I8, 42L)]
    [InlineData("ldc.r8 3.5", Code.Ldc_R8, 3.5)]
    [InlineData("ldc.r4 1.5", Code.Ldc_R4, 1.5f)]
    [InlineData("ldstr \"a \\\"q\\\" \\n// not a comment\"", Code.Ldstr, "a \"q\" \n// not a comment")]
    [InlineData("IL_0010: ret // trailing comment", Code.Ret, null)]
    [InlineData("tail.", Code.Tailcall, null)]
    public void ParsesLiteralsAndPrefixes(string text, Code code, object? operand)
    {
        using var f = new Fixture();
        var instr = f.Assembly.Parser.ParseSingleInstruction(text)!;
        Assert.Equal(code, instr.OpCode.Code);
        Assert.Equal(operand, instr.Operand);
    }

    [Fact]
    public void ResolvesVariablesToLocalsAndParameters()
    {
        using var f = new Fixture();
        f.Assembly.Read(a =>
        {
            var add = MemberResolver.ResolveMethod(a, "TestTarget.Calculator::Add");
            var p = a.Parser.ParseSingleInstruction("ldarg b", add)!;
            Assert.Same(add.Parameters[1], p.Operand);
            Assert.Same(add.Parameters[0], a.Parser.ParseSingleInstruction("ldarg.s 0", add)!.Operand);
            Assert.Throws<ArgumentException>(() => a.Parser.ParseSingleInstruction("ldarg 9", add));
            return 0;
        });
    }

    [Fact]
    public void ResolvesInstanceAndStaticMemberRefs()
    {
        using var f = new Fixture();
        f.Assembly.Read(a =>
        {
            var getLength = (IMethod)a.Parser.ParseSingleInstruction("callvirt instance int32 [System.Runtime]System.String::get_Length()")!.Operand;
            Assert.True(getLength.MethodSig.HasThis);
            Assert.Equal("System.Int32 System.String::get_Length()", getLength.FullName);

            var field = (IField)a.Parser.ParseSingleInstruction("ldsfld int32 TestTarget.Calculator::Counter")!.Operand;
            Assert.IsType<FieldDef>(field, exactMatch: false);

            var type = (ITypeDefOrRef)a.Parser.ParseSingleInstruction("newarr System.Byte")!.Operand;
            Assert.Equal("System.Byte", type.FullName);
            return 0;
        });
    }

    [Fact]
    public void ReportsLineNumbersOnErrors()
    {
        using var f = new Fixture();
        var ex = Assert.Throws<ArgumentException>(() => f.Assembly.Parser.ParseInstructions("nop\nnop\nbogus"));
        Assert.Contains("Line 3", ex.Message);
    }
}

public class PagingTests
{
    [Fact]
    public void Paginate_Bounds()
    {
        var items = Enumerable.Range(0, 10).ToArray();
        var p = Paging.Paginate(items, 8, 5);
        Assert.Equal([8, 9], p.Items);
        Assert.Null(p.NextOffset);
        Assert.Equal(10, p.Total);

        Assert.Empty(Paging.Paginate(items, 50, 5).Items);
        Assert.Equal(10, Paging.Paginate(items, 0, 0).Count);
        Assert.Equal(0, Paging.Paginate(items, -3, 1).Offset);
        Assert.Equal(3, Paging.Paginate(items, 0, 3).NextOffset);
    }
}
