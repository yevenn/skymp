# Numidium local dev setup

Files for running the server and a test copy of Skyrim on one machine. Paths assume
the repo is at `E:\Jarldom\skymp` and the test game copy at `E:\Jarldom\SkyrimDev`.

| File | Goes to | Purpose |
|---|---|---|
| `server-settings.json` | `build/dist/server/` | Offline local server: test load order, Whiterun start point, `gamemodePath` pointing at `numidium-gamemode/gamemode.js`, and console commands (`additem` etc.) allowed for everyone. Never use `enableConsoleCommandsForAll` on a public server |
| `skyrimdev-plugins.txt` | `E:\Jarldom\` | Plugins the launcher enables in the test copy; keep it in sync with the server's `loadOrder` |
| `skymp5-front-config.js` | `skymp5-front/config.js` | Builds the in-game UI into `build/dist/client/Data/Platform/UI` (the real `config.js` is gitignored) |
| `launch-skyrimdev-vanilla.ps1` + `.bat` | `E:\Jarldom\` | Starts the test copy with the plugins in `skyrimdev-plugins.txt`, then restores the shared `plugins.txt` when the game closes |
| `install-mods.ps1` | run from anywhere | Installs the character customization mods and clothing refits from `E:\Jarldom\mod-downloads` into the test copy with the chosen installer options, recording every file in `E:\Jarldom\mod-installs\<mod>.json` and backing up replaced files to `E:\Jarldom\mod-backups\<mod>\` |
| `bodyslide/Numidium.xml` | `<Data>\CalienteTools\BodySlide\SliderGroups\` | BodySlide groups "Numidium Female" / "Numidium Male": exactly one variant per output file |
| `bodyslide/add-refits-to-groups.py` | run with `SliderSets` and `Numidium.xml` | Adds the clothing refit projects to the groups and reports output files claimed twice; its skip list records which variant won each clash. Dry run unless `--write` |
| `Build Numidium Bodies.bat` | `E:\Jarldom\` | Builds both groups with BodySlide's command line, with morphs (`--trimorphs`) for RaceMenu's body sliders |
| `package-client.ps1` | run after a build | Packages our client files from `build\dist\client` into `build\numidium-client\` (a zip plus `numidium-client.json` with version, size and SHA-256) for the launcher to install. Leaves out developer files, the per-player settings file and Address Library for other game versions; SKSE isn't included, the launcher fetches it from its own site. Also copies `numidium-modlist.json`, and with `-Collection <archive or collection.json>` the Nexus collection's `collection.json` |
| `numidium-modlist.json` | next to the client package | What the Nexus collection can't say: mods the launcher installs itself (SKSE) and per-file install rules, e.g. a DLL for the game folder. The launcher installs the collection's mods with the FOMOD choices recorded in its `collection.json` |
| `patch-racemenu-height.py` | run with the game's `Data` folder | Limits RaceMenu's Height slider to 0.95–1.05 by patching the installed `RaceMenu.bsa`'s slider script into a loose `Scripts\racemenuplugin.pex` (players patch their own copy; no modified RaceMenu file is shipped). The server clamps height to the same range either way |

## Character customization

Installed in the test copy by `install-mods.ps1`, downloaded from Nexus by hand:

| Mod | Version | Installer choices |
|---|---|---|
| XP32 Maximum Skeleton Special Extended | 5.06 | Minimal (skeleton only, no FNIS/Nemesis) |
| RaceMenu Anniversary Edition | 0.4.20.0 | everything except `ModderResource` (needs SKSE 2.2.6, game 1.6.1170) |
| BodySlide and Outfit Studio | 6.0.0 | everything except `FOMod` |
| CBBE SFW Edition | 1.1.4 | Vanilla Outfits, Face Pack, Thin + Trimmed Brows, Dirt to Beauty Marks, CBBE Underwear (normal, black) |
| HIMBO V5 Core (Nevernude Body) + BG-DG-DB Refits | 5.8 / 5.8a | everything |

BodySlide groups (`bodyslide/Numidium.xml`) and why:

- Female: CBBE (SFW) bodies and vanilla, Dawnguard, Dragonborn and Creation Club outfits, using
  the **CBBE Underwear** body, not NeverNude (both write `femalebody`).
- Male: HIMBO Vanilla **Nevernude** body (not the SOS or High Poly variants) and the base game
  refits; "No Skirt Bones" variants since there's no physics mod; HIMBO's Creation Club refits
  are a separate download and not included.
- Shared default shapes: **CBBE Vanilla** and **HIMBO Default** presets. Every player needs the
  same built meshes; RaceMenu sliders vary characters from there.

## Clothing refits

Modded clothing ships vanilla-shaped bodies; these BodySlide projects rebuild it for CBBE and HIMBO
(also installed by `install-mods.ps1`, built with the groups above):

| Download | Covers | Notes |
|---|---|---|
| [Sentinel - 3BA Conversion](https://www.nexusmods.com/skyrimspecialedition/mods/159370) | Sentinel, City Guards, Priests and Acolytes (female) | 3BA shares CBBE's sliders. Adds `Sentinel - 3BA.esp` (light, female meshes for 12 armor addons), appended to the load order. Some of its sets write to another set's mesh; the losers (gilded ebony, berserk light, Dawnstar heavy 1st person, officer 2) keep Sentinel's own female meshes |
| [Sentinel - HIMBO Refit](https://www.nexusmods.com/skyrimspecialedition/mods/175029) | the same, male | |
| [Mage Clothing Expansion](https://www.nexusmods.com/skyrimspecialedition/mods/128173) 2.0.1 | itself | Only `CalienteTools` installed; same version as the load order's plugin. Modest variants chosen |
| [Moon Monk's Robes - CBBE Patch](https://www.nexusmods.com/skyrimspecialedition/mods/82495) | Moon Monk's Robes | Fixes the wrist gap with CBBE hands; the robes stay vanilla-shaped |

Stay vanilla-shaped, no conversion exists or fits: Common Clothes and Armors, Cheddar's Mashups,
Moon Monk's Robes, More Craftable Equipment (6 pieces) and Heavy Legion (its HIMBO refit targets an
older version's mesh paths). Height and Body Scales still apply in them; only the body shape is
vanilla.

The new plugins were appended to the load order so earlier plugins keep their slots: saved
inventories store raw form ids, so after launch only ever add plugins at the end.

Notes:

- `build/dist/server/gamemode.js` is emptied by the build when `BUILD_GAMEMODE` is off, so the
  server settings point at `numidium-gamemode/gamemode.js` instead.
- The default SkyMP spawn point (high above Windhelm) crashes the game in terrain loading on
  login; the Whiterun start point avoids it.
- `SkyrimSoulsRE.dll` was removed from the test copy while debugging that crash. It turned out
  not to be the cause.
