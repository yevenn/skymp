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
        DownloadsDirBox.Text = DownloadsDir;
        Title = $"Numidium · launcher {LauncherVersion}";
        RefreshStatus();
        Loaded += async (_, _) =>
        {
            await CheckReleaseAsync();
            await RefreshModsAsync();
        };
    }

    private static readonly HttpClient Http = new();

    /// <summary>The release this launcher was built for (release.ps1 sets it), or "dev".</summary>
    private static string LauncherVersion =>
        typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "dev";

    /// <summary>
    /// Where the client package and the mod list come from: a folder picked for
    /// testing, else the newest GitHub release (mirrored), else a package bundled
    /// beside the launcher.
    /// </summary>
    private string? PackageDir =>
        settings.ClientPackageDir is string dir && File.Exists(Path.Combine(dir, ClientPackage.InfoFile)) ? dir
        : File.Exists(Path.Combine(LauncherSettings.ReleaseDir, ClientPackage.InfoFile)) ? LauncherSettings.ReleaseDir
        : LauncherSettings.BundledPackageDir();

    private bool UsesRelease => PackageDir == LauncherSettings.ReleaseDir;

    /// <summary>Mirrors the newest release's package info and mod list, unless a
    /// local package folder is picked. Offline, the last mirrored release stays.</summary>
    private async Task CheckReleaseAsync()
    {
        if (settings.ClientPackageDir != null) return;
        try
        {
            var release = await Releases.FetchLatestAsync(Http);
            if (release == null)
            {
                Log($"No Numidium release on github.com/{Releases.Repository} yet.");
                return;
            }
            var before = Releases.ReadMirrored(LauncherSettings.ReleaseDir)?.Tag;
            await Releases.SyncAsync(Http, release, LauncherSettings.ReleaseDir);
            if (before != release.Tag) Log($"Newest release: {release.Tag}.");
            if (release.Find(Releases.LauncherAsset) != null && LauncherVersion != "dev" && release.Tag != "numidium-" + LauncherVersion)
                Log($"A newer launcher is in {release.Tag}: download {Releases.LauncherAsset} from {release.PageUrl} and replace this one.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            var mirrored = Releases.ReadMirrored(LauncherSettings.ReleaseDir)?.Tag;
            Log($"Couldn't check GitHub for releases ({ex.Message})." + (mirrored != null ? $" Using {mirrored}." : ""));
        }
        finally
        {
            RefreshStatus();
        }
    }

    private Collection? collection;
    private ModList modList = new();
    private readonly Dictionary<string, string> hashCache = new();
    private List<ModRow> modRows = new();

    private string DownloadsDir => settings.DownloadsDir ?? Downloads.DefaultFolder;

    private string? CollectionPath =>
        PackageDir is string dir && File.Exists(Path.Combine(dir, Collection.FileName))
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

        try
        {
            if (Bodies.PointAtCopy(gameDir)) Log("BodySlide now builds into the Numidium game folder, not Steam's.");
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or InvalidDataException)
        {
            Log($"Couldn't point BodySlide at the Numidium game folder: {ex.Message}");
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

    private string? PackageInfoPath => PackageDir is string dir ? Path.Combine(dir, ClientPackage.InfoFile) : null;

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
                SetStatus(SteamStatus, where.Length == 0 ? Good : Bad, $"Version {version}, the one Numidium runs on.{where}");
            else
                SetStatus(SteamStatus, where.Length == 0 ? Muted : Bad,
                    $"Version {version}. Numidium runs on {SteamSkyrim.SupportedVersion}: Set up has Steam download that " +
                    $"version's files for the Numidium copy (up to {BaseGame.Depots.Sum(d => d.Size) / 1e9:0} GB). " +
                    $"Your Steam install stays as it is.{where}");
        }

        var copy = new GameCopy(settings.GameDir!);
        if (!copy.Exists)
        {
            SetStatus(GameStatus, Muted, "Not set up yet. Set up copies the base game here; your Steam folder is left as it is.");
            CopyButton.Content = "Set up";
        }
        else
        {
            CopyButton.Content = "Update";
            var missing = copy.MissingFiles();
            if (missing.Count > 0)
                SetStatus(GameStatus, Bad, $"Not ready: {missing.Count} base game files aren't Skyrim {SteamSkyrim.SupportedVersion}'s, " +
                                           $"or changed since they were checked. {CopyButton.Content} checks them and fetches what's missing.");
            else SetStatus(GameStatus, Good, $"Ready, version {SteamSkyrim.GetVersion(copy.Dir)}.");
        }

        PackageDirBox.Text = settings.ClientPackageDir is string local ? local
            : UsesRelease ? $"GitHub release {Releases.ReadMirrored(LauncherSettings.ReleaseDir)?.Tag} (github.com/{Releases.Repository})"
            : PackageDir is string bundled ? bundled
            : $"Releases on github.com/{Releases.Repository}";
        var installed = ClientPackage.ReadInstalled(copy.Dir);
        var available = PackageInfoPath is string info ? ClientPackage.ReadInfo(info) : null;
        var skse = Skse.IsInstalled(copy.Dir) ? "SKSE installed" : "SKSE not installed";
        if (available == null)
            SetStatus(PackageStatus, installed == null ? Bad : Muted,
                $"{skse}. Client {installed?.Version ?? "not installed"}; no release found yet (check your connection, then restart the launcher).");
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

    private async void UseReleases_Click(object sender, RoutedEventArgs e)
    {
        settings.ClientPackageDir = null;
        settings.Save();
        await CheckReleaseAsync();
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
            Log($"Checking the base game in {copy.Dir} and copying what it lacks from {source}...");
            var copyProgress = CopyProgressReporter();
            var result = await Task.Run(() => copy.Update(source, copyProgress));
            Log($"Base game: {result.Copied} copied ({result.BytesWritten / (1024 * 1024)} MB), " +
                $"{result.Linked} hard-linked, {result.Unchanged} already right.");
            var depots = result.Depots();
            if (depots.Count > 0 && !await FetchFromSteamAsync(copy, source, depots)) return;

            if (!Skse.IsInstalled(copy.Dir))
            {
                Log($"Downloading SKSE from {Skse.Url}...");
                var archive = await Skse.DownloadAsync(LauncherSettings.CacheDir, Http);
                var files = await Task.Run(() => Skse.Install(copy.Dir, archive));
                Log($"SKSE installed ({files.Count} files).");
            }

            if (PackageInfoPath is string info)
            {
                var available = ClientPackage.ReadInfo(info);
                if (ClientPackage.ReadInstalled(copy.Dir)?.Version != available.Version)
                {
                    if (UsesRelease)
                    {
                        Log($"Downloading the Numidium client {available.Version} ({available.Size / (1024 * 1024)} MB)...");
                        var download = new Progress<(long Done, long Total)>(p =>
                        {
                            CopyProgress.Maximum = Math.Max(1, p.Total);
                            CopyProgress.Value = p.Done;
                        });
                        await Releases.EnsurePackageAsync(Http, LauncherSettings.ReleaseDir, download);
                    }
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

    // Create it on the window's thread, outside Task.Run: Progress reports back to the
    // thread that made it, and only the window's thread may touch the progress bar
    private IProgress<GameCopy.Progress> CopyProgressReporter() => new Progress<GameCopy.Progress>(p =>
    {
        CopyProgress.Maximum = Math.Max(1, p.Total);
        CopyProgress.Value = p.Done;
    });

    /// <summary>
    /// Has Steam download <paramref name="depots"/>, those with base game files the
    /// Steam install doesn't have in 1.6.1170's version, and moves those files into
    /// the copy. Returns false when the player declines or it fails.
    /// </summary>
    private async Task<bool> FetchFromSteamAsync(GameCopy copy, string source,
        List<(BaseGame.Depot Depot, List<BaseGame.Entry> Files)> depots)
    {
        string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";
        var what = string.Join(", ", depots.Select(d => $"{d.Depot.Contents} ({Gb(d.Depot.Size)})"));
        Log($"{depots.Sum(d => d.Files.Count(f => !f.Optional))} base game files aren't Skyrim {SteamSkyrim.SupportedVersion}'s in the Steam install " +
            $"(version {SteamSkyrim.GetVersion(source)}). Steam can download them: {what}.");

        var steamRoot = SteamSkyrim.FindSteamRoot();
        if (steamRoot == null)
        {
            Log("The Steam client wasn't found, so it can't download them.");
            return false;
        }
        var answer = MessageBox.Show(this,
            $"Numidium runs on Skyrim {SteamSkyrim.SupportedVersion}, and your Steam install is version {SteamSkyrim.GetVersion(source)}.\n\n" +
            $"The launcher will ask Steam to download {SteamSkyrim.SupportedVersion}'s {what} " +
            $"and move them into the Numidium copy. Your Steam install stays as it is.\n\n" +
            "Steam's console will open; keep Steam open and signed in until it's done. Continue?",
            "Download Skyrim 1.6.1170", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            Log("Not downloaded; the Numidium copy can't be played until it is.");
            return false;
        }

        foreach (var (depot, files) in depots)
        {
            var dir = SteamDepots.ContentDir(steamRoot, depot.Id);
            var checkProgress = CopyProgressReporter();
            if (Directory.Exists(dir) && await Task.Run(() => copy.DepotHas(dir, files, checkProgress)))
            {
                Log($"Steam already downloaded {depot.Contents}.");
            }
            else
            {
                Log($"Asking Steam for {depot.Contents} ({SteamDepots.Command(depot)})...");
                string? lastMessage = null;
                var status = new Progress<SteamDepots.Status>(s =>
                {
                    CopyProgress.Maximum = Math.Max(1, s.Total);
                    CopyProgress.Value = s.Bytes;
                    if (s.Message != lastMessage) Log(lastMessage = s.Message);
                });
                dir = await Task.Run(() => SteamDepots.DownloadAsync(steamRoot, depot, d => copy.DepotHas(d, files), status));
            }
            Log($"Moving {files.Count} files of {depot.Contents} into the Numidium copy...");
            var moveProgress = CopyProgressReporter();
            await Task.Run(() => copy.InstallFromDepot(dir, files, moveProgress));
            SteamDepots.Remove(dir);
        }

        var missing = copy.MissingFiles();
        if (missing.Count > 0)
        {
            Log($"Still not Skyrim {SteamSkyrim.SupportedVersion}'s: {string.Join(", ", missing)}.");
            return false;
        }
        Log($"The Numidium copy is Skyrim {SteamSkyrim.GetVersion(copy.Dir)}.");
        return true;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadServerFields(out var host, out var port, out var profileId)) return;
        var copy = new GameCopy(settings.GameDir!);
        if (!copy.Exists)
        {
            Log("Set up the Numidium game folder first.");
            return;
        }
        if (copy.MissingFiles().Count > 0)
        {
            Log($"Click Update first: the base game in {copy.Dir} needs checking.");
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
                    "Numidium's mods aren't installed yet. Download them from Nexus (the Nexus buttons in MODS), " +
                    "then click Install downloaded.");
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
            if (manifest.UsesDiscordSignIn)
            {
                var session = await EnsureSignedInAsync(host, manifest);
                if (session == null) return;
                ClientSettings.Write(copy.Dir, host, port, session: session);
            }
            else
            {
                ClientSettings.Write(copy.Dir, host, port, profileId);
            }
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

    /// <summary>The session to play on <paramref name="host"/> with: the saved one if
    /// the server still knows it, else a new Discord sign-in. Null if that fails.</summary>
    private async Task<string?> EnsureSignedInAsync(string host, ServerManifest manifest)
    {
        var baseUrl = ServerManifest.BaseUrl(host);
        if (settings.GetSession(host) is string saved)
        {
            var signedIn = await DiscordSignIn.CheckAsync(Http, baseUrl, saved);
            if (signedIn != null)
            {
                ShowAccount($"Signed in as {signedIn.Username}.", Good);
                return saved;
            }
            Log("The server's Discord sign-in expired; signing in again.");
            settings.SetSession(host, null);
            settings.Save();
        }
        return await SignInAsync(host, manifest.Auth!);
    }

    /// <summary>Signs in to <paramref name="host"/> with Discord and saves the session.</summary>
    private async Task<string?> SignInAsync(string host, ServerManifest.ServerAuth auth)
    {
        var state = DiscordSignIn.NewState();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var waiting = DiscordSignIn.WaitForCodeAsync(auth.RedirectUri, state, cancel.Token);
            Log("Opening Discord in your browser to sign in...");
            ShowAccount("Waiting for Discord in your browser...", Muted);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiscordSignIn.AuthorizeUrl(auth, state)) { UseShellExecute = true });
            var code = await waiting;
            var signedIn = await DiscordSignIn.ExchangeAsync(Http, ServerManifest.BaseUrl(host), code, auth.RedirectUri);
            settings.SetSession(host, signedIn.Session);
            settings.Save();
            Log($"Signed in to {host} as {signedIn.Username}.");
            ShowAccount($"Signed in as {signedIn.Username}.", Good);
            return signedIn.Session;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or InvalidDataException or HttpRequestException)
        {
            var reason = cancel.IsCancellationRequested ? "no answer from Discord within 5 minutes" : ex.Message;
            Log($"Not signed in: {reason}");
            ShowAccount("Not signed in.", Bad);
            return null;
        }
    }

    private void ShowAccount(string text, Brush brush) => SetStatus(AccountStatus, brush, text);

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadServerFields(out var host, out _, out _)) return;
        SignInButton.IsEnabled = false;
        try
        {
            var manifest = await ServerManifest.FetchAsync(host);
            if (!manifest.UsesDiscordSignIn)
            {
                ShowAccount("This server doesn't use Discord sign-in (a dev server: it uses the profile number).", Muted);
                return;
            }
            if (settings.GetSession(host) is string old)
            {
                try { await DiscordSignIn.SignOutAsync(Http, ServerManifest.BaseUrl(host), old); } catch (HttpRequestException) { }
            }
            await SignInAsync(host, manifest.Auth!);
        }
        catch (HttpRequestException ex)
        {
            Log($"Couldn't reach the server: {ex.Message}");
        }
        finally
        {
            SignInButton.IsEnabled = true;
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
