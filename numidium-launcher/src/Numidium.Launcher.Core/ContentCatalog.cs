namespace Numidium.Launcher.Core;

/// <summary>
/// Skyrim keeps a list of Creation Club content in ContentCatalog.txt, next to
/// plugins.txt in %LOCALAPPDATA%, shared by every install. One written by a newer
/// game (1.7) crashes 1.6.1170 at start, and Steam's game rewrites it whenever
/// the player runs it, so the launcher moves the player's file aside while
/// Numidium runs and puts it back afterwards, dropping the one Numidium's game made.
/// </summary>
public static class ContentCatalog
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Skyrim Special Edition", "ContentCatalog.txt");

    public static IDisposable SetAside(string? path = null)
    {
        path ??= DefaultPath;
        var backup = path + ".numidium-backup";
        // A leftover backup means the last session never restored it: that one is the player's
        if (File.Exists(backup)) File.Delete(path);
        else if (File.Exists(path)) File.Move(path, backup);
        return new Restore(path, backup);
    }

    private sealed class Restore(string path, string backup) : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (done) return;
            done = true;
            if (File.Exists(backup)) File.Move(backup, path, overwrite: true);
            else File.Delete(path);
        }
    }
}
