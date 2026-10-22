# Numidium local dev setup

Files for running the server and a test copy of Skyrim on one machine. Paths assume
the repo is at `E:\Jarldom\skymp` and the test game copy at `E:\Jarldom\SkyrimDev`.

| File | Goes to | Purpose |
|---|---|---|
| `server-settings.json` | `build/dist/server/` | Offline local server: base-game load order, Whiterun start point, `gamemodePath` pointing at `numidium-gamemode/gamemode.js` |
| `skymp5-front-config.js` | `skymp5-front/config.js` | Builds the in-game UI into `build/dist/client/Data/Platform/UI` (the real `config.js` is gitignored) |
| `launch-skyrimdev-vanilla.ps1` + `.bat` | `E:\Jarldom\` | Starts the test copy with a base-game load order, then restores the shared `plugins.txt` when the game closes |

Notes:

- `build/dist/server/gamemode.js` is emptied by the build when `BUILD_GAMEMODE` is off, so the
  server settings point at `numidium-gamemode/gamemode.js` instead.
- The default SkyMP spawn point (high above Windhelm) crashes the game in terrain loading on
  login; the Whiterun start point avoids it.
- `SkyrimSoulsRE.dll` was removed from the test copy while debugging that crash. It turned out
  not to be the cause.
