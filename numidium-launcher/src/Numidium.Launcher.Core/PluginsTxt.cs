using System.Text;

namespace Numidium.Launcher.Core;

/// <summary>
/// Skyrim reads its load order from one plugins.txt in %LOCALAPPDATA% that every
/// install on the machine shares. Numidium swaps in its own list while the game
/// runs and puts the player's back afterwards.
/// </summary>
public static class PluginsTxt
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Skyrim Special Edition", "plugins.txt");

    /// <summary>The plugins.txt text enabling <paramref name="plugins"/> in order. The
    /// base masters load implicitly and are left out.</summary>
    public static string Format(IEnumerable<string> plugins)
    {
        var sb = new StringBuilder("# Numidium load order, written by the launcher\r\n");
        foreach (var plugin in plugins)
        {
            if (BaseGame.Masters.Contains(plugin, StringComparer.OrdinalIgnoreCase)) continue;
            sb.Append('*').Append(plugin).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes the Numidium list and returns a handle that restores the previous
    /// file (or removes ours, if there was none) when disposed.
    /// </summary>
    public static IDisposable Swap(IEnumerable<string> plugins, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = path + ".numidium-backup";
        // A leftover backup means the last session never restored it: that one is the player's
        if (!File.Exists(backup) && File.Exists(path)) File.Copy(path, backup);
        File.WriteAllText(path, Format(plugins), Encoding.ASCII);
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
