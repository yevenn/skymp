using System.Text.Json.Nodes;
using Numidium.Launcher.Core;

namespace Numidium.Launcher.Tests;

public class CollectionEditorTests
{
    // A collection as Vortex writes it: one mod with installer choices, one plain, a rule between them
    private const string Json = """
        {
          "info": {
            "author": "MsHyeven",
            "name": "Test",
            "installInstructions": "Don't use Vortex, it's the launcher's job"
          },
          "mods": [
            {
              "name": "Body Mod",
              "version": "1.0",
              "optional": false,
              "domainName": "skyrimspecialedition",
              "source": { "type": "nexus", "modId": 10, "fileId": 100, "md5": "aaaa", "fileSize": 5, "logicalFilename": "Body Mod", "updatePolicy": "exact", "tag": "keepme" },
              "choices": { "options": [ { "name": "Main", "groups": [ { "name": "Body", "choices": [ { "name": "CBBE", "idx": 0 } ] } ] } ], "type": "fomod" },
              "author": "Someone",
              "phase": 0
            },
            {
              "name": "Outfit",
              "version": "2.0",
              "optional": false,
              "domainName": "skyrimspecialedition",
              "source": { "type": "nexus", "modId": 20, "fileId": 200, "md5": "bbbb", "fileSize": 6, "logicalFilename": "Outfit" },
              "phase": 0
            }
          ],
          "modRules": [
            {
              "type": "after",
              "reference": { "fileMD5": "aaaa", "logicalFileName": "Body Mod" },
              "source": { "fileMD5": "bbbb", "logicalFileName": "Outfit" }
            }
          ],
          "plugins": [ { "name": "Outfit.esp", "enabled": true } ]
        }
        """;

    private static readonly NexusFiles.ModInfo Info = new("Body Mod", "Someone", "Models and Textures");

    private static CollectionEditor.NewFile BodyMod(int fileId, string md5, string version = "1.1",
        List<(Fomod.Option, Collection.FomodChoice)>? choices = null) =>
        new(10, new NexusFiles.File(fileId, "Body Mod", version, $"Body Mod-10-1-1-{fileId}.7z", 7, "MAIN", 1), Info, md5, 7, choices, false);

    [Fact]
    public void SavingWithoutChangesKeepsTheRealCollectionAsItIs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "numidium-dev", Collection.FileName))) dir = dir.Parent;
        if (dir == null) return; // not inside the repository
        var original = File.ReadAllText(Path.Combine(dir.FullName, "numidium-dev", Collection.FileName));
        Assert.Equal(original.ReplaceLineEndings("\n").TrimEnd(), CollectionEditor.Parse(original).ToJson().ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void AddsANewModAtTheEnd()
    {
        var editor = CollectionEditor.Parse(Json);
        var file = new CollectionEditor.NewFile(30, new NexusFiles.File(300, "Hair", "3.0", "Hair-30-3-0-1.zip", 9, "MAIN", 1),
            new NexusFiles.ModInfo("Hair", "Hairdresser", "Hair"), "cccc", 9, null, true);
        Assert.Equal(CollectionEditor.Change.Added, editor.Upsert(file, out var replaced));
        Assert.Null(replaced);
        var hair = editor.Read().Mods.Last();
        Assert.Equal((30, 300, "cccc", 9L, true, "Hair"), (hair.ModId, hair.FileId, hair.Md5, hair.Size, hair.Optional, hair.LogicalFileName));
        Assert.Null(hair.Choices);
        Assert.Contains("\"author\": \"Hairdresser\"", editor.ToJson());
        Assert.Equal(CollectionEditor.Change.AlreadyListed, editor.Upsert(file, out _));
    }

    [Fact]
    public void ANewVersionReplacesTheOldInPlaceAndItsRulesFollow()
    {
        var editor = CollectionEditor.Parse(Json);
        Assert.Equal(CollectionEditor.Change.Updated, editor.Upsert(BodyMod(101, "dddd"), out var replaced));
        Assert.Equal("aaaa", replaced);
        var mods = editor.Read().Mods;
        Assert.Equal(new[] { 101, 200 }, mods.Select(m => m.FileId)); // still first, and Outfit still after it
        Assert.Equal("1.1", mods[0].Version);
        Assert.Equal("CBBE", Assert.Single(mods[0].Choices!).Option); // no new choices given: the old ones stay
        var json = editor.ToJson();
        Assert.DoesNotContain("aaaa", json);
        Assert.Contains("\"tag\": \"keepme\"", json);
        Assert.Contains("\"plugins\"", json); // fields the editor doesn't know are kept
    }

    [Fact]
    public void NewChoicesReplaceTheOldOnes()
    {
        var editor = CollectionEditor.Parse(Json);
        var unp = new Fomod.Option("Main", "Body", "SelectExactlyOne", "UNP", 1, "Optional");
        editor.Upsert(BodyMod(101, "dddd", choices: new() { (unp, new Collection.FomodChoice("Main", "Body", "UNP")) }), out _);
        var choice = Assert.Single(editor.Read().Mods[0].Choices!);
        Assert.Equal(("Main", "Body", "UNP"), (choice.Step, choice.Group, choice.Option));
        Assert.Contains("\"idx\": 1", editor.ToJson());
    }

    [Fact]
    public void RemovesAModAndTheRulesNamingIt()
    {
        var editor = CollectionEditor.Parse(Json);
        Assert.Equal("Body Mod", editor.Remove("100"));
        Assert.Equal(new[] { 200 }, editor.Read().Mods.Select(m => m.FileId));
        Assert.Empty(JsonNode.Parse(editor.ToJson())!["modRules"]!.AsArray());
        Assert.Null(editor.Remove("Not there"));
    }

    [Fact]
    public void InstallAfterOrdersModsAndRefusesACycle()
    {
        var editor = CollectionEditor.Parse(Json);
        editor.Remove("Outfit");
        var file = new CollectionEditor.NewFile(20, new NexusFiles.File(200, "Outfit", "2.0", "Outfit-20-2-0-1.7z", 6, "MAIN", 1),
            new NexusFiles.ModInfo("Outfit", "Tailor", "Armour"), "bbbb", 6, null, false);
        editor.Upsert(file, out _);
        editor.InstallAfter("Body Mod", "Outfit");
        Assert.Equal(new[] { 200, 100 }, editor.Read().Mods.Select(m => m.FileId));
        Assert.Throws<InvalidDataException>(() => editor.InstallAfter("outfit", "body mod"));
        Assert.Throws<ArgumentException>(() => editor.InstallAfter("Body Mod", "Missing Mod"));
    }
}

