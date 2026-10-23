using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Numidium.Launcher.Core;

namespace Numidium.Launcher.Tests;

public class FileInstallTests
{
    [Theory]
    [InlineData("Data/Scripts/a.pex")]
    [InlineData(@"Data\SKSE\Plugins\b.dll")]
    public void AcceptsPathsInsideTheFolder(string relative)
    {
        using var dir = new TempDir();
        Assert.StartsWith(dir.Path, FileInstall.SafePath(dir.Path, relative));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData(@"Data\..\..\evil.dll")]
    [InlineData(@"C:\Windows\evil.dll")]
    public void RefusesPathsOutsideTheFolder(string relative)
    {
        using var dir = new TempDir();
        Assert.Throws<InvalidDataException>(() => FileInstall.SafePath(dir.Path, relative));
    }

    [Fact]
    public void ReplaceDoesNotWriteThroughAHardLink()
    {
        using var dir = new TempDir();
        var steam = dir.File("steam/Skyrim.esm", "steam's file");
        var copy = Path.Combine(dir.Path, "copy", "Skyrim.esm");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        Assert.True(CreateHardLink(copy, steam, IntPtr.Zero));

        FileInstall.Replace(copy, s => s.Write("installed"u8));

        Assert.Equal("installed", File.ReadAllText(copy));
        Assert.Equal("steam's file", File.ReadAllText(steam));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}

public class ClientPackageTests
{
    // A package as package-client.ps1 makes it: a zip plus numidium-client.json
    private static string MakePackage(string dir, string version, Dictionary<string, string> files)
    {
        Directory.CreateDirectory(dir);
        var zipName = $"numidium-client-{version}.zip";
        var zipPath = Path.Combine(dir, zipName);
        File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (path, content) in files)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
            }
        }
        var bytes = File.ReadAllBytes(zipPath);
        var info = new ClientPackage.Info(version, zipName, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var infoPath = Path.Combine(dir, ClientPackage.InfoFile);
        File.WriteAllText(infoPath, JsonSerializer.Serialize(info));
        return infoPath;
    }

    [Fact]
    public void InstallsAndRecordsTheFiles()
    {
        using var game = new TempDir();
        using var release = new TempDir();
        var info = MakePackage(release.Path, "1.0", new()
        {
            ["Data/SKSE/Plugins/SkyrimPlatform.dll"] = "sp",
            ["Data/Platform/Plugins/skymp5-client.js"] = "client",
        });

        var installed = ClientPackage.Install(game.Path, info);

        Assert.Equal("1.0", installed.Version);
        Assert.Equal("client", File.ReadAllText(Path.Combine(game.Path, "Data", "Platform", "Plugins", "skymp5-client.js")));
        Assert.Equal("1.0", ClientPackage.ReadInstalled(game.Path)!.Version);
        Assert.Equal(2, ClientPackage.ReadInstalled(game.Path)!.Files.Count);
    }

    [Fact]
    public void AnUpdateRemovesFilesTheNewVersionDropped()
    {
        using var game = new TempDir();
        using var release = new TempDir();
        ClientPackage.Install(game.Path, MakePackage(Path.Combine(release.Path, "v1"), "1.0", new()
        {
            ["Data/a.txt"] = "1", ["Data/old.txt"] = "old",
        }));
        game.File("Data/players-own.txt", "keep");

        ClientPackage.Install(game.Path, MakePackage(Path.Combine(release.Path, "v2"), "2.0", new() { ["Data/a.txt"] = "2" }));

        Assert.Equal("2", File.ReadAllText(Path.Combine(game.Path, "Data", "a.txt")));
        Assert.False(File.Exists(Path.Combine(game.Path, "Data", "old.txt")));
        Assert.True(File.Exists(Path.Combine(game.Path, "Data", "players-own.txt")), "files we never installed are left alone");
    }

    [Fact]
    public void RefusesADamagedOrIncompleteDownload()
    {
        using var game = new TempDir();
        using var release = new TempDir();
        // Same size, different contents
        var wrongHash = MakePackage(Path.Combine(release.Path, "a"), "1.0", new() { ["Data/a.txt"] = "1" });
        var tampered = ClientPackage.ReadInfo(wrongHash) with { Sha256 = new string('0', 64) };
        File.WriteAllText(wrongHash, JsonSerializer.Serialize(tampered));
        Assert.Throws<InvalidDataException>(() => ClientPackage.Install(game.Path, wrongHash));

        var info = MakePackage(Path.Combine(release.Path, "b"), "1.0", new() { ["Data/a.txt"] = "1" });
        var zip = Path.Combine(release.Path, "b", ClientPackage.ReadInfo(info).File);
        File.WriteAllBytes(zip, File.ReadAllBytes(zip)[..^10]);
        Assert.Throws<InvalidDataException>(() => ClientPackage.Install(game.Path, info));

        Assert.False(File.Exists(Path.Combine(game.Path, "Data", "a.txt")), "nothing installed");
    }

    [Fact]
    public void RefusesEntriesOutsideTheGameFolder()
    {
        using var game = new TempDir();
        using var release = new TempDir();
        var info = MakePackage(release.Path, "1.0", new() { ["../outside.txt"] = "x" });
        Assert.Throws<InvalidDataException>(() => ClientPackage.Install(game.Path, info));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(game.Path)!.FullName, "outside.txt")));
    }
}

