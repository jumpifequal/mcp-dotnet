using MCPPOC.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var options = CommandLine.Parse(args);
if (options.ShowHelp)
{
    if (options.Error != null)
        Console.Error.WriteLine($"error: {options.Error}\n");
    Console.Error.WriteLine(CommandLine.Usage);
    return options.Error == null ? 0 : 1;
}

var settings = new ServerSettings(options.ReadOnly);

if (options.Http)
{
    var web = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
    web.WebHost.UseUrls(options.Urls);
    web.Logging.SetMinimumLevel(options.LogLevel);
    web.Services.AddDotnetReServer(settings, b => b.WithHttpTransport(o => o.Stateless = false));
    var app = web.Build();
    app.MapMcp(options.Path);
    app.Logger.LogInformation("mcp-dotnet {Version} listening on {Urls}{Path} (read-only: {ReadOnly})", ServerSetup.Version, options.Urls, options.Path, options.ReadOnly);
    await app.RunAsync();
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
// stdout carries the JSON-RPC stream: every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(options.LogLevel);
builder.Services.AddDotnetReServer(settings, b => b.WithStdioServerTransport());
await builder.Build().RunAsync();
return 0;

internal sealed record CommandLine(bool Http, string Urls, string Path, bool ReadOnly, LogLevel LogLevel, bool ShowHelp, string? Error)
{
    public const string Usage = """
        mcp-dotnet - MCP server for .NET reverse engineering (dnlib + ILSpy)

        Usage: mcp-dotnet [options]

          --http                 Serve MCP over Streamable HTTP instead of stdio.
          --urls <urls>          Listen address(es) for --http (default http://127.0.0.1:5005).
          --path <path>          Endpoint path for --http (default /mcp).
          --read-only            Disable tools that modify assemblies or write them to disk.
          --log-level <level>    Trace, Debug, Information, Warning (default), Error.
          -h, --help             Show this help.

        Environment: MCP_DOTNET_READONLY=1 is equivalent to --read-only.
        """;

    public static CommandLine Parse(string[] args)
    {
        var http = false;
        var urls = "http://127.0.0.1:5005";
        var path = "/mcp";
        var readOnly = Environment.GetEnvironmentVariable("MCP_DOTNET_READONLY") is "1" or "true" or "TRUE" or "True";
        var level = LogLevel.Warning;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
            try
            {
                switch (args[i])
                {
                    case "--http": http = true; break;
                    case "--urls": urls = Next(); break;
                    case "--path": path = Next(); break;
                    case "--read-only": readOnly = true; break;
                    case "--log-level": level = Enum.Parse<LogLevel>(Next(), ignoreCase: true); break;
                    case "-h" or "--help" or "/?": return new(http, urls, path, readOnly, level, true, null);
                    default: return new(http, urls, path, readOnly, level, true, $"Unknown argument '{args[i]}'");
                }
            }
            catch (ArgumentException ex)
            {
                return new(http, urls, path, readOnly, level, true, ex.Message);
            }
        }
        return new(http, urls, path, readOnly, level, false, null);
    }
}
