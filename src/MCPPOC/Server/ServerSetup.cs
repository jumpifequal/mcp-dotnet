using System.Reflection;
using MCPPOC.Analysis;
using MCPPOC.Tools;
using MCPPOC.Workspace;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace MCPPOC.Server;

public sealed record ServerSettings(bool ReadOnly = false);

public static class ServerSetup
{
    public const string ServerName = "mcp-dotnet";

    public static string Version =>
        typeof(ServerSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public const string Instructions = """
        mcp-dotnet: static reverse engineering of .NET assemblies (dnlib for metadata/IL, ILSpy for decompilation).
        Workflow: call 'load_assembly' first (several assemblies can be loaded; tools default to the active one,
        or take an 'assembly' id). Identify members by metadata token (e.g. 0x06000012), which every tool returns
        and accepts; names like 'Namespace.Type::Method' also work when unambiguous.
        Discover with list_namespaces / list_types / find_methods / find_strings / scan_suspicious_apis,
        understand with decompile / get_method_il / get_control_flow_graph / find_callers / find_callees,
        and (unless the server is read-only) modify with patch_method_il / replace_method_body / rename_member, then save_assembly.
        Results are paginated: pass 'nextOffset' as 'offset' to continue. Nothing is ever executed.
        """;

    public static IServiceCollection AddDotnetReServer(this IServiceCollection services, ServerSettings settings, Action<IMcpServerBuilder> transport)
    {
        services.AddSingleton<AssemblyWorkspace>();
        services.AddSingleton<DecompilerService>();

        var builder = services.AddMcpServer(o =>
        {
            o.ServerInfo = new Implementation
            {
                Name = ServerName,
                Title = ".NET Reverse Engineering (dnlib + ILSpy)",
                Version = Version,
                Description = "Inspect, decompile, cross-reference, scan and patch .NET assemblies.",
                WebsiteUrl = "https://github.com/jumpifequal/mcp-dotnet",
            };
            o.ServerInstructions = Instructions + (settings.ReadOnly ? "\nThis server runs in READ-ONLY mode: patching and saving tools are disabled." : "");
        });

        builder
            .WithTools<WorkspaceTools>()
            .WithTools<BrowseTools>()
            .WithTools<CodeTools>()
            .WithTools<SearchTools>()
            .WithTools<ResourceTools>()
            .WithPrompts<Prompts>()
            .WithResources<Resources>();

        if (!settings.ReadOnly)
            builder.WithTools<PatchTools>();

        transport(builder);
        return services;
    }
}
