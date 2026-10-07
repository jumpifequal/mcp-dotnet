using MCPPOC.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var options = CommandLine.Parse(args);
if (options.GenToken)
{
    Console.WriteLine(HttpTransport.GenerateToken());
    return 0;
}
if (options.ShowHelp)
{
    if (options.Error != null)
        Console.Error.WriteLine($"error: {options.Error}\n");
    Console.Error.WriteLine(CommandLine.Usage);
    return options.Error == null ? 0 : 1;
}

var settings = new ServerSettings(options.ReadOnly);

if (options.Transport == TransportKind.Http)
    return await HttpTransport.RunAsync(options, settings);

// stdio: stdout carries the JSON-RPC stream, so every log line must go to stderr.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(options.LogLevel);
builder.Services.AddDotnetReServer(settings, b => b.WithStdioServerTransport());
await builder.Build().RunAsync();
return 0;

internal enum TransportKind { Stdio, Http }

internal sealed record CommandLine(
    TransportKind Transport,
    string Host,
    int Port,
    string Path,
    IReadOnlyList<string> AllowedHosts,
    string? SslCertFile,
    string? SslKeyFile,
    bool NoAuth,
    bool GenToken,
    bool ReadOnly,
    LogLevel LogLevel,
    bool ShowHelp,
    string? Error)
{
    public const string Usage = """
        mcp-dotNET - MCP server for .NET reverse engineering (dnlib + ILSpy)

        Usage: mcp-dotnet [--transport stdio|http] [options]

        Transport:
          --transport <stdio|http>   stdio (default) or Streamable HTTP.

        HTTP options (with --transport http):
          --host <addr>              Bind address (default 127.0.0.1, loopback).
          --port <n>                 Port (default 5005).
          --path <path>              Endpoint path (default /mcp).
          --allowed-host <host>      Extra allowed Host header value (repeatable).
          --ssl-certfile <file>      TLS certificate (PEM, or PKCS#12 if no key file).
          --ssl-keyfile <file>       TLS private key (PEM) for --ssl-certfile.
          --no-auth                  Disable bearer auth (loopback bind only).
          --gen-token                Print a random bearer token and exit.

        Common:
          --read-only                Disable tools that modify or save assemblies.
          --log-level <level>        Trace, Debug, Information, Warning (default), Error.
          -h, --help                 Show this help.

        HTTP auth: set the bearer token in the MCP_DOTNET_HTTP_TOKEN environment variable.
        On a loopback bind with no token and no --no-auth, an ephemeral token is generated and printed.
        Environment: MCP_DOTNET_READONLY=1 is equivalent to --read-only.
        """;

    public static CommandLine Parse(string[] args)
    {
        var transport = TransportKind.Stdio;
        var host = "127.0.0.1";
        var port = 5005;
        var path = "/mcp";
        var allowed = new List<string>();
        string? certFile = null, keyFile = null;
        var noAuth = false;
        var genToken = false;
        var readOnly = Environment.GetEnvironmentVariable("MCP_DOTNET_READONLY") is "1" or "true" or "TRUE" or "True";
        var level = LogLevel.Warning;

        CommandLine Result(bool help, string? error) =>
            new(transport, host, port, path, allowed, certFile, keyFile, noAuth, genToken, readOnly, level, help, error);

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
            try
            {
                switch (args[i])
                {
                    case "--transport": transport = Enum.Parse<TransportKind>(Next(), ignoreCase: true); break;
                    case "--http": transport = TransportKind.Http; break; // compatibility alias
                    case "--host": host = Next(); break;
                    case "--port": port = int.Parse(Next()); break;
                    case "--path": path = Next(); break;
                    case "--allowed-host": allowed.Add(Next()); break;
                    case "--ssl-certfile": certFile = Next(); break;
                    case "--ssl-keyfile": keyFile = Next(); break;
                    case "--no-auth": noAuth = true; break;
                    case "--gen-token": genToken = true; break;
                    case "--read-only": readOnly = true; break;
                    case "--log-level": level = Enum.Parse<LogLevel>(Next(), ignoreCase: true); break;
                    case "-h" or "--help" or "/?": return Result(true, null);
                    default: return Result(true, $"Unknown argument '{args[i]}'");
                }
            }
            catch (ArgumentException ex)
            {
                return Result(true, ex.Message);
            }
            catch (FormatException ex)
            {
                return Result(true, ex.Message);
            }
        }
        return Result(false, null);
    }
}
