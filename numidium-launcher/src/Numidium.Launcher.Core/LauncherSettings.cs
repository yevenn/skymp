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
    /// <summary>A folder holding numidium-client.json and its zip (package-client.ps1's
    /// output), for testing a package before it's released. Null: GitHub releases.</summary>
    public string? ClientPackageDir { get; set; }
    /// <summary>Where the player's browser saves Nexus downloads.</summary>
    public string? DownloadsDir { get; set; }

    /// <summary>Where the newest GitHub release is mirrored (<see cref="Releases"/>).</summary>
    public static string ReleaseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Numidium", "Release");

    /// <summary>A client package shipped in a numidium-client folder beside the launcher,
    /// or null. Used when no package folder was picked and no release could be fetched.</summary>
    public static string? BundledPackageDir(string? launcherDir = null)
    {
        var dir = Path.Combine(launcherDir ?? AppContext.BaseDirectory, "numidium-client");
        return File.Exists(Path.Combine(dir, ClientPackage.InfoFile)) ? dir : null;
    }

    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Numidium", "Downloads");

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
