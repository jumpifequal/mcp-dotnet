using System.IO.Pipelines;
using System.Text.Json;
using MCPPOC.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MCPPOC.Tests;

/// <summary>End-to-end over the real MCP protocol (in-memory pipes): what an MCP client actually sees.</summary>
public sealed class McpProtocolTests : IAsyncLifetime
{
    private Pipe _clientToServer = new();
    private Pipe _serverToClient = new();
    private IHost? _host;
    private McpClient? _client;

    private McpClient Client => _client!;

    public async ValueTask InitializeAsync() => _client = await StartAsync(readOnly: false);

    private async Task<McpClient> StartAsync(bool readOnly)
    {
        _clientToServer = new Pipe();
        _serverToClient = new Pipe();
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddDotnetReServer(new ServerSettings(readOnly),
            b => b.WithStreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream()));
        _host = builder.Build();
        await _host.StartAsync(TestContext.Current.CancellationToken);
        return await McpClient.CreateAsync(
            new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
            await _client.DisposeAsync();
        _client = null;
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        _host = null;
    }

    [Fact]
    public async Task Initialize_AdvertisesServerInfoAndInstructions()
    {
        Assert.Equal(ServerSetup.ServerName, Client.ServerInfo.Name);
        Assert.Contains("load_assembly", Client.ServerInstructions);
        Assert.NotNull(Client.ServerCapabilities.Tools);
        Assert.NotNull(Client.ServerCapabilities.Prompts);
        Assert.NotNull(Client.ServerCapabilities.Resources);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Tools_HaveSnakeCaseNamesAnnotationsAndSchemas()
    {
        var tools = await Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var names = tools.Select(t => t.Name).ToHashSet();
        foreach (var expected in new[]
                 {
                     "load_assembly", "list_assemblies", "get_assembly_info", "list_types", "get_type", "find_methods", "get_method",
                     "decompile", "disassemble", "get_method_il", "get_control_flow_graph", "find_strings", "find_callers",
                     "scan_suspicious_apis", "list_resources", "get_resource", "patch_method_il", "save_assembly", "rename_member",
                 })
            Assert.Contains(expected, names);

        var listTypes = tools.Single(t => t.Name == "list_types").ProtocolTool;
        Assert.True(listTypes.Annotations?.ReadOnlyHint);
        Assert.NotNull(listTypes.OutputSchema);
        Assert.Contains("pattern", listTypes.InputSchema.GetRawText());

        var patch = tools.Single(t => t.Name == "patch_method_il").ProtocolTool;
        Assert.True(patch.Annotations?.DestructiveHint);
        Assert.False(patch.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task CallTools_ReturnStructuredContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var load = await Client.CallToolAsync("load_assembly", new Dictionary<string, object?> { ["path"] = Fixture.TargetPath }, cancellationToken: ct);
        Assert.NotEqual(true, load.IsError);
        Assert.Equal("TestTarget", load.StructuredContent!.Value.GetProperty("id").GetString());

        var strings = await Client.CallToolAsync("find_strings", new Dictionary<string, object?> { ["filter"] = "api-key" }, cancellationToken: ct);
        var items = strings.StructuredContent!.Value.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("secret-api-key-123", items[0].GetProperty("value").GetString());

        var code = await Client.CallToolAsync("decompile", new Dictionary<string, object?> { ["member"] = "TestTarget.Calculator::Answer" }, cancellationToken: ct);
        Assert.Contains("return 41;", ((TextContentBlock)code.Content[0]).Text);
    }

    [Fact]
    public async Task ToolErrors_AreReturnedAsIsErrorResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var noAssembly = await Client.CallToolAsync("list_types", new Dictionary<string, object?>(), cancellationToken: ct);
        Assert.True(noAssembly.IsError);
        Assert.Contains("load_assembly", ((TextContentBlock)noAssembly.Content[0]).Text);
    }

    [Fact]
    public async Task Prompts_And_Resources()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompts = await Client.ListPromptsAsync(cancellationToken: ct);
        Assert.Contains(prompts, p => p.Name == "malware_triage");
        var prompt = await Client.GetPromptAsync("triage_assembly", new Dictionary<string, object?> { ["path"] = @"C:\x.dll" }, cancellationToken: ct);
        Assert.Contains(@"C:\x.dll", ((TextContentBlock)prompt.Messages[0].Content).Text);

        await Client.CallToolAsync("load_assembly", new Dictionary<string, object?> { ["path"] = Fixture.TargetPath }, cancellationToken: ct);
        var templates = await Client.ListResourceTemplatesAsync(cancellationToken: ct);
        Assert.Contains(templates, t => t.UriTemplate == "dotnet://{assembly}/source/{member}");
        var source = await Client.ReadResourceAsync("dotnet://TestTarget/source/TestTarget.Calculator%2BInner", cancellationToken: ct);
        Assert.Contains("Depth", ((TextResourceContents)source.Contents[0]).Text);
    }

    [Fact]
    public async Task ReadOnlyMode_HidesModifyingTools()
    {
        await DisposeAsync();
        var readOnly = await StartAsync(readOnly: true);
        _client = readOnly;
        var tools = await readOnly.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tools, t => t.Name is "patch_method_il" or "save_assembly" or "rename_member");
        Assert.Contains(tools, t => t.Name == "decompile");
        Assert.Contains("READ-ONLY", readOnly.ServerInstructions);
    }
}