public class SkseTests
{
    [Theory]
    [InlineData("skse64_2_02_06/skse64_loader.exe", "skse64_loader.exe")]
    [InlineData("skse64_2_02_06/skse64_1_6_1170.dll", "skse64_1_6_1170.dll")]
    [InlineData("skse64_2_02_06/Data/Scripts/Actor.pex", @"Data\Scripts\Actor.pex")]
    [InlineData("skse64_2_02_06/Data/Scripts/Source/Actor.psc", null)]
    [InlineData("skse64_2_02_06/skse64_readme.txt", null)]
    [InlineData("skse64_2_02_06/src/skse64/main.cpp", null)]
    [InlineData("skse64_loader.exe", null)]
    public void PicksTheGameFilesFromTheArchive(string key, string? expected) =>
        Assert.Equal(expected, Skse.TargetFor(key));

    [Fact]
    public void InstallsFromAnArchive()
    {
        using var game = new TempDir();
        using var downloads = new TempDir();
        var archive = Path.Combine(downloads.Path, "skse.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var entry in new[] { "skse64_2_02_06/skse64_loader.exe", "skse64_2_02_06/skse64_1_6_1170.dll",
                         "skse64_2_02_06/Data/Scripts/Actor.pex", "skse64_2_02_06/skse64_readme.txt" })
            {
                using var stream = zip.CreateEntry(entry).Open();
                stream.Write("x"u8);
            }
        }
        Assert.False(Skse.IsInstalled(game.Path));
        var written = Skse.Install(game.Path, archive);
        Assert.Equal(3, written.Count);
        Assert.True(Skse.IsInstalled(game.Path));
        Assert.True(File.Exists(Path.Combine(game.Path, "Data", "Scripts", "Actor.pex")));
        Assert.False(File.Exists(Path.Combine(game.Path, "skse64_readme.txt")));
    }

    // The real archive, when NUMIDIUM_SKSE_ARCHIVE points at a downloaded copy
    [Fact]
    public void InstallsTheOfficialArchive()
    {
        var archive = Environment.GetEnvironmentVariable("NUMIDIUM_SKSE_ARCHIVE");
        if (string.IsNullOrEmpty(archive)) return;
        Assert.Equal(Skse.Sha256, Skse.HashOf(archive));
        using var game = new TempDir();
        var written = Skse.Install(game.Path, archive);
        Assert.True(Skse.IsInstalled(game.Path));
        Assert.All(written.Where(f => f.StartsWith("Data")), f => Assert.EndsWith(".pex", f));
        Assert.DoesNotContain(written, f => f.Contains("Source") || f.Contains("src") || f.EndsWith(".txt"));
    }

    [Fact]
    public void RefusesAnArchiveWithoutTheLoader()
    {
        using var game = new TempDir();
        using var downloads = new TempDir();
        var archive = Path.Combine(downloads.Path, "not-skse.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("something/readme.txt").Open();
            stream.Write("x"u8);
        }
        Assert.Throws<InvalidDataException>(() => Skse.Install(game.Path, archive));
    }
}
