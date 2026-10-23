namespace Numidium.Launcher.Core;

/// <summary>
/// Which files of a Steam install make up the base game. Players' Steam folders
/// also hold their own mods, script extenders, ReShade, screenshots and so on,
/// none of which may reach the Numidium copy, so this is an allowlist.
/// Creation Club content and Skyrim.ccc are left out on purpose: they load
/// implicitly and would put plugins in the load order the server doesn't have.
/// </summary>
public static class BaseGame
{
    public static readonly string[] RootFiles =
    {
        "SkyrimSE.exe", "SkyrimSELauncher.exe", "bink2w64.dll", "steam_api64.dll", "installscript.vdf",
        "Skyrim_Default.ini", "Low.ini", "Medium.ini", "High.ini", "Ultra.ini",
    };

    /// <summary>Plugins the game loads implicitly, in their fixed order.</summary>
    public static readonly string[] Masters =
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    /// <summary>Base game files present in <paramref name="gameDir"/>, relative to it.</summary>
    public static List<string> ListFiles(string gameDir)
    {
        var files = new List<string>();
        foreach (var name in RootFiles)
        {
            if (File.Exists(Path.Combine(gameDir, name))) files.Add(name);
        }
        var data = Path.Combine(gameDir, "Data");
        foreach (var name in Masters)
        {
            if (File.Exists(Path.Combine(data, name))) files.Add(Path.Combine("Data", name));
        }
        if (Directory.Exists(data))
        {
            foreach (var bsa in Directory.EnumerateFiles(data, "Skyrim - *.bsa"))
                files.Add(Path.Combine("Data", Path.GetFileName(bsa)));
            var video = Path.Combine(data, "Video");
            if (Directory.Exists(video))
            {
                // BGS_Logo.bik; mod managers sometimes deploy whole folders in here
                foreach (var file in Directory.EnumerateFiles(video, "*.bik"))
                    files.Add(Path.GetRelativePath(gameDir, file));
            }
        }
        return files;
    }

    /// <summary>What a usable copy must have; ListFiles finds the rest when present.</summary>
    public static IEnumerable<string> RequiredFiles() =>
        new[] { "SkyrimSE.exe", "steam_api64.dll" }.Concat(Masters.Select(m => Path.Combine("Data", m)));
}
