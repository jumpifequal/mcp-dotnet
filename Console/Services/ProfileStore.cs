using System.IO;
using System.Text.Json;
using MCPPOC.Console.Models;

namespace MCPPOC.Console.Services;

/// <summary>Persists named server profiles to %LOCALAPPDATA%\mcp-dotnet\console-profiles.json.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public ProfileStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcp-dotnet");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "console-profiles.json");
    }

    public List<ServerProfile> Load()
    {
        if (!File.Exists(_path))
            return [new ServerProfile()];
        try
        {
            var list = JsonSerializer.Deserialize<List<ServerProfile>>(File.ReadAllText(_path));
            return list is { Count: > 0 } ? list : [new ServerProfile()];
        }
        catch (JsonException)
        {
            return [new ServerProfile()];
        }
    }

    public void Save(IEnumerable<ServerProfile> profiles) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(profiles, JsonOptions));
}
