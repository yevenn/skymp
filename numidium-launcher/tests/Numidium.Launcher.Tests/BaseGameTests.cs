using System.Security.Cryptography;
using Numidium.Launcher.Core;

namespace Numidium.Launcher.Tests;

public class BaseGameTests
{
    [Fact]
    public void ListsOnlyBaseGameFilesEachInAKnownDepot()
    {
        Assert.Contains(BaseGame.Files, f => f.Path == "SkyrimSE.exe" && f.Depot == "489833");
        foreach (var master in BaseGame.Masters)
            Assert.Contains(BaseGame.Files, f => f.Path == Path.Combine("Data", master));
        Assert.DoesNotContain(BaseGame.Files, f => f.Path.Contains("Skyrim.ccc") || Path.GetFileName(f.Path).StartsWith("cc") ||
                                                   f.Path.Contains("_ResourcePack") || f.Path.Contains("SkyrimPrefs"));
        Assert.Equal(BaseGame.Files.Length, BaseGame.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var depot in BaseGame.Depots)
            Assert.True(BaseGame.Files.Where(f => f.Depot == depot.Id).Sum(f => f.Size) <= depot.Size, depot.Id);
        Assert.All(BaseGame.Files, f => Assert.Contains(BaseGame.Depots, d => d.Id == f.Depot));
    }

    // A real Steam install of 1.6.1170, when NUMIDIUM_STEAM_SKYRIM points at one: every
    // file in the table is there with that size and hash (hashes all 13.6 GB), except
    // optional ones the player replaced
    [Fact]
    public void MatchesARealSteamInstall()
    {
        var dir = Environment.GetEnvironmentVariable("NUMIDIUM_STEAM_SKYRIM");
        if (string.IsNullOrEmpty(dir)) return;
        foreach (var entry in BaseGame.Files)
        {
            var path = Path.Combine(dir, entry.Path);
            if (entry.Optional && (!File.Exists(path) || new FileInfo(path).Length != entry.Size)) continue;
            Assert.Equal(entry.Size, new FileInfo(path).Length);
            using var stream = File.OpenRead(path);
            Assert.Equal(entry.Sha1, Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant());
        }
    }
}

public class GameCopyTests
{
    // The fake install's base game files, as the table of the version the copy wants
    private static List<BaseGame.Entry> TableFor(TempDir steam)
    {
        var names = new[] { "SkyrimSE.exe", "SkyrimSELauncher.exe", "steam_api64.dll", "bink2w64.dll", "Skyrim_Default.ini", "High.ini",
            @"Data\Skyrim.esm", @"Data\Update.esm", @"Data\Dawnguard.esm", @"Data\HearthFires.esm", @"Data\Dragonborn.esm",
            @"Data\Skyrim - Meshes0.bsa", @"Data\Skyrim - Textures0.bsa", @"Data\Video\BGS_Logo.bik" };
        return names.Select(n => Entry(n, File.ReadAllBytes(Path.Combine(steam.Path, n)), n == "SkyrimSE.exe" ? "489833" : "489832")
            with { Optional = n.EndsWith(".bik") }).ToList();
    }

    [Fact]
    public void AReplacedOptionalFileDownloadsNothingButComesAlongWithItsDepot()
    {
        using var steam = TempDir.SteamGame();
        using var target = new TempDir();
        var table = TableFor(steam);
        ReplaceFile(Path.Combine(steam.Path, "Data", "Video", "BGS_Logo.bik"), ""); // a skip-the-intro mod
        var copy = new GameCopy(Path.Combine(target.Path, "Game"), table);

        var result = copy.Update(steam.Path);
        Assert.Equal(new[] { @"Data\Video\BGS_Logo.bik" }, result.Needed.Select(e => e.Path));
        Assert.Empty(result.Depots());
        Assert.Empty(copy.MissingFiles());

        ReplaceFile(Path.Combine(steam.Path, "Data", "Skyrim.esm"), "1.7 master");
        File.Delete(Path.Combine(copy.Dir, "Data", "Skyrim.esm"));
        var depots = copy.Update(steam.Path).Depots();
        var (depot, files) = Assert.Single(depots);
        Assert.Equal("489832", depot.Id);
        Assert.Equal(new[] { @"Data\Skyrim.esm", @"Data\Video\BGS_Logo.bik" }, files.Select(f => f.Path).Order());
    }

    private static BaseGame.Entry Entry(string path, byte[] content, string depot) =>
        new(path, content.Length, Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant(), depot);

