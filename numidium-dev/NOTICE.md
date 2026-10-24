# Numidium: licenses and credits

Numidium is a roleplay server for The Elder Scrolls V: Skyrim Special Edition,
built on SkyMP. It is not affiliated with or endorsed by Bethesda Softworks,
ZeniMax Media or the authors of the mods it uses.

## Source code

This build was made from commit `{commit}` of
https://github.com/yevenn/skymp ({source}).

- **SkyMP client, SkyrimPlatform, Papyrus scripts and in-game UI** (everything
  under `Data\Platform`, `Data\SKSE\Plugins\SkyrimPlatform.dll`,
  `Data\SKSE\Plugins\MpClientPlugin.dll`, `Data\Scripts` and `Data\Interface`
  from this package): GNU General Public License v3.0, see `GPL-3.0.txt`. By the
  SkyMP team (github.com/skyrim-multiplayer/skymp) and Keizaal Online
  (github.com/skyrim-roleplay/skymp), with Numidium's changes.
- **The Numidium server** (not in this package; what Numidium's servers run):
  GNU Affero General Public License v3.0, see `AGPL-3.0.txt`. Its source is the
  same repository.
- **Numidium launcher and gamemode**: in the same repository.

## Bundled third-party files

- **Address Library for SKSE Plugins** by meh321
  (https://www.nexusmods.com/skyrimspecialedition/mods/32444): the 1.6.1170
  `versionlib` files in `Data\SKSE\Plugins`, shared with the author's permission
  to upload with credit.
- **Chromium Embedded Framework** (`libcef.dll` and its runtime files), BSD
  license: `third-party\cef-prebuilt.txt`. Chromium's own component notices are
  shown by the in-game browser at `chrome://credits`.
- **Node.js** (`libnode.dll`): `third-party\node-embedder-api-prebuilt.txt`.
- **Libraries built into the SkyMP and SkyrimPlatform DLLs**: each one's notice
  is in `third-party\`, named after its package.
- **TiltedUI, TiltedCore, TiltedHooks, TiltedReverse** by Tilted Phoques, and the
  fonts Black Horse Courier, Tavern and Tavern Light: see `THIRD_PARTY_LICENSES.txt`.

## The launcher

`Numidium.Launcher.exe` is a self-contained .NET application. It includes the
.NET runtime (MIT, © .NET Foundation and Contributors), SharpCompress (MIT,
© Adam Hathcock) and System.Security.Cryptography.ProtectedData (MIT,
© .NET Foundation).

## Mods

Numidium's mods are not part of this package. Players download them from Nexus
Mods through the Numidium RP collection, each under its author's terms; see
`numidium-dev/mod-permissions.md` in the repository. RaceMenu's height limit and
the BodySlide bodies are made on the player's own PC from their own downloads.
