namespace MCPPOC.Console.Models;

/// <summary>A saved set of launch options for the HTTP transport, picked from the "Profile" dropdown.</summary>
public sealed class ServerProfile
{
    public string Name { get; set; } = "Default";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5005;
    public string Path { get; set; } = "/mcp";
    public bool ReadOnly { get; set; }
    public bool NoAuth { get; set; }
    public string LogLevel { get; set; } = "Information";
    public string AllowedHosts { get; set; } = ""; // comma-separated
    public string SslCertFile { get; set; } = "";
    public string SslKeyFile { get; set; } = "";

    public ServerProfile Clone() => new()
    {
        Name = Name,
        Host = Host,
        Port = Port,
        Path = Path,
        ReadOnly = ReadOnly,
        NoAuth = NoAuth,
        LogLevel = LogLevel,
        AllowedHosts = AllowedHosts,
        SslCertFile = SslCertFile,
        SslKeyFile = SslKeyFile,
    };
}
