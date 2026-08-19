# modWitcherTrack — hook points

`witchertrack.ws` is additive and touches nothing. To make the game actually report,
seven one-line calls have to be added to three vanilla script files.

Every insertion **only appends a log line**. None of them changes a value, skips vanilla
code, or alters a return. Removing the mod restores the original files exactly.

The three patched files **are** shipped, in `mod/modWitcherTrack/` and in the release's
`modWitcherTrack.zip`. There is no way around it: a WitcherScript mod replaces a whole file,
so a mod that adds a line to `playerWitcher.ws` has to carry the entire `playerWitcher.ws`.
They are CDPR's scripts with seven lines added, redistributed only because the reporter
cannot function without them, and each added line is marked `// modWitcherTrack` so the
difference from vanilla is one `diff` away.

So this document is not assembly instructions - you do not have to patch anything by hand.
It exists to make the change auditable, and to be able to regenerate it: the shipped copies
match the game version they were built against, so after an update that touches those files
you re-patch your own with
`python mod/patch_mod.py --scripts "<game>\content\content0\scripts"` rather than letting
an old copy overwrite the new script.

If you use other script mods, run Script Merger afterwards.

---

## 1. `content/scripts/game/player/playerWitcher.ws`

Find `W3PlayerWitcher.OnSpawned` and add the call at the **end** of the function body,
after everything vanilla does:

```witcherscript
event OnSpawned( spawnData : SEntitySpawnData )
{
    // ... vanilla body, unchanged ...

    WT_OnPlayerSpawned( this );   // modWitcherTrack
}
```

**Why this one matters most.** `OnSpawned` fires every time the player entity is created,
which includes every savegame load. That gives the tracker a full snapshot at exactly the
moment it needs one: right after the player dies and reloads an earlier save. An
OCR-based tracker can only ever add completions, so a reload leaves it permanently ahead
of reality. This hook is what makes the run self-correcting.

### The second call in this file: `CR4Player.AddGwentCard`

```witcherscript
if (cardIndex != -1)
{
    // ... vanilla body, unchanged ...
    WT_OnGwentCardAdded( cardIndex );   // modWitcherTrack
}
```

One line at the **end** of that block in `CR4Player.AddGwentCard`, after the game has
added the card, and inside its own test that the card name resolved to a real index.

This is the only live signal a Gwent card has. There is no journal notification for one.
The collection quest was hooked for this in v0.11 and looked correct on the evidence
available then; a full session since has shown it updates when it first goes active and
then not again. `AddGwentCard` is where the game itself decides a
card is being added, so there is nowhere earlier to look.

It is also the only hook where the value is trusted rather than swept. 

---

## 2. `content/scripts/game/gui/hud/modules/hudModuleJournalUpdate.ws`

Four functions in `CR4HudModuleJournalUpdate`. These are the functions that draw the
on-screen notifications the previous version of this tracker was reading with OCR, so
hooking them is a one-to-one replacement.

Add each call at the **start** of the function body, so it still reports if vanilla
decides not to show the popup:

```witcherscript
function AddQuestUpdate( journalQuest : CJournalQuest, isQuestUpdate : bool ) : void
{
    WT_OnQuestUpdate( journalQuest );   // modWitcherTrack
    // ... vanilla body, unchanged ...
}

function AddCraftingSchematicUpdate( schematicName : name ) : void
{
    WT_OnCraftingSchematicUpdate( schematicName );   // modWitcherTrack
    // ... vanilla body, unchanged ...
}

function AddAlchemySchematicUpdate( schematicName : name ) : void
{
    WT_OnAlchemySchematicUpdate( schematicName );   // modWitcherTrack
    // ... vanilla body, unchanged ...
}

function AddMapPinUpdate( mapPinName : name ) : void
{
    WT_OnMapPinUpdate( mapPinName );   // modWitcherTrack
    // ... vanilla body, unchanged ...
}
```

---

## 3. `content/scripts/game/gameplay/effects/effectManager.ws`

```
if(owner == thePlayer && IsBuffShrine(effectType))
{
    WT_OnShrineBuff();   // modWitcherTrack
    // ... vanilla body, unchanged ...
}
```

