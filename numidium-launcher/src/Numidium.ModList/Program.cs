using Numidium.Launcher.Core;

// Changes the Numidium modlist (numidium-dev/collection.json) without Vortex.
// See Usage below, or numidium-dev/README.md.

const string Usage = """
    numidium-modlist <command> [--collection <collection.json>]

      list                          Lists the mods, in install order.
      check                         Lists mods with a newer version on Nexus, or gone from it.
      options <archive>             Lists an archive's installer options.
      add <archive> [options]       Adds the Nexus file an archive is, or updates the listed
                                    file of the same mod and title to it (a new version).
          --choice "Page|Group|Option"  An installer option to take (repeat for several).
                                        Without it, an update keeps its earlier choices.
          --optional                Players may skip it.
          --after <mod>             Install it after <mod>, so its files win.
          --before <mod>            Install it before <mod>, so <mod>'s files win.
          --mod-id <id> --file-id <id>  When the archive was renamed and can't be matched.
          --dry-run                 Show what would change, write nothing.
      remove <mod>                  Removes a mod (Nexus file id or name) and its rules.
      after <mod> <other mod>       Installs <mod> after <other mod>.
      stale-outputs <game folder>   Lists meshes BodySlide built for outfits the Numidium groups
                                    no longer list (the launcher puts the mods' files back).

    The collection defaults to numidium-dev/collection.json in this repository.
    A release (numidium-dev/release.ps1) carries the changed list to players.
    """;

