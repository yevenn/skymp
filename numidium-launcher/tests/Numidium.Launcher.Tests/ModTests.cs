using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Numidium.Launcher.Core;

namespace Numidium.Launcher.Tests;

/// <summary>Builds archives and collection.json files for the mod tests.</summary>
internal static class ModFixtures
{
    public static string Zip(string dir, string name, Dictionary<string, string> files)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in files)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    public static string Md5(string path) => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public static Collection.Mod ModFor(string archive, int modId, int fileId, List<Collection.FomodChoice>? choices = null) =>
        new(Path.GetFileNameWithoutExtension(archive), "1.0", false, modId, fileId, Md5(archive), new FileInfo(archive).Length,
            Path.GetFileNameWithoutExtension(archive), choices);
}

public class CollectionTests
{
    private const string Json = """
        { "info": { "name": "Numidium" },
          "mods": [
            { "name": "A", "version": "1", "optional": false,
              "source": { "type": "nexus", "modId": 1, "fileId": 11, "md5": "AAA", "fileSize": 10, "logicalFilename": "A main" } },
            { "name": "B", "version": "2", "optional": true,
              "source": { "type": "nexus", "modId": 2, "fileId": 22, "md5": "bbb", "fileSize": "20", "logicalFilename": "B main" },
              "choices": { "type": "fomod", "options": [ { "name": "Main", "groups": [ { "name": "Body", "choices": [ { "name": "CBBE", "idx": 1 } ] } ] } ] } },
            { "name": "C", "version": "3", "optional": false,
              "source": { "type": "nexus", "modId": 3, "fileId": 33, "md5": "ccc", "fileSize": 30, "logicalFilename": "C main" } },
            { "name": "Bundled", "source": { "type": "bundle" } } ],
          "modRules": [
            { "type": "before", "source": { "fileMD5": "ccc" }, "reference": { "fileMD5": "aaa" } } ] }
        """;

    [Fact]
    public void ReadsModsChoicesAndChecksums()
    {
        var collection = Collection.Parse(Json);
        Assert.Equal("Numidium", collection.Name);
        Assert.Equal(3, collection.Mods.Count); // the bundled file isn't a download
        var b = collection.Mods.Single(m => m.Name == "B");
        Assert.Equal((2, 22, "bbb", 20L, true), (b.ModId, b.FileId, b.Md5, b.Size, b.Optional));
        Assert.Equal(new Collection.FomodChoice("Main", "Body", "CBBE"), Assert.Single(b.Choices!));
        Assert.Equal("aaa", collection.Mods.Single(m => m.Name == "A").Md5); // checksums are lower case
        Assert.Equal("https://www.nexusmods.com/skyrimspecialedition/mods/2?tab=files&file_id=22", b.PageUrl);
    }

    [Fact]
    public void RulesMoveModsSoTheWinnerInstallsLast()
    {
        // C "before" A: C installs first, A overwrites it; B keeps its place relative to the rest
        Assert.Equal(new[] { "B", "C", "A" }.OrderBy(x => x), Collection.Parse(Json).Mods.Select(m => m.Name).OrderBy(x => x));
        var order = Collection.Parse(Json).Mods.Select(m => m.Name).ToList();
        Assert.True(order.IndexOf("C") < order.IndexOf("A"));
    }

    [Fact]
    public void ContradictoryRulesAreReported()
    {
        var json = Json.Replace("""{ "type": "before", "source": { "fileMD5": "ccc" }, "reference": { "fileMD5": "aaa" } }""",
            """{ "type": "before", "source": { "fileMD5": "ccc" }, "reference": { "fileMD5": "aaa" } }, { "type": "after", "source": { "fileMD5": "ccc" }, "reference": { "fileMD5": "aaa" } }""");
        Assert.Throws<InvalidDataException>(() => Collection.Parse(json));
    }
}

