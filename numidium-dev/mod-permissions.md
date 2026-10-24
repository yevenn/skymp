# Mod permissions

What each mod in the Numidium RP collection allows, read from its Nexus page's
"Permissions and credits" on 2026-10-24, and what Numidium's own release ships.
Recheck a mod's page before relying on a line here: authors change them.

**Upload** is what matters for hosting a mod's files ourselves (one-click
installs without Nexus). **Modify** matters for shipping changed copies.

## What we do today, and why it's allowed

- The **collection** on Nexus only lists files; players download them from Nexus
  themselves. That is what collections are for, whatever a mod's upload rule.
- The launcher installs those downloads into the player's own game copy.
- **RaceMenu's height limit** is patched on the player's PC from their own
  RaceMenu download. RaceMenu says "You may not redistribute any files under any
  circumstances", so a patched file must never be shipped.
- **Bodies and outfits** are built by BodySlide on the player's PC. BodySlide says
  built meshes may not be released without permission; ours never leave the PC.
  Our `Numidium.xml` slider groups are our own file.
- **SKSE** is downloaded by the launcher from its own site (silverlock.org), never
  re-hosted: SKSE allows no uploads.

## The collection's mods

| Mod | Nexus | Upload | Modify | Notes |
|---|---|---|---|---|
| Address Library for SKSE Plugins | 32444 | yes, credit | ask | Our client package ships its 1.6.1170 `versionlib` files: credit meh321 |
| SSE Engine Fixes | 17230 | yes, credit | yes, credit | |
| powerofthree's Tweaks | 51073 | yes, credit | yes, credit | |
| Pristine Vanilla Movement | 66635 | yes, credit | yes, credit | |
| New Legion | 30468 | yes, credit | yes, credit | |
| Nirn Necessities - SMP Accessories | 112481 | yes, credit | yes, credit | Edits should require the original mod |
| The Great Cities - Minor Cities and Towns | 20272 | yes, credit | ask | |
| The Great City of Solitude | 22243 | yes, credit | ask | |
| Cheddar's Mashups | 182534 | yes, credit | ask | Uses other authors' assets: their rules apply too |
| MCM Helper | 53000 | MIT license | MIT license | |
| Constructible Object Custom Keyword System | 81409 | MIT license | MIT license | SkyUI parts follow SkyUI's terms |
| SSE Display Tweaks | 34705 | MIT license | MIT license | |
| Simple Dual Sheath | 50049 | MIT license | MIT license | |
| Nemesis Unlimited Behavior Engine | 60033 | GPL-3.0 | GPL-3.0 | Redistribution must carry the license and source |
| Skyrim Souls RE | 27859 | GPL-3.0 (modding exception) | GPL-3.0 | |
| More Craftable Equipment | 44666 | CC BY-NC-SA 4.0 | CC BY-NC-SA 4.0 | Credit all contributors, non-commercial |
| Crash Logger | 59818 | no (Nexus) | ask | "See open source LICENSE on GitHub which trumps Nexus": check it |
| Capital Windhelm Expansion | 42990 | **no** | patches OK | "Just don't reupload the whole mod here or anywhere else" |
| Actor Limit Fix | 32349 | **no** | ask | |
| Animation Queue Fix | 82395 | **no** | ask | |
| Arena - Markarth Side Town | 114252 | **no** | ask | |
| Cloaks and Capes | 2019 | **no** | ask | |
| Common Clothes | 5063 | **no** (nothing granted) | no | The page states no permissions |
| Common Clothing Expansion | 134088 | **no** | yes, credit | Files using it must stay on Nexus |
| Granite Hill | 14658 | **no** | ask | |
| JK's Riverwood | 2013 | **no** | ask | |
| JK's Whiterun | 2259 | **no** | ask | |
| JK's Whiterun Outskirts | 78351 | **no** | ask | |
| Mage Clothing Expansion | 128173 | **no** | yes, credit | Body conversions always allowed, must require the mod |
| Moon Monk's Robes | 82495 | **no** | yes, credit | Patches ship only the files they change |
| Native EditorID Fix | 85260 | **no** | ask | |
| Nirn Necessities (Xtudo's patches, 2K textures) | 133857 | **no** | ask | |
| Northkeep | 165638 | **no** | yes, credit | |
| Oakwood | 61007 | **no** | ask | |
| Oars Rest | 41812 | **no** | ask | |
| Sentinel - An Equipment Overhaul | 100985 | **no** | yes, credit | Sentinel Resources belong to their original authors |
| Sentinel - HIMBO Refit | 175029 | **no** | ask | |
| Sentinel - 3BA Conversion | 159370 | **no** | yes, credit | |
| SkyPatcher | 106659 | **no** | ask | |
| SkyUI | 12604 | **no** | yes, credit | Patches must require the original |
| Vanilla hair remake | 63979 | **no** | yes, credit | |
| RaceMenu | 19080 | **no** | no | "You may not redistribute any files under any circumstances" |
| BodySlide and Outfit Studio | 201 | **no** | ask | Presets are free to share; built meshes need permission |
| CBBE SFW Edition | 74257 | ask first | rules on page | Free to use for outfits with credit and a link |
| Skyrim Script Extender (SKSE64) | 30379 | **no** | no | The launcher downloads it from silverlock.org |
| HIMBO | 46311 | not read | not read | Page needs a Nexus login (adult flag): check by hand |
| XP32 Maximum Skeleton Special Extended | 1988 | not read | not read | Page needs a Nexus login (adult flag): check by hand |

**Totals:** 16 of 47 could be re-hosted by us (9 with credit, 7 under open
licenses), 28 can't be without asking their authors, 1 must be asked first (CBBE),
and 2 still need reading.

So a full one-click install hosted by us isn't possible today. What is possible:
host the 16 permitted ones (with credits and licenses), which cuts a player's
manual Nexus downloads from 47 to about 31; ask the other authors; or use Nexus's
own download API for Premium members (needs a registered Nexus app).

## What Numidium's own release ships

The release's client zip and launcher contain:

| Part | License | What it requires |
|---|---|---|
| SkyMP client, SkyrimPlatform, Papyrus scripts, in-game UI | GPL-3.0 | Ship the license text and the source (or where to get it: github.com/yevenn/skymp at the release's commit) |
| SkyMP server (our modified copy, run for players) | AGPL-3.0 | Players using the server must be able to get its source: push every change we run |
| Address Library `versionlib` files | Nexus: upload with credit | Credit meh321 |
| Chromium Embedded Framework, Node.js (`libcef`, `libnode` and friends) | BSD-3-Clause, MIT and others | Ship their license notices |
| Fonts (Tavern, Black Horse Courier, Tavern Light) | OFL / free, Tavern Light non-commercial | Keep the credits in `THIRD_PARTY_LICENSES` |
| TiltedUI/Core/Hooks/Reverse | Tilted Phoques notice | Keep the notice |
| Numidium launcher (SharpCompress, System.Security.Cryptography.ProtectedData) | MIT | Ship the notices |

Gaps to fix before releases go to more players:
- The client zip has no license texts, no `THIRD_PARTY_LICENSES` and no Address
  Library credit; the release notes name the commit but not the licenses.
- Under the AGPL, players of a public server need to be able to get the source
  of what it runs: push every server change before running it for players.