public class NexusFilesTests
{
    [Theory]
    [InlineData("Moon Monk's Robes - 2K-82495-1-2-1731869060.zip", 82495, 1731869060L)]
    [InlineData("Moon Monk's Robes - 2K-82495-1-2-1731869060 (1).zip", 82495, 1731869060L)]
    [InlineData("SSE Engine Fixes for 1.6.1170-17230-7-0-20-1719338463.7z", 17230, 1719338463L)]
    public void ReadsNexusDownloadNames(string name, int modId, long time)
    {
        var parsed = NexusFiles.ParseArchiveName(Path.Combine(@"C:\Downloads", name));
        Assert.Equal((modId, time), (parsed!.ModId, parsed.UploadTime));
    }

    [Fact]
    public void AnythingElseIsNoNexusName() => Assert.Null(NexusFiles.ParseArchiveName("my renamed mod.7z"));

    private static readonly NexusFiles.File[] Files =
    {
        new(1, "Robes - 2K", "1.0", "Robes - 2K-82495-1-0-1673412090.zip", 100, "OLD_VERSION", 1673412090),
        new(2, "Robes - 2K", "1.2", "Robes - 2K-82495-1-2-1731869060.zip", 120, "MAIN", 1731869060),
        new(3, "Robes - 4K", "1.2", "Robes - 4K-82495-1-2-1731869100.zip", 120, "MAIN", 1731869100),
    };

    [Fact]
    public void FindsNewerVersionsOfTheSameFileTitleOnly()
    {
        var files = Files.Append(new NexusFiles.File(4, "Robes - 2K", "1.3", "Robes - 2K-82495-1-3-1731900000.zip", 121, "MAIN", 1731900000))
            .Append(new NexusFiles.File(5, "Robes - 2K", "1.25", "x.zip", 1, "OLD_VERSION", 1731890000)).ToList();
        Assert.Equal(new[] { 4, 2 }, NexusFiles.NewerVersions(files, Files[0]).Select(f => f.FileId)); // not the 4K, not old versions
        Assert.Empty(NexusFiles.NewerVersions(files, files[3]));
    }

    [Fact]
    public void MatchesAnArchiveByNameThenBySizeAndTime()
    {
        Assert.Equal(2, NexusFiles.Match(Files, "Robes - 2K-82495-1-2-1731869060.zip", 120)?.FileId);
        Assert.Equal(2, NexusFiles.Match(Files, "Robes - 2K-82495-1-2-1731869060 (1).zip", 120)?.FileId);
        Assert.Equal(3, NexusFiles.Match(Files, "renamed-82495-x-1731869100.zip", 120)?.FileId); // same size as 2: the time decides
        Assert.Equal(1, NexusFiles.Match(Files, "anything.zip", 100)?.FileId); // one file of that size
        Assert.Null(NexusFiles.Match(Files, "anything.zip", 120)); // two of that size, nothing else to go on
    }
}

public class FomodOptionsTests
{
    [Fact]
    public void ListsEveryPagesOptionsWithTheirGroupRules()
    {
        const string config = """
            <config>
              <installSteps>
                <installStep name="Main"><optionalFileGroups>
                  <group name="Body" type="SelectExactlyOne"><plugins>
                    <plugin name="CBBE"><typeDescriptor><type name="Optional" /></typeDescriptor></plugin>
                    <plugin name="UNP"><typeDescriptor><type name="Recommended" /></typeDescriptor></plugin>
                  </plugins></group>
                </optionalFileGroups></installStep>
                <installStep name="Extras"><visible><flagDependency flag="x" value="y" /></visible><optionalFileGroups>
                  <group name="Always" type="SelectAll"><plugins><plugin name="Core"><typeDescriptor><type name="Required" /></typeDescriptor></plugin></plugins></group>
                </optionalFileGroups></installStep>
              </installSteps>
            </config>
            """;
        var options = Fomod.Options(config);
        Assert.Equal(new[] { "Main|Body|CBBE|0|Optional", "Main|Body|UNP|1|Recommended", "Extras|Always|Core|0|Required" },
            options.Select(o => $"{o.Step}|{o.Group}|{o.Name}|{o.Index}|{o.Type}"));
        Assert.True(Fomod.Matches(options[1], new Collection.FomodChoice(" main ", "BODY", "unp")));
        Assert.Equal("SelectAll", options[2].GroupType);
    }
}
