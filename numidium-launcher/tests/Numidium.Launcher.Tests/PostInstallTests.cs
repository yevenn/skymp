using System.Buffers.Binary;
using Numidium.Launcher.Core;

namespace Numidium.Launcher.Tests;

public class HeightPatchTests
{
    private static byte[] FloatArgs(params float[] values)
    {
        var bytes = new byte[values.Length * 5];
        for (var i = 0; i < values.Length; i++)
        {
            bytes[i * 5] = 4;
            BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(i * 5 + 1), values[i]);
        }
        return bytes;
    }

    private static byte[] Script(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void ChangesOnlyTheHeightRange()
    {
        var before = new byte[] { 1, 2, 3 };
        var biceps = FloatArgs(0.1f, 2.00f, 0.01f); // another slider ending in 2.00 stays as it is
        var pex = Script(before, FloatArgs(0.25f, 2.00f, 0.01f), biceps);
        var patched = HeightPatch.Patch(pex);
        Assert.Equal(Script(before, FloatArgs(HeightPatch.Min, HeightPatch.Max, 0.01f), biceps), patched);
        Assert.Equal(pex.Length, patched.Length);
    }

    [Fact]
    public void RefusesAScriptItDoesntKnow()
    {
        Assert.Throws<InvalidDataException>(() => HeightPatch.Patch(FloatArgs(0.1f, 2.00f, 0.01f)));
        var twice = Script(FloatArgs(0.25f, 2.00f, 0.01f), FloatArgs(0.25f, 2.00f, 0.01f));
        Assert.Throws<InvalidDataException>(() => HeightPatch.Patch(twice));
    }

    [Fact]
    public void ApplyWithoutRaceMenuDoesNothing()
    {
        using var game = new TempDir();
        Assert.False(HeightPatch.Apply(game.Path));
        Assert.False(File.Exists(HeightPatch.LoosePath(game.Path)));
    }

    // The real RaceMenu.bsa and the script numidium-dev/patch-racemenu-height.py made from it,
    // when NUMIDIUM_RACEMENU_DATA points at a Data folder that has both (SkyrimDev's)
    [Fact]
    public void MatchesThePythonPatchOnTheRealArchive()
    {
        var data = Environment.GetEnvironmentVariable("NUMIDIUM_RACEMENU_DATA");
        if (string.IsNullOrEmpty(data)) return;
        using var game = new TempDir();
        Directory.CreateDirectory(Path.Combine(game.Path, "Data"));
        File.Copy(Path.Combine(data, "RaceMenu.bsa"), Path.Combine(game.Path, "Data", "RaceMenu.bsa"));
        Assert.True(HeightPatch.Apply(game.Path));
        Assert.Equal(File.ReadAllBytes(Path.Combine(data, "Scripts", "racemenuplugin.pex")), File.ReadAllBytes(HeightPatch.LoosePath(game.Path)));
        Assert.True(HeightPatch.Apply(game.Path)); // already patched: fine
    }
}

public class BodiesTests
{
    [Fact]
    public void NeedsBodySlideAndTheGroups()
    {
        using var game = new TempDir();
        Assert.False(Bodies.CanBuild(game.Path));
        game.File(@"Data\CalienteTools\BodySlide\BodySlide.exe");
        Assert.False(Bodies.CanBuild(game.Path));
        game.File(@"Data\CalienteTools\BodySlide\SliderGroups\Numidium.xml", "<SliderGroups/>");
        Assert.True(Bodies.CanBuild(game.Path));
    }

    [Fact]
    public void PointsBodySlideAtTheCopyInsteadOfSteam()
    {
        using var game = new TempDir();
        Assert.False(Bodies.PointAtCopy(game.Path), "no BodySlide yet");
        var config = game.File(@"Data\CalienteTools\BodySlide\Config.xml", """
            <Config>
                <!-- 4 = SKYRIMSE -->
                <TargetGame>4</TargetGame>
                <GameDataPath>E:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data\</GameDataPath>
                <OutputDataPath></OutputDataPath>
            </Config>
            """);
        Assert.True(Bodies.PointAtCopy(game.Path));
        var text = File.ReadAllText(config);
        Assert.Contains($"<GameDataPath>{Path.Combine(game.Path, "Data")}\\</GameDataPath>", text);
        Assert.Contains("<!-- 4 = SKYRIMSE -->", text); // the rest is kept
        Assert.Contains("<TargetGame>4</TargetGame>", text);
        Assert.False(Bodies.PointAtCopy(game.Path), "already pointing at the copy");
    }

    [Fact]
    public void RebuildsWhenGroupsOrModsChange()
    {
        using var game = new TempDir();
        var groups = game.File(@"Data\CalienteTools\BodySlide\SliderGroups\Numidium.xml", "<SliderGroups>v1</SliderGroups>");
        Assert.False(Bodies.IsUpToDate(game.Path), "never built");
        File.WriteAllText(Path.Combine(game.Path, Bodies.StateFile), Bodies.Fingerprint(game.Path));
        Assert.True(Bodies.IsUpToDate(game.Path));

        File.WriteAllText(groups, "<SliderGroups>v2</SliderGroups>");
        Assert.False(Bodies.IsUpToDate(game.Path), "groups changed");
        File.WriteAllText(Path.Combine(game.Path, Bodies.StateFile), Bodies.Fingerprint(game.Path));

        using var downloads = new TempDir();
        var archive = ModFixtures.Zip(downloads.Path, "Outfit.zip", new() { ["Outfit.esp"] = "x" });
        ModInstaller.Install(game.Path, ModFixtures.ModFor(archive, 9, 99), archive, null);
        Assert.False(Bodies.IsUpToDate(game.Path), "a mod was installed");
    }
}
