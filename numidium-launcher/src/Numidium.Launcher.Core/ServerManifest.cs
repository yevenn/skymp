using System.Text.Json;
using System.Text.Json.Serialization;

namespace Numidium.Launcher.Core;

/// <summary>
/// The server's load order, served as manifest.json on its resources port (the
/// game port's resources are on 3000). The client compares against the same
/// file at login, so building plugins.txt from it keeps the two in step.
/// </summary>
public sealed class ServerManifest
{
    public const int ResourcesPort = 3000;

    public sealed record Plugin(
        [property: JsonPropertyName("filename")] string FileName,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("crc32")] long Crc32,
        [property: JsonPropertyName("isLight")] bool IsLight);

    private sealed record Document([property: JsonPropertyName("mods")] List<Plugin> Mods);

    public List<Plugin> Plugins { get; }

    private ServerManifest(List<Plugin> plugins) => Plugins = plugins;

    public static ServerManifest Parse(string json) =>
        new(JsonSerializer.Deserialize<Document>(json)?.Mods ?? throw new FormatException("manifest.json has no mods"));

    public static async Task<ServerManifest> FetchAsync(string host, int resourcesPort = ResourcesPort, CancellationToken cancel = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await http.GetStringAsync($"http://{host}:{resourcesPort}/manifest.json", cancel);
        return Parse(json);
    }

    /// <summary>Whether the game copy has none of the server's plugins beyond the base masters,
    /// i.e. Numidium's mods were never installed there.</summary>
    public bool HasNoModsIn(string gameDir) =>
        Plugins.Where(p => !BaseGame.Masters.Contains(p.FileName, StringComparer.OrdinalIgnoreCase))
            .All(p => !File.Exists(Path.Combine(gameDir, "Data", p.FileName)));

    /// <summary>Server plugins missing from, or a different size in, the game copy's Data.</summary>
    public List<string> Mismatches(string gameDir)
    {
        var problems = new List<string>();
        foreach (var plugin in Plugins)
        {
            var file = new FileInfo(Path.Combine(gameDir, "Data", plugin.FileName));
            if (!file.Exists) problems.Add($"{plugin.FileName} is missing");
            else if (file.Length != plugin.Size) problems.Add($"{plugin.FileName} is a different version");
        }
        return problems;
    }
}
