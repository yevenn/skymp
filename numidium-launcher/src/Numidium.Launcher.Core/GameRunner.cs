using System.Diagnostics;

namespace Numidium.Launcher.Core;

/// <summary>Starts the game copy through SKSE with the server's load order and
/// waits for it to close, restoring the player's plugins.txt and
/// ContentCatalog.txt afterwards.</summary>
public static class GameRunner
{
    public static bool IsSkyrimRunning() => Process.GetProcessesByName("SkyrimSE").Length > 0;

    public static async Task RunAsync(string gameDir, IEnumerable<string> plugins, Action<string>? log = null,
        string? pluginsTxtPath = null, string? contentCatalogPath = null, CancellationToken cancel = default)
    {
        if (IsSkyrimRunning()) throw new InvalidOperationException("Skyrim is already running. Close it first.");
        var loader = Path.Combine(gameDir, "skse64_loader.exe");
        if (!File.Exists(loader)) throw new FileNotFoundException("SKSE is not installed in the game folder.", loader);

        using (PluginsTxt.Swap(plugins, pluginsTxtPath))
        using (ContentCatalog.SetAside(contentCatalogPath))
        {
            log?.Invoke("Starting Skyrim through SKSE...");
            Process.Start(new ProcessStartInfo(loader) { WorkingDirectory = gameDir, UseShellExecute = false });

            // skse64_loader exits as soon as it has started SkyrimSE.exe
            var game = await WaitForGameAsync(gameDir, TimeSpan.FromSeconds(60), cancel)
                ?? throw new TimeoutException("Skyrim did not start within a minute.");
            log?.Invoke("Skyrim is running.");
            await game.WaitForExitAsync(cancel);
            log?.Invoke("Skyrim closed.");
        }
    }

    private static async Task<Process?> WaitForGameAsync(string gameDir, TimeSpan timeout, CancellationToken cancel)
    {
        var exe = Path.GetFullPath(Path.Combine(gameDir, "SkyrimSE.exe"));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var process in Process.GetProcessesByName("SkyrimSE"))
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return process;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Not ours to inspect (another user's or elevated process)
                }
            }
            await Task.Delay(500, cancel);
        }
        return null;
    }
}
