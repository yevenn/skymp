namespace Numidium.Launcher.Tests;

/// <summary>A scratch folder for one test, deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "numidium-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative, string content = "x")
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }

    /// <summary>A fake Steam install: base game files plus the clutter real ones have.</summary>
    public static TempDir SteamGame()
    {
        var dir = new TempDir();
        foreach (var name in new[] { "SkyrimSE.exe", "SkyrimSELauncher.exe", "steam_api64.dll", "bink2w64.dll", "Skyrim_Default.ini", "High.ini" })
            dir.File(name);
        foreach (var name in new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
                     "Skyrim - Meshes0.bsa", "Skyrim - Textures0.bsa", "Video/BGS_Logo.bik" })
            dir.File(System.IO.Path.Combine("Data", name), new string('d', 64));
        // Not base game: script extender, ReShade, CC autoload list, mods, screenshots
        foreach (var name in new[] { "skse64_loader.exe", "dxgi.dll", "Skyrim.ccc", "ScreenShot1.png", "Data/SkyUI_SE.esp",
                     "Data/ccBGSSSE001-Fish.esm", "Data/_ResourcePack.bsa", "Data/meshes/armor/thing.nif", "Data/Video/SKSE/x.txt" })
            dir.File(name);
        return dir;
    }
}
