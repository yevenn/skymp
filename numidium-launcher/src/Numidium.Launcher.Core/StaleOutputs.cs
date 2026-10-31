using System.Text.Json;
using System.Xml.Linq;
using SharpCompress.Archives;

namespace Numidium.Launcher.Core;

/// <summary>
/// Meshes BodySlide built for projects the Numidium groups no longer list. They
/// stay in Data when a group drops a project and keep shadowing the file the mod
/// ships: Sentinel's 3BA refits were dropped for Sentinel's own female meshes, and
/// game copies built before still had the refits. Each is put back from the archive
/// of the mod that installed that file, or deleted when no mod did (BodySlide made
/// it, like a _0 variant the mod doesn't have).
///
/// Builds are recorded (<see cref="BuiltFile"/>, written after each build), so a
/// stale file is one the launcher built that no listed project builds any more.
/// Game copies built before that record existed fall back on BodySlide's mark:
/// the BODYTRI extra data --trimorphs adds to what it builds.
/// </summary>
public static class StaleOutputs
{
    public const string BuiltFile = "numidium-bodies-built.json";

    private sealed record Project(bool Listed, string[] Files);

    /// <summary>Every project's output files (its nifs, then its .tri), relative to the game folder.</summary>
    private static List<Project> Projects(string gameDir)
    {
        var bodySlide = Bodies.BodySlideDir(gameDir);
        var groupsFile = Path.Combine(bodySlide, "SliderGroups", Bodies.GroupsFile);
        var sliderSets = Path.Combine(bodySlide, "SliderSets");
        var projects = new List<Project>();
        if (!File.Exists(groupsFile) || !Directory.Exists(sliderSets)) return projects;
        var members = XDocument.Load(groupsFile).Descendants("Member")
            .Select(m => (string?)m.Attribute("name")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var osp in Directory.EnumerateFiles(sliderSets, "*.osp"))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(osp);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }
            foreach (var set in doc.Descendants("SliderSet"))
            {
                if ((string?)set.Attribute("name") is not string name) continue;
                var outputPath = (string?)set.Element("OutputPath");
                var outputFile = set.Element("OutputFile");
                if (string.IsNullOrWhiteSpace(outputPath) || outputFile == null || string.IsNullOrWhiteSpace(outputFile.Value)) continue;
                var folder = Path.Combine("Data", Fomod.Normalize(outputPath));
                var genWeights = !string.Equals((string?)outputFile.Attribute("GenWeights"), "false", StringComparison.OrdinalIgnoreCase);
                var files = (genWeights
                        ? new[] { outputFile.Value + "_0.nif", outputFile.Value + "_1.nif" }
                        : new[] { outputFile.Value + ".nif" })
                    .Append(outputFile.Value + ".tri")
                    .Select(n => Path.Combine(folder, n)).ToArray();
                projects.Add(new Project(members.Contains(name), files));
            }
        }
        return projects;
    }

    // Files a listed project writes (variants of one outfit often share their output)
    private static HashSet<string> StillBuilt(List<Project> projects) =>
        projects.Where(p => p.Listed).SelectMany(p => p.Files).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ReadRecord(string gameDir)
    {
        var path = Path.Combine(gameDir, BuiltFile);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))!.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteRecord(string gameDir, IEnumerable<string> files) =>
        File.WriteAllText(Path.Combine(gameDir, BuiltFile),
            JsonSerializer.Serialize(files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(), new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// Records what the listed projects built (after a build), keeping files of
    /// earlier builds that are stale and still waiting to be put back.
    /// </summary>
    public static void RecordBuilt(string gameDir)
    {
        var still = StillBuilt(Projects(gameDir));
        var built = still.Where(f => File.Exists(Path.Combine(gameDir, f)));
        var waiting = ReadRecord(gameDir).Where(f => !still.Contains(f) && File.Exists(Path.Combine(gameDir, f)));
        WriteRecord(gameDir, built.Concat(waiting).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Stale built files, relative to the game folder ("Data\meshes\...").</summary>
    public static List<string> Find(string gameDir)
    {
        var projects = Projects(gameDir);
        var still = StillBuilt(projects);
        if (File.Exists(Path.Combine(gameDir, BuiltFile)))
            return ReadRecord(gameDir).Where(f => !still.Contains(f) && File.Exists(Path.Combine(gameDir, f))).ToList();

        // No record yet: what BodySlide marked, among files nothing listed builds
        var stale = new List<string>();
        foreach (var project in projects.Where(p => !p.Listed))
        {
            var nifs = project.Files.Where(f => f.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && !still.Contains(f)).ToList();
            var built = nifs.Where(f => IsBuilt(Path.Combine(gameDir, f))).ToList();
            stale.AddRange(built);
            var tri = project.Files[^1];
            if (built.Count > 0 && !still.Contains(tri) && File.Exists(Path.Combine(gameDir, tri))) stale.Add(tri);
        }
        return stale.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsBuilt(string nifPath)
    {
        if (!File.Exists(nifPath)) return false;
        try
        {
            return NifFile.ReadStrings(nifPath).Contains("BODYTRI");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts the stale files back from their mods' archives (by Nexus file id), or
    /// deletes them when no mod installed them. Files of a mod whose archive isn't
    /// at hand stay, and are reported. Returns how many files were restored or deleted.
    /// </summary>
    public static int Restore(string gameDir, IReadOnlyCollection<string> stale, IReadOnlyDictionary<int, string> archives, Action<string>? log = null)
    {
        var owner = new Dictionary<string, ModInstaller.InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in ModInstaller.ReadRecords(gameDir).Values)
            foreach (var file in mod.Files) owner[Fomod.Normalize(file)] = mod;

        var handled = new List<string>();
        var byMod = new Dictionary<ModInstaller.InstalledMod, List<string>>();
        foreach (var file in stale.Select(Fomod.Normalize))
        {
            if (owner.TryGetValue(file, out var mod))
            {
                if (!byMod.TryGetValue(mod, out var list)) byMod[mod] = list = new();
                list.Add(file);
            }
            else
            {
                File.Delete(Path.Combine(gameDir, file));
                handled.Add(file);
            }
        }

        foreach (var (mod, files) in byMod)
        {
            if (!archives.TryGetValue(mod.FileId, out var archive) || !File.Exists(archive))
            {
                log?.Invoke($"{files.Count} files of {mod.Name} were built over by BodySlide; put its download back in the downloads folder " +
                            "and update again to restore them.");
                continue;
            }
            var restored = RestoreFrom(gameDir, archive, files);
            handled.AddRange(restored);
            if (restored.Count < files.Count) log?.Invoke($"{files.Count - restored.Count} files of {mod.Name} weren't found in {Path.GetFileName(archive)}.");
        }

        // Handled files are no longer the launcher's builds
        if (File.Exists(Path.Combine(gameDir, BuiltFile)))
            WriteRecord(gameDir, ReadRecord(gameDir).Where(f => !handled.Contains(f, StringComparer.OrdinalIgnoreCase)));
        return handled.Count;
    }

    // Archive entries are matched by their path under Data, wherever the archive
    // keeps them (its root, or an installer's module folder)
    private static List<string> RestoreFrom(string gameDir, string archivePath, List<string> files)
    {
        var wanted = files.ToDictionary(f => f.StartsWith("Data\\", StringComparison.OrdinalIgnoreCase) ? f[5..] : f, f => f,
            StringComparer.OrdinalIgnoreCase);
        var restored = new List<string>();
        string? Match(string? key)
        {
            if (key == null) return null;
            key = Fomod.Normalize(key);
            foreach (var (underData, file) in wanted)
                if (!restored.Contains(file) && (key.Equals(underData, StringComparison.OrdinalIgnoreCase) ||
                                                 key.EndsWith("\\" + underData, StringComparison.OrdinalIgnoreCase)))
                    return file;
            return null;
        }
        void Write(string file, Action<Stream> copy)
        {
            var target = Path.Combine(gameDir, file);
            var temp = target + ".numidium-tmp";
            using (var output = File.Create(temp)) copy(output);
            File.Move(temp, target, overwrite: true); // replaces rather than writes into: hard links stay safe
            restored.Add(file);
        }

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry() && restored.Count < wanted.Count)
            {
                if (reader.Entry.IsDirectory || Match(reader.Entry.Key) is not string file) continue;
                Write(file, reader.WriteEntryTo);
            }
        }
        else
        {
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                if (Match(entry.Key) is not string file) continue;
                Write(file, output =>
                {
                    using var input = entry.OpenEntryStream();
                    input.CopyTo(output);
                });
            }
        }
        return restored;
    }
}
