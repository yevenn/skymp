using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Numidium.Launcher.Core;

namespace Numidium.Launcher.Tests;

public class ReleaseTests
{
    // GitHub's release list, newest first, with only the fields the launcher reads
    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false, string published = "2026-10-24T12:00:00Z",
        params string[] assets) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = $"https://github.com/yevenn/skymp/releases/tag/{tag}",
            draft,
            prerelease,
            published_at = published,
            assets = assets.Select(a => new { name = a, browser_download_url = $"https://files.test/{tag}/{a}", size = 1 }),
        });

    [Fact]
    public void PicksTheNewestPublishedReleaseWithAClientPackage()
    {
        var list = "[" + string.Join(",",
            ReleaseJson("numidium-3", draft: true, published: "2026-10-26T00:00:00Z", assets: ClientPackage.InfoFile),
            ReleaseJson("numidium-2", prerelease: true, published: "2026-10-25T00:00:00Z", assets: ClientPackage.InfoFile),
            ReleaseJson("v5.0.0", published: "2026-10-27T00:00:00Z", assets: "skymp-server.zip"), // an upstream-style release without one
            ReleaseJson("numidium-1", assets: new[] { ClientPackage.InfoFile, "numidium-client-1.zip" })) + "]";
        var release = Releases.PickLatest(list);
        Assert.Equal("numidium-1", release?.Tag);
        Assert.Equal("https://files.test/numidium-1/numidium-client-1.zip", release!.Find("NUMIDIUM-CLIENT-1.ZIP")?.Url);
        Assert.Null(Releases.PickLatest("[]"));
    }

    // As GitHub listed them on 2026-10-24: not newest first
    [Fact]
    public void PicksByPublishDateNotListOrder()
    {
        var list = "[" + string.Join(",",
            ReleaseJson("numidium-2026.10.24-8df7d5e5", published: "2026-10-24T13:45:10Z", assets: ClientPackage.InfoFile),
            ReleaseJson("numidium-2026.10.24-56a67b54", published: "2026-10-24T16:20:31Z", assets: ClientPackage.InfoFile),
            ReleaseJson("numidium-2026.10.24-4edb21af", published: "2026-10-24T12:30:06Z", assets: ClientPackage.InfoFile)) + "]";
        Assert.Equal("numidium-2026.10.24-56a67b54", Releases.PickLatest(list)?.Tag);
    }

    /// <summary>Serves <c>https://files.test/...</c> from a dictionary and counts requests.</summary>
    private sealed class FakeFiles(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(files.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (Releases.Release Release, Dictionary<string, byte[]> Files) MakeRelease(string version, byte[]? zipOverride = null)
    {
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Data/SKSE/Plugins/SkyrimPlatform.dll").Open());
            writer.Write("platform " + version);
        }
        var zipBytes = zipStream.ToArray();
        var zipName = $"numidium-client-{version}.zip";
        var info = JsonSerializer.Serialize(new ClientPackage.Info(version, zipName, zipBytes.Length,
            Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant()));
        var tag = "numidium-" + version;
        var files = new Dictionary<string, byte[]>
        {
            [$"https://files.test/{tag}/{ClientPackage.InfoFile}"] = Encoding.UTF8.GetBytes(info),
            [$"https://files.test/{tag}/{zipName}"] = zipOverride ?? zipBytes,
            [$"https://files.test/{tag}/{Collection.FileName}"] = Encoding.UTF8.GetBytes("{\"info\":{\"name\":\"" + version + "\"}}"),
        };
        var assets = files.Keys.Select(url => new Releases.Asset(url[(url.LastIndexOf('/') + 1)..], url, files[url].Length)).ToList();
        return (new Releases.Release(tag, $"https://github.com/yevenn/skymp/releases/tag/{tag}", false, false, assets), files);
    }

    [Fact]
    public async Task MirrorsTheSmallFilesThenDownloadsTheZipOnce()
    {
        using var dir = new TempDir();
        var (release, files) = MakeRelease("1");
        var fake = new FakeFiles(files);
        var http = new HttpClient(fake);

        await Releases.SyncAsync(http, release, dir.Path);
        Assert.True(File.Exists(Path.Combine(dir.Path, ClientPackage.InfoFile)));
        Assert.Contains("\"1\"", File.ReadAllText(Path.Combine(dir.Path, Collection.FileName)));
        Assert.False(File.Exists(Path.Combine(dir.Path, ModList.FileName)), "not in this release");
        Assert.Equal("numidium-1", Releases.ReadMirrored(dir.Path)?.Tag);
        Assert.DoesNotContain(fake.Requested, u => u.EndsWith(".zip"));

        await Releases.EnsurePackageAsync(http, dir.Path);
        await Releases.EnsurePackageAsync(http, dir.Path); // already there and checks out
        Assert.Single(fake.Requested, u => u.EndsWith(".zip"));

        // The mirror then works like a local package folder
        using var game = new TempDir();
        var installed = ClientPackage.Install(game.Path, Path.Combine(dir.Path, ClientPackage.InfoFile));
        Assert.Equal("1", installed.Version);
        Assert.Equal("platform 1", File.ReadAllText(Path.Combine(game.Path, "Data", "SKSE", "Plugins", "SkyrimPlatform.dll")));

        // Same release again: nothing downloaded
        var count = fake.Requested.Count;
        await Releases.SyncAsync(http, release, dir.Path);
        Assert.Equal(count, fake.Requested.Count);
    }

    [Fact]
    public async Task ANewReleaseReplacesTheOldOnesFiles()
    {
        using var dir = new TempDir();
        var (first, firstFiles) = MakeRelease("1");
        await Releases.SyncAsync(new HttpClient(new FakeFiles(firstFiles)), first, dir.Path);
        await Releases.EnsurePackageAsync(new HttpClient(new FakeFiles(firstFiles)), dir.Path);

        var (second, secondFiles) = MakeRelease("2");
        await Releases.SyncAsync(new HttpClient(new FakeFiles(secondFiles)), second, dir.Path);
        Assert.Equal("numidium-2", Releases.ReadMirrored(dir.Path)?.Tag);
        Assert.Equal("2", ClientPackage.ReadInfo(Path.Combine(dir.Path, ClientPackage.InfoFile)).Version);
        Assert.False(File.Exists(Path.Combine(dir.Path, "numidium-client-1.zip")), "the old version's zip is gone");
    }

    // The real newest release on GitHub, when NUMIDIUM_RELEASE_TEST=1: found, mirrored,
    // and its zip downloaded and checked (about 150 MB)
    [Fact]
    public async Task MirrorsTheRealNewestRelease()
    {
        if (Environment.GetEnvironmentVariable("NUMIDIUM_RELEASE_TEST") != "1") return;
        using var dir = new TempDir();
        using var http = new HttpClient();
        var release = await Releases.FetchLatestAsync(http);
        Assert.NotNull(release);
        await Releases.SyncAsync(http, release!, dir.Path);
        await Releases.EnsurePackageAsync(http, dir.Path);
        var info = ClientPackage.ReadInfo(Path.Combine(dir.Path, ClientPackage.InfoFile));
        Assert.Equal(release!.Tag, "numidium-" + info.Version);
        Assert.True(Collection.Load(Path.Combine(dir.Path, Collection.FileName)).Mods.Count > 0);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "numidium-release-test.txt"), $"{release.Tag} {info.File} {info.Size}");
    }

    [Fact]
    public async Task RefusesADownloadThatDoesntMatchItsChecksum()
    {
        using var dir = new TempDir();
        var (release, files) = MakeRelease("1", zipOverride: Encoding.UTF8.GetBytes("not the zip"));
        var http = new HttpClient(new FakeFiles(files));
        await Releases.SyncAsync(http, release, dir.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => Releases.EnsurePackageAsync(http, dir.Path));
    }
}
