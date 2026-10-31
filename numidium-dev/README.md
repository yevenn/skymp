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
| `release.ps1` | run from a clean, pushed commit | Packages the client, builds the launcher with the same version and gathers the release's six files in `build\numidium-release\<version>`; `-Publish` creates the GitHub release through the GitHub CLI |
| `patch-racemenu-height.py` | run with the game's `Data` folder | Limits RaceMenu's Height slider to 0.95–1.05 by patching the installed `RaceMenu.bsa`'s slider script into a loose `Scripts\racemenuplugin.pex` (players patch their own copy; no modified RaceMenu file is shipped). The server clamps height to the same range either way |

## Changing the mod list (without Vortex)

`collection.json` here is the mod list players install; a release carries it to
them. Change it with `numidium-modlist` (in `numidium-launcher/src/Numidium.ModList`;
`dotnet run --project numidium-launcher/src/Numidium.ModList -- <command>`, or the
built `numidium-modlist.exe`), never through Vortex, which would deploy into the
Steam game folder:

- `check`: which mods have a newer file on Nexus, or are gone from it. SKSE plugins
  updated since August 2026 are often for Skyrim 1.7 only: check the file says
  1.6.1170 first.
- `add <archive>`: downloads come from the mod's Nexus Files tab as usual (Manual
  download). The tool reads the mod id from the file name, finds the exact Nexus file
  through Nexus's public API, and computes the MD5 from the archive. An archive of a
  file already listed under the same title is an update: it replaces that entry in
  place, keeps its installer choices and moves its rules to the new file. Archives
  with an installer need `--choice "Page|Group|Option"` (`options <archive>` lists
  them; an update keeps the earlier ones). `--after`/`--before <mod>` sets which
  mod's files win, `--optional` marks it skippable, `--dry-run` writes nothing.
  It lists the plugins the mod installs: new ones go at the end of `loadOrder`
  in `server-settings.json` and in `skyrimdev-plugins.txt` (the list only grows).
- `remove <mod>`, `after <mod> <other mod>`, `list`.

The Nexus collection page isn't updated by this. Players don't need it (the
launcher links each mod's own page), so it can stay as it is.

## Discord sign-in (servers for players)

The settings above are an offline dev server: anyone can play as any profile
number. A server for players signs them in with Discord instead, through a
Discord application of our own (not SkyMP's master server):

1. At https://discord.com/developers/applications, create an application
   ("Numidium"). Under **OAuth2**, copy the **Client ID**, reset and copy the
   **Client Secret**, and add the redirect `http://127.0.0.1:53134/callback`
   (the launcher receives Discord's answer there, on the player's own PC).
2. In `build/dist/server/server-settings.json` (gitignored; never put the secret
   in this repo), set `"offlineMode": false`, `"enableConsoleCommandsForAll": false`
   and add:
   ```json
   "numidiumAuth": {
     "discordClientId": "<Client ID>",
     "discordClientSecret": "<Client Secret>",
     "redirectPort": 53134,
     "sessionDays": 30,
     "admins": ["<your Discord user id>"],
     "profiles": { "<a Discord user id>": 1 }
   }
   ```
   `admins` get `/double`, `/racemenu` and the game console. `profiles` keeps
   characters made on the dev server (profile 1, 2, 3...) for their players; new
   players get profiles from 100 up. Discord user ids: Discord's Settings >
   Advanced > Developer Mode, then right-click a name > Copy User ID.
3. Restart the server. Its `manifest.json` now tells the launcher to sign in:
   Play (or Sign in with Discord) opens Discord in the browser, the server trades
   the code for the player's Discord id and returns a 30-day session, which the
   launcher keeps encrypted for that Windows user and gives the game. Accounts and
   sessions (hashed) are in `numidium-accounts.json` next to the server.
4. Players outside your network need TCP 3000 and UDP 7777 forwarded to this PC on
   the router, and connect to your public address. Port 53134 stays local.

The server and launcher speak plain HTTP on port 3000, so sign-in codes and
sessions cross the network unencrypted; put HTTPS in front before it matters.

## Character customization

Installed in the test copy by `install-mods.ps1`, downloaded from Nexus by hand:

| Mod | Version | Installer choices |
|---|---|---|
| XP32 Maximum Skeleton Special Extended | 5.06 | Minimal (skeleton only, no FNIS/Nemesis) |
| RaceMenu Anniversary Edition | 0.4.20.0 | everything except `ModderResource` (needs SKSE 2.2.6, game 1.6.1170) |
| BodySlide and Outfit Studio | 6.0.0 | everything except `FOMod` |
| CBBE SFW Edition | 1.1.4 | Vanilla Outfits, Face Pack, Thin + Trimmed Brows, Dirt to Beauty Marks, NeverNude underwear |
| HIMBO V5 Core (Nevernude Body) + BG-DG-DB Refits | 5.8 / 5.8a | everything |

BodySlide groups (`bodyslide/Numidium.xml`) and why:

- Female: CBBE (SFW) bodies and vanilla, Dawnguard, Dragonborn and Creation Club outfits, using
  the **CBBE NeverNude** body, not CBBE Underwear (both write `femalebody`): its underwear uses
  the game's own `femaleunderwear.dds`, while CBBE Underwear's needs the textures CBBE SFW's
  "Underwear" installer option adds (without them it shows purple).
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