    // What Steam's updater does: a new file under the same name, not an in-place write
    private static void ReplaceFile(string path, string content)
    {
        File.Delete(path);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void CopiesTheBaseGameAndDoesntCheckItTwice()
    {
        using var steam = TempDir.SteamGame();
        using var target = new TempDir();
        var copy = new GameCopy(Path.Combine(target.Path, "Game"), TableFor(steam));
        Assert.False(copy.Exists);

        var first = copy.Update(steam.Path);
        Assert.Equal(14, first.Copied + first.Linked);
        Assert.Empty(first.Needed);
        Assert.True(copy.Exists);
        Assert.Empty(copy.MissingFiles());
        Assert.False(File.Exists(Path.Combine(copy.Dir, "skse64_loader.exe")), "mods stay behind");
        Assert.False(File.Exists(Path.Combine(copy.Dir, "Skyrim.ccc")), "no Creation Club autoload");
        Assert.False(File.Exists(Path.Combine(copy.Dir, "Data", "SkyUI_SE.esp")));

        var second = copy.Update(steam.Path);
        Assert.Equal(14, second.Unchanged);
    }

    [Fact]
    public void HardLinksDataOnTheSameDriveAndCopiesTheExe()
    {
        using var steam = TempDir.SteamGame();
        using var target = new TempDir(); // same drive as steam: both under the temp folder
        var copy = new GameCopy(Path.Combine(target.Path, "Game"), TableFor(steam));
        var result = copy.Update(steam.Path);
        Assert.Equal(8, result.Linked);  // Data files
        Assert.Equal(6, result.Copied);  // root files, including SkyrimSE.exe
    }

    [Fact]
    public void KeepsItsVersionWhenSteamUpdates()
    {
        using var steam = TempDir.SteamGame();
        using var target = new TempDir();
        var copy = new GameCopy(Path.Combine(target.Path, "Game"), TableFor(steam));
        copy.Update(steam.Path);
        var copyEsm = Path.Combine(copy.Dir, "Data", "Skyrim.esm");
        var before = File.ReadAllText(copyEsm);

        // Steam moves to 1.7: the copy's links keep the old files, and Update leaves them
        ReplaceFile(Path.Combine(steam.Path, "Data", "Skyrim.esm"), "1.7 master");
        ReplaceFile(Path.Combine(steam.Path, "SkyrimSE.exe"), "1.7 exe");
        var result = copy.Update(steam.Path);
        Assert.Equal(14, result.Unchanged);
        Assert.Equal(before, File.ReadAllText(copyEsm));
        Assert.Empty(copy.MissingFiles());
    }

    [Fact]
    public void FilesSteamHasInAnotherVersionAreNeededFromTheDepots()
    {
        using var steam = TempDir.SteamGame();
        using var target = new TempDir();
        var table = TableFor(steam);
        ReplaceFile(Path.Combine(steam.Path, "SkyrimSE.exe"), "1.7 exe");
        ReplaceFile(Path.Combine(steam.Path, "Data", "Skyrim - Meshes0.bsa"), new string('n', 64)); // same size, other content
        var copy = new GameCopy(Path.Combine(target.Path, "Game"), table);

        var result = copy.Update(steam.Path);
        Assert.Equal(new[] { "SkyrimSE.exe", @"Data\Skyrim - Meshes0.bsa" }, result.Needed.Select(e => e.Path));
        Assert.False(File.Exists(Path.Combine(copy.Dir, "SkyrimSE.exe")));
        Assert.Equal(result.Needed.Select(e => e.Path), copy.MissingFiles());
    }

    [Fact]
    public void AFileChangedInTheCopyIsCheckedAgainAndReplaced()
    {
        using var steam = TempDir.SteamGame();
        using var target = new TempDir();
        var copy = new GameCopy(Path.Combine(target.Path, "Game"), TableFor(steam));
        copy.Update(steam.Path);
        var exe = Path.Combine(copy.Dir, "SkyrimSE.exe");
        ReplaceFile(exe, "something else");
        Assert.Equal(new[] { "SkyrimSE.exe" }, copy.MissingFiles());

        var result = copy.Update(steam.Path);
        Assert.Equal(1, result.Copied);
        Assert.Equal("x", File.ReadAllText(exe));
        Assert.Empty(copy.MissingFiles());
    }

    [Fact]
    public void MovesCheckedFilesInFromADepot()
    {
        using var steam = TempDir.SteamGame();
        using var target = new TempDir();
        using var depot = new TempDir();
        var table = TableFor(steam);
        ReplaceFile(Path.Combine(steam.Path, "SkyrimSE.exe"), "1.7 exe");
        var copy = new GameCopy(Path.Combine(target.Path, "Game"), table);
        var needed = copy.Update(steam.Path).Needed;

        Assert.False(copy.DepotHas(depot.Path, needed));
        var fromDepot = depot.File("SkyrimSE.exe", "x"); // 1.6.1170's, as the table says
        depot.File(@"Data\ccBGSSSE001-Fish.esm"); // depots also carry Creation Club files
        Assert.True(copy.DepotHas(depot.Path, needed));
        Assert.Equal(1, copy.InstallFromDepot(depot.Path, needed));
        Assert.False(File.Exists(fromDepot));
        Assert.Equal("x", File.ReadAllText(Path.Combine(copy.Dir, "SkyrimSE.exe")));
        Assert.Empty(copy.MissingFiles());
        Assert.False(File.Exists(Path.Combine(copy.Dir, "Data", "ccBGSSSE001-Fish.esm")));
    }

    [Fact]
    public void RefusesADepotFileInAnotherVersion()
    {
        using var steam = TempDir.SteamGame();
        using var target = new TempDir();
        using var depot = new TempDir();
        var table = TableFor(steam);
        ReplaceFile(Path.Combine(steam.Path, "SkyrimSE.exe"), "1.7 exe");
        var copy = new GameCopy(Path.Combine(target.Path, "Game"), table);
        var needed = copy.Update(steam.Path).Needed;
        depot.File("SkyrimSE.exe", "y"); // right size, wrong content: an unfinished download
        Assert.Throws<InvalidDataException>(() => copy.InstallFromDepot(depot.Path, needed));
        Assert.Equal(new[] { "SkyrimSE.exe" }, copy.MissingFiles());
    }

    [Fact]
    public void RefusesToCopyOntoTheSteamFolder()
    {
        using var steam = TempDir.SteamGame();
        Assert.Throws<InvalidOperationException>(() => new GameCopy(steam.Path, TableFor(steam)).Update(steam.Path));
    }

    [Fact]
    public void RefusesAFolderWithoutSkyrim()
    {
        using var empty = new TempDir();
        using var target = new TempDir();
        Assert.Throws<DirectoryNotFoundException>(() => new GameCopy(target.Path).Update(empty.Path));
    }
}

public class SteamDepotsTests
{
    [Fact]
    public void ReadsTheDownloadResultFromSteamsConsoleLog()
    {
        // As Steam wrote it in a real run
        const string log = """
            [2026-10-24 03:48:07] ExecCommandLine: ""c:\program files (x86)\steam\steam.exe" -console +download_depot 489830 489833 1914580699073641964"
            [2026-10-24 03:48:07] Downloading depot 489833 (1 files, 26 MB) ...
            [2026-10-24 03:48:08] Depot download complete : "C:\Program Files (x86)\Steam\steamapps\content\app_489830\depot_489833" (manifest 1914580699073641964)
            """;
        var outcome = SteamDepots.ReadOutcome(log, "489833");
        Assert.NotNull(outcome);
        Assert.False(outcome.Failed);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\content\app_489830\depot_489833", outcome.Path);

        Assert.Null(SteamDepots.ReadOutcome(log, "489832")); // another depot's result
        Assert.Null(SteamDepots.ReadOutcome("[2026-10-23 14:00:02] Downloading depot 489832 ...\n", "489832"));

        var failed = SteamDepots.ReadOutcome("[2026-10-23 14:00:02] Depot download failed : Access denied\n", "489832");
        Assert.True(failed!.Failed);
        Assert.Contains("Access denied", failed.Line);
    }

