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
        settings.ClientPackageDir ??= LauncherSettings.BundledPackageDir();
        SteamDirBox.Text = settings.SteamGameDir ?? "";
        GameDirBox.Text = settings.GameDir;
        HostBox.Text = settings.ServerHost;
        PortBox.Text = settings.ServerPort.ToString();
        ProfileBox.Text = settings.ProfileId?.ToString() ?? "";
        PackageDirBox.Text = settings.ClientPackageDir ?? "";
        DownloadsDirBox.Text = DownloadsDir;
        RefreshStatus();
        Loaded += async (_, _) => await RefreshModsAsync();
    }

    private Collection? collection;
    private ModList modList = new();
    private readonly Dictionary<string, string> hashCache = new();
    private List<ModRow> modRows = new();

    private string DownloadsDir => settings.DownloadsDir ?? Downloads.DefaultFolder;

    private string? CollectionPath =>
        settings.ClientPackageDir is string dir && File.Exists(Path.Combine(dir, Collection.FileName))
            ? Path.Combine(dir, Collection.FileName)
            : null;

    /// <summary>Reloads the collection and modlist from the client package folder and
    /// rescans the Downloads folder (hashing only files of a wanted size).</summary>
    private async Task RefreshModsAsync()
    {
        try
        {
            if (CollectionPath is not string path)
            {
                collection = null;
                modRows = new();
                ModsList.ItemsSource = null;
                ModsHeading.Text = "MODS";
                ModsStatus.Text = $"The client package folder has no {Collection.FileName} yet.";
                InstallModsButton.IsEnabled = false;
                return;
            }
            collection = Collection.Load(path);
            modList = ModList.Load(Path.Combine(Path.GetDirectoryName(path)!, ModList.FileName));
            ModsStatus.Text = "Looking for downloads...";
            var mods = collection.Mods;
            var folders = new[] { DownloadsDir, LauncherSettings.CacheDir };
            var found = await Task.Run(() => Downloads.Find(mods, folders, hashCache));
            ShowMods(found);
        }
        catch (Exception ex)
        {
            ModsStatus.Text = $"Couldn't read the collection: {ex.Message}";
        }
    }

    private void ShowMods(Dictionary<Collection.Mod, string> found)
    {
        if (collection == null) return;
        var records = ModInstaller.ReadRecords(settings.GameDir!);
        modRows = collection.Mods.Select(mod =>
        {
            if (modList.RuleFor(mod)?.Skip == true) return new ModRow(mod, ModRow.State.ByLauncher, null);
            if (records.ContainsKey(mod.FileId)) return new ModRow(mod, ModRow.State.Installed, null);
            return found.TryGetValue(mod, out var archive)
                ? new ModRow(mod, ModRow.State.Downloaded, archive)
                : new ModRow(mod, ModRow.State.Missing, null);
        }).ToList();
        ModsList.ItemsSource = modRows;
        ModsHeading.Text = $"MODS · {collection.Name.ToUpperInvariant()}";
        int Count(ModRow.State s) => modRows.Count(r => r.Status_ == s);
        var missing = modRows.Count(r => r.Status_ == ModRow.State.Missing && !r.Mod.Optional);
        ModsStatus.Text = $"{Count(ModRow.State.Installed)} installed, {Count(ModRow.State.Downloaded)} ready to install, " +
                          (missing > 0 ? $"{missing} still to download." : "nothing left to download.");
        InstallModsButton.IsEnabled = !busy && (Count(ModRow.State.Downloaded) > 0);
    }

    private async void RefreshMods_Click(object sender, RoutedEventArgs e) => await RefreshModsAsync();

    private async void BrowseDownloads_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Where your browser saves downloads" };
        if (dialog.ShowDialog(this) != true) return;
        settings.DownloadsDir = dialog.FolderName;
        DownloadsDirBox.Text = dialog.FolderName;
        settings.Save();
        await RefreshModsAsync();
    }

    private void OpenOnNexus_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url && url.StartsWith("https://www.nexusmods.com/"))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void InstallMods_Click(object sender, RoutedEventArgs e)
    {
        if (collection == null) return;
        var copy = new GameCopy(settings.GameDir!);
        if (!copy.Exists)
        {
            Log("Set up the Numidium game folder first.");
            return;
        }
        busy = true;
        RefreshStatus();
        InstallModsButton.IsEnabled = false;
        try
        {
            var current = collection;
            foreach (var removed in await Task.Run(() => ModInstaller.RemoveUnlisted(copy.Dir, current)))
                Log($"Removed {removed.Name} ({removed.Files.Count} files), no longer in the collection.");

            // In install order: a mod that must win over another overwrites it
            var items = modRows.Where(r => r.Status_ == ModRow.State.Downloaded)
                .Select(r => new ModInstaller.InstallItem(r.Mod, r.ArchivePath!, modList.RuleFor(r.Mod))).ToList();
            var parallelism = ModInstaller.DefaultParallelism;
            Log($"Unpacking {items.Count} mods, {parallelism} at a time...");
            var started = DateTime.Now;
            var progress = new Progress<string>(Log);
            var results = await Task.Run(() => ModInstaller.InstallAll(copy.Dir, items, parallelism, ((IProgress<string>)progress).Report));
            foreach (var (item, installed, error) in results)
            {
                if (error != null) Log($"Failed: {item.Mod.Name}: {error.Message}");
            }
            var files = results.Sum(r => r.Installed?.Files.Count ?? 0);
            Log($"Installed {results.Count(r => r.Installed != null)} of {items.Count} mods ({files} files) " +
                $"in {(DateTime.Now - started).TotalMinutes:F1} minutes.");
            await AfterInstallAsync(copy.Dir);
        }
        finally
        {
            busy = false;
            RefreshStatus();
            await RefreshModsAsync();
        }
    }

    /// <summary>What installed mods need before playing: RaceMenu's height limit,
    /// and building bodies and outfits when the mods or groups changed.</summary>
    private async Task AfterInstallAsync(string gameDir)
    {
        try
        {
            if (await Task.Run(() => HeightPatch.Apply(gameDir)))
                Log($"RaceMenu's Height slider limited to {HeightPatch.Min:0.00}-{HeightPatch.Max:0.00}.");
        }
        catch (Exception ex)
        {
            Log($"Couldn't limit RaceMenu's Height slider: {ex.Message}");
        }

        if (!Bodies.CanBuild(gameDir) || Bodies.IsUpToDate(gameDir)) return;
        try
        {
            Log("Building bodies and outfits with BodySlide; this takes a few minutes...");
            var progress = new Progress<string>(Log);
            var started = DateTime.Now;
            await Task.Run(() => Bodies.BuildAsync(gameDir, ((IProgress<string>)progress).Report));
            Log($"Built in {(DateTime.Now - started).TotalMinutes:F1} minutes.");
        }
        catch (Exception ex)
        {
            Log($"Building bodies failed: {ex.Message}");
        }
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

    private async void BrowsePackage_Click(object sender, RoutedEventArgs e)
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
        await RefreshModsAsync();
    }

    private void GameDirBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GameDirBox.Text)) return;
        settings.GameDir = GameDirBox.Text.Trim();
        settings.Save();
        RefreshStatus();
        _ = RefreshModsAsync();
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
            // The client package carries the body groups, so a new one can mean a rebuild
            await AfterInstallAsync(copy.Dir);
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
