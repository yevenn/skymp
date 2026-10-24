using System.Text;
using Numidium.Launcher.Core;

namespace Numidium.Launcher.Tests;

public class VdfTests
{
    [Fact]
    public void ReadsLibraryFolders()
    {
        const string text = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "123" }
                }
                // a comment
                "1" { "path" "E:\\SteamLibrary" "apps" { "489830" "456" } }
            }
            """;
        var root = Vdf.Parse(text);
        var folders = root.Children["libraryfolders"].Children;
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders["0"].Values["path"]);
        Assert.Equal(@"E:\SteamLibrary", folders["1"].Values["PATH"]);
        Assert.Equal("456", folders["1"].Children["apps"].Values["489830"]);
    }

    [Theory]
    [InlineData("\"a\" { \"b\" \"c\"")]
    [InlineData("\"a\" \"b\" }")]
    [InlineData("\"a\"")]
    [InlineData("a b")]
    public void RejectsBrokenFiles(string text) => Assert.Throws<FormatException>(() => Vdf.Parse(text));
}

public class SteamSkyrimTests
{
    [Fact]
    public void FindsTheGameThroughTheLibraryList()
    {
        using var steam = new TempDir();
        using var game = TempDir.SteamGame();
        // The game lives in a second library: <lib>\steamapps\common\Skyrim Special Edition
        var library = Directory.GetParent(game.Path)!.FullName;
        var commonDir = Path.Combine(library, "steamapps", "common", "Skyrim Special Edition");
        Directory.CreateDirectory(Path.GetDirectoryName(commonDir)!);
        Directory.Move(game.Path, commonDir);
        try
        {
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_489830.acf"),
                "\"AppState\" { \"appid\" \"489830\" \"installdir\" \"Skyrim Special Edition\" }");
            steam.File(Path.Combine("steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{library.Replace("\\", "\\\\")}\" }} }}");
            Assert.Equal(commonDir, SteamSkyrim.FindInSteam(steam.Path), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(Path.Combine(library, "steamapps"), recursive: true);
        }
    }

    [Theory]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Skyrim Special Edition", true)]
    [InlineData(@"C:\Program Files (x86)\Steam\SteamApps\common\Skyrim Special Edition", true)]
    [InlineData(@"E:\Jarldom\SkyrimDev", false)]
    public void KnowsASteamLibraryFolder(string dir, bool expected) =>
        Assert.Equal(expected, SteamSkyrim.IsInSteamLibrary(dir));

    [Fact]
    public void NoManifestMeansNotFound()
    {
        using var steam = new TempDir();
        Assert.Null(SteamSkyrim.FindInSteam(steam.Path));
    }
}

public class PluginsTxtTests
{
    [Fact]
    public void FormatEnablesPluginsAndLeavesOutTheMasters()
    {
        var text = PluginsTxt.Format(new[] { "Skyrim.esm", "Update.esm", "SkyUI_SE.esp", "Sentinel - 3BA.esp" });
        Assert.Equal("# Numidium load order, written by the launcher\r\n*SkyUI_SE.esp\r\n*Sentinel - 3BA.esp\r\n", text);
    }

    [Fact]
    public void SwapRestoresThePlayersFile()
    {
        using var dir = new TempDir();
        var path = dir.File("plugins.txt", "*KeizaalStuff.esp\r\n");
        using (PluginsTxt.Swap(new[] { "A.esp" }, path))
        {
            Assert.Contains("*A.esp", File.ReadAllText(path));
        }
        Assert.Equal("*KeizaalStuff.esp\r\n", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".numidium-backup"));
    }

    [Fact]
    public void SwapRemovesItsFileWhenThereWasNone()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Skyrim Special Edition", "plugins.txt");
        using (PluginsTxt.Swap(new[] { "A.esp" }, path)) Assert.True(File.Exists(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ALeftoverBackupIsThePlayersAndIsKept()
    {
        // A crash during the last session left our list in place and the player's in the backup
        using var dir = new TempDir();
        var path = dir.File("plugins.txt", "# Numidium load order\r\n*A.esp\r\n");
        dir.File("plugins.txt.numidium-backup", "*Players.esp\r\n");
        using (PluginsTxt.Swap(new[] { "B.esp" }, path)) { }
        Assert.Equal("*Players.esp\r\n", File.ReadAllText(path));
    }
}

public class ClientSettingsTests
{
    [Fact]
    public void WritesServerAndProfileWithoutABomAndKeepsOtherSettings()
    {
        using var game = new TempDir();
        var path = ClientSettings.PathIn(game.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"master\": \"\", \"server-ip\": \"old\", \"gameData\": { \"profileId\": 1, \"other\": true } }");

        ClientSettings.Write(game.Path, "play.numidium.example", 7777, profileId: 2);

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        var json = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(bytes))!;
        Assert.Equal("play.numidium.example", (string)json["server-ip"]!);
        Assert.Equal(7777, (int)json["server-port"]!);
        Assert.Equal(2, (int)json["gameData"]!["profileId"]!);
        Assert.True((bool)json["gameData"]!["other"]!);
        Assert.Equal("", (string)json["master"]!);
    }

    [Fact]
    public void CreatesTheFileWhenMissing()
    {
        using var game = new TempDir();
        ClientSettings.Write(game.Path, "127.0.0.1", 7777);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ClientSettings.PathIn(game.Path)))!;
        Assert.Equal("127.0.0.1", (string)json["server-ip"]!);
        Assert.Null(json["gameData"]);
    }
}

public class ServerManifestTests
{
    private const string Json = """
        { "versionMajor": 1, "mods": [
            { "crc32": 1, "filename": "Skyrim.esm", "size": 3, "isLight": false },
            { "crc32": 2, "filename": "Sentinel - 3BA.esp", "size": 5, "isLight": true },
            { "crc32": 3, "filename": "Gone.esp", "size": 1, "isLight": false } ] }
        """;

    [Fact]
    public void ParsesThePluginsInOrder()
    {
        var manifest = ServerManifest.Parse(Json);
        Assert.Equal(new[] { "Skyrim.esm", "Sentinel - 3BA.esp", "Gone.esp" }, manifest.Plugins.Select(p => p.FileName));
        Assert.True(manifest.Plugins[1].IsLight);
    }

    [Fact]
    public void ReportsMissingAndDifferentPlugins()
    {
        using var game = new TempDir();
        game.File("Data/Skyrim.esm", "abc");
        game.File("Data/Sentinel - 3BA.esp", "abcdefg");
        var manifest = ServerManifest.Parse(Json);
        Assert.Equal(new[] { "Sentinel - 3BA.esp is a different version", "Gone.esp is missing" }, manifest.Mismatches(game.Path));
        Assert.False(manifest.HasNoModsIn(game.Path));
    }

    [Fact]
    public void ABaseGameOnlyFolderHasNoMods()
    {
        using var game = new TempDir();
        game.File("Data/Skyrim.esm", "abc");
        Assert.True(ServerManifest.Parse(Json).HasNoModsIn(game.Path));
    }
}

public class LauncherSettingsTests
{
    [Fact]
    public void RoundTripsAndSurvivesAGarbledFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "launcher.json");
        new LauncherSettings { GameDir = @"E:\Numidium\Game", ServerHost = "h", ServerPort = 1, ProfileId = 2 }.Save(path);
        var loaded = LauncherSettings.Load(path);
        Assert.Equal((@"E:\Numidium\Game", "h", 1, (int?)2), (loaded.GameDir, loaded.ServerHost, loaded.ServerPort, loaded.ProfileId));

        File.WriteAllText(path, "{ not json");
        Assert.Equal(7777, LauncherSettings.Load(path).ServerPort);
    }

    [Fact]
    public void FindsAPackageBundledBesideTheLauncher()
    {
        using var launcher = new TempDir();
        Assert.Null(LauncherSettings.BundledPackageDir(launcher.Path));
        launcher.File(Path.Combine("numidium-client", ClientPackage.InfoFile), "{}");
        Assert.Equal(Path.Combine(launcher.Path, "numidium-client"), LauncherSettings.BundledPackageDir(launcher.Path));
    }

    [Fact]
    public void SuggestsAFolderOnTheSteamDrive() =>
        Assert.Equal(@"E:\Numidium\Game", LauncherSettings.SuggestGameDir(@"E:\SteamLibrary\steamapps\common\Skyrim Special Edition"));
}
