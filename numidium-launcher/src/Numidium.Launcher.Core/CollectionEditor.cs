using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Numidium.Launcher.Core;

/// <summary>
/// Changes the Numidium modlist (collection.json, in Vortex's format) without
/// Vortex: adds or updates a mod from an archive downloaded from Nexus, removes
/// one, and orders mods with before/after rules. Every other field is kept as it
/// is, so <see cref="Collection"/> and the launcher read the result like before.
/// </summary>
public sealed class CollectionEditor
{
    private readonly JsonObject root;

    private CollectionEditor(JsonObject root) => this.root = root;

    public static CollectionEditor Load(string path) => Parse(File.ReadAllText(path));

    public static CollectionEditor Parse(string json) =>
        new(JsonNode.Parse(json) as JsonObject ?? throw new FormatException("collection.json isn't a JSON object"));

    // Vortex's own layout: two-space indents, apostrophes and accents as they are
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => root.ToJsonString(Format);

    public void Save(string path) => File.WriteAllText(path, ToJson());

    private JsonArray Mods => root["mods"] as JsonArray ?? (JsonArray)(root["mods"] = new JsonArray());

    private JsonArray Rules => root["modRules"] as JsonArray ?? (JsonArray)(root["modRules"] = new JsonArray());

    public Collection Read() => Collection.Parse(ToJson());

    /// <summary>What adding a file changed: a new mod, an update of the same file title
    /// (another version), or nothing (this exact file is already listed).</summary>
    public enum Change { Added, Updated, AlreadyListed }

    public sealed record NewFile(
        int ModId, NexusFiles.File File, NexusFiles.ModInfo Mod, string Md5, long Size,
        List<(Fomod.Option Option, Collection.FomodChoice Choice)>? Choices, bool Optional);

    /// <summary>
    /// Adds <paramref name="file"/>, or updates the listed file of the same mod and
    /// title (a newer version) in place, keeping its position and its rules, which
    /// follow the new MD5. <paramref name="file"/>'s Choices replace the old ones only
    /// when given.
    /// </summary>
    public Change Upsert(NewFile file, out string? replacedMd5)
    {
        replacedMd5 = null;
        var existing = FindNode(n => (int?)n["source"]?["fileId"] == file.File.FileId);
        if (existing != null) return Change.AlreadyListed;

        var sameTitle = FindNode(n => (int?)n["source"]?["modId"] == file.ModId &&
                                      string.Equals((string?)n["source"]?["logicalFilename"], file.File.Name, StringComparison.OrdinalIgnoreCase));
        var node = sameTitle as JsonObject ?? new JsonObject();
        node["name"] = file.File.Name;
        node["version"] = file.File.Version;
        node["optional"] = file.Optional || ((bool?)sameTitle?["optional"] ?? false);
        node["domainName"] = Collection.GameDomain;
        var oldSource = sameTitle?["source"] as JsonObject;
        replacedMd5 = (string?)oldSource?["md5"];
        node["source"] = new JsonObject
        {
            ["type"] = "nexus",
            ["modId"] = file.ModId,
            ["fileId"] = file.File.FileId,
            ["md5"] = file.Md5,
            ["fileSize"] = file.Size,
            ["logicalFilename"] = file.File.Name,
            ["updatePolicy"] = "exact",
            ["tag"] = (string?)oldSource?["tag"] ?? NewTag(),
        };
        if (file.Choices != null)
        {
            if (file.Choices.Count == 0) node.Remove("choices");
            else node["choices"] = ChoicesNode(file.Choices);
        }
        node["author"] = file.Mod.Author;
        node["details"] = new JsonObject { ["category"] = file.Mod.Category, ["type"] = "" };
        node["phase"] = (int?)sameTitle?["phase"] ?? 0;

        if (sameTitle == null)
        {
            Mods.Add(node);
            return Change.Added;
        }
        // Rules name mods by MD5: point them at the new file
        foreach (var rule in Rules)
            foreach (var side in new[] { "source", "reference" })
                if (rule?[side] is JsonObject end && string.Equals((string?)end["fileMD5"], replacedMd5, StringComparison.OrdinalIgnoreCase))
                {
                    end["fileMD5"] = file.Md5;
                    end["logicalFileName"] = file.File.Name;
                }
        return Change.Updated;
    }

    /// <summary>Removes a mod (by Nexus file id, or exact name) and the rules naming it;
    /// returns its name, or null when no mod matched.</summary>
    public string? Remove(string fileIdOrName)
    {
        var node = Find(fileIdOrName);
        if (node == null) return null;
        var md5 = (string?)node["source"]?["md5"];
        Mods.Remove(node);
        foreach (var rule in Rules.Where(r => RuleNames(r, md5)).ToList()) Rules.Remove(rule);
        return (string?)node["name"];
    }

    /// <summary>Makes <paramref name="later"/> install after <paramref name="earlier"/>,
    /// so its files win where both have one (a rule of type "after").</summary>
    public void InstallAfter(string later, string earlier)
    {
        var source = Find(later) ?? throw new ArgumentException($"No mod \"{later}\" in the collection");
        var reference = Find(earlier) ?? throw new ArgumentException($"No mod \"{earlier}\" in the collection");
        if (ReferenceEquals(source, reference)) throw new ArgumentException("A mod can't be installed after itself");
        JsonObject End(JsonNode mod) => new()
        {
            ["fileExpression"] = (string?)mod["source"]?["logicalFilename"],
            ["fileMD5"] = (string?)mod["source"]?["md5"],
            ["versionMatch"] = "*",
            ["logicalFileName"] = (string?)mod["source"]?["logicalFilename"],
        };
        Rules.Add(new JsonObject { ["type"] = "after", ["reference"] = End(reference), ["source"] = End(source) });
        Read(); // throws when the rules now contradict each other
    }

    /// <summary>A mod by Nexus file id, or by name or file title (either, ignoring case).</summary>
    public JsonNode? Find(string fileIdOrName) =>
        int.TryParse(fileIdOrName, out var fileId)
            ? FindNode(n => (int?)n["source"]?["fileId"] == fileId)
            : FindNode(n => string.Equals((string?)n["name"], fileIdOrName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals((string?)n["source"]?["logicalFilename"], fileIdOrName, StringComparison.OrdinalIgnoreCase));

    private JsonNode? FindNode(Func<JsonNode, bool> predicate) => Mods.FirstOrDefault(n => n != null && predicate(n));

    private static bool RuleNames(JsonNode? rule, string? md5) =>
        md5 != null && new[] { "source", "reference" }.Any(side =>
            string.Equals((string?)rule?[side]?["fileMD5"], md5, StringComparison.OrdinalIgnoreCase));

    // Vortex's "choices" layout: pages, their groups, the chosen options with their index
    private static JsonObject ChoicesNode(List<(Fomod.Option Option, Collection.FomodChoice Choice)> choices)
    {
        var steps = new JsonArray();
        foreach (var step in choices.GroupBy(c => c.Option.Step))
        {
            var groups = new JsonArray();
            foreach (var group in step.GroupBy(c => c.Option.Group))
                groups.Add(new JsonObject
                {
                    ["name"] = group.Key,
                    ["choices"] = new JsonArray(group.Select(c => (JsonNode)new JsonObject { ["name"] = c.Option.Name, ["idx"] = c.Option.Index }).ToArray()),
                });
            steps.Add(new JsonObject { ["name"] = step.Key, ["groups"] = groups });
        }
        return new JsonObject { ["options"] = steps, ["type"] = "fomod" };
    }

    private static string NewTag()
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_";
        return new string(RandomNumberGenerator.GetBytes(10).Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}
