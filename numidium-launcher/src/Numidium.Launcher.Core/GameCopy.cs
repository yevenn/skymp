using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Numidium.Launcher.Core;

/// <summary>
/// Creates and refreshes the Numidium game folder: a base-game-only copy of
/// Skyrim 1.6.1170, so Numidium's mods never touch the player's Steam install (or
/// Keizaal, or any other modded setup), and players on a newer Steam version
/// still get the version Numidium is built for.
/// Every file is checked against <see cref="BaseGame.Files"/> by SHA-1. Files the
/// Steam install has in the right version are taken from it: Data files are
/// hard-linked when both folders are on the same drive, which takes no extra
/// space. The others come from Steam's depots (<see cref="SteamDepots"/>) and are
/// moved in with <see cref="InstallFromDepot"/>.
/// A hard link is the same file as Steam's: anything installing into the copy
/// must replace files (delete, then write) and never overwrite them in place,
/// or it would change the player's Steam install too.
/// </summary>
public sealed class GameCopy
{
    public const string MarkerFile = "numidium-game.json";

    /// <summary>Bytes checked or copied so far, of all the base game's.</summary>
    public sealed record Progress(long Done, long Total, string File);

    /// <summary><paramref name="Needed"/>: files neither the copy nor the Steam
    /// install has in the right version, to fetch from Steam's depots. Optional ones
    /// are listed only when the copy has none of its own.</summary>
    public sealed record Result(int Copied, int Linked, int Unchanged, long BytesWritten, List<BaseGame.Entry> Needed)
    {
        /// <summary>The depots to download: those with a file the game can't do without,
        /// each with every needed file it has.</summary>
        public List<(BaseGame.Depot Depot, List<BaseGame.Entry> Files)> Depots() =>
            Needed.Where(e => !e.Optional).Select(e => e.Depot).Distinct()
                .Select(id => (BaseGame.Depots.First(d => d.Id == id), Needed.Where(e => e.Depot == id).ToList()))
                .OrderBy(d => d.Item1.Size).ToList();
    }

    /// <summary>A file's SHA-1 as of its size and last write time: a file whose
    /// size and time haven't changed isn't hashed again.</summary>
    public sealed record Checked(long Size, long WriteTicks, string Sha1);