try
{
    return await Run(args);
}
catch (Exception ex) when (ex is ArgumentException or InvalidDataException or FileNotFoundException or HttpRequestException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> Run(string[] args)
{
    var rest = new List<string>(args);
    string? Take(string flag)
    {
        var i = rest.IndexOf(flag);
        if (i < 0) return null;
        if (i + 1 >= rest.Count) throw new ArgumentException($"{flag} needs a value");
        var value = rest[i + 1];
        rest.RemoveRange(i, 2);
        return value;
    }
    List<string> TakeAll(string flag)
    {
        var values = new List<string>();
        while (Take(flag) is string v) values.Add(v);
        return values;
    }
    bool Has(string flag) => rest.Remove(flag);

    if (rest.Count > 0 && rest[0] == "stale-outputs")
    {
        var stale = StaleOutputs.Find(Single(rest.Skip(1).ToList(), "a game folder"));
        foreach (var file in stale) Console.WriteLine(file);
        Console.WriteLine($"{stale.Count} stale built files.");
        return 0;
    }

    var collectionPath = Take("--collection") ?? FindCollection()
        ?? throw new FileNotFoundException("No numidium-dev/collection.json found here or above; pass --collection.");
    if (rest.Count == 0 || rest[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine(Usage);
        return rest.Count == 0 ? 1 : 0;
    }

    var command = rest[0];
    rest.RemoveAt(0);
    var editor = CollectionEditor.Load(collectionPath);
    switch (command)
    {
        case "list":
            var index = 1;
            foreach (var mod in editor.Read().Mods)
                Console.WriteLine($"{index++,3}. {mod.Name} {mod.Version}  (mod {mod.ModId}, file {mod.FileId}" +
                                  $"{(mod.Choices is { Count: > 0 } c ? $", {c.Count} installer choices" : "")}{(mod.Optional ? ", optional" : "")})");
            return 0;

        case "check":
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                var outdated = 0;
                var mods = editor.Read().Mods;
                foreach (var group in mods.GroupBy(m => m.ModId))
                {
                    var files = await NexusFiles.FilesAsync(http, group.Key);
                    foreach (var mod in group)
                    {
                        var listed = files.FirstOrDefault(f => f.FileId == mod.FileId);
                        if (listed == null)
                        {
                            Console.WriteLine($"{mod.Name}: file {mod.FileId} is gone from Nexus (players can't download it): {mod.PageUrl}");
                            outdated++;
                        }
                        else if (NexusFiles.NewerVersions(files, listed).FirstOrDefault() is NexusFiles.File newest)
                        {
                            Console.WriteLine($"{mod.Name}: {mod.Version} -> {newest.Version}  " +
                                              $"https://www.nexusmods.com/{Collection.GameDomain}/mods/{mod.ModId}?tab=files&file_id={newest.FileId}");
                            outdated++;
                        }
                    }
                }
                Console.WriteLine(outdated == 0 ? $"All {mods.Count} files are the newest on Nexus." :
                    $"{outdated} of {mods.Count} files have a newer version or are gone. Download the new file, then: add <archive>.\n" +
                    $"Careful with SKSE plugins (DLLs): their updates since August 2026 are often for Skyrim 1.7 only, and Numidium " +
                    $"runs {SteamSkyrim.SupportedVersion}. Check the file's description says 1.6.1170 before updating.");
            }
            return 0;

        case "options":
            var xml = ModInstaller.ReadInstaller(Single(rest, "an archive"));
            if (xml == null)
            {
                Console.WriteLine("No installer: the archive is installed as it is.");
                return 0;
            }
            PrintOptions(Fomod.Options(xml));
            return 0;

        case "add":
            return await Add(editor, collectionPath, rest, TakeAll("--choice"), Has("--optional"), TakeAll("--after"), TakeAll("--before"),
                Take("--mod-id"), Take("--file-id"), Has("--dry-run"));

        case "remove":
            var removed = editor.Remove(Single(rest, "a mod"));
            if (removed == null) throw new ArgumentException($"No mod \"{rest[0]}\" in the collection");
            editor.Save(collectionPath);
            Console.WriteLine($"Removed {removed} and the rules naming it.");
            Console.WriteLine("Its plugins stay in the server's loadOrder: that list only grows (saved items keep form ids by position).");
            return 0;

        case "after":
            if (rest.Count != 2) throw new ArgumentException("after needs two mods: the one to install later, then the other");
            editor.InstallAfter(rest[0], rest[1]);
            editor.Save(collectionPath);
            Console.WriteLine($"{rest[0]} now installs after {rest[1]}.");
            return 0;

        default:
            throw new ArgumentException($"Unknown command {command}.\n\n{Usage}");
    }
}

static async Task<int> Add(CollectionEditor editor, string collectionPath, List<string> rest, List<string> choiceArgs, bool optional,
    List<string> after, List<string> before, string? modIdArg, string? fileIdArg, bool dryRun)
{
    var archive = Path.GetFullPath(Single(rest, "an archive"));
    if (!File.Exists(archive)) throw new FileNotFoundException($"No archive at {archive}");
    var size = new FileInfo(archive).Length;
    Console.WriteLine($"Reading {Path.GetFileName(archive)} ({size / (1024.0 * 1024):0.0} MB)...");
    var md5 = Downloads.Md5Of(archive);

    var modId = modIdArg != null ? int.Parse(modIdArg) : NexusFiles.ParseArchiveName(archive)?.ModId
        ?? throw new ArgumentException("The archive's name isn't a Nexus download name (Title-<mod id>-<version>-<time>.7z): pass --mod-id and --file-id.");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var files = await NexusFiles.FilesAsync(http, modId);
    var file = fileIdArg != null
        ? files.FirstOrDefault(f => f.FileId == int.Parse(fileIdArg)) ?? throw new ArgumentException($"Mod {modId} has no file {fileIdArg} on Nexus")
        : NexusFiles.Match(files, archive, size);
    if (file == null)
    {
        Console.Error.WriteLine($"None of mod {modId}'s files on Nexus is this archive. Its files:");
        foreach (var f in files) Console.Error.WriteLine($"  file {f.FileId}: {f.Uri} ({f.Size:n0} bytes, {f.Category})");
        Console.Error.WriteLine("Pass --file-id when the archive was renamed.");
        return 1;
    }
    if (file.Size != size)
        Console.WriteLine($"Warning: Nexus lists file {file.FileId} as {file.Size:n0} bytes, the archive is {size:n0}.");
    var info = await NexusFiles.ModAsync(http, modId);
    Console.WriteLine($"Nexus: {info.Name} by {info.Author}, file {file.FileId} \"{file.Name}\" {file.Version} ({file.Category}).");
    if (NexusFiles.NewerVersions(files, file).FirstOrDefault() is NexusFiles.File newest)
        Console.WriteLine($"Note: Nexus has a newer version, {newest.Version} (file {newest.FileId}): " +
                          $"https://www.nexusmods.com/{Collection.GameDomain}/mods/{modId}?tab=files&file_id={newest.FileId}");

    // The installer's choices: given, or kept from the listed version, or not needed
    var existing = editor.Read().Mods.FirstOrDefault(m => m.ModId == modId && m.LogicalFileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase));
    var xml = ModInstaller.ReadInstaller(archive);
    List<(Fomod.Option, Collection.FomodChoice)>? choices = null;
    if (xml != null)
    {
        var options = Fomod.Options(xml);
        var wanted = choiceArgs.Count > 0 ? choiceArgs.Select(ParseChoice).ToList() : existing?.Choices;
        if (wanted == null || wanted.Count == 0)
        {
            if (options.Any(o => !o.GroupType.Equals("SelectAll", StringComparison.OrdinalIgnoreCase) && !o.Type.Equals("Required", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine("This archive has an installer. Choose its options with --choice \"Page|Group|Option\":");
                PrintOptions(options, Console.Error);
                return 1;
            }
            wanted = new List<Collection.FomodChoice>();
        }
        choices = new();
        foreach (var choice in wanted)
        {
            var option = options.FirstOrDefault(o => Fomod.Matches(o, choice));
            if (option == null)
            {
                Console.Error.WriteLine($"The installer has no option \"{choice.Step}|{choice.Group}|{choice.Option}\"" +
                                        (choiceArgs.Count == 0 ? " (kept from the listed version)" : "") + ". Its options:");
                PrintOptions(options, Console.Error);
                return 1;
            }
            choices.Add((option, choice));
        }
    }
    else if (choiceArgs.Count > 0)
    {
        throw new ArgumentException("This archive has no installer, so it takes no --choice.");
    }

    // What it installs, as the launcher will: also checks the choices produce files
    var asMod = new Collection.Mod(file.Name, file.Version, optional, modId, file.FileId, md5, size, file.Name,
        choices?.Select(c => c.Item2).ToList());
    var modList = ModList.Load(Path.Combine(Path.GetDirectoryName(collectionPath)!, ModList.FileName));
    var placements = ModInstaller.Plan(asMod, archive, modList.RuleFor(asMod));
    if (placements.Count == 0) throw new InvalidDataException("With these choices the archive installs nothing.");
    var plugins = placements.Select(p => p.Destination).Where(d => !d.Contains('\\') &&
        (d.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || d.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
         d.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    var change = editor.Upsert(new CollectionEditor.NewFile(modId, file, info, md5, size, choices, optional), out var replacedMd5);
    if (change == CollectionEditor.Change.AlreadyListed)
    {
        Console.WriteLine($"File {file.FileId} is already in the collection; nothing changed.");
        return 0;
    }
    foreach (var other in after) editor.InstallAfter(file.Name, other);
    foreach (var other in before) editor.InstallAfter(other, file.Name);

    Console.WriteLine(change == CollectionEditor.Change.Added
        ? $"Added {file.Name} {file.Version}: {placements.Count} files."
        : $"Updated {file.Name} from {existing?.Version} to {file.Version}: {placements.Count} files; its rules follow the new file.");
    if (choices is { Count: > 0 })
        foreach (var (option, _) in choices) Console.WriteLine($"  installer: {option.Step} | {option.Group} | {option.Name}");
    if (plugins.Count > 0)
        Console.WriteLine($"Plugins: {string.Join(", ", plugins)}. New ones go at the END of the server's loadOrder " +
                          "(numidium-dev/server-settings.json and skyrimdev-plugins.txt): the list only grows.");
    if (dryRun)
    {
        Console.WriteLine("Dry run: collection.json not written.");
        return 0;
    }
    editor.Save(collectionPath);
    Console.WriteLine($"Saved {collectionPath}.");
    return 0;
}

static string Single(List<string> rest, string what) =>
    rest.Count == 1 ? rest[0] : throw new ArgumentException($"Expected {what}{(rest.Count > 1 ? $", got: {string.Join(" ", rest)}" : "")}");

static Collection.FomodChoice ParseChoice(string text)
{
    var parts = text.Split('|');
    if (parts.Length != 3) throw new ArgumentException($"--choice \"{text}\" needs three parts: \"Page|Group|Option\"");
    return new Collection.FomodChoice(parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
}

static void PrintOptions(List<Fomod.Option> options, TextWriter? to = null)
{
    to ??= Console.Out;
    foreach (var step in options.GroupBy(o => o.Step))
    {
        to.WriteLine($"Page \"{step.Key}\"");
        foreach (var group in step.GroupBy(o => (o.Group, o.GroupType)))
        {
            to.WriteLine($"  Group \"{group.Key.Group}\" ({group.Key.GroupType})");
            foreach (var option in group)
                to.WriteLine($"    --choice \"{$"{option.Step}|{option.Group}|{option.Name}".Replace("\"", "\\\"")}\"" +
                             (option.Type is "" or "Optional" ? "" : $"  [{option.Type}]"));
        }
    }
}

// numidium-dev/collection.json in this folder or one above it
static string? FindCollection()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
    {
        var path = Path.Combine(dir.FullName, "numidium-dev", Collection.FileName);
        if (File.Exists(path)) return path;
    }
    return null;
}
