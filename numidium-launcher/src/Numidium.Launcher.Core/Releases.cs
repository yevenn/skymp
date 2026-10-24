using System.Text.Json;
using System.Text.Json.Serialization;

namespace Numidium.Launcher.Core;

/// <summary>
/// Numidium's releases on GitHub (numidium-dev/release.ps1 makes them): each
/// carries the client package (numidium-client.json and its zip), the mod list
/// (collection.json, numidium-modlist.json) and the launcher itself. The launcher
/// mirrors the newest release's small files into a folder and downloads the zip
/// when it installs, so that folder works like a local package folder.
/// </summary>
public static class Releases
{
    public const string Repository = "yevenn/skymp";
    public const string LauncherAsset = "Numidium.Launcher.exe";
    public const string ReleaseFile = "release.json";

    /// <summary>Small files mirrored as soon as a release is seen.</summary>
    public static readonly string[] SmallAssets = { ClientPackage.InfoFile, Collection.FileName, ModList.FileName };

    public sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("size")] long Size);

    public sealed record Release(
        [property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("html_url")] string PageUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<Asset> Assets)
    {
        public Asset? Find(string name) => Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The newest published release with a client package in GitHub's
    /// release list (newest first), or null. The fork's other releases, if any, lack one.</summary>
    public static Release? PickLatest(string releasesJson) =>
        (JsonSerializer.Deserialize<List<Release>>(releasesJson) ?? new())
        .FirstOrDefault(r => !r.Draft && !r.Prerelease && r.Find(ClientPackage.InfoFile) != null);

    public static async Task<Release?> FetchLatestAsync(HttpClient http, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd("Numidium-Launcher");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        return PickLatest(await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>The release last mirrored into <paramref name="dir"/>, or null.</summary>
    public static Release? ReadMirrored(string dir)
    {
        var path = Path.Combine(dir, ReleaseFile);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Release>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mirrors <paramref name="release"/>'s small files into <paramref name="dir"/>,
    /// replacing an older release's (a file the new one lacks is removed). Zips of
    /// other versions are removed too; this version's is downloaded by <see cref="EnsurePackageAsync"/>.
    /// </summary>
    public static async Task SyncAsync(HttpClient http, Release release, string dir, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(dir);
        if (ReadMirrored(dir)?.Tag == release.Tag && SmallAssets.All(n => release.Find(n) == null || File.Exists(Path.Combine(dir, n))))
            return;
        foreach (var name in SmallAssets)
        {
            var target = Path.Combine(dir, name);
            if (release.Find(name) is Asset asset) await DownloadAsync(http, asset, target, null, cancel);
            else if (File.Exists(target)) File.Delete(target);
        }
        var info = ClientPackage.ReadInfo(Path.Combine(dir, ClientPackage.InfoFile));
        foreach (var zip in Directory.EnumerateFiles(dir, "numidium-client-*.zip"))
            if (!Path.GetFileName(zip).Equals(info.File, StringComparison.OrdinalIgnoreCase)) File.Delete(zip);
        File.WriteAllText(Path.Combine(dir, ReleaseFile), JsonSerializer.Serialize(release, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Downloads the client package zip of the release mirrored in
    /// <paramref name="dir"/>, unless it's already there and checks out.</summary>
    public static async Task EnsurePackageAsync(HttpClient http, string dir, IProgress<(long Done, long Total)>? progress = null,
        CancellationToken cancel = default)
    {
        var info = ClientPackage.ReadInfo(Path.Combine(dir, ClientPackage.InfoFile));
        var zip = FileInstall.SafePath(dir, info.File);
        if (File.Exists(zip))
        {
            try
            {
                ClientPackage.Verify(info, zip);
                return;
            }
            catch (InvalidDataException)
            {
                File.Delete(zip);
            }
        }
        var release = ReadMirrored(dir) ?? throw new InvalidOperationException($"{dir} isn't a mirrored release.");
        var asset = release.Find(info.File) ?? throw new InvalidDataException($"Release {release.Tag} has no {info.File}.");
        await DownloadAsync(http, asset, zip, progress, cancel);
        ClientPackage.Verify(info, zip);
    }

    private static async Task DownloadAsync(HttpClient http, Asset asset, string target, IProgress<(long Done, long Total)>? progress,
        CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.UserAgent.ParseAdd("Numidium-Launcher");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var part = target + ".part";
        await using (var input = await response.Content.ReadAsStreamAsync(cancel))
        await using (var output = File.Create(part))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, cancel)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), cancel);
                done += n;
                progress?.Report((done, asset.Size));
            }
        }
        File.Move(part, target, overwrite: true);
    }
}
