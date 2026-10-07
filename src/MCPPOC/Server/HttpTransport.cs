using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MCPPOC.Server;

/// <summary>
/// Streamable HTTP transport for the server, following the same approach as the sibling mcp-calibre server:
/// loopback bind by default, static bearer-token auth (from the MCP_DOTNET_HTTP_TOKEN environment variable),
/// a Host-header allow-list for DNS-rebinding protection, and optional TLS. '--no-auth' is accepted only on a
/// loopback bind.
/// </summary>
internal static class HttpTransport
{
    public const string TokenEnvVar = "MCP_DOTNET_HTTP_TOKEN";

    internal static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    internal static async Task<int> RunAsync(CommandLine o, ServerSettings settings)
    {
        var loopback = IsLoopback(o.Host);
        var token = Environment.GetEnvironmentVariable(TokenEnvVar)?.Trim();

        if (o.NoAuth && !loopback)
        {
            Console.Error.WriteLine("error: --no-auth is only allowed on a loopback bind (127.0.0.1 / ::1 / localhost).");
            return 1;
        }
        if (!o.NoAuth && string.IsNullOrEmpty(token))
        {
            if (!loopback)
            {
                Console.Error.WriteLine($"error: non-loopback bind requires a bearer token. Set {TokenEnvVar} (see --gen-token), or use --no-auth on a loopback bind.");
                return 1;
            }
            // Loopback with no token configured and no explicit --no-auth: generate an ephemeral token so the
            // endpoint is never unauthenticated by accident. It is printed below for the client to use.
            token = GenerateToken();
        }

        var web = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        web.Logging.ClearProviders();
        web.Logging.AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace);
        web.Logging.SetMinimumLevel(o.LogLevel);

        var usingTls = !string.IsNullOrEmpty(o.SslCertFile);
        web.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            var ip = o.Host switch
            {
                "0.0.0.0" => IPAddress.Any,
                "::" or "[::]" => IPAddress.IPv6Any,
                _ => IPAddress.TryParse(o.Host.Trim('[', ']'), out var parsed) ? parsed : IPAddress.Loopback,
            };
            k.Listen(ip, o.Port, listen =>
            {
                if (usingTls)
                {
                    var cert = string.IsNullOrEmpty(o.SslKeyFile)
                        ? X509CertificateLoader.LoadPkcs12FromFile(o.SslCertFile!, null)
                        : X509Certificate2.CreateFromPemFile(o.SslCertFile!, o.SslKeyFile);
                    listen.UseHttps(cert);
                }
            });
        });

        web.Services.AddDotnetReServer(settings, b => b.WithHttpTransport(h => h.Stateless = false));
        var app = web.Build();

        // Host-header allow-list (DNS-rebinding protection). On a loopback bind only localhost names are accepted.
        var allowedHosts = BuildAllowedHosts(o, loopback);
        app.Use(async (ctx, next) =>
        {
            if (allowedHosts != null)
            {
                var host = ctx.Request.Host.Host;
                if (!allowedHosts.Contains(host))
                {
                    ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                    await ctx.Response.WriteAsync("Host not allowed.");
                    return;
                }
            }
            if (!o.NoAuth && !AuthOk(ctx, token!))
            {
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("Missing or invalid bearer token.");
                return;
            }
            await next();
        });

        app.MapMcp(o.Path);

        if (!loopback && !usingTls)
            app.Logger.LogWarning("Non-loopback bind WITHOUT TLS: the bearer token and all data travel in clear. Use --ssl-certfile/--ssl-keyfile or a TLS reverse proxy.");

        var scheme = usingTls ? "https" : "http";
        app.Logger.LogInformation("mcp-dotNET {Version} listening on {Scheme}://{Host}:{Port}{Path} (auth: {Auth}, read-only: {RO})",
            ServerSetup.Version, scheme, o.Host, o.Port, o.Path, o.NoAuth ? "off" : "bearer", settings.ReadOnly);
        if (!o.NoAuth && Environment.GetEnvironmentVariable(TokenEnvVar) is null or "")
            app.Logger.LogInformation("Ephemeral bearer token for this run: {Token}", token);

        await app.RunAsync();
        return 0;
    }

    private static bool AuthOk(HttpContext ctx, string token)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var provided = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static HashSet<string>? BuildAllowedHosts(CommandLine o, bool loopback)
    {
        if (o.Host is "0.0.0.0" or "::" or "[::]")
            return null; // bound to everything on purpose: no host restriction
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (loopback)
        {
            hosts.Add("127.0.0.1");
            hosts.Add("localhost");
            hosts.Add("::1");
            hosts.Add("[::1]");
        }
        else
        {
            hosts.Add(o.Host);
        }
        foreach (var extra in o.AllowedHosts)
            hosts.Add(extra);
        return hosts;
    }

    private static bool IsLoopback(string host)
    {
        var h = host.Trim('[', ']');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip);
    }
}
