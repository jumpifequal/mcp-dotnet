using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MCPPOC.Console.Services;

/// <summary>Drives install.ps1 (Integration tab) and reports whether each client is currently registered.</summary>
public sealed class InstallerRunner(string repoRoot)
{
    public event Action<string>? OutputReceived;

    public async Task<int> RegisterAsync(string target, bool readOnly, CancellationToken ct = default)
    {
        var script = Path.Combine(repoRoot, "Scripts", "install.ps1");
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("-Target"); psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("-NoBuild");
        if (readOnly) psi.ArgumentList.Add("-ReadOnly");

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    /// <summary>Read-only check: does each client's config already have a 'dotnet' (or custom name) MCP server entry?</summary>
    public static (bool ClaudeCode, bool ClaudeDesktop, bool Codex) CheckRegistration(string name = "dotnet")
    {
        bool claudeCode = false, claudeDesktop = false, codex = false;

        try
        {
            var claudeJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (File.Exists(claudeJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(claudeJson));
                if (doc.RootElement.TryGetProperty("projects", out var projects))
                    foreach (var proj in projects.EnumerateObject())
                        if (proj.Value.TryGetProperty("mcpServers", out var servers) && servers.TryGetProperty(name, out _))
                            claudeCode = true;
            }
        }
        catch (JsonException) { /* leave false */ }
        catch (IOException) { /* leave false */ }

        try
        {
            var desktopJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
            if (File.Exists(desktopJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(desktopJson));
                claudeDesktop = doc.RootElement.TryGetProperty("mcpServers", out var servers) && servers.TryGetProperty(name, out _);
            }
        }
        catch (JsonException) { }
        catch (IOException) { }

        try
        {
            var codexToml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            if (File.Exists(codexToml))
                codex = System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(codexToml),
                    $@"(?m)^\[mcp_servers\.{System.Text.RegularExpressions.Regex.Escape(name)}\]");
        }
        catch (IOException) { }

        return (claudeCode, claudeDesktop, codex);
    }

    public static bool IsClaudeDesktopRunning() =>
        Process.GetProcessesByName("claude").Any(p =>
        {
            try { return p.MainModule?.FileName?.Contains("AnthropicClaude", StringComparison.OrdinalIgnoreCase) == true; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
        });
}