    private sealed class Marker
    {
        public string? Source { get; set; }
        public string? Version { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Dictionary<string, Checked> Checked { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public string Dir { get; }

    /// <summary>The files the copy is made of: <see cref="BaseGame.Files"/>, or a test's own.</summary>
    public IReadOnlyList<BaseGame.Entry> Files { get; }

    public GameCopy(string dir, IReadOnlyList<BaseGame.Entry>? files = null)
    {
        Dir = Path.GetFullPath(dir);
        Files = files ?? BaseGame.Files;
    }

    public bool Exists => File.Exists(MarkerPath);

    private string MarkerPath => Path.Combine(Dir, MarkerFile);

    /// <summary>
    /// Base game files the copy doesn't have in the right version, as far as the
    /// last check knows: a file changed since (by size or time) counts as missing
    /// until <see cref="Update"/> checks it again. Empty when the copy is usable.
    /// </summary>
    public List<string> MissingFiles()
    {
        var known = ReadMarker()?.Checked ?? new();
        return Files.Where(entry => !entry.Optional).Where(entry =>
        {
            var file = new FileInfo(Path.Combine(Dir, entry.Path));
            return !(file.Exists && known.TryGetValue(file.FullName, out var c) && IsCurrent(c, file) && c.Sha1 == entry.Sha1);
        }).Select(entry => entry.Path).ToList();
    }

    /// <summary>
    /// Checks the copy and takes what it lacks from <paramref name="source"/>, the
    /// Steam install, when Steam has it in the right version. Files the copy already
    /// has right are kept whatever Steam has, so a Steam update never changes the
    /// copy's version; a file in the wrong version is removed. Run it again after
    /// installing depots, or whenever: checked files aren't hashed twice.
    /// </summary>
    public Result Update(string source, IProgress<Progress>? progress = null, CancellationToken cancel = default)
    {
        source = Path.GetFullPath(source);
        if (string.Equals(source.TrimEnd('\\'), Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Numidium game folder can't be the Steam game folder.");
        if (!SteamSkyrim.IsGameDir(source))
            throw new DirectoryNotFoundException($"No Skyrim Special Edition in {source}");

        var marker = ReadMarker() ?? new Marker();
        var sameDrive = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(Dir), StringComparison.OrdinalIgnoreCase);
        var total = Files.Sum(f => f.Size);
        long done = 0;
        int copied = 0, linked = 0, unchanged = 0;
        long written = 0;
        var needed = new List<BaseGame.Entry>();
        foreach (var entry in Files)
        {
            cancel.ThrowIfCancellationRequested();
            var hashed = done;
            void Report(long bytes) => progress?.Report(new Progress(hashed + bytes, total, entry.Path));
            var to = new FileInfo(Path.Combine(Dir, entry.Path));
            var from = new FileInfo(Path.Combine(source, entry.Path));
            if (Matches(to, entry, marker.Checked, Report))
            {
                unchanged++;
            }
            else if (Matches(from, entry, marker.Checked, Report))
            {
                Directory.CreateDirectory(to.DirectoryName!);
                if (to.Exists) to.Delete(); // never write through an old hard link into Steam's file
                // Root files are small and include the exe, whose version the copy must keep
                if (sameDrive && entry.Path.StartsWith("Data" + Path.DirectorySeparatorChar) && TryHardLink(from.FullName, to.FullName))
                {
                    linked++;
                }
                else
                {
                    from.CopyTo(to.FullName);
                    File.SetLastWriteTimeUtc(to.FullName, from.LastWriteTimeUtc);
                    written += from.Length;
                    copied++;
                }
                Remember(marker.Checked, new FileInfo(to.FullName), entry.Sha1);
            }
            else if (!entry.Optional)
            {
                if (to.Exists) to.Delete(); // another version; a link to it costs Steam nothing
                needed.Add(entry);
            }
            else if (!to.Exists)
            {
                needed.Add(entry); // taken along if its depot is downloaded for other files
            }
            done = hashed + entry.Size;
        }
        progress?.Report(new Progress(total, total, ""));

        marker.Source = source;
        Save(marker);
        return new Result(copied, linked, unchanged, written, needed);
    }

    /// <summary>Whether <paramref name="depotDir"/>, a depot Steam downloaded,
    /// has all of <paramref name="entries"/> in the right version.</summary>
    public bool DepotHas(string depotDir, IEnumerable<BaseGame.Entry> entries, IProgress<Progress>? progress = null)
    {
        var marker = ReadMarker() ?? new Marker();
        var list = entries.ToList();
        var total = list.Sum(e => e.Size);
        long done = 0;
        var all = true;
        foreach (var entry in list)
        {
            var before = done;
            if (!Matches(new FileInfo(Path.Combine(depotDir, entry.Path)), entry, marker.Checked,
                    b => progress?.Report(new Progress(before + b, total, entry.Path))))
            {
                all = false;
                break;
            }
            done += entry.Size;
        }
        Save(marker);
        return all;
    }

    /// <summary>
    /// Moves <paramref name="entries"/> from <paramref name="depotDir"/> into the
    /// copy, checking each first. Moving rather than copying: the depot folder is
    /// Steam's download scratch space, and on the same drive a move is instant.
    /// </summary>
    public int InstallFromDepot(string depotDir, IEnumerable<BaseGame.Entry> entries, IProgress<Progress>? progress = null)
    {
        var marker = ReadMarker() ?? new Marker();
        var list = entries.ToList();
        var total = list.Sum(e => e.Size);
        long done = 0;
        foreach (var entry in list)
        {
            var from = new FileInfo(Path.Combine(depotDir, entry.Path));
            var before = done;
            if (!Matches(from, entry, marker.Checked, b => progress?.Report(new Progress(before + b, total, entry.Path))))
                throw new InvalidDataException($"{entry.Path} in {depotDir} isn't Skyrim 1.6.1170's; the download may be incomplete.");
            var to = Path.Combine(Dir, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from.FullName, to);
            marker.Checked.Remove(from.FullName);
            Remember(marker.Checked, new FileInfo(to), entry.Sha1);
            done += entry.Size;
            progress?.Report(new Progress(done, total, entry.Path));
        }
        Save(marker);
        return list.Count;
    }

    private static bool IsCurrent(Checked c, FileInfo file) =>
        c.Size == file.Length && c.WriteTicks == file.LastWriteTimeUtc.Ticks;

    private static void Remember(Dictionary<string, Checked> known, FileInfo file, string sha1) =>
        known[file.FullName] = new Checked(file.Length, file.LastWriteTimeUtc.Ticks, sha1);

    /// <summary>Whether <paramref name="file"/> is <paramref name="entry"/>'s version,
    /// hashing it only when its size is right and it changed since its last check.</summary>
    private static bool Matches(FileInfo file, BaseGame.Entry entry, Dictionary<string, Checked> known, Action<long>? progress)
    {
        file.Refresh();
        if (!file.Exists || file.Length != entry.Size) return false;
        if (!known.TryGetValue(file.FullName, out var c) || !IsCurrent(c, file))
        {
            c = new Checked(file.Length, file.LastWriteTimeUtc.Ticks, Sha1(file.FullName, progress));
            known[file.FullName] = c;
        }
        return c.Sha1 == entry.Sha1;
    }

    private static string Sha1(string path, Action<long>? progress)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buffer = new byte[4 << 20];
        long read = 0;
        int n;
        while ((n = stream.Read(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            read += n;
            progress?.Invoke(read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private Marker? ReadMarker()
    {
        if (!File.Exists(MarkerPath)) return null;
        try
        {
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath)) ?? new Marker();
            // Keys are paths, which Windows compares without case
            marker.Checked = new(marker.Checked ?? new(), StringComparer.OrdinalIgnoreCase);
            return marker;
        }
        catch (JsonException)
        {
            return new Marker(); // unreadable: everything gets checked again
        }
    }

    private void Save(Marker marker)
    {
        Directory.CreateDirectory(Dir);
        // Only what still exists: depot files moved away, copies replaced
        foreach (var path in marker.Checked.Keys.Where(p => !File.Exists(p)).ToList()) marker.Checked.Remove(path);
        marker.Version = SteamSkyrim.GetVersion(Dir)?.ToString() ?? "unknown";
        marker.UpdatedUtc = DateTime.UtcNow;
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool TryHardLink(string existing, string link)
    {
        if (CreateHardLink(link, existing, IntPtr.Zero)) return true;
        // Not NTFS, too many links, no permission: fall back to copying
        var error = Marshal.GetLastWin32Error();
        System.Diagnostics.Debug.WriteLine($"Hard link {link} failed: {new Win32Exception(error).Message}");
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
