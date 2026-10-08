using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MCPPOC.Console.Models;

namespace MCPPOC.Console.Services;

public enum ServerState { Stopped, Starting, Running, Stopping, Error }

/// <summary>
/// Owns the mcp-dotnet server child process for the HTTP transport. Redirects stdout/stderr and reads
/// them asynchronously via the standard .NET event-based pattern (never Start-Process -NoNewWindow,
/// which shares the console with an interactive host and can make it stop responding to input).
/// </summary>
public sealed partial class ServerProcessManager : IDisposable
{
    private Process? _process;
    private readonly object _gate = new();

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? Token { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public string? Endpoint { get; private set; }
    public string? BinaryPath { get; private set; }

    public event Action<LogEntry>? LogReceived;
    public event Action? StateChanged;
    public event Action? Exited;

    public bool IsRunning => _process is { HasExited: false };
    public int? Pid => _process is { HasExited: false } p ? p.Id : null;

    public static string? FindServerDll(string repoRoot)
    {
        var dist = Path.Combine(repoRoot, "app", "mcp-dotnet.dll");
        if (File.Exists(dist)) return dist;
        var built = Path.Combine(repoRoot, "bin", "MCPPOC", "Release", "net10.0", "mcp-dotnet.dll");
        return File.Exists(built) ? built : null;
    }

    public void Start(string dllPath, ServerProfile profile)
    {
        lock (_gate)
        {
            if (IsRunning) return;
            BinaryPath = dllPath;
            State = ServerState.Starting;
            StateChanged?.Invoke();

            Token = profile.NoAuth ? null : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(dllPath),
            };
            psi.ArgumentList.Add(dllPath);
            psi.ArgumentList.Add("--transport"); psi.ArgumentList.Add("http");
            psi.ArgumentList.Add("--host"); psi.ArgumentList.Add(profile.Host);
            psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(profile.Port.ToString());
            psi.ArgumentList.Add("--path"); psi.ArgumentList.Add(profile.Path);
            psi.ArgumentList.Add("--log-level"); psi.ArgumentList.Add(profile.LogLevel);
            if (profile.ReadOnly) psi.ArgumentList.Add("--read-only");
            if (profile.NoAuth) psi.ArgumentList.Add("--no-auth");
            foreach (var h in (profile.AllowedHosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                psi.ArgumentList.Add("--allowed-host"); psi.ArgumentList.Add(h);
            }
            if (!string.IsNullOrWhiteSpace(profile.SslCertFile)) { psi.ArgumentList.Add("--ssl-certfile"); psi.ArgumentList.Add(profile.SslCertFile); }
            if (!string.IsNullOrWhiteSpace(profile.SslKeyFile)) { psi.ArgumentList.Add("--ssl-keyfile"); psi.ArgumentList.Add(profile.SslKeyFile); }
            if (Token != null) psi.Environment["MCP_DOTNET_HTTP_TOKEN"] = Token;

            Endpoint = $"http://{profile.Host}:{profile.Port}{profile.Path}";

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) HandleLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) HandleLine(e.Data); };
            p.Exited += (_, _) =>
            {
                lock (_gate)
                {
                    State = ServerState.Stopped;
                    StartedAt = null;
                }
                StateChanged?.Invoke();
                Exited?.Invoke();
            };

            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _process = p;
                StartedAt = DateTime.Now;
                State = ServerState.Running;
            }
            catch (Exception ex)
            {
                State = ServerState.Error;
                Log(LogLevel.Error, "console", $"Failed to start server: {ex.Message}");
            }
            StateChanged?.Invoke();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false } p) return;
            State = ServerState.Stopping;
            StateChanged?.Invoke();
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
        }
    }

    [GeneratedRegex(@"^(trce|dbug|info|warn|fail|crit):\s*(\S+)")]
    private static partial Regex LevelLine();

    private void HandleLine(string line)
    {
        var m = LevelLine().Match(line);
        var level = m.Success ? m.Groups[1].Value switch
        {
            "fail" or "crit" => LogLevel.Error,
            "warn" => LogLevel.Warning,
            "info" => line.Contains("Request finished", StringComparison.Ordinal) ? LogLevel.Request : LogLevel.Info,
            _ => LogLevel.Debug,
        } : LogLevel.Debug;
        var source = m.Success ? ShortCategory(m.Groups[2].Value) : "server";
        Log(level, source, line.Trim());
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private void Log(LogLevel level, string source, string message) =>
        LogReceived?.Invoke(new LogEntry(DateTime.Now, level, source, message));

    public void Dispose()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        _process?.Dispose();
    }
}
