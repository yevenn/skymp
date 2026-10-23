using System.Text.Json;

namespace Numidium.Launcher.Core;

/// <summary>The launcher's own settings, in %LOCALAPPDATA%\Numidium\launcher.json.</summary>
public sealed class LauncherSettings
{
    public string? SteamGameDir { get; set; }
    public string? GameDir { get; set; }
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 7777;
    /// <summary>Only offline (dev) servers use it; real servers authenticate.</summary>
    public int? ProfileId { get; set; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Numidium", "launcher.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static LauncherSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (JsonException)
        {
            // Unreadable settings: start over rather than refuse to open
        }
        return new();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Where a new game copy goes: on the Steam install's drive, so its
    /// Data files can be hard-linked instead of copied.</summary>
    public static string SuggestGameDir(string? steamGameDir) =>
        Path.Combine(Path.GetPathRoot(steamGameDir ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? "C:\\",
            "Numidium", "Game");
}
