using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Numidium.Launcher.Core;

/// <summary>
/// Creates and refreshes the Numidium game folder: a base-game-only copy of the
/// player's Steam install, so Numidium's mods never touch it (or Keizaal, or
/// any other modded setup). Data files are hard-linked when both folders are on
/// the same drive, which takes no extra space.
/// A hard link is the same file as Steam's: anything installing into the copy
/// must replace files (delete, then write) and never overwrite them in place,
/// or it would change the player's Steam install too. Steam's updater replaces
/// files, which leaves the copy's links on the old ones until Update refreshes them.
/// </summary>
public sealed class GameCopy
{
    public const string MarkerFile = "numidium-game.json";

    public sealed record Progress(int Done, int Total, string File, long Bytes);

    public sealed record Result(int Copied, int Linked, int Unchanged, long BytesWritten);

    private sealed record Marker(string Source, string Version, DateTime UpdatedUtc);

    public string Dir { get; }

    public GameCopy(string dir) => Dir = Path.GetFullPath(dir);

    public bool Exists => File.Exists(Path.Combine(Dir, MarkerFile));

    /// <summary>Base game files the copy lacks; empty when it is usable.</summary>
    public List<string> MissingFiles() =>
        BaseGame.RequiredFiles().Where(f => !File.Exists(Path.Combine(Dir, f))).ToList();

    /// <summary>
    /// Copies the base game from <paramref name="source"/>. Files already matching
    /// in size and timestamp are kept, so running it again after a Steam update
    /// only refreshes what changed. Never deletes anything in the copy.
    /// </summary>
    public Result Update(string source, IProgress<Progress>? progress = null, CancellationToken cancel = default)
    {
        source = Path.GetFullPath(source);
        if (string.Equals(source.TrimEnd('\\'), Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Numidium game folder can't be the Steam game folder.");
        if (!SteamSkyrim.IsGameDir(source))
            throw new DirectoryNotFoundException($"No Skyrim Special Edition in {source}");

        var files = BaseGame.ListFiles(source);
        var sameDrive = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(Dir), StringComparison.OrdinalIgnoreCase);
        int copied = 0, linked = 0, unchanged = 0, done = 0;
        long written = 0;
        foreach (var rel in files)
        {
            cancel.ThrowIfCancellationRequested();
            var from = new FileInfo(Path.Combine(source, rel));
            var to = new FileInfo(Path.Combine(Dir, rel));
            progress?.Report(new Progress(done, files.Count, rel, from.Length));
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc)
            {
                unchanged++;
            }
            else
            {
                Directory.CreateDirectory(to.DirectoryName!);
                if (to.Exists) to.Delete(); // never write through an old hard link into Steam's file
                // Root files are small and include the exe, whose version the copy must keep
                if (sameDrive && rel.StartsWith("Data" + Path.DirectorySeparatorChar) && TryHardLink(from.FullName, to.FullName))
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
            }
            done++;
        }
        progress?.Report(new Progress(done, files.Count, "", 0));

        var marker = new Marker(source, SteamSkyrim.GetVersion(Dir)?.ToString() ?? "unknown", DateTime.UtcNow);
        File.WriteAllText(Path.Combine(Dir, MarkerFile), JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
        return new Result(copied, linked, unchanged, written);
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
