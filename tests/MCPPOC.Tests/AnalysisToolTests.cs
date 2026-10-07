using MCPPOC.Analysis;
using MCPPOC.Workspace;
using ModelContextProtocol;
using Xunit;

namespace MCPPOC.Tests;

public class WorkspaceTests
{
    [Fact]
    public void Load_ReportsIdentityAndCounts()
    {
        using var f = new Fixture();
        Assert.Equal("TestTarget", f.Info.Id);
        Assert.Equal("TestTarget", f.Info.Name);
        Assert.True(f.Info.IlOnly);
        Assert.Contains(".NETCoreApp", f.Info.TargetFramework);
        Assert.True(f.Info.TypeCount >= 8);
        Assert.Contains(f.Info.NativeModuleReferences, r => r.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_DoesNotLockTheFile()
    {
        using var tmp = new TempDir();
        var copy = tmp.File("copy.dll");
        File.Copy(Fixture.TargetPath, copy);
        using var f = new Fixture(copy);
        using (File.Open(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact]
    public void MultipleAssemblies_AreAddressableById()
    {
        using var f = new Fixture();
        var second = f.WorkspaceTools.LoadAssembly(Fixture.FixturePath("ConsoleApplication1.exe"), "console");
        Assert.Equal("console", second.Id);
        Assert.NotNull(second.EntryPoint);
        Assert.Equal(2, f.WorkspaceTools.ListAssemblies().Count);
        Assert.Equal("console", f.Workspace.ActiveId);

        // Explicit id still reaches the first one.
        Assert.Contains(f.Browse.ListTypes("Calculator", assembly: "TestTarget").Items, t => t.FullName == "TestTarget.Calculator");
        f.WorkspaceTools.SetActiveAssembly("TestTarget");
        Assert.Equal("TestTarget", f.Workspace.ActiveId);
    }

    [Fact]
    public void Errors_AreActionable()
    {
        using var ws = new AssemblyWorkspace();
        var ex = Assert.Throws<McpException>(() => ws.Get());
        Assert.Contains("load_assembly", ex.Message);
        Assert.Throws<McpException>(() => ws.Load(@"C:\does\not\exist.dll"));
        Assert.Throws<McpException>(() => ws.Load(typeof(WorkspaceTests).Assembly.Location.Replace(".dll", ".deps.json")));
    }
}

public class BrowseToolTests
{
    [Fact]
    public void ListTypes_IncludesNestedTypes()
    {
        using var f = new Fixture();
        var page = f.Browse.ListTypes("Inner");
        Assert.Contains(page.Items, t => t.FullName == "TestTarget.Calculator/Inner" && t.DeclaringType == "TestTarget.Calculator");
    }

    [Fact]
    public void ListTypes_FiltersAndPaginates()
    {
        using var f = new Fixture();
        var interfaces = f.Browse.ListTypes(kind: "interface");
        Assert.Single(interfaces.Items);
        var first = f.Browse.ListTypes(limit: 2);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.NextOffset);
        var rest = f.Browse.ListTypes(offset: first.NextOffset!.Value, limit: 0);
        Assert.Equal(first.Total - 2, rest.Count);
        Assert.Null(rest.NextOffset);
    }

    [Theory]
    [InlineData("TestTarget.Calculator+Inner")]
    [InlineData("TestTarget.Calculator/Inner")]
    [InlineData("Inner")]
    public void GetType_AcceptsSeveralNameForms(string name)
    {
        using var f = new Fixture();
        var t = f.Browse.GetType(name);
        Assert.Equal("TestTarget.Calculator/Inner", t.FullName);
        Assert.Contains(t.Methods, m => m.Name == "Depth");
    }

    [Fact]
    public void GetType_UnknownSuggestsAlternatives()
    {
        using var f = new Fixture();
        var ex = Assert.Throws<McpException>(() => f.Browse.GetType("TestTarget.Calculatr"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Methods_ResolveByNameTokenAndRid()
    {
        using var f = new Fixture();
        var byName = f.Browse.GetMethod("TestTarget.Calculator::Add");
        var byToken = f.Browse.GetMethod(byName.Token);
        var rid = Convert.ToUInt32(byName.Token, 16) & 0x00FFFFFF;
        var byRid = f.Browse.GetMethod(rid.ToString());
        Assert.Equal(byName.FullName, byToken.FullName);
        Assert.Equal(byName.FullName, byRid.FullName);
        Assert.Equal(["System.Int32 a", "System.Int32 b"], byName.Parameters);
        Assert.NotNull(byName.Rva);
    }

    [Fact]
    public void FindMethods_And_FindMembers()
    {
        using var f = new Fixture();
        Assert.Contains(f.Browse.FindMethods("^Check").Items, m => m.Name == "CheckLicense");
        var props = f.Browse.FindMembers("Radius", "property");
        Assert.Single(props.Items);
        Assert.Contains(f.Browse.FindMembers("Changed", "event").Items, e => e.Kind == "event");
    }

    [Fact]
    public void StaticFieldValues_ShowConstantsInitialDataAndCctorAssignments()
    {
        using var f = new Fixture();
        var fields = f.Browse.GetStaticFieldValues("TestTarget.Calculator");
        Assert.Equal("\"calc-v1\"", fields.Single(x => x.Name == "Banner").Constant);
        Assert.Equal("7", fields.Single(x => x.Name == "Counter").InitializerInCctor);
        // byte[] initializers are stored as FieldRVA data on a compiler-generated field.
        var all = f.Browse.FindMembers(".", "field").Items;
        Assert.Contains(all, m => m.DeclaringType.Contains("<PrivateImplementationDetails>"));
    }

    [Fact]
    public void EntryPoints_And_PInvokes()
    {
        using var f = new Fixture();
        var entries = f.Browse.GetEntryPoints();
        Assert.Contains(entries, e => e.Role == "Static constructor" && e.Method.FullName.Contains("Calculator"));
        var pinvokes = f.Browse.ListPInvokes();
        var va = Assert.Single(pinvokes);
        Assert.Equal("VirtualAlloc", va.PInvoke!.EntryPoint);
        Assert.Equal("kernel32.dll", va.PInvoke.Module);
    }

    [Fact]
    public void Resources_AreDetectedAndReadable()
    {
        using var f = new Fixture();
        var res = Assert.Single(f.Resources.ListResources());
        Assert.Equal("TestTarget.payload.txt", res.Name);
        Assert.Equal("Text", res.Detected);
        using var tmp = new TempDir();
        var content = f.Resources.GetResource(res.Name, saveTo: tmp.File("payload.bin"));
        Assert.Contains("TT-PAYLOAD-0001", content.Text);
        Assert.True(File.Exists(content.SavedTo));
    }
}

public class SearchToolTests
{
    [Fact]
    public void FindStrings_FiltersBySubstringAndRegex()
    {
        using var f = new Fixture();
        var hit = Assert.Single(f.Search.FindStrings("secret").Items);
        Assert.Equal("secret-api-key-123", hit.Value);
        Assert.Contains("Secret", hit.Method.FullName);
        Assert.Single(f.Search.FindStrings(@"^https?://", isRegex: true).Items);
        Assert.Throws<McpException>(() => f.Search.FindStrings("(", isRegex: true));
    }

    [Fact]
    public void FindCallers_WorksForInternalAndExternalMethods()
    {
        using var f = new Fixture();
        var launch = Assert.Single(f.Search.FindCallers("System.Diagnostics.Process::Start").Items);
        Assert.Contains("Launch", launch.Method.FullName);

        var alloc = Assert.Single(f.Search.FindCallers("TestTarget.Suspicious::VirtualAlloc").Items);
        Assert.Contains("Allocate", alloc.Method.FullName);
    }

    [Fact]
    public void FindCallees_SummarisesBehaviour()
    {
        using var f = new Fixture();
        var callees = f.Search.FindCallees("TestTarget.Suspicious::LoadPayload");
        Assert.Contains(callees, c => c.Detail.Contains("Convert::FromBase64String"));
        Assert.Contains(callees, c => c.Detail.Contains("Assembly::Load"));
    }

    [Fact]
    public void FindFieldReferences_DistinguishesReadsAndWrites()
    {
        using var f = new Fixture();
        var refs = f.Search.FindFieldReferences("TestTarget.Calculator::Counter").Items;
        Assert.Contains(refs, r => r.Detail == "write" && r.Method.Name == ".cctor");
    }

    [Fact]
    public void FindDerivedTypes_IsTransitive()
    {
        using var f = new Fixture();
        var shapes = f.Search.FindDerivedTypes("TestTarget.ShapeBase").Select(t => t.Name).Order().ToList();
        Assert.Equal(["Circle", "Square"], shapes);
        var implementors = f.Search.FindDerivedTypes("TestTarget.IShape").Select(t => t.Name).Order().ToList();
        Assert.Equal(["Circle", "ShapeBase", "Square"], implementors);
    }

    [Fact]
    public void FindTypeUsages_FindsInstantiationAndSignatures()
    {
        using var f = new Fixture();
        var usages = f.Search.FindTypeUsages("System.Diagnostics.Process").Items;
        Assert.Contains(usages, u => u.Method.FullName.Contains("Launch"));
    }

    [Fact]
    public void ScanSuspiciousApis_FlagsTheRightCategories()
    {
        using var f = new Fixture();
        var scan = f.Search.ScanSuspiciousApis();
        foreach (var category in new[] { "Injection", "Process", "DynamicCode", "AntiAnalysis", "Encoding" })
            Assert.True(scan.CountsByCategory.ContainsKey(category), $"missing {category}");
        Assert.Equal("high", scan.Findings.Items[0].Severity);

        var onlyInjection = f.Search.ScanSuspiciousApis("injection");
        Assert.All(onlyInjection.Findings.Items, x => Assert.Equal("Injection", x.Category));
        Assert.Throws<McpException>(() => f.Search.ScanSuspiciousApis("Nope"));
    }
}

public class CodeToolTests
{
    [Fact]
    public void Decompile_MethodTypeAndByToken()
    {
        using var f = new Fixture();
        Assert.Contains("return 41;", f.Code.Decompile("TestTarget.Calculator::Answer"));
        var type = f.Code.Decompile("TestTarget.Square");
        Assert.Contains("class Square : ShapeBase", type);
        Assert.Contains("return 41;", f.Code.Decompile(f.Token("TestTarget.Calculator::Answer")));
    }

    [Fact]
    public void Disassemble_ProducesIlasmText()
    {
        using var f = new Fixture();
        var il = f.Code.Disassemble("TestTarget.Calculator::SafeDivide");
        Assert.Contains(".try", il);
        Assert.Contains("catch [System.Runtime]System.DivideByZeroException", il);
    }

    [Fact]
    public void GetMethodIl_IncludesBytesAndFileOffsets()
    {
        using var f = new Fixture();
        var il = f.Code.GetMethodIl("TestTarget.Calculator::Answer");
        var first = il.Instructions[0];
        Assert.Equal("IL_0000", first.Offset);
        Assert.Equal("ldc.i4.s", first.OpCode);
        Assert.Equal("1F 29", first.Bytes);
        Assert.NotNull(first.FileOffset);

        // The file offset really points at those bytes.
        var dump = f.Code.ReadBytes(first.FileOffset!, 2, isRva: false);
        Assert.Contains("1F 29", dump.Dump);
    }

    [Fact]
    public void ControlFlowGraph_SplitsSwitchAndHandlers()
    {
        using var f = new Fixture();
        var cfg = f.Code.GetControlFlowGraph("TestTarget.Calculator::Classify");
        var switchBlock = cfg.Blocks.Single(b => b.Terminator == "switch");
        Assert.True(switchBlock.Successors.Count >= 5);
        Assert.StartsWith("flowchart TD", cfg.Mermaid);

        var eh = f.Code.GetControlFlowGraph("TestTarget.Calculator::SafeDivide");
        Assert.True(eh.BlockCount >= 3);
    }

    [Fact]
    public void ExportProject_WritesCompilableLayout()
    {
        using var f = new Fixture();
        using var tmp = new TempDir();
        var outDir = Path.Combine(tmp.Path, "out");
        var result = f.Code.ExportProject(outDir);
        Assert.Contains(".csproj", result);
        Assert.NotEmpty(Directory.GetFiles(outDir, "*.cs", SearchOption.AllDirectories));
        Assert.Throws<McpException>(() => f.Code.ExportProject(outDir)); // not empty any more
    }
}

public class NetFrameworkFixtureTests
{
    [Fact]
    public void NetFrameworkAssembly_DecompilesAndResolvesBareMethodNames()
    {
        using var f = new Fixture(Fixture.FixturePath("ConsoleApplication1.exe"));
        Assert.StartsWith(".NETFramework", f.Info.TargetFramework);
        var main = f.Code.Decompile("Main");
        Assert.Contains("static void Main(string[] args)", main);
        Assert.NotEmpty(f.Search.FindStrings().Items);
    }
}