    // Really downloads the 37 MB exe depot through this PC's Steam, when
    // NUMIDIUM_STEAM_DEPOT_TEST=1, then removes it again
    [Fact]
    public async Task DownloadsTheExeDepotThroughSteam()
    {
        if (Environment.GetEnvironmentVariable("NUMIDIUM_STEAM_DEPOT_TEST") != "1") return;
        var steamRoot = SteamSkyrim.FindSteamRoot();
        Assert.NotNull(steamRoot);
        var depot = BaseGame.Depots.Single(d => d.Id == "489833");
        var files = BaseGame.Files.Where(f => f.Depot == depot.Id).ToList();
        using var target = new TempDir();
        var copy = new GameCopy(target.Path, files);
        var messages = new List<string>();
        var status = new Progress<SteamDepots.Status>(s => { lock (messages) if (messages.LastOrDefault() != s.Message) messages.Add(s.Message); });

        var dir = await SteamDepots.DownloadAsync(steamRoot!, depot, d => copy.DepotHas(d, files), status);
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "numidium-depot-test.txt"), messages.Prepend(dir));
        Assert.True(copy.DepotHas(dir, files));
        Assert.Equal(1, copy.InstallFromDepot(dir, files));
        Assert.Equal(SteamSkyrim.SupportedVersion, SteamSkyrim.GetVersion(target.Path));
        SteamDepots.Remove(dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void CommandIsWhatToTypeInSteamsConsole() =>
        Assert.Equal("download_depot 489830 489833 1914580699073641964", SteamDepots.Command(BaseGame.Depots[0]));
}

public class ContentCatalogTests
{
    [Fact]
    public void SetsThePlayersCatalogAsideAndRestoresIt()
    {
        using var dir = new TempDir();
        var path = dir.File("ContentCatalog.txt", "written by 1.7");
        using (ContentCatalog.SetAside(path))
        {
            Assert.False(File.Exists(path));
            File.WriteAllText(path, "written by 1.6.1170"); // the game makes its own
        }
        Assert.Equal("written by 1.7", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".numidium-backup"));
    }

    [Fact]
    public void DropsTheGamesCatalogWhenThePlayerHadNone()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "ContentCatalog.txt");
        using (ContentCatalog.SetAside(path)) File.WriteAllText(path, "written by 1.6.1170");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ALeftoverBackupIsThePlayers()
    {
        using var dir = new TempDir();
        var path = dir.File("ContentCatalog.txt", "from a session that crashed");
        dir.File("ContentCatalog.txt.numidium-backup", "the player's");
        using (ContentCatalog.SetAside(path)) Assert.False(File.Exists(path));
        Assert.Equal("the player's", File.ReadAllText(path));
    }
}