public class FomodTests
{
    private const string Config = """
        <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <moduleName>Body Mod</moduleName>
          <requiredInstallFiles>
            <folder source="00 Core" destination="" />
          </requiredInstallFiles>
          <installSteps order="Explicit">
            <installStep name="Main">
              <optionalFileGroups order="Explicit">
                <group name="Body" type="SelectExactlyOne">
                  <plugins order="Explicit">
                    <plugin name="CBBE">
                      <files><folder source="10 CBBE" destination="meshes" priority="1" /></files>
                      <conditionFlags><flag name="body">cbbe</flag></conditionFlags>
                      <typeDescriptor><type name="Optional" /></typeDescriptor>
                    </plugin>
                    <plugin name="UNP">
                      <files><folder source="11 UNP" destination="meshes" /></files>
                      <conditionFlags><flag name="body">unp</flag></conditionFlags>
                      <typeDescriptor><type name="Optional" /></typeDescriptor>
                    </plugin>
                  </plugins>
                </group>
                <group name="Always" type="SelectAll">
                  <plugins><plugin name="Readme"><files><file source="docs/readme.txt" destination="Docs/BodyMod.txt" /></files></plugin></plugins>
                </group>
              </optionalFileGroups>
            </installStep>
            <installStep name="Unp extras">
              <visible><flagDependency flag="body" value="unp" /></visible>
              <optionalFileGroups><group name="Extras" type="SelectAny"><plugins>
                <plugin name="Forced"><files><file source="11 UNP/extra.nif" /></files><typeDescriptor><type name="Required" /></typeDescriptor></plugin>
              </plugins></group></optionalFileGroups>
            </installStep>
          </installSteps>
          <conditionalFileInstalls><patterns>
            <pattern>
              <dependencies operator="And"><flagDependency flag="body" value="cbbe" /></dependencies>
              <files><file source="20 Patches/cbbe.esp" destination="BodyMod - CBBE.esp" /></files>
            </pattern>
            <pattern>
              <dependencies operator="Or"><flagDependency flag="body" value="unp" /><fileDependency file="Other.esp" state="Active" /></dependencies>
              <files><file source="20 Patches/other.esp" destination="" /></files>
            </pattern>
          </patterns></conditionalFileInstalls>
        </config>
        """;

    private static readonly string[] Keys =
    {
        @"Body Mod\fomod\ModuleConfig.xml", @"Body Mod\00 Core\BodyMod.esp", @"Body Mod\00 Core\scripts\a.pex",
        @"Body Mod\10 CBBE\body.nif", @"Body Mod\11 UNP\body.nif", @"Body Mod\11 UNP\extra.nif",
        @"Body Mod\docs\readme.txt", @"Body Mod\20 Patches\cbbe.esp", @"Body Mod\20 Patches\other.esp",
    };

    private static Dictionary<string, string> Plan(string option, Func<string, bool>? installed = null)
    {
        var root = Fomod.FindModuleRoot(Keys)!;
        var choices = new List<Collection.FomodChoice> { new("Main", "Body", option) };
        return Fomod.Plan(Config, Keys, root, choices, installed)
            .GroupBy(p => p.Destination).ToDictionary(g => g.Key, g => g.Last().Source); // last write wins
    }

    [Fact]
    public void FindsTheModuleRootInsideATopFolder()
    {
        Assert.Equal("Body Mod", Fomod.FindModuleRoot(Keys));
        Assert.Equal("", Fomod.FindModuleRoot(new[] { "fomod/ModuleConfig.xml", "a.esp" }));
        Assert.Null(Fomod.FindModuleRoot(new[] { "a.esp", "notfomod/ModuleConfig.xml.bak" }));
    }

    [Fact]
    public void InstallsRequiredFilesTheChosenOptionAndItsConditionalFiles()
    {
        var plan = Plan("CBBE");
        Assert.Equal(@"Body Mod\00 Core\BodyMod.esp", plan["BodyMod.esp"]);
        Assert.Equal(@"Body Mod\00 Core\scripts\a.pex", plan[@"scripts\a.pex"]);
        Assert.Equal(@"Body Mod\10 CBBE\body.nif", plan[@"meshes\body.nif"]);
        Assert.Equal(@"Body Mod\docs\readme.txt", plan[@"Docs\BodyMod.txt"]); // SelectAll group, though not recorded
        Assert.Equal(@"Body Mod\20 Patches\cbbe.esp", plan["BodyMod - CBBE.esp"]);
        Assert.False(plan.ContainsKey("other.esp"));
        Assert.False(plan.ContainsKey(@"11 UNP\extra.nif"), "a hidden step's required option stays out");
        Assert.DoesNotContain(plan.Values, s => s.Contains("fomod"));
    }

    [Fact]
    public void FlagsFromTheOtherOptionShowItsStepAndPatches()
    {
        var plan = Plan("UNP");
        Assert.Equal(@"Body Mod\11 UNP\body.nif", plan[@"meshes\body.nif"]);
        Assert.Equal(@"Body Mod\11 UNP\extra.nif", plan[@"11 UNP\extra.nif"]); // destination defaults to the source
        Assert.Equal(@"Body Mod\20 Patches\other.esp", plan["other.esp"]);     // empty destination: file name at the root
        Assert.False(plan.ContainsKey("BodyMod - CBBE.esp"));
    }

    [Fact]
    public void FileConditionsCheckWhatIsInstalled()
    {
        Assert.True(Plan("CBBE", f => f == "Other.esp").ContainsKey("other.esp"));
        Assert.False(Plan("CBBE", _ => false).ContainsKey("other.esp"));
    }

