using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Numidium.Launcher.Core;

/// <summary>
/// Downloads Skyrim 1.6.1170's depots through the player's own Steam client, with
/// its console command download_depot, as Keizaal's launcher does: Steam checks
/// that the player owns the game and downloads from Valve, so the launcher never
/// handles a Steam sign-in and nobody redistributes Bethesda's files. Steam puts
/// the files in steamapps\content\app_489830\depot_&lt;id&gt; under its own folder and
/// leaves the installed game as it is.
/// </summary>
public static partial class SteamDepots
{
    public sealed record Status(long Bytes, long Total, string Message);

    /// <summary>How long Steam may take to start a download (signing in included).</summary>
    public static TimeSpan StartTimeout = TimeSpan.FromMinutes(15);

    /// <summary>How long a started download may go without growing.</summary>
    public static TimeSpan StallTimeout = TimeSpan.FromMinutes(30);

    public static string ContentDir(string steamRoot, string depotId) =>
        Path.Combine(steamRoot, "steamapps", "content", $"app_{SteamSkyrim.AppId}", $"depot_{depotId}");

    public static string ConsoleLog(string steamRoot) => Path.Combine(steamRoot, "logs", "console_log.txt");

    /// <summary>The console command, for players to run themselves if they have to.</summary>
    public static string Command(BaseGame.Depot depot) => $"download_depot {SteamSkyrim.AppId} {depot.Id} {depot.Manifest}";

    public sealed record Outcome(bool Failed, string? Path, string Line);

    /// <summary>
    /// What Steam's console log says about a depot download, from lines such as
    /// <c>Depot download complete : "C:\...\depot_489833" (1 files, manifest 1914580699073641964)</c>
    /// or <c>Depot download failed : Access denied</c>; null while it says nothing.
    /// Failures don't name the depot, so any after the download started counts.
    /// </summary>
    public static Outcome? ReadOutcome(string log, string depotId)
    {
        foreach (var line in log.Split('\n'))
        {
            var complete = CompleteLine().Match(line);
            if (complete.Success && complete.Groups["path"].Value.EndsWith($"depot_{depotId}", StringComparison.OrdinalIgnoreCase))
                return new Outcome(false, complete.Groups["path"].Value, line.Trim());
            if (line.Contains("Depot download failed", StringComparison.OrdinalIgnoreCase))
                return new Outcome(true, null, line.Trim());
        }
        return null;
    }

    [GeneratedRegex("Depot download complete\\s*:\\s*\"(?<path>[^\"]+?)\\\\?\"")]
    private static partial Regex CompleteLine();

    /// <summary>
    /// Asks Steam for <paramref name="depot"/> and waits until it is downloaded,
    /// returning the folder it is in. A leftover folder from an earlier attempt is
    /// removed first; callers check one that may already be complete before this.
    /// <paramref name="isComplete"/> tells whether the needed files are all there,
    /// for when Steam's log doesn't say (it is checked once the folder stops growing).
    /// </summary>
    public static async Task<string> DownloadAsync(string steamRoot, BaseGame.Depot depot, Func<string, bool> isComplete,
        IProgress<Status>? progress = null, CancellationToken cancel = default)
    {
        var steamExe = Path.Combine(steamRoot, "steam.exe");
        if (!File.Exists(steamExe)) throw new FileNotFoundException($"Steam isn't in {steamRoot}.", steamExe);
        var dir = ContentDir(steamRoot, depot.Id);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(steamRoot))!).AvailableFreeSpace;
        var room = depot.Size + (1L << 30) - (Directory.Exists(dir) ? DirectorySize(dir) : 0);
        if (free < room)
            throw new IOException($"Steam downloads into {Path.GetPathRoot(steamRoot)}, which has {Gb(free)} free; " +
                                  $"{depot.Contents} need {Gb(room)}.");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        var log = ConsoleLog(steamRoot);
        var logStart = File.Exists(log) ? new FileInfo(log).Length : 0;

        progress?.Report(new Status(0, depot.Size, "Opening Steam's console..."));
        // download_depot only runs with the console open; Steam passes both on to a running client
        Process.Start(new ProcessStartInfo("steam://open/console") { UseShellExecute = true });
        await Task.Delay(TimeSpan.FromSeconds(5), cancel);
        var start = new ProcessStartInfo(steamExe) { UseShellExecute = false };
        foreach (var arg in new[] { "-console", "+download_depot", SteamSkyrim.AppId, depot.Id, depot.Manifest })
            start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();

        var started = DateTime.UtcNow;
        var lastGrowth = DateTime.UtcNow;
        long lastSize = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancel);
            var outcome = ReadOutcome(ReadFrom(log, ref logStart), depot.Id);
            if (outcome is { Failed: true })
                throw new InvalidOperationException($"Steam couldn't download {depot.Contents}: {outcome.Line}");
            if (outcome != null)
            {
                var done = Directory.Exists(outcome.Path) ? outcome.Path! : dir;
                progress?.Report(new Status(depot.Size, depot.Size, $"Steam downloaded {depot.Contents}."));
                return done;
            }

            var size = Directory.Exists(dir) ? DirectorySize(dir) : 0;
            if (size > lastSize)
            {
                lastSize = size;
                lastGrowth = DateTime.UtcNow;
            }
            if (size == 0)
            {
                progress?.Report(new Status(0, depot.Size, $"Waiting for Steam to start downloading {depot.Contents}..."));
                if (DateTime.UtcNow - started > StartTimeout)
                    throw new TimeoutException($"Steam didn't start downloading {depot.Contents}. Make sure you're signed in to Steam " +
                                               $"and own Skyrim Special Edition. You can also type \"{Command(depot)}\" in Steam's " +
                                               "console (Win+R, steam://open/console) and click Update when it's done.");
                continue;
            }
            progress?.Report(new Status(Math.Min(size, depot.Size), depot.Size, $"Steam is downloading {depot.Contents}..."));
            var quiet = DateTime.UtcNow - lastGrowth;
            if (quiet > TimeSpan.FromMinutes(2) && isComplete(dir)) return dir;
            if (quiet > StallTimeout)
                throw new TimeoutException($"Steam's download of {depot.Contents} stopped at {Gb(size)} of {Gb(depot.Size)}. " +
                                           "Check Steam's console, then click Update to try again.");
        }
    }

    /// <summary>Removes what's left of a depot after its files were moved out
    /// (Creation Club files the copy doesn't use), to give the space back.</summary>
    public static void Remove(string depotDir)
    {
        if (Directory.Exists(depotDir)) Directory.Delete(depotDir, recursive: true);
        // And app_489830 once no depot is left in it
        var app = Path.GetDirectoryName(Path.GetFullPath(depotDir).TrimEnd('\\'));
        if (app != null && Directory.Exists(app) && !Directory.EnumerateFileSystemEntries(app).Any()) Directory.Delete(app);
    }

    /// <summary>The log's text after <paramref name="offset"/>, moving it on. Steam
    /// starts a new file now and then, which shows as a shorter one.</summary>
    private static string ReadFrom(string path, ref long offset)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < offset) offset = 0;
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        // Keep a partly written last line for the next read
        var end = text.LastIndexOf('\n') + 1;
        offset += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, end));
        return text[..end];
    }

    private static long DirectorySize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch (IOException) { return 0; } });

    private static string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";
}
