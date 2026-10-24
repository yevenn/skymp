using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Numidium.Launcher.Core;

/// <summary>
/// Builds the bodies and refitted outfits with BodySlide's command line, like
/// numidium-dev's "Build Numidium Bodies.bat": the Numidium groups (shipped in
/// the client package) with the shared default presets, plus morph files for
/// RaceMenu's body sliders. Every player needs the same built meshes.
/// </summary>
public static class Bodies
{
    public const string StateFile = "numidium-bodies.json";
    public const string GroupsFile = "Numidium.xml";

    /// <summary>Group and preset pairs, female then male.</summary>
    public static readonly (string Group, string Preset)[] Builds =
    {
        ("Numidium Female", "CBBE Vanilla"),
        ("Numidium Male", "HIMBO Default"),
    };

    public static string BodySlideDir(string gameDir) => Path.Combine(gameDir, "Data", "CalienteTools", "BodySlide");

    /// <summary>
    /// Points the copy's BodySlide at the copy's Data folder (Config.xml's GameDataPath).
    /// On its first run BodySlide takes the path from the registry, which is the Steam
    /// install's: building by hand would then write meshes into Steam's folder. Returns
    /// whether it changed anything.
    /// </summary>
    public static bool PointAtCopy(string gameDir)
    {
        var config = Path.Combine(BodySlideDir(gameDir), "Config.xml");
        if (!File.Exists(config)) return false;
        var doc = System.Xml.Linq.XDocument.Load(config, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidDataException($"{config} is empty");
        var data = Path.GetFullPath(Path.Combine(gameDir, "Data")).TrimEnd('\\') + "\\";
        var element = root.Element("GameDataPath");
        if (element != null && string.Equals(element.Value, data, StringComparison.OrdinalIgnoreCase)) return false;
        if (element == null) root.Add(element = new System.Xml.Linq.XElement("GameDataPath"));
        element.Value = data;
        FileInstall.Replace(config, s =>
        {
            using var writer = System.Xml.XmlWriter.Create(s, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(true), OmitXmlDeclaration = doc.Declaration == null });
            doc.Save(writer);
        });
        return true;
    }

    public static bool CanBuild(string gameDir) =>
        File.Exists(Path.Combine(BodySlideDir(gameDir), "BodySlide.exe")) &&
        File.Exists(Path.Combine(BodySlideDir(gameDir), "SliderGroups", GroupsFile));

    /// <summary>
    /// What the last build was made from: the groups file, the installed mods
    /// (their projects and the outfits they refit) and the presets. A build is
    /// needed when this differs from the recorded one.
    /// </summary>
    public static string Fingerprint(string gameDir)
    {
        var bodySlide = BodySlideDir(gameDir);
        var sb = new StringBuilder();
        var groups = Path.Combine(bodySlide, "SliderGroups", GroupsFile);
        if (File.Exists(groups)) sb.Append(File.ReadAllText(groups));
        foreach (var fileId in ModInstaller.ReadRecords(gameDir).Keys.Order()) sb.Append('|').Append(fileId);
        foreach (var (group, preset) in Builds) sb.Append('|').Append(group).Append('=').Append(preset);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    public static bool IsUpToDate(string gameDir)
    {
        var state = Path.Combine(gameDir, StateFile);
        return File.Exists(state) && File.ReadAllText(state).Trim() == Fingerprint(gameDir);
    }

    /// <summary>Runs both builds and records the fingerprint when they succeed.
    /// BodySlide writes new loose meshes into Data, never into the base game's archives.</summary>
    public static async Task BuildAsync(string gameDir, Action<string>? log = null, CancellationToken cancel = default)
    {
        if (!CanBuild(gameDir)) throw new InvalidOperationException("BodySlide or the Numidium body groups aren't installed yet.");
        PointAtCopy(gameDir);
        var bodySlide = BodySlideDir(gameDir);
        var data = Path.Combine(gameDir, "Data");
        foreach (var (group, preset) in Builds)
        {
            log?.Invoke($"Building {group} ({preset})...");
            var start = new ProcessStartInfo(Path.Combine(bodySlide, "BodySlide.exe"))
            {
                WorkingDirectory = bodySlide,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "--groupbuild", group, "--preset", preset, "--targetdir", data, "--trimorphs" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("BodySlide didn't start");
            await process.WaitForExitAsync(cancel);
            if (process.ExitCode != 0) throw new InvalidOperationException($"BodySlide failed building {group} (exit code {process.ExitCode})");
        }
        File.WriteAllText(Path.Combine(gameDir, StateFile), Fingerprint(gameDir));
        log?.Invoke("Bodies and outfits built.");
    }
}
