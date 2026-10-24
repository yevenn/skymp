using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Numidium.Launcher.Core;

/// <summary>
/// What Nexus Mods says about a mod and its files, from its public GraphQL API
/// (no account needed), and how a downloaded archive's name maps to one of them.
/// </summary>
public static partial class NexusFiles
{
    public const int SkyrimSpecialEditionGameId = 1704;

    /// <summary>A file on a mod's Files tab. <paramref name="Uri"/> is the archive's
    /// name as Nexus serves it; <paramref name="Name"/> is the file's title there.</summary>
    public sealed record File(int FileId, string Name, string Version, string Uri, long Size, string Category, long Date);

    public sealed record ModInfo(string Name, string Author, string Category);

    /// <summary>The Nexus parts of a downloaded archive's name,
    /// "&lt;file title&gt;-&lt;mod id&gt;-&lt;version&gt;-&lt;upload time&gt;.7z", or null when it isn't one.</summary>
    public sealed record ArchiveName(int ModId, long UploadTime, string NexusName);

    public static ArchiveName? ParseArchiveName(string path)
    {
        var name = Path.GetFileName(path);
        // Browsers add " (1)" to a second download of the same name
        name = DuplicateSuffix().Replace(name, "$1$2");
        var match = NexusName().Match(name);
        return match.Success ? new ArchiveName(int.Parse(match.Groups["mod"].Value), long.Parse(match.Groups["time"].Value), name) : null;
    }

    [GeneratedRegex(@"^(.*?) \(\d+\)(\.[^.]+)$")]
    private static partial Regex DuplicateSuffix();

    [GeneratedRegex(@"^.+?-(?<mod>\d+)-.*-(?<time>\d{9,11})\.(7z|zip|rar)$", RegexOptions.IgnoreCase)]
    private static partial Regex NexusName();

    /// <summary>The Nexus file a downloaded archive is: same name, else same size and upload time.</summary>
    public static File? Match(IEnumerable<File> files, string archivePath, long size)
    {
        var parsed = ParseArchiveName(archivePath);
        var list = files.ToList();
        if (parsed != null && list.FirstOrDefault(f => f.Uri.Equals(parsed.NexusName, StringComparison.OrdinalIgnoreCase)) is File byName)
            return byName;
        var bySize = list.Where(f => f.Size == size).ToList();
        if (parsed != null && bySize.FirstOrDefault(f => f.Uri.Contains($"-{parsed.UploadTime}.", StringComparison.Ordinal)) is File byTime)
            return byTime;
        return bySize.Count == 1 ? bySize[0] : null;
    }

    /// <summary>Newer uploads of <paramref name="file"/>'s title (a file's title stays the
    /// same across its versions) that Nexus still offers, newest first.</summary>
    public static List<File> NewerVersions(IEnumerable<File> files, File file) =>
        files.Where(f => f.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase) && f.Date > file.Date &&
                         f.Category is not ("OLD_VERSION" or "ARCHIVED" or "DELETED"))
            .OrderByDescending(f => f.Date).ToList();

    public static async Task<List<File>> FilesAsync(HttpClient http, int modId, CancellationToken cancel = default)
    {
        var data = await QueryAsync(http,
            $"{{ modFiles(modId: {modId}, gameId: {SkyrimSpecialEditionGameId}) {{ fileId name version uri sizeInBytes category date }} }}", cancel);
        return (data["modFiles"]?.AsArray() ?? new JsonArray()).Select(f => new File(
            (int)f!["fileId"]!, (string?)f["name"] ?? "", (string?)f["version"] ?? "", (string?)f["uri"] ?? "",
            long.Parse(f["sizeInBytes"]?.ToString() ?? "0"), (string?)f["category"] ?? "", (long?)f["date"] ?? 0)).ToList();
    }

    public static async Task<ModInfo> ModAsync(HttpClient http, int modId, CancellationToken cancel = default)
    {
        var data = await QueryAsync(http,
            $"{{ mod(modId: {modId}, gameId: {SkyrimSpecialEditionGameId}) {{ name author modCategory {{ name }} }} }}", cancel);
        var mod = data["mod"] ?? throw new InvalidDataException($"Nexus has no Skyrim SE mod {modId}");
        return new ModInfo((string?)mod["name"] ?? "", (string?)mod["author"] ?? "", (string?)mod["modCategory"]?["name"] ?? "");
    }

    private static async Task<JsonNode> QueryAsync(HttpClient http, string query, CancellationToken cancel)
    {
        using var response = await http.PostAsJsonAsync("https://api.nexusmods.com/v2/graphql", new { query }, cancel);
        response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel)) ?? throw new InvalidDataException("Nexus sent nothing");
        if (root["errors"] is JsonArray { Count: > 0 } errors) throw new InvalidDataException($"Nexus: {errors[0]?["message"]}");
        return root["data"] ?? throw new InvalidDataException("Nexus sent no data");
    }
}
