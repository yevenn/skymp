using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Numidium.Launcher.Core;

/// <summary>
/// Numidium's own client files (SkyrimPlatform, the SkyMP client, our SKSE
/// plugins and scripts), as built by numidium-dev/package-client.ps1: a zip and
/// numidium-client.json describing it. Installing records which files it wrote,
/// so an update can remove the ones a newer version no longer has.
/// </summary>
public static class ClientPackage
{
    public const string InfoFile = "numidium-client.json";
    public const string InstalledFile = "numidium-client-installed.json";

    public sealed record Info(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha256")] string Sha256);

    public sealed record Installed(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("files")] List<string> Files);

    public static Info ReadInfo(string infoPath) =>
        JsonSerializer.Deserialize<Info>(File.ReadAllText(infoPath)) ?? throw new InvalidDataException($"{infoPath} is empty");

    public static Installed? ReadInstalled(string gameDir)
    {
        var path = Path.Combine(gameDir, InstalledFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<Installed>(File.ReadAllText(path)) : null;
    }

    /// <summary>Checks the zip against its info: size first, then SHA-256.</summary>
    public static void Verify(Info info, string zipPath)
    {
        var size = new FileInfo(zipPath).Length;
        if (size != info.Size)
            throw new InvalidDataException($"{Path.GetFileName(zipPath)} is {size} bytes, expected {info.Size}: incomplete or a different version");
        using var stream = File.OpenRead(zipPath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (hash != info.Sha256.ToLowerInvariant())
            throw new InvalidDataException($"{Path.GetFileName(zipPath)} doesn't match its checksum: download it again");
    }

    /// <summary>Installs the package described by <paramref name="infoPath"/> (the zip
    /// sits next to it) into the game copy.</summary>
    public static Installed Install(string gameDir, string infoPath, IProgress<(int Done, int Total)>? progress = null)
    {
        var info = ReadInfo(infoPath);
        var zipPath = FileInstall.SafePath(Path.GetDirectoryName(Path.GetFullPath(infoPath))!, info.File);
        Verify(info, zipPath);
        var previous = ReadInstalled(gameDir);

        var written = new List<string>();
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList(); // skip folder entries
            foreach (var entry in entries)
            {
                var target = FileInstall.SafePath(gameDir, entry.FullName);
                FileInstall.Replace(target, output =>
                {
                    using var input = entry.Open();
                    input.CopyTo(output);
                });
                written.Add(Path.GetRelativePath(gameDir, target));
                progress?.Report((written.Count, entries.Count));
            }
        }

        // Files the previous version installed that this one doesn't have
        if (previous != null)
        {
            var current = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase);
            foreach (var old in previous.Files.Where(f => !current.Contains(f)))
            {
                var path = FileInstall.SafePath(gameDir, old);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        var installed = new Installed(info.Version, written);
        File.WriteAllText(Path.Combine(gameDir, InstalledFile), JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true }));
        return installed;
    }
}