    [Fact]
    public void HigherPriorityWins()
    {
        var keys = new[] { "fomod/ModuleConfig.xml", "low/a.nif", "high/a.nif" };
        const string config = """
            <config><requiredInstallFiles>
              <file source="high/a.nif" destination="a.nif" priority="5" />
              <file source="low/a.nif" destination="a.nif" priority="0" />
            </requiredInstallFiles></config>
            """;
        var plan = Fomod.Plan(config, keys, "", new List<Collection.FomodChoice>());
        Assert.Equal(@"high\a.nif", plan.Last(p => p.Destination == "a.nif").Source);
    }

    [Fact]
    public void AMissingFileIsAnError()
    {
        const string config = """<config><requiredInstallFiles><file source="gone.esp" /></requiredInstallFiles></config>""";
        Assert.Throws<InvalidDataException>(() => Fomod.Plan(config, new[] { "fomod/ModuleConfig.xml" }, "", new List<Collection.FomodChoice>()));
    }
}

public class ModInstallerTests
{
    [Theory]
    [InlineData(new[] { "MyMod.esp", "meshes/a.nif" }, "")]
    [InlineData(new[] { "MyMod v1/MyMod.esp", "MyMod v1/textures/a.dds" }, "MyMod v1")]
    [InlineData(new[] { "Data/SKSE/Plugins/x.dll", "readme.txt" }, "Data")]
    [InlineData(new[] { "Pack/Data/meshes/a.nif" }, @"Pack\Data")]
    public void FindsWhereDataStarts(string[] keys, string expected) =>
        Assert.Equal(expected, ModInstaller.FindDataRoot(keys.Select(Fomod.Normalize).ToList(), "x.7z"));

    [Fact]
    public void RefusesToGuessWithoutDataLikeFiles() =>
        Assert.Throws<InvalidDataException>(() => ModInstaller.FindDataRoot(new[] { "d3dx9_42.dll" }, "x.7z"));

    [Fact]
    public void InstallsAnArchiveWithoutAnInstaller()
    {
        using var game = new TempDir();
        using var downloads = new TempDir();
        var archive = ModFixtures.Zip(downloads.Path, "Plain-1.zip", new()
        {
            ["Plain/Plain.esp"] = "esp", ["Plain/meshes/a.nif"] = "nif", ["Plain/meta.ini"] = "mo2",
        });
        var mod = ModFixtures.ModFor(archive, 1, 101);
        var installed = ModInstaller.Install(game.Path, mod, archive, null);
        Assert.Equal(2, installed.Files.Count);
        Assert.Equal("esp", File.ReadAllText(Path.Combine(game.Path, "Data", "Plain.esp")));
        Assert.True(File.Exists(Path.Combine(game.Path, "Data", "meshes", "a.nif")));
        Assert.True(ModInstaller.ReadRecords(game.Path).ContainsKey(101));
    }

    [Fact]
    public void InstallsThroughTheFomodWithRecordedChoices()
    {
        using var game = new TempDir();
        using var downloads = new TempDir();
        var archive = ModFixtures.Zip(downloads.Path, "Choice-1.zip", new()
        {
            ["fomod/ModuleConfig.xml"] = """
                <config><installSteps><installStep name="Main"><optionalFileGroups><group name="Size" type="SelectExactlyOne"><plugins>
                  <plugin name="Big"><files><folder source="big" destination="" /></files></plugin>
                  <plugin name="Small"><files><folder source="small" destination="" /></files></plugin>
                </plugins></group></optionalFileGroups></installStep></installSteps></config>
                """,
            ["big/thing.nif"] = "big", ["small/thing.nif"] = "small",
        });
        var mod = ModFixtures.ModFor(archive, 2, 202, new() { new("Main", "Size", "Small") });
        ModInstaller.Install(game.Path, mod, archive, null);
        Assert.Equal("small", File.ReadAllText(Path.Combine(game.Path, "Data", "thing.nif")));
        Assert.False(Directory.Exists(Path.Combine(game.Path, "Data", "fomod")));
    }

    [Fact]
    public void AGameFolderRuleInstallsBesideTheExe()
    {
        using var game = new TempDir();
        using var downloads = new TempDir();
        var archive = ModFixtures.Zip(downloads.Path, "Preloader-1.7z.zip", new()
        {
            ["d3dx9_42.dll"] = "dll", ["vortex_override_instructions.json"] = "{}",
        });
        var mod = ModFixtures.ModFor(archive, 17230, 725261);
        var rule = new ModList.Rule { ModId = 17230, FileId = 725261, Target = "game" };
        ModInstaller.Install(game.Path, mod, archive, rule);
        Assert.Equal("dll", File.ReadAllText(Path.Combine(game.Path, "d3dx9_42.dll")));
        Assert.False(File.Exists(Path.Combine(game.Path, "vortex_override_instructions.json")));
        Assert.False(Directory.Exists(Path.Combine(game.Path, "Data")));
    }

