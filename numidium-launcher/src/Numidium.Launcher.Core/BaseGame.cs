namespace Numidium.Launcher.Core;

/// <summary>
/// The base game the Numidium copy is made of: Skyrim Special Edition 1.6.1170's
/// files as Steam ships them (English), with their size and SHA-1 from Steam's
/// depot manifests, and the depot each comes in. Players' Steam folders also hold
/// their own mods, script extenders, ReShade, screenshots and so on, none of which
/// may reach the copy, so this is an allowlist.
/// Creation Club content and Skyrim.ccc are left out on purpose: they load
/// implicitly and would put plugins in the load order the server doesn't have.
/// </summary>
public static class BaseGame
{
    /// <summary>A file of 1.6.1170. <paramref name="Optional"/>: the game runs without it,
    /// and players often replace it (an empty BGS_Logo.bik skips the intro), so it never
    /// makes the launcher download a depot or remove the copy's own.</summary>
    public sealed record Entry(string Path, long Size, string Sha1, string Depot, bool Optional = false);

    /// <summary>A Steam depot of Skyrim SE at the manifest 1.6.1170 shipped with.</summary>
    public sealed record Depot(string Id, string Manifest, long Size, string Contents);

    public static readonly Depot[] Depots =
    {
        new("489833", "1914580699073641964", 37157144, "the game executable"),
        new("489832", "8042843504692938467", 8566183335, "the master files, textures and interface"),
        new("489831", "8442952117333549665", 7492390909, "meshes, sounds and voices"),
    };

    /// <summary>Plugins the game loads implicitly, in their fixed order.</summary>
    public static readonly string[] Masters =
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    public static readonly Entry[] Files =
    {
        new(@"SkyrimSE.exe", 37157144, "1c9636207b4613254b03d76c410540e10b539dc9", "489833"),
        new(@"bink2w64.dll", 391360, "b87186c17b2ab472c2d8987751f4c56ec7999470", "489832"),
        new(@"Data\Dawnguard.esm", 25885111, "06a27b47a3d395df1ee53700dfd16116a7639215", "489832"),
        new(@"Data\Dragonborn.esm", 64663863, "58298dd4d7f98d0af48a4c0bfefcd6026e9f8040", "489832"),
        new(@"Data\HearthFires.esm", 3977420, "4951925a1956853bb0dbda3fbf40a874b1eb3a5d", "489832"),
        new(@"Data\Skyrim - Interface.bsa", 105799354, "5032978a9087fa971a00e5c567bb08c60f7b1ce5", "489832"),
        new(@"Data\Skyrim - Shaders.bsa", 67308970, "b77e0bb5ff919220edc86256cf896aa62a499921", "489832"),
        new(@"Data\Skyrim - Textures0.bsa", 652490581, "30f22217d50c3fbfff97ea8eb8910f8efd69461b", "489832"),
        new(@"Data\Skyrim - Textures1.bsa", 1511492648, "6dbde689a4130958014f23aa667d3eb01f4f2c90", "489832"),
        new(@"Data\Skyrim - Textures2.bsa", 1345109830, "2ba4386fadfabd39ad86f2bc7515303ae8d1d256", "489832"),
        new(@"Data\Skyrim - Textures3.bsa", 1406304717, "1d19f67477562e52c28d081776e23d196dc15946", "489832"),
        new(@"Data\Skyrim - Textures4.bsa", 1265325033, "92b9a7ff3dc46d43a02ce6559f44dd8d8f755a7a", "489832"),
        new(@"Data\Skyrim - Textures5.bsa", 813639992, "be889f8ddf2798f8047c0a6a8d3401934ed8e180", "489832"),
        new(@"Data\Skyrim - Textures6.bsa", 97210200, "8e6c15926fafad25683d2f872a009f27e1b12481", "489832"),
        new(@"Data\Skyrim - Textures7.bsa", 677006639, "d63392dbda15432e7ca882c401fe68db0f9b5f40", "489832"),
        new(@"Data\Skyrim - Textures8.bsa", 255927303, "b4f5b61f1aa1cb39f0b741c36179a286d2ff9ac3", "489832"),
        new(@"Data\Skyrim.esm", 249753412, "0273422b62a4d39737dcb03e3eb067922aae074a", "489832"),
        new(@"Data\Update.esm", 18874041, "06ae741c9e11f765e4f902d1f8813cd6d9860a90", "489832"),
        new(@"High.ini", 906, "9cc819d3bbd7b16ede914e04b5c8463dd8a0bfbd", "489832", Optional: true),
        new(@"Low.ini", 898, "a95d00379824c11c92badf3a7a56dbc73a42d565", "489832", Optional: true),
        new(@"Medium.ini", 898, "2057781e4ac78bc99c74d7b0704fb17a9c353d1a", "489832", Optional: true),
        new(@"SkyrimSELauncher.exe", 4713472, "70db49944b12b7bf74d0bb9e677e5b00c3a97d3d", "489832", Optional: true),
        new(@"Skyrim_Default.ini", 1859, "71142431f6d1913d589cfb09b86de1c2e3affd2b", "489832", Optional: true),
        new(@"steam_api64.dll", 298384, "39cac2889d8ca497ee0d8434fc9f6966f18fa336", "489832"),
        new(@"Ultra.ini", 911, "105913329ba0b872809e5d25d81285fd004f8279", "489832", Optional: true),
        new(@"Data\Skyrim - Animations.bsa", 65155583, "cf5db82a0641ca126429db4a0ee4989eab7c7348", "489831"),
        new(@"Data\Skyrim - Meshes0.bsa", 1153126177, "3ef11ac67da5ebfe928c04ca097ae40508706ac7", "489831"),
        new(@"Data\Skyrim - Meshes1.bsa", 378762772, "46725c10726702dbfab6861701286dac25f48b5b", "489831"),
        new(@"Data\Skyrim - Misc.bsa", 17713449, "b7f1abc81020cd3c926e7d01a784e32df1b2eefc", "489831"),
        new(@"Data\Skyrim - Sounds.bsa", 1538656059, "c32c29f66b202f8e844ba9fa5943b0553c4ba4d8", "489831"),
        new(@"Data\Skyrim - Voices_en0.bsa", 1807969854, "ebaea6bba0ab9283bb1d712d2ec196e5aaafb05f", "489831"),
        new(@"Data\Video\BGS_Logo.bik", 13835808, "87ca9c1a46c93262be00b1688106f3b269ebcac4", "489831", Optional: true),
        new(@"installscript.vdf", 648, "d2869dc62902e7107aaf11afbf439b09928caec4", "489831", Optional: true),
    };

    public static Depot DepotOf(Entry entry) => Depots.First(d => d.Id == entry.Depot);
}
