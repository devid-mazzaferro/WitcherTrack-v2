@ -0,0 +1,99 @@
# WitcherTrack — setup

The full version of this, with screenshots and troubleshooting, is the README on the
project page. This is the offline copy that ships in the download.

Two pieces: the **tracker** (this folder) and the **in-game reporter** (`modWitcherTrack.zip`).
Both are needed.

---

## 1. The tracker

You already have it. `WitcherTrack.exe` is the whole tracker: the catalogue, the map
artwork and the interface all travel inside it, so it needs nothing beside it. A `data\`
folder placed next to it is read in preference, which is how a catalogue rebuilt after a
game update takes effect.

Run `WitcherTrack.exe`. The dashboard opens at <http://127.0.0.1:7355/>. It listens on the
loopback address only and makes no outbound connections.

Windows SmartScreen may warn about an unsigned executable: *More info* → *Run anyway*.

---

## 2. The in-game reporter

Find the folder containing `bin`, `content` and `DLC`:

| Store | Default path |
|---|---|
| Steam | `C:\Program Files (x86)\Steam\steamapps\common\The Witcher 3\` |
| GOG | `C:\Program Files (x86)\GOG Galaxy\Games\The Witcher 3 Wild Hunt GOTY\` |
| Epic | `C:\Program Files\Epic Games\The Witcher 3\` |

Extract `modWitcherTrack.zip` into a `Mods` folder there, creating `Mods` if it does not
exist. Windows will ask for administrator rights; that is expected. The result must look
exactly like this:

```
<game>\Mods\modWitcherTrack\content\scripts\
    local\witchertrack.ws
    game\player\playerWitcher.ws
    game\gui\hud\modules\hudModuleJournalUpdate.ws
    game\gameplay\effects\effectManager.ws
```

The three files under `game\` are the game's own scripts with seven one-line calls added.
Each only writes a line to the log; nothing else is changed.

> **If you use other script mods, run Script Merger afterwards.** Those three files are
> commonly edited by other mods, and without merging, whichever loads last wins.

---

## 3. Turn on the script log

The game only writes its script log when started with `-debugscripts`.

- **Steam** — right-click the game → *Properties* → *General* → *Launch Options*, and put
  `-debugscripts` there.
- **GOG Galaxy** — the cog next to *Play* → *Manage installation* → *Configure*, and add
  `-debugscripts` to the executable arguments.
- **Anything else** — make a desktop shortcut to `bin\x64\witcher3.exe` (or
  `bin\x64_dx12\witcher3.exe` for DirectX 12), open its *Properties*, and append
  `-debugscripts` to *Target*, outside the quotes. Launch from that shortcut.

---

## 4. Check it works

Start the game and load a savegame, then open `Documents\The Witcher 3\scriptslog.txt`. It
should contain lines beginning `WT|v1|`. Start `WitcherTrack.exe` — before or after the
game, the order does not matter — and the dashboard fills in.

**No `WT|` lines?** The game is running pre-compiled scripts with logging stripped. Delete
the `.redscripts` files in `<game>\content\content0\` and start it again; it regenerates
them with logging on, and that first launch takes longer than usual.

**Script compilation error on startup?** Remove `Mods\modWitcherTrack` and the game starts
normally. The error text names the file and line — that is the useful part of a report.

---

## 5. OBS

Add a **Browser** source pointing at <http://127.0.0.1:7355/overlay> and turn off *Shutdown
source when not visible*. The overlay is transparent and composites over gameplay. 
Resize and reposition at your own liking.

---

## Uninstalling

Delete `Mods\modWitcherTrack`, remove `-debugscripts`, delete this folder. Nothing else is
touched: the mod never writes to disk, never modifies a savegame, and never alters game
state, and `run.json` in this folder is the only file the tracker writes.