    [Fact]
    public void TheRepositorysModlistLoads()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "numidium-dev", ModList.FileName))) dir = dir.Parent;
        Assert.NotNull(dir);
        var list = ModList.Load(Path.Combine(dir!.FullName, "numidium-dev", ModList.FileName));
        Collection.Mod Mod(int modId, int fileId) => new("x", "1", false, modId, fileId, "", 1, "x", null);
        Assert.True(list.RuleFor(Mod(30379, 1))!.Skip);
        Assert.True(list.RuleFor(Mod(17230, 725261))!.IntoGameFolder);
        Assert.Null(list.RuleFor(Mod(17230, 725753)));
    }

    [Fact]
    public void RulesForOneFileDontApplyToTheModsOtherFiles()
    {
        var list = new ModList { Mods = { new ModList.Rule { ModId = 17230, FileId = 725261, Target = "game" }, new ModList.Rule { ModId = 30379, Skip = true } } };
        Collection.Mod Mod(int modId, int fileId) => new("x", "1", false, modId, fileId, "", 1, "x", null);
        Assert.True(list.RuleFor(Mod(17230, 725261))!.IntoGameFolder);
        Assert.Null(list.RuleFor(Mod(17230, 725753)));
        Assert.True(list.RuleFor(Mod(30379, 999))!.Skip);
    }

    [Fact]
    public void LaterModsOwnWhatTheyOverwriteAndUnlistedFilesAreRemoved()
    {
        using var game = new TempDir();
        using var downloads = new TempDir();
        var first = ModFixtures.Zip(downloads.Path, "First.zip", new() { ["First.esp"] = "1", ["meshes/shared.nif"] = "first" });
        var second = ModFixtures.Zip(downloads.Path, "Second.zip", new() { ["meshes/shared.nif"] = "second" });
        var firstMod = ModFixtures.ModFor(first, 1, 11);
        var secondMod = ModFixtures.ModFor(second, 2, 22);
        ModInstaller.Install(game.Path, firstMod, first, null);
        ModInstaller.Install(game.Path, secondMod, second, null);
        Assert.Equal("second", File.ReadAllText(Path.Combine(game.Path, "Data", "meshes", "shared.nif")));
        Assert.DoesNotContain(ModInstaller.ReadRecords(game.Path)[11].Files, f => f.EndsWith("shared.nif"));

        // The collection drops the first mod: its own files go, the overwritten one stays with the second
        var json = $$"""{ "mods": [ { "name": "Second", "source": { "type": "nexus", "modId": 2, "fileId": 22, "md5": "{{secondMod.Md5}}", "fileSize": {{secondMod.Size}} } } ] }""";
        var removed = ModInstaller.RemoveUnlisted(game.Path, Collection.Parse(json));
        Assert.Equal(11, Assert.Single(removed).FileId);
        Assert.False(File.Exists(Path.Combine(game.Path, "Data", "First.esp")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(game.Path, "Data", "meshes", "shared.nif")));
        Assert.Equal(new[] { 22 }, ModInstaller.ReadRecords(game.Path).Keys);
    }
}

public class DownloadsTests
{
    [Fact]
    public void FindsDownloadsByChecksumEvenWhenRenamed()
    {
        using var downloads = new TempDir();
        var archive = ModFixtures.Zip(downloads.Path, "Mod-1-0.zip", new() { ["Mod.esp"] = "x" });
        var mod = ModFixtures.ModFor(archive, 1, 11);
        File.Move(archive, Path.Combine(downloads.Path, "Mod-1-0 (1).zip"));
        downloads.File("Other.zip", new string('y', (int)mod.Size)); // same size, different content
        var cache = new Dictionary<string, string>();

        var found = Downloads.Find(new[] { mod }, new[] { downloads.Path, Path.Combine(downloads.Path, "missing") }, cache);

        Assert.EndsWith("Mod-1-0 (1).zip", found[mod]);
        Assert.Equal(2, cache.Count); // both same-size files hashed, once
        Assert.Single(Downloads.Find(new[] { mod }, new[] { downloads.Path }, cache));
    }

    [Fact]
    public void SkipsAFileItCantRead()
    {
        using var downloads = new TempDir();
        var archive = ModFixtures.Zip(downloads.Path, "Mod.zip", new() { ["Mod.esp"] = "x" });
        var mod = ModFixtures.ModFor(archive, 1, 11);
        // A download still being written: same size as the mod, but locked
        var locked = downloads.File("Other.zip", new string('y', (int)mod.Size));
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Single(Downloads.Find(new[] { mod }, new[] { downloads.Path }));
    }
}