One line, inside `W3EffectManager.OnBuffAdded`, in a block the game already guards with
its own player-and-shrine test (it is there to award the *Power Overwhelming*
achievement). 

This exists because a Place of Power is the one completion no notification covers: using
one grants an ability point and applies a buff, and that is all, so none of the four hooks 
above ever ran. The buff is what gives it away; the game names them per sign 
(`W3Effect_ShrineAxii` and its four siblings), caught in a captured session at the moment of use.

## What you get

| Hook | Effect |
|---|---|
| `OnSpawned` | full snapshot on every load, so reloads resynchronise the run |
| `AddGwentCard` | a Gwent card the moment it enters the collection |
| `AddQuestUpdate` | quest status the instant the journal changes |
| `AddCraftingSchematicUpdate` | diagram learned, by internal name |
| `AddAlchemySchematicUpdate` | formula learned, by internal name |
| `AddMapPinUpdate` | map pin changed, with the authoritative state read back from the map manager |

## Where and what

A quest, a diagram, a formula and a Gwent card have no place of their own: unlike a point
of interest, the game holds no coordinates for any of them. So the reporter sends the one
place it does know, which is `WT|v1|at|<x>|<y>|<world path>`, meaning *the player is here, now* -
immediately **before** the record that finishes something. The tracker holds the last
place it was told and hands it to whatever completes next, which is why the order matters:
a place sent after its completion arrives one record too late and lands on the following
one.

It carries no identifier on purpose. The tracker already works out what newly completed,
and that is the only way a Gwent card can be placed at all - the card sweep re-lists the
whole collection and cannot say which card is new.

The place is spent once something takes it, so it can never be attached to a second, later
completion somewhere else. It is deliberately not discarded when nothing takes it, because
every already-owned card in a sweep has to be passed through to reach the new one.

Only quests that reach `done` send one. 

## Why a notification is never the last word

Every hook above only prompts the reporter to *read the state back*, but none of them is
trusted to describe it. Points of interest and Gwent cards are read by a sweep, because
the game announces neither being cleared; and the report on load re-reads everything, so
anything missed between hooks is corrected the next time a savegame is loaded.

## Without the hooks

If you would rather not touch any vanilla file, the additive half still works, but
something has to invoke it. That means launching the game with `-net` as well and driving
the `exec` functions over the debug channel. It is a fair trade: no merged files, but two
launch flags and a network client instead of a log reader.

---

## Three traps, if you want to edit these by hand

**Encoding.** The vanilla `.ws` files are **UTF-16 LE**. An editor that silently saves as
UTF-8 produces a mod the game cannot compile, and an ordinary `grep` finds nothing in
them.

**Exactly one byte-order mark.** Decoding a UTF-16 file leaves the mark as a leading
`U+FEFF` character. Re-encoding without stripping it, and prepending a fresh mark, gives
a file with two — and the compiler rejects the second one:

```
Error [modwitchertrack]game\player\playerwitcher.ws(1): Unexpected ''
```

**Reserved words.** `state` and `quest` are compiler tokens, so neither can be used as a
parameter or variable name. The failure names the token rather than your identifier,
which makes it easy to misread:

```
Error ...witchertrack.ws(23): syntax error, unexpected TOKEN_STATE, expecting TOKEN_IDENT, near 'state'
```

The full list this project avoids is in `patch_mod.py`. Along the same lines, the lines
added to the vanilla files call ordinary functions - `WT_OnPlayerSpawned()`, which in turn
calls `WT_ReportFull()` - rather than the `exec` functions directly, because whether one
`exec` function may call another is not worth depending on.

The packaging script handles all three: it patches your vanilla files, converts the UTF-8
master of `witchertrack.ws` to UTF-16 with a single byte-order mark, checks for reserved
words, and prints each of the seven insertions with the file, function and line it landed
on. Confirming that nothing else changed is a `diff` against your own vanilla copies -
every added line ends in `// modWitcherTrack`, so the two should differ by exactly seven
lines and nothing else.
