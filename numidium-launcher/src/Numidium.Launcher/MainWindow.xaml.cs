using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Numidium.Launcher.Core;

namespace Numidium.Launcher;

public partial class MainWindow : Window
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x8F, 0xBF, 0x7A));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x6A));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0x95, 0x8A));

    private readonly LauncherSettings settings = LauncherSettings.Load();
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        settings.SteamGameDir ??= SteamSkyrim.Find();
        settings.GameDir ??= LauncherSettings.SuggestGameDir(settings.SteamGameDir);
        SteamDirBox.Text = settings.SteamGameDir ?? "";
        GameDirBox.Text = settings.GameDir;
        HostBox.Text = settings.ServerHost;
        PortBox.Text = settings.ServerPort.ToString();
        ProfileBox.Text = settings.ProfileId?.ToString() ?? "";
        PackageDirBox.Text = settings.ClientPackageDir ?? "";
        RefreshStatus();
    }

    private string? PackageInfoPath =>
        settings.ClientPackageDir is string dir && File.Exists(Path.Combine(dir, ClientPackage.InfoFile))
            ? Path.Combine(dir, ClientPackage.InfoFile)
            : null;

    private void RefreshStatus()
    {
        var steam = settings.SteamGameDir;
        if (!SteamSkyrim.IsGameDir(steam))
        {
            SetStatus(SteamStatus, Bad, "Skyrim Special Edition wasn't found. Use Browse to pick its Steam folder.");
        }
        else
        {
            var version = SteamSkyrim.GetVersion(steam!);
            var where = SteamSkyrim.IsInSteamLibrary(steam!)
                ? ""
                : " This isn't in a Steam library: it is only used to copy the base game from, so pick Steam's own folder.";
            if (version == SteamSkyrim.SupportedVersion)
                SetStatus(SteamStatus, where.Length == 0 ? Good : Bad, $"Version {version}, supported.{where}");
            else
                SetStatus(SteamStatus, Bad, $"Version {version}. Numidium needs {SteamSkyrim.SupportedVersion}; downgrade the game first.{where}");
        }

        var copy = new GameCopy(settings.GameDir!);
        if (!copy.Exists)
        {
            SetStatus(GameStatus, Muted, "Not set up yet. Set up copies the base game here; your Steam folder is left as it is.");
            CopyButton.Content = "Set up";
        }
        else
        {
            var missing = copy.MissingFiles();
            if (missing.Count > 0) SetStatus(GameStatus, Bad, $"Incomplete, missing {string.Join(", ", missing)}.");
            else SetStatus(GameStatus, Good, $"Ready, version {SteamSkyrim.GetVersion(copy.Dir)}.");
            CopyButton.Content = "Update";
        }

        var installed = ClientPackage.ReadInstalled(copy.Dir);
        var available = PackageInfoPath is string info ? ClientPackage.ReadInfo(info) : null;
        var skse = Skse.IsInstalled(copy.Dir) ? "SKSE installed" : "SKSE not installed";
        if (available == null)
            SetStatus(PackageStatus, installed == null ? Bad : Muted,
                $"{skse}. Client {installed?.Version ?? "not installed"}; pick the folder with numidium-client.json to install or update it.");
        else if (installed?.Version == available.Version)
            SetStatus(PackageStatus, Skse.IsInstalled(copy.Dir) ? Good : Bad, $"{skse}. Client {installed.Version}, up to date.");
        else
            SetStatus(PackageStatus, Muted, $"{skse}. Client {installed?.Version ?? "not installed"}; {available.Version} is installed by {CopyButton.Content}.");

        PlayButton.IsEnabled = !busy;
        CopyButton.IsEnabled = !busy;
    }

    private static void SetStatus(System.Windows.Controls.TextBlock block, Brush brush, string text)
    {
        block.Foreground = brush;
        block.Text = text;
    }

    private void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\n");
        LogBox.ScrollToEnd();
    }

    private void BrowseSteam_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Skyrim Special Edition's Steam folder" };
        if (dialog.ShowDialog(this) != true) return;
        settings.SteamGameDir = dialog.FolderName;
        SteamDirBox.Text = dialog.FolderName;
        settings.Save();
        RefreshStatus();
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Folder for the Numidium game copy" };
        if (dialog.ShowDialog(this) != true) return;
        GameDirBox.Text = dialog.FolderName;
        GameDirBox_LostFocus(sender, e);
    }

    private void BrowsePackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Folder with numidium-client.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(dialog.FolderName, ClientPackage.InfoFile)))
        {
            Log($"{dialog.FolderName} has no {ClientPackage.InfoFile}.");
            return;
        }
        settings.ClientPackageDir = dialog.FolderName;
        PackageDirBox.Text = dialog.FolderName;
        settings.Save();
        RefreshStatus();
    }

    private void GameDirBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GameDirBox.Text)) return;
        settings.GameDir = GameDirBox.Text.Trim();
        settings.Save();
        RefreshStatus();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!SteamSkyrim.IsGameDir(settings.SteamGameDir))
        {
            Log("Pick the Steam folder of Skyrim Special Edition first.");
            return;
        }
        busy = true;
        RefreshStatus();
        CopyProgress.Visibility = Visibility.Visible;
        try
        {
            var copy = new GameCopy(settings.GameDir!);
            var source = settings.SteamGameDir!;
            Log($"Copying the base game from {source} to {copy.Dir}...");
            var progress = new Progress<GameCopy.Progress>(p =>
            {
                CopyProgress.Maximum = Math.Max(1, p.Total);
                CopyProgress.Value = p.Done;
            });
            var result = await Task.Run(() => copy.Update(source, progress));
            Log($"Base game: {result.Copied} copied ({result.BytesWritten / (1024 * 1024)} MB), " +
                $"{result.Linked} hard-linked, {result.Unchanged} already up to date.");

            if (!Skse.IsInstalled(copy.Dir))
            {
                Log($"Downloading SKSE from {Skse.Url}...");
                using var http = new HttpClient();
                var archive = await Skse.DownloadAsync(LauncherSettings.CacheDir, http);
                var files = await Task.Run(() => Skse.Install(copy.Dir, archive));
                Log($"SKSE installed ({files.Count} files).");
            }

            if (PackageInfoPath is string info)
            {
                var available = ClientPackage.ReadInfo(info);
                if (ClientPackage.ReadInstalled(copy.Dir)?.Version != available.Version)
                {
                    Log($"Installing the Numidium client {available.Version}...");
                    var packageProgress = new Progress<(int Done, int Total)>(p =>
                    {
                        CopyProgress.Maximum = Math.Max(1, p.Total);
                        CopyProgress.Value = p.Done;
                    });
                    var installed = await Task.Run(() => ClientPackage.Install(copy.Dir, info, packageProgress));
                    Log($"Numidium client {installed.Version} installed ({installed.Files.Count} files).");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Setting up failed: {ex.Message}");
        }
        finally
        {
            busy = false;
            CopyProgress.Visibility = Visibility.Collapsed;
            RefreshStatus();
        }
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadServerFields(out var host, out var port, out var profileId)) return;
        var copy = new GameCopy(settings.GameDir!);
        if (!copy.Exists || copy.MissingFiles().Count > 0)
        {
            Log("Set up the Numidium game folder first.");
            return;
        }
        busy = true;
        RefreshStatus();
        try
        {
            Log($"Asking {host} for its load order...");
            var manifest = await ServerManifest.FetchAsync(host);
            if (manifest.HasNoModsIn(copy.Dir))
            {
                Log($"{copy.Dir} has the base game but none of the server's {manifest.Plugins.Count} plugins: " +
                    "Numidium's mods aren't installed there. The launcher can't install them yet; " +
                    "to test, set the game folder to one that has them.");
                return;
            }
            var problems = manifest.Mismatches(copy.Dir);
            if (problems.Count > 0)
            {
                Log("The game folder doesn't match the server's mods:");
                foreach (var problem in problems.Take(15)) Log("  " + problem);
                if (problems.Count > 15) Log($"  ...and {problems.Count - 15} more");
                return;
            }
            ClientSettings.Write(copy.Dir, host, port, profileId);
            Log($"{manifest.Plugins.Count} plugins match. Connecting to {host}:{port}.");
            await GameRunner.RunAsync(copy.Dir, manifest.Plugins.Select(p => p.FileName), Log);
        }
        catch (HttpRequestException ex)
        {
            Log($"Couldn't reach the server: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log(ex.Message);
        }
        finally
        {
            busy = false;
            RefreshStatus();
        }
    }

    private bool ReadServerFields(out string host, out int port, out int? profileId)
    {
        host = HostBox.Text.Trim();
        profileId = null;
        if (host.Length == 0 || !int.TryParse(PortBox.Text.Trim(), out port) || port is <= 0 or > 65535)
        {
            Log("Enter a server address and a port between 1 and 65535.");
            port = 0;
            return false;
        }
        if (ProfileBox.Text.Trim().Length > 0)
        {
            if (!int.TryParse(ProfileBox.Text.Trim(), out var id))
            {
                Log("The profile has to be a number, or empty.");
                return false;
            }
            profileId = id;
        }
        settings.ServerHost = host;
        settings.ServerPort = port;
        settings.ProfileId = profileId;
        settings.Save();
        return true;
    }
}
