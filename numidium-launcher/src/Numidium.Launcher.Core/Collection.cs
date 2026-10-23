using System.Text.Json.Nodes;

namespace Numidium.Launcher.Core;

/// <summary>
/// A Nexus Mods collection as Vortex writes it (collection.json in the
/// collection's archive): each mod's exact Nexus file with its MD5, the FOMOD
/// choices made when installing it, and before/after rules for files several
/// mods provide. The Numidium modlist is the collection's collection.json.
/// </summary>
public sealed class Collection
{
    public const string FileName = "collection.json";
    public const string GameDomain = "skyrimspecialedition";

    /// <summary>One choice in a FOMOD installer: installer page, group, option.</summary>
    public sealed record FomodChoice(string Step, string Group, string Option);

    public sealed record Mod(
        string Name, string Version, bool Optional, int ModId, int FileId, string Md5, long Size,
        string LogicalFileName, List<FomodChoice>? Choices)
    {
        public string PageUrl => $"https://www.nexusmods.com/{GameDomain}/mods/{ModId}?tab=files&file_id={FileId}";
    }

    public string Name { get; }
    /// <summary>Install order: collection order, adjusted so a mod that must win
    /// ("after" another) is installed later and overwrites it.</summary>
    public List<Mod> Mods { get; }

    private Collection(string name, List<Mod> mods) => (Name, Mods) = (name, mods);

    public static Collection Load(string path) => Parse(System.IO.File.ReadAllText(path));

    public static Collection Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("collection.json is empty");
        var name = (string?)root["info"]?["name"] ?? "";
        var mods = new List<Mod>();
        foreach (var node in root["mods"]?.AsArray() ?? new JsonArray())
        {
            var source = node?["source"];
            if ((string?)source?["type"] != "nexus") continue; // bundled files and external links aren't downloads
            mods.Add(new Mod(
                (string?)node!["name"] ?? "", (string?)node["version"] ?? "", (bool?)node["optional"] ?? false,
                (int)source!["modId"]!, (int)source["fileId"]!, ((string?)source["md5"] ?? "").ToLowerInvariant(),
                long.Parse(source["fileSize"]!.ToString()), (string?)source["logicalFilename"] ?? "",
                ParseChoices(node["choices"])));
        }
        return new Collection(name, Order(mods, root["modRules"]?.AsArray()));
    }

    private static List<FomodChoice>? ParseChoices(JsonNode? choices)
    {
        if (choices == null || (string?)choices["type"] != "fomod") return null;
        var list = new List<FomodChoice>();
        foreach (var step in choices["options"]?.AsArray() ?? new JsonArray())
            foreach (var group in step?["groups"]?.AsArray() ?? new JsonArray())
                foreach (var option in group?["choices"]?.AsArray() ?? new JsonArray())
                    list.Add(new FomodChoice((string?)step!["name"] ?? "", (string?)group!["name"] ?? "", (string?)option?["name"] ?? ""));
        return list;
    }

    /// <summary>
    /// Applies "before"/"after" rules (rules reference mods by MD5) with a stable
    /// topological sort: mods keep collection order unless a rule moves them.
    /// "A before B" and "B after A" both mean B is installed after A.
    /// </summary>
    private static List<Mod> Order(List<Mod> mods, JsonArray? rules)
    {
        var byMd5 = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods) byMd5.TryAdd(mod.Md5, mod);
        var later = mods.ToDictionary(m => m, _ => new HashSet<Mod>()); // mod -> mods that install after it
        foreach (var rule in rules ?? new JsonArray())
        {
            var type = (string?)rule?["type"];
            if (type != "before" && type != "after") continue;
            if (!byMd5.TryGetValue((string?)rule!["source"]?["fileMD5"] ?? "", out var source)) continue;
            if (!byMd5.TryGetValue((string?)rule["reference"]?["fileMD5"] ?? "", out var reference)) continue;
            if (type == "before") later[source].Add(reference);
            else later[reference].Add(source);
        }

        var incoming = mods.ToDictionary(m => m, m => mods.Count(other => later[other].Contains(m)));
        var result = new List<Mod>();
        var remaining = new List<Mod>(mods);
        while (remaining.Count > 0)
        {
            // The first mod in collection order that nothing still has to precede
            var next = remaining.FirstOrDefault(m => incoming[m] == 0)
                ?? throw new InvalidDataException("The collection's mod rules contradict each other (a cycle)");
            remaining.Remove(next);
            result.Add(next);
            foreach (var after in later[next]) incoming[after]--;
        }
        return result;
    }
}
