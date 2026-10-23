using System.Diagnostics;
using Microsoft.Win32;

namespace Numidium.Launcher.Core;

/// <summary>Finds the player's Steam copy of Skyrim Special Edition and checks its version.</summary>
public static class SteamSkyrim
{
    public const string AppId = "489830";

    /// <summary>The game version SKSE, SkyrimPlatform and the SkyMP client are built for.</summary>
    public static readonly Version SupportedVersion = new(1, 6, 1170, 0);

    /// <summary>Steam's install folder for Skyrim SE, or null when it can't be found.</summary>
    public static string? Find()
    {
        foreach (var steam in SteamRoots())
        {
            var found = FindInSteam(steam);
            if (found != null) return found;
        }
        // Set by the game's installer; Steam libraries moved later can leave it stale
        var installed = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition",
            "Installed Path", null) as string;
        return IsGameDir(installed) ? Path.GetFullPath(installed!) : null;
    }

    /// <summary>Looks through a Steam install's libraries for the Skyrim SE app manifest.</summary>
    public static string? FindInSteam(string steamRoot)
    {
        var libraries = new List<string> { steamRoot };
        var foldersFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(foldersFile))
        {
            var folders = Vdf.Parse(File.ReadAllText(foldersFile));
            if (folders.Children.TryGetValue("libraryfolders", out var list))
            {
                foreach (var library in list.Children.Values)
                {
                    if (library.Values.TryGetValue("path", out var path)) libraries.Add(path);
                }
            }
        }
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf");
            if (!File.Exists(manifest)) continue;
            var app = Vdf.Parse(File.ReadAllText(manifest));
            if (!app.Children.TryGetValue("AppState", out var state)) continue;
            if (!state.Values.TryGetValue("installdir", out var installDir)) continue;
            var dir = Path.Combine(library, "steamapps", "common", installDir);
            if (IsGameDir(dir)) return Path.GetFullPath(dir);
        }
        return null;
    }

    /// <summary>Whether <paramref name="dir"/> sits in a Steam library (…\steamapps\common\…),
    /// rather than being some other copy of the game.</summary>
    public static bool IsInSteamLibrary(string dir) =>
        Path.GetFullPath(dir).Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase);

    public static bool IsGameDir(string? dir) =>
        !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "SkyrimSE.exe"))
        && File.Exists(Path.Combine(dir, "Data", "Skyrim.esm"));

    /// <summary>SkyrimSE.exe's file version, e.g. 1.6.1170.0.</summary>
    public static Version? GetVersion(string gameDir)
    {
        var exe = Path.Combine(gameDir, "SkyrimSE.exe");
        if (!File.Exists(exe)) return null;
        var info = FileVersionInfo.GetVersionInfo(exe);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    private static IEnumerable<string> SteamRoots()
    {
        // Current user first: SteamPath uses forward slashes
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string user)
            yield return user.Replace('/', '\\');
        if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string machine)
            yield return machine;
    }
}
