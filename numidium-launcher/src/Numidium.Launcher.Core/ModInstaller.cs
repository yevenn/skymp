using System.Text.Json;
using SharpCompress.Archives;

namespace Numidium.Launcher.Core;

/// <summary>
/// Installs downloaded collection files into the game copy's Data folder:
/// through the archive's FOMOD installer with the collection's recorded
/// choices, an install rule from the modlist, or else by finding where Data
/// starts in the archive. Records which collection file owns each installed
/// file (numidium-mods-installed.json), keyed by Nexus file id: one mod can
/// have several files in a collection, and an update is a new file id. A file
/// a later one overwrote belongs to that later one, so removing the earlier
/// one leaves it alone.
/// </summary>
public static class ModInstaller
{
    public const string RecordFile = "numidium-mods-installed.json";

    public sealed record InstalledMod(int ModId, int FileId, string Name, List<string> Files);

    /// <summary>Top-level names that only appear in a Data folder.</summary>
    private static readonly HashSet<string> DataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "meshes", "textures", "scripts", "skse", "interface", "sound", "music", "seq", "strings", "video",
        "calientetools", "platform", "lodsettings", "grass", "shadersfx", "mcm", "dialogueviews", "nemesis_engine",
        "source", "materials", "netscriptframework", "tools",
    };

    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase) { ".esp", ".esm", ".esl", ".bsa" };

    /// <summary>Installed collection files, by Nexus file id.</summary>
    public static Dictionary<int, InstalledMod> ReadRecords(string gameDir)
    {
        var path = Path.Combine(gameDir, RecordFile);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<InstalledMod>>(File.ReadAllText(path))!.ToDictionary(m => m.FileId)
            : new();
    }

    private static void WriteRecords(string gameDir, Dictionary<int, InstalledMod> records) =>
        File.WriteAllText(Path.Combine(gameDir, RecordFile),
            JsonSerializer.Serialize(records.Values.OrderBy(m => m.ModId).ThenBy(m => m.FileId).ToList(), new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>An archive's FOMOD installer (fomod\ModuleConfig.xml), or null when it has none.</summary>
    public static string? ReadInstaller(string archivePath)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(e => !e.IsDirectory && e.Key != null).ToList();
        var moduleRoot = Fomod.FindModuleRoot(entries.Select(e => e.Key!));
        if (moduleRoot == null) return null;
        var configKey = (moduleRoot.Length == 0 ? "" : moduleRoot + "\\") + "fomod\\ModuleConfig.xml";
        var configEntry = entries.First(e => Fomod.Normalize(e.Key!).Equals(configKey, StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(configEntry.OpenEntryStream(), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Where each archive file would go in Data, without installing anything.</summary>
    public static List<Fomod.Placement> Plan(Collection.Mod mod, string archivePath, ModList.Rule? rule, string? dataDir = null)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(e => !e.IsDirectory && e.Key != null).ToList();
        var keys = entries.Select(e => Fomod.Normalize(e.Key!)).ToList();
        var name = Path.GetFileName(archivePath);

        if (rule?.Folders is { Count: > 0 })
            return PlanFolders(keys, rule.Folders.Select(f => f == "." ? "" : Fomod.Normalize(f)), rule, name);
        if (rule?.IntoGameFolder == true)
            return PlanFolders(keys, new[] { "" }, rule, name);

        var moduleRoot = Fomod.FindModuleRoot(keys);
        if (moduleRoot != null)
        {
            var configKey = (moduleRoot.Length == 0 ? "" : moduleRoot + "\\") + "fomod\\ModuleConfig.xml";
            var configEntry = entries.First(e => Fomod.Normalize(e.Key!).Equals(configKey, StringComparison.OrdinalIgnoreCase));
            string xml;
            using (var reader = new StreamReader(configEntry.OpenEntryStream(), detectEncodingFromByteOrderMarks: true))
                xml = reader.ReadToEnd();
            var choices = mod.Choices ?? new List<Collection.FomodChoice>();
            var installed = dataDir == null ? null : (Func<string, bool>)(f => File.Exists(Path.Combine(dataDir, f)));
            var placements = Fomod.Plan(xml, keys, moduleRoot, choices, installed);
            return rule == null ? placements : placements.Where(p => Selected(p.Destination, rule)).ToList();
        }

        return PlanFolders(keys, new[] { FindDataRoot(keys, name) }, rule, name);
    }

    private static List<Fomod.Placement> PlanFolders(List<string> keys, IEnumerable<string> folders, ModList.Rule? rule, string archiveName)
    {
        var placements = new List<Fomod.Placement>();
        foreach (var folder in folders)
        {
            var prefix = folder.Length == 0 ? "" : folder + "\\";
            var under = keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (under.Count == 0) throw new InvalidDataException($"{archiveName} has no folder \"{folder}\"; its install rule needs updating");
            foreach (var key in under)
            {
                var rel = key[prefix.Length..];
                if (IsArchiveMetadata(rel)) continue;
                if (rule != null && !Selected(rel, rule)) continue;
                placements.Add(new Fomod.Placement(key, rel));
            }
        }
        return placements;
    }

    /// <summary>Installs one collection file. Reinstalling the same file replaces it.</summary>
    public static InstalledMod Install(string gameDir, Collection.Mod mod, string archivePath, ModList.Rule? rule) =>
        Apply(gameDir, Stage(gameDir, mod, archivePath, rule));

    /// <summary>An archive unpacked into staging: final destinations (each once, the
    /// last placement winning) and the staged source file for each.</summary>
    public sealed record StagedMod(Collection.Mod Mod, ModList.Rule? Rule, string StagingDir, List<(string Destination, string Source)> Files);

    public static string StagingRoot(string gameDir) => Path.Combine(gameDir, ".numidium-staging");

    /// <summary>
    /// Unpacks the files a mod installs into a staging folder inside the game folder,
    /// reading the archive once from start to end: solid 7z archives can't be read
    /// file by file without decompressing everything before each file again. Mods
    /// can be staged in parallel; Apply then moves them into place in install order.
    /// </summary>
    public static StagedMod Stage(string gameDir, Collection.Mod mod, string archivePath, ModList.Rule? rule)
    {
        if (rule?.Skip == true) throw new InvalidOperationException($"{mod.Name} is installed by the launcher itself");
        var plan = Plan(mod, archivePath, rule, Path.Combine(gameDir, "Data"));

        // A destination written twice keeps the last placement; keep first-seen order
        var final = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var placement in plan)
        {
            if (!final.ContainsKey(placement.Destination)) order.Add(placement.Destination);
            final[placement.Destination] = placement.Source;
        }
        var needed = new HashSet<string>(final.Values, StringComparer.OrdinalIgnoreCase);

        var stagingDir = Path.Combine(StagingRoot(gameDir), mod.FileId.ToString());
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);
        var staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Stream? Target(string? rawKey)
        {
            if (rawKey == null) return null;
            var key = Fomod.Normalize(rawKey);
            if (!needed.Contains(key) || !staged.Add(key)) return null;
            var path = FileInstall.SafePath(stagingDir, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return File.Create(path);
        }
        using (var archive = ArchiveFactory.OpenArchive(archivePath))
        {
            if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
            {
                // One pass from start to end
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    if (reader.Entry.IsDirectory) continue;
                    using var output = Target(reader.Entry.Key);
                    if (output != null) reader.WriteEntryTo(output);
                }
            }
            else
            {
                // Zip and non-solid rar reach each file directly
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    using var output = Target(entry.Key);
                    if (output == null) continue;
                    using var input = entry.OpenEntryStream();
                    input.CopyTo(output);
                }
            }
        }
        var missing = needed.Where(n => !staged.Contains(n)).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"{Path.GetFileName(archivePath)} is missing {missing[0]}");

        return new StagedMod(mod, rule, stagingDir, order.Select(d => (d, FileInstall.SafePath(stagingDir, final[d]))).ToList());
    }

    /// <summary>Moves a staged mod into place and records it. Moving replaces files
    /// rather than writing into them (hard links stay safe), and on one drive it is
    /// only a rename.</summary>
    public static InstalledMod Apply(string gameDir, StagedMod staged)
    {
        var mod = staged.Mod;
        var targetDir = staged.Rule?.IntoGameFolder == true ? gameDir : Path.Combine(gameDir, "Data");
        var records = ReadRecords(gameDir);
        var written = new List<string>();
        var writtenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A source placed at several destinations is copied for all but its last one
        var uses = staged.Files.GroupBy(f => f.Source, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var (destination, source) in staged.Files)
        {
            var target = FileInstall.SafePath(targetDir, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) File.Delete(target);
            if (--uses[source] > 0) File.Copy(source, target);
            else File.Move(source, target);
            var relToGame = Path.GetRelativePath(gameDir, target);
            if (writtenSet.Add(relToGame)) written.Add(relToGame);
        }
        Directory.Delete(staged.StagingDir, recursive: true);

        // Files a previous install of this same file had that this one doesn't
        if (records.TryGetValue(mod.FileId, out var previous))
        {
            foreach (var old in previous.Files.Where(f => !writtenSet.Contains(f)))
            {
                var path = FileInstall.SafePath(gameDir, old);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        // What this mod wrote is now its own, even where another mod had it before
        foreach (var other in records.Values.Where(m => m.FileId != mod.FileId))
            other.Files.RemoveAll(writtenSet.Contains);

        var installed = new InstalledMod(mod.ModId, mod.FileId, mod.Name, written);
        records[mod.FileId] = installed;
        WriteRecords(gameDir, records);
        return installed;
    }

    public sealed record InstallItem(Collection.Mod Mod, string ArchivePath, ModList.Rule? Rule);

    /// <summary>
    /// Installs mods: unpacks up to <paramref name="parallelism"/> archives at once
    /// (each archive decompresses on one core), then moves them into place one by
    /// one in the given install order, so a later mod still overwrites an earlier
    /// one. A mod that fails is reported and skipped; the rest still install.
    /// </summary>
    public static async Task<List<(InstallItem Item, InstalledMod? Installed, Exception? Error)>> InstallAll(
        string gameDir, IReadOnlyList<InstallItem> items, int parallelism, Action<string>? log = null, CancellationToken cancel = default)
    {
        var root = StagingRoot(gameDir);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true); // left by an interrupted install

        var staged = new (StagedMod? Staged, Exception? Error)[items.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancel },
            (i, _) =>
            {
                var item = items[i];
                try { staged[i] = (Stage(gameDir, item.Mod, item.ArchivePath, item.Rule), null); }
                catch (Exception ex) { staged[i] = (null, ex); }
                log?.Invoke($"Unpacked {Interlocked.Increment(ref done)} of {items.Count}: {item.Mod.Name}");
                return ValueTask.CompletedTask;
            });

        var results = new List<(InstallItem, InstalledMod?, Exception?)>();
        for (var i = 0; i < items.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            if (staged[i].Error is Exception error) { results.Add((items[i], null, error)); continue; }
            try { results.Add((items[i], Apply(gameDir, staged[i].Staged!), null)); }
            catch (Exception ex) { results.Add((items[i], null, ex)); }
        }
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return results;
    }

    /// <summary>A sensible number of archives to unpack at once on this machine.</summary>
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>Removes installed files the collection no longer lists: earlier
    /// versions of its files (an update is a new file id) and dropped mods.
    /// Returns what was removed.</summary>
    public static List<InstalledMod> RemoveUnlisted(string gameDir, Collection collection)
    {
        var listed = collection.Mods.Select(m => m.FileId).ToHashSet();
        var records = ReadRecords(gameDir);
        var removed = records.Values.Where(r => !listed.Contains(r.FileId)).ToList();
        foreach (var record in removed)
        {
            foreach (var file in record.Files)
            {
                var path = FileInstall.SafePath(gameDir, file);
                if (File.Exists(path)) File.Delete(path);
            }
            records.Remove(record.FileId);
        }
        if (removed.Count > 0) WriteRecords(gameDir, records);
        return removed;
    }

    /// <summary>Where Data starts in an archive without an installer or rule: a Data
    /// folder, the root, or a single top folder wrapping either.</summary>
    public static string FindDataRoot(IReadOnlyList<string> keys, string archiveName)
    {
        foreach (var candidate in new[] { "" }.Concat(keys.Select(k => k.Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase)))
        {
            var prefix = candidate.Length == 0 ? "" : candidate + "\\";
            var under = keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(k => k[prefix.Length..]).ToList();
            if (under.Any(k => k.StartsWith("Data\\", StringComparison.OrdinalIgnoreCase))) return prefix + "Data";
            if (under.Any(LooksLikeData)) return candidate;
        }
        throw new InvalidDataException($"Can't tell where {archiveName}'s Data folder starts; it needs an install rule in {ModList.FileName}");
    }

    private static bool LooksLikeData(string rel)
    {
        var parts = rel.Split('\\');
        return parts.Length > 1 ? DataFolders.Contains(parts[0]) : DataExtensions.Contains(Path.GetExtension(rel));
    }

    // Mod manager metadata where Data starts, not game files
    private static bool IsArchiveMetadata(string rel) =>
        rel.StartsWith("fomod\\", StringComparison.OrdinalIgnoreCase) || rel.Equals("meta.ini", StringComparison.OrdinalIgnoreCase) ||
        (rel.StartsWith("vortex_", StringComparison.OrdinalIgnoreCase) && !rel.Contains('\\'));

    private static bool Selected(string rel, ModList.Rule rule)
    {
        static bool Matches(string rel, string item)
        {
            var normalized = Fomod.Normalize(item);
            return rel.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                   rel.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase);
        }
        if (rule.Exclude.Any(x => Matches(rel, x))) return false;
        return rule.Include.Count == 0 || rule.Include.Any(x => Matches(rel, x));
    }
}
