using System.Text.Json;

namespace Numidium.Launcher.Core;

/// <summary>The launcher's own settings, in %LOCALAPPDATA%\Numidium\launcher.json.</summary>
public sealed class LauncherSettings
{
    /// <summary>Numidium's own server (the Scaleway VPS), which players use unless
    /// they pick another one. Moving the server means a launcher release.</summary>
    public const string NumidiumHost = "07611da2-89df-4204-af40-5008440d60c9.pub.instances.scw.cloud";
    public const int NumidiumPort = 7777;

    public string? SteamGameDir { get; set; }
    public string? GameDir { get; set; }
    /// <summary>Play on Numidium's server. Off: the custom server below (home or test
    /// servers). Settings saved before this existed lack it, so they switch to
    /// Numidium's server and keep their old address as the custom one.</summary>
    public bool UseNumidiumServer { get; set; } = true;
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 7777;
    /// <summary>Only offline (dev) servers use it; real servers authenticate.</summary>
    public int? ProfileId { get; set; }

    /// <summary>The server Play connects to.</summary>
    public (string Host, int Port) Server => UseNumidiumServer ? (NumidiumHost, NumidiumPort) : (ServerHost, ServerPort);
    /// <summary>A folder holding numidium-client.json and its zip (package-client.ps1's
    /// output), for testing a package before it's released. Null: GitHub releases.</summary>
    public string? ClientPackageDir { get; set; }
    /// <summary>Where the player's browser saves Nexus downloads.</summary>
    public string? DownloadsDir { get; set; }

    /// <summary>Discord sign-in sessions by server address, encrypted for this Windows
    /// user (DPAPI): each lets its holder play as the player on that server.</summary>
    public Dictionary<string, string> Sessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetSession(string server)
    {
        if (!Sessions.TryGetValue(server, out var stored)) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(stored), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null; // another Windows user's, or damaged: sign in again
        }
    }

    public void SetSession(string server, string? session)
    {
        if (session == null)
        {
            Sessions.Remove(server);
            return;
        }
        Sessions[server] = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(session), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }

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
