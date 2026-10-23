using System.Security.Cryptography;
using SharpCompress.Archives;

namespace Numidium.Launcher.Core;

/// <summary>
/// The Skyrim Script Extender, which SkyrimPlatform runs on. Its licence doesn't
/// allow repackaging, so the launcher downloads the official archive and checks
/// it against a pinned checksum before installing.
/// </summary>
public static class Skse
{
    /// <summary>SKSE 2.2.6, the last build for game version 1.6.1170 (Steam).</summary>
    public const string Url = "https://skse.silverlock.org/download/archive/skse64_2_02_06.7z";
    /// <summary>Of skse64_2_02_06.7z (751,136 bytes, 2024-01-17); its DLL and loader match the
    /// SKSE 2.2.6 distributed on Nexus.</summary>
    public const string Sha256 = "d7297f1a1d613e5265e1af4dbbfe8bd37a32719c1ccef363fc6187fa6eba0848";
    public const string Loader = "skse64_loader.exe";
    public const string RuntimeDll = "skse64_1_6_1170.dll";

    public static bool IsInstalled(string gameDir) =>
        File.Exists(Path.Combine(gameDir, Loader)) && File.Exists(Path.Combine(gameDir, RuntimeDll));

    /// <summary>Downloads the archive to <paramref name="cacheDir"/> (reusing a good copy)
    /// and returns its path.</summary>
    public static async Task<string> DownloadAsync(string cacheDir, HttpClient http, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, Path.GetFileName(new Uri(Url).LocalPath));
        if (File.Exists(path) && HashOf(path) == Sha256) return path;
        var temp = path + ".part";
        using (var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            await using var output = File.Create(temp);
            await response.Content.CopyToAsync(output, cancel);
        }
        if (HashOf(temp) != Sha256)
        {
            File.Delete(temp);
            throw new InvalidDataException("The SKSE download doesn't match its checksum; it may have been changed or damaged.");
        }
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Installs from the official archive: the loader and runtime into the game
    /// folder, the scripts into Data\Scripts. Readmes and source code stay out.
    /// Returns the files written, relative to the game folder.
    /// </summary>
    public static List<string> Install(string gameDir, string archivePath)
    {
        var written = new List<string>();
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key != null))
        {
            var target = TargetFor(entry.Key!);
            if (target == null) continue;
            var full = FileInstall.SafePath(gameDir, target);
            FileInstall.Replace(full, output =>
            {
                using var input = entry.OpenEntryStream();
                input.CopyTo(output);
            });
            written.Add(target);
        }
        if (!written.Contains(Loader, StringComparer.OrdinalIgnoreCase) || !written.Contains(RuntimeDll, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(archivePath)} doesn't contain {Loader} and {RuntimeDll}");
        return written;
    }

    /// <summary>Where an archive entry goes in the game folder, or null to skip it.
    /// Entries sit under a top folder named after the release (skse64_2_02_06\...).</summary>
    public static string? TargetFor(string key)
    {
        var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var rest = parts[1..];
        if (rest.Length == 1 && (rest[0].Equals(Loader, StringComparison.OrdinalIgnoreCase) ||
                                 rest[0].Equals(RuntimeDll, StringComparison.OrdinalIgnoreCase)))
            return rest[0];
        if (rest.Length == 3 && rest[0].Equals("Data", StringComparison.OrdinalIgnoreCase) &&
            rest[1].Equals("Scripts", StringComparison.OrdinalIgnoreCase) &&
            rest[2].EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
            return Path.Combine("Data", "Scripts", rest[2]);
        return null;
    }

    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
