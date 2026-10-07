using ModelContextProtocol;
using Xunit;

namespace MCPPOC.Tests;

/// <summary>Patches are verified end-to-end: save to disk, load into the runtime, execute.</summary>
public class PatchToolTests
{
    private static object? SaveAndRun(Fixture f, TempDir tmp, string method, params object?[] args)
    {
        var path = tmp.File($"patched-{Guid.NewGuid():N}.dll");
        var saved = f.Patch.SaveAssembly(path);
        Assert.False(saved.MixedMode);
        return Runner.Invoke(saved.Path, "TestTarget.Calculator", method, args);
    }

    [Fact]
    public void PatchMethodIl_OverwritesInstruction()
    {
        using var f = new Fixture();
        using var tmp = new TempDir();
        f.Patch.PatchMethodIl("TestTarget.Calculator::Answer", "IL_0000", "ldc.i4 42");
        Assert.Equal(42, SaveAndRun(f, tmp, "Answer"));
        // Decompiler sees the in-memory patch.
        Assert.Contains("return 42;", f.Code.Decompile("TestTarget.Calculator::Answer"));
        Assert.True(f.WorkspaceTools.GetAssemblyInfo().Modified);
    }

    [Fact]
    public void ReplaceMethodBody_WithCallToInternalMethod()
    {
        using var f = new Fixture();
        using var tmp = new TempDir();
        f.Patch.ReplaceMethodBody("TestTarget.Calculator::Answer", """
            ldc.i4.s 40          // first operand
            ldc.i4.2
            call int32 TestTarget.Calculator::Add(int32, int32)
            ret
            """);
        Assert.Equal(42, SaveAndRun(f, tmp, "Answer"));
    }

    [Fact]
    public void ReplaceMethodBody_WithExternalBclCall()
    {
        using var f = new Fixture();
        using var tmp = new TempDir();
        f.Patch.ReplaceMethodBody("TestTarget.Calculator::Url", """
            ldstr "a\tb"
            ldstr "c"
            call string [System.Runtime]System.String::Concat(string, string)
            ret
            """);
        Assert.Equal("a\tbc", SaveAndRun(f, tmp, "Url"));
    }

    [Fact]
    public void ReplaceMethodBody_DnlibSyntaxAlsoWorks()
    {
        using var f = new Fixture();
        using var tmp = new TempDir();
        f.Patch.ReplaceMethodBody("TestTarget.Calculator::Url", """
            IL_0000: ldstr "x"
            IL_0005: ldstr "y"
            IL_000A: call System.String System.String::Concat(System.String,System.String)
            IL_000F: ret
            """);
        Assert.Equal("xy", SaveAndRun(f, tmp, "Url"));
    }

    [Fact]
    public void LicenseCheck_CanBeBypassed()
    {
        using var f = new Fixture();
        using var tmp = new TempDir();
        f.Patch.ReplaceMethodBody("TestTarget.Calculator::CheckLicense", "ldc.i4.1\nret");
        Assert.Equal(true, SaveAndRun(f, tmp, "CheckLicense", "nope"));
    }

    [Fact]
    public void Insert_WithArgumentsAndSnippetBranches()
    {
        using var f = new Fixture();
        using var tmp = new TempDir();
        // Prepend: if (a == 0) return -1; ... using a forward branch inside the snippet (#4 = 'ldarg.0' of original body).
        var il = f.Code.GetMethodIl("TestTarget.Calculator::Add");
        f.Patch.PatchMethodIl("TestTarget.Calculator::Add", "IL_0000", """
            ldarg a
            brtrue IL_0000
            ldc.i4.m1
            ret
            """, removeCount: 0);
        Assert.Equal(-1, SaveAndRun(f, tmp, "Add", 0, 5));
        Assert.Equal(7, SaveAndRun(f, tmp, "Add", 2, 5));
        Assert.Equal(il.Instructions.Count + 4, f.Code.GetMethodIl("TestTarget.Calculator::Add").Instructions.Count);
    }

    [Fact]
    public void RemovingABranchTarget_RedirectsBranches()
    {
        using var f = new Fixture();
        using var tmp = new TempDir();
        // Classify's switch jumps to every 'ldstr' case; replacing the "two" case keeps the switch valid.
        var il = f.Code.GetMethodIl("TestTarget.Calculator::Classify");
        var two = il.Instructions.First(i => i.Operand == "\"two\"");
        f.Patch.PatchMethodIl("TestTarget.Calculator::Classify", two.Offset, "ldstr \"TWO\"");
        Assert.Equal("TWO", SaveAndRun(f, tmp, "Classify", 2));
        Assert.Equal("three", SaveAndRun(f, tmp, "Classify", 3));
    }

    [Fact]
    public void Nop_And_Rename()
    {
        using var f = new Fixture();
        f.Patch.RenameMember("TestTarget.Calculator::Secret", "GetApiKey");
        Assert.Contains("GetApiKey", f.Code.Decompile("TestTarget.Calculator"));
        f.Patch.RenameMember("TestTarget.Square", "Box", newNamespace: "Renamed");
        Assert.Equal("Renamed.Box", f.Browse.GetType("Renamed.Box").FullName);

        var r = f.Patch.NopInstructions("TestTarget.Calculator::Answer", "IL_0000", 1);
        Assert.Equal("nop", r.Window[0].OpCode);
    }

    [Fact]
    public void InvalidPatches_FailWithoutCorruptingTheMethod()
    {
        using var f = new Fixture();
        var before = f.Code.GetMethodIl("TestTarget.Calculator::Answer").Instructions.Count;
        Assert.Throws<McpException>(() => f.Patch.PatchMethodIl("TestTarget.Calculator::Answer", "IL_0000", "frobnicate 1"));
        Assert.Throws<McpException>(() => f.Patch.PatchMethodIl("TestTarget.Calculator::Answer", "IL_0001", "nop"));
        Assert.Throws<McpException>(() => f.Patch.PatchMethodIl("TestTarget.Calculator::Answer", "IL_0000", "br IL_0999"));
        Assert.Throws<McpException>(() => f.Patch.PatchMethodIl("TestTarget.Calculator::Answer", "IL_0000", "ldloc 3"));
        Assert.Equal(before, f.Code.GetMethodIl("TestTarget.Calculator::Answer").Instructions.Count);
    }

    [Fact]
    public void SaveAssembly_RefusesToOverwriteByDefault()
    {
        using var f = new Fixture();
        using var tmp = new TempDir();
        var path = tmp.File("x.dll");
        f.Patch.SaveAssembly(path);
        Assert.Throws<McpException>(() => f.Patch.SaveAssembly(path));
        f.Patch.SaveAssembly(path, overwrite: true);
    }
}
