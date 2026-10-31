using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Numidium.Launcher.Core;

namespace Numidium.Launcher.Tests;

public class StaleOutputsTests
{
    // A game folder where BodySlide once built "Dropped" (now gone from the groups),
    // still builds "Kept", and never built "Shipped" (a mod's own mesh)
    private static TempDir GameWithStaleOutputs()
    {
        var game = new TempDir();
        game.File(@"Data\CalienteTools\BodySlide\SliderGroups\Numidium.xml",
            """<SliderGroups><Group name="Numidium Female"><Member name="Kept"/></Group></SliderGroups>""");
        game.File(@"Data\CalienteTools\BodySlide\SliderSets\Test.osp", """
            <SliderSetInfo version="1">
                <SliderSet name="Kept"><OutputPath>meshes\a</OutputPath><OutputFile GenWeights="true">kept</OutputFile></SliderSet>
                <SliderSet name="Dropped"><OutputPath>meshes\a</OutputPath><OutputFile GenWeights="true">dropped</OutputFile></SliderSet>
                <SliderSet name="Shipped"><OutputPath>meshes\a</OutputPath><OutputFile GenWeights="true">shipped</OutputFile></SliderSet>
                <SliderSet name="Kept (Variant)"><OutputPath>meshes\A</OutputPath><OutputFile GenWeights="true">Kept</OutputFile></SliderSet>
            </SliderSetInfo>
            """);
        Nif(game, @"Data\meshes\a\kept_1.nif", "BODYTRI");
        Nif(game, @"Data\meshes\a\dropped_0.nif", "BODYTRI"); // BodySlide's: the mod ships only _1
        Nif(game, @"Data\meshes\a\dropped_1.nif", "BODYTRI");
        game.File(@"Data\meshes\a\dropped.tri", "morphs");
        Nif(game, @"Data\meshes\a\shipped_1.nif", "armor");
        File.WriteAllText(Path.Combine(game.Path, ModInstaller.RecordFile), JsonSerializer.Serialize(new[]
        {
            new ModInstaller.InstalledMod(1, 77, "Armor Mod", new() { @"Data\meshes\a\dropped_1.nif", @"Data\meshes\a\shipped_1.nif" }),
        }));
        return game;
    }

    // A header-only NIF whose string table is the given names
    private static void Nif(TempDir dir, string relative, params string[] strings)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        w.Write(0x14020007u); w.Write((byte)1); w.Write(12u); w.Write(0u); w.Write(100u);
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)0);
        w.Write((uint)strings.Length); w.Write((uint)strings.Max(s => s.Length));
        foreach (var s in strings) { w.Write((uint)s.Length); w.Write(Encoding.ASCII.GetBytes(s)); }
        w.Write(0u);
        w.Flush();
        var path = Path.Combine(dir.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ms.ToArray());
    }

    [Fact]
    public void FindsBuiltMeshesOfProjectsNoLongerBuilt()
    {
        using var game = GameWithStaleOutputs();
        Assert.Equal(new[] { @"Data\meshes\a\dropped_0.nif", @"Data\meshes\a\dropped_1.nif", @"Data\meshes\a\dropped.tri" },
            StaleOutputs.Find(game.Path));
    }

    [Fact]
    public void PutsTheModsFileBackAndDeletesBodySlidesOwn()
    {
        using var game = GameWithStaleOutputs();
        using var downloads = new TempDir();
        var archive = Path.Combine(downloads.Path, "armor.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            // Under an installer's module folder, as many archives keep it
            using var writer = new StreamWriter(zip.CreateEntry("01 Main/meshes/a/dropped_1.nif").Open());
            writer.Write("the mod's own mesh");
        }

        var stale = StaleOutputs.Find(game.Path);
        Assert.Equal(3, StaleOutputs.Restore(game.Path, stale, new Dictionary<int, string> { [77] = archive }));
        Assert.Equal("the mod's own mesh", File.ReadAllText(Path.Combine(game.Path, @"Data\meshes\a\dropped_1.nif")));
        Assert.False(File.Exists(Path.Combine(game.Path, @"Data\meshes\a\dropped_0.nif")));
        Assert.False(File.Exists(Path.Combine(game.Path, @"Data\meshes\a\dropped.tri")));
        Assert.True(File.Exists(Path.Combine(game.Path, @"Data\meshes\a\kept_1.nif")));
        Assert.Empty(StaleOutputs.Find(game.Path));
    }

    // Once the launcher records its builds, only those count: a mod's own pre-built
    // mesh (BODYTRI and all) from a project nobody builds is left alone
    [Fact]
    public void WithARecordOnlyTheLaunchersBuildsGoStale()
    {
        using var game = GameWithStaleOutputs();
        File.Delete(Path.Combine(game.Path, @"Data\meshes\a\dropped_0.nif"));
        File.Delete(Path.Combine(game.Path, @"Data\meshes\a\dropped.tri"));
        StaleOutputs.RecordBuilt(game.Path);
        Assert.Empty(StaleOutputs.Find(game.Path)); // dropped_1.nif: the mod's, never built by the launcher

        // "Kept" leaves the group: what the launcher built for it is now stale
        File.WriteAllText(Path.Combine(game.Path, @"Data\CalienteTools\BodySlide\SliderGroups\Numidium.xml"),
            """<SliderGroups><Group name="Numidium Female"><Member name="Other"/></Group></SliderGroups>""");
        Assert.Equal(new[] { @"Data\meshes\a\kept_1.nif" }, StaleOutputs.Find(game.Path));
        Assert.Equal(1, StaleOutputs.Restore(game.Path, StaleOutputs.Find(game.Path), new Dictionary<int, string>()));
        Assert.Empty(StaleOutputs.Find(game.Path));
    }

    [Fact]
    public void LeavesTheModsFilesWhenItsArchiveIsMissing()
    {
        using var game = GameWithStaleOutputs();
        var log = new List<string>();
        Assert.Equal(2, StaleOutputs.Restore(game.Path, StaleOutputs.Find(game.Path), new Dictionary<int, string>(), log.Add));
        Assert.True(File.Exists(Path.Combine(game.Path, @"Data\meshes\a\dropped_1.nif")));
        Assert.Contains(log, l => l.Contains("Armor Mod") && l.Contains("downloads folder"));
    }
}
