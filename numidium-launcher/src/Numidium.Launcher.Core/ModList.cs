using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Numidium.Launcher.Core;

/// <summary>
/// numidium-modlist.json, next to the collection's collection.json in the
/// client package: what a collection can't say. Mods the launcher installs
/// itself (SKSE), and install rules for the odd archive that has no FOMOD
/// installer and no recognisable Data folder.
/// </summary>
public sealed class ModList
{
    public const string FileName = "numidium-modlist.json";

    /// <summary>How to install one mod's archive instead of its FOMOD choices or the
    /// automatic Data detection. Paths inside the archive use either slash.</summary>
    public sealed class Rule
    {
        [JsonPropertyName("modId")] public int ModId { get; set; }
        /// <summary>One file of the mod; without it the rule applies to all of the mod's files.</summary>
        [JsonPropertyName("fileId")] public int? FileId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        /// <summary>Not installed from the collection (e.g. SKSE, which the launcher installs itself).</summary>
        [JsonPropertyName("skip")] public bool Skip { get; set; }
        /// <summary>"game" installs into the game folder instead of Data (e.g. a DLL loaded
        /// before SKSE). The archive root is installed unless folders say otherwise.</summary>
        [JsonPropertyName("target")] public string? Target { get; set; }

        public bool IntoGameFolder => string.Equals(Target, "game", StringComparison.OrdinalIgnoreCase);
        /// <summary>Archive folders whose contents go into Data, in order (later ones overwrite).
        /// "." is the archive root.</summary>
        [JsonPropertyName("folders")] public List<string> Folders { get; set; } = new();
        /// <summary>Paths (relative to a folder) to leave out: a file, or a folder and everything under it.</summary>
        [JsonPropertyName("exclude")] public List<string> Exclude { get; set; } = new();
        /// <summary>When set, only these paths (same form as exclude) are installed.</summary>
        [JsonPropertyName("include")] public List<string> Include { get; set; } = new();
    }

    [JsonPropertyName("mods")] public List<Rule> Mods { get; set; } = new();

    /// <summary>The rule for one file: a rule naming that file, else one for the whole mod.</summary>
    public Rule? RuleFor(Collection.Mod mod) =>
        Mods.FirstOrDefault(r => r.ModId == mod.ModId && r.FileId == mod.FileId) ??
        Mods.FirstOrDefault(r => r.ModId == mod.ModId && r.FileId == null);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static ModList Load(string path) =>
        System.IO.File.Exists(path)
            ? JsonSerializer.Deserialize<ModList>(System.IO.File.ReadAllText(path), Json) ?? new()
            : new();
}

/// <summary>Finds collection files the player has downloaded.</summary>
public static class Downloads
{
    /// <summary>The player's Downloads folder (the shell's, which may have been moved).</summary>
    public static string DefaultFolder =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
            "{374DE290-123F-4565-9164-39C4925E467B}", null) is string folder
            ? Environment.ExpandEnvironmentVariables(folder)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// Where each mod's file is in <paramref name="folders"/> (not their subfolders),
    /// matched by size and MD5, so a browser's renaming doesn't matter. Only files
    /// of a wanted size get hashed; <paramref name="hashCache"/> keeps hashes
    /// between calls, keyed by path, size and timestamp.
    /// </summary>
    public static Dictionary<Collection.Mod, string> Find(IEnumerable<Collection.Mod> mods, IEnumerable<string> folders,
        Dictionary<string, string>? hashCache = null)
    {
        var wanted = mods.GroupBy(m => m.Size).ToDictionary(g => g.Key, g => g.ToList());
        var found = new Dictionary<Collection.Mod, string>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFiles())
            {
                if (!wanted.TryGetValue(info.Length, out var candidates)) continue;
                var cacheKey = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                if (hashCache == null || !hashCache.TryGetValue(cacheKey, out var md5))
                {
                    try
                    {
                        md5 = Md5Of(info.FullName);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue; // removed meanwhile, or still being written by a browser or mod manager
                    }
                    if (hashCache != null) hashCache[cacheKey] = md5;
                }
                foreach (var mod in candidates.Where(m => m.Md5 == md5)) found.TryAdd(mod, info.FullName);
            }
        }
        return found;
    }

    public static string Md5Of(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }
}
