using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPPOC.Console.Services;

public sealed record AssemblyRow(string Id, string Path, string Name, bool Active, bool Modified, int Types, int Methods);

/// <summary>
/// Minimal Streamable HTTP JSON-RPC client used only to self-test the running server and to read its
/// live workspace (assemblies loaded by any connected client) for the Overview tab -- not a general
/// MCP client.
/// </summary>
public sealed class McpProbe(string endpoint, string? token) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private HttpRequestMessage BuildRequest(object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>Parses either a plain JSON response or a single-event "text/event-stream" response.</summary>
    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var dataLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
            text = dataLine?[5..].Trim() ?? "{}";
        }
        return JsonNode.Parse(text);
    }

    public async Task<(bool Ok, string Message, string? ServerVersion)> SelfTestAsync(CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "mcp-dotnet-console", version = "1.0" } },
            };
            using var resp = await _http.SendAsync(BuildRequest(payload), ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", null);
            var json = await ReadJsonAsync(resp);
            var version = json?["result"]?["serverInfo"]?["version"]?.GetValue<string>();
            return (true, $"HTTP {(int)resp.StatusCode} - initialize ok", version);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    public async Task<List<AssemblyRow>> ListAssembliesAsync(CancellationToken ct = default)
    {
        var payload = new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "list_assemblies", arguments = new { } } };
        using var resp = await _http.SendAsync(BuildRequest(payload), ct);
        resp.EnsureSuccessStatusCode();
        var json = await ReadJsonAsync(resp);
        var structured = json?["result"]?["structuredContent"];
        var items = structured?["result"]?.AsArray() ?? structured?.AsArray();
        var rows = new List<AssemblyRow>();
        if (items == null) return rows;
        foreach (var it in items)
        {
            if (it == null) continue;
            rows.Add(new AssemblyRow(
                it["id"]?.GetValue<string>() ?? "",
                it["path"]?.GetValue<string>() ?? "",
                it["name"]?.GetValue<string>() ?? "",
                it["active"]?.GetValue<bool>() ?? false,
                it["modified"]?.GetValue<bool>() ?? false,
                it["typeCount"]?.GetValue<int>() ?? 0,
                it["methodCount"]?.GetValue<int>() ?? 0));
        }
        return rows;
    }

    public void Dispose() => _http.Dispose();
}
