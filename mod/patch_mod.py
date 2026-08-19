#!/usr/bin/env python3
"""
Insert the modWitcherTrack hook calls into two vanilla Witcher 3 script files.

The vanilla sources are UTF-16 LE with CRLF line endings; the game will not compile
them if that changes, so the output is written back in exactly the same encoding.

WitcherScript requires every `var` declaration to appear at the top of a function body
before any statement, so a call cannot simply be prepended to the body: it is inserted
after the last declaration instead.
"""

import argparse
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

# Where the vanilla sources are read from and where the patched copies are written.
# Both are overridable, because the vanilla scripts are not in this repository: they
# belong to the game, and every user already has them under
#   <game>\content\content0\scripts
# Extract that folder (or point --scripts straight at it) and the patcher does the rest.
DEFAULT_SRC = HERE / "vanilla"
DEFAULT_OUT = HERE / "modWitcherTrack/content/scripts/game"

MARK = "  // modWitcherTrack"


def read(path):
    raw = path.read_bytes()
    assert raw[:2] == b"\xff\xfe", f"{path} is not UTF-16 LE with a BOM"

    # Decoding the whole file leaves the byte-order mark as a leading U+FEFF character.
    # It has to be stripped here, because write() adds the mark back as bytes. Leaving it
    # in produces a file with two marks, and the game's script compiler rejects the second
    # one with "Unexpected ''" on line 1.
    text = raw.decode("utf-16-le").lstrip("﻿")
    return text.split("\r\n")


def write(path, lines):
    path.parent.mkdir(parents=True, exist_ok=True)
    text = "\r\n".join(lines).lstrip("﻿")
    path.write_bytes(b"\xff\xfe" + text.encode("utf-16-le"))


def find_function(lines, signature_pattern):
    """Return the index of the line holding the opening brace of a function body."""
    rx = re.compile(signature_pattern)
    for i, line in enumerate(lines):
        if rx.search(line):
            # The opening brace is on this line or the next non-blank one.
            for j in range(i, min(i + 4, len(lines))):
                if lines[j].strip() == "{":
                    return j
            if lines[i].rstrip().endswith("{"):
                return i
    raise SystemExit(f"could not locate a function matching {signature_pattern!r}")


def insert_after_declarations(lines, brace_index, call):
    """Insert a call after the run of `var` declarations that opens a function body."""
    i = brace_index + 1
    last_declaration = brace_index

    while i < len(lines):
        stripped = lines[i].strip()
        if stripped.startswith("var "):
            last_declaration = i
        elif stripped and not stripped.startswith("//"):
            break
        i += 1

    at = last_declaration + 1
    lines.insert(at, f"\t\t{call}{MARK}")
    return at


def insert_inside_block(lines, condition_pattern, call):
    """Insert a call as the first statement of the `if` block a condition opens.

    Used where the game already tests for exactly the situation we want to react
    to, so that the test does not have to be duplicated - and cannot drift from
    the game's own.
    """
    rx = re.compile(condition_pattern)

    for i, line in enumerate(lines):
        if not rx.search(line):
            continue

        # The opening brace is on this line or the next non-blank one.
        for j in range(i, min(i + 4, len(lines))):
            if lines[j].strip() == "{":
                lines.insert(j + 1, f"\t\t\t{call}{MARK}")
                return j + 1
            if lines[j].rstrip().endswith("{"):
                lines.insert(j + 1, f"\t\t\t{call}{MARK}")
                return j + 1

        raise SystemExit(f"found {condition_pattern!r} but no block brace after it")

    raise SystemExit(f"could not locate a condition matching {condition_pattern!r}")


def insert_at_block_end(lines, condition_pattern, call, start=0):
    """Insert a call as the *last* statement of the `if` block a condition opens.

    Different from insert_inside_block in where it lands and in what it is for:
    this reports something the block has finished doing, so it has to sit after
    the block's own work rather than in front of it.

    `start` scopes the search, because a condition as ordinary as
    `if (cardIndex != -1)` occurs in more than one function in the same file and
    the first match is not necessarily the wanted one.
    """
    rx = re.compile(condition_pattern)

    for i in range(start, len(lines)):
        if not rx.search(lines[i]):
            continue

        brace = None
        for j in range(i, min(i + 4, len(lines))):
            if lines[j].strip() == "{" or lines[j].rstrip().endswith("{"):
                brace = j
                break

        if brace is None:
            raise SystemExit(f"found {condition_pattern!r} but no block brace after it")

        depth = 0
        for j in range(brace, len(lines)):
            depth += lines[j].count("{") - lines[j].count("}")
            if depth == 0:
                lines.insert(j, f"\t\t\t{call}{MARK}")
                return j

        raise SystemExit(f"unbalanced braces after {condition_pattern!r}")

    raise SystemExit(f"could not locate a condition matching {condition_pattern!r}")


def find_one(root, filename):
    """Locate a vanilla script by name, wherever the game keeps it.

    The mod mirrors the game's own directory layout, so the relative path has to
    come from the game rather than be assumed here - it differs between the
    files this patcher touches and is not worth hard-coding wrongly.
    """
    matches = sorted(root.rglob(filename))

    if not matches:
        return None
    if len(matches) > 1:
        raise SystemExit(f"{filename}: found {len(matches)} copies under {root}, expected one")

    return matches[0]


def insert_after_line(lines, pattern, call, start=0):
    """Insert a call directly after the first line matching `pattern`.

    For a function whose body ends in a `return`, appending to the end of the body would
    put the call after it, where it can never run. This anchors on the last statement that
    does run instead.
    """
    rx = re.compile(pattern)

    for i in range(start, len(lines)):
        if rx.search(lines[i]):
            lines.insert(i + 1, f"\t\t{call}{MARK}")
            return i + 1

    raise SystemExit(f"could not locate a line matching {pattern!r}")


def insert_before_closing_brace(lines, brace_index, call):
    """Insert a call as the last statement of a function body."""
    depth = 0
    for i in range(brace_index, len(lines)):
        depth += lines[i].count("{") - lines[i].count("}")
        if depth == 0:
            lines.insert(i, f"\t\t{call}{MARK}")
            return i
    raise SystemExit("unbalanced braces while looking for the end of a function")


# Words the WitcherScript compiler treats as tokens. Using one as a parameter or variable
# name produces a syntax error that names the token rather than the identifier, which is
# easy to misread, so the names are checked here instead of in the game.
RESERVED = {
    "abstract", "array", "autobind", "break", "case", "class", "cleanup", "const",
    "continue", "default", "delete", "do", "editable", "else", "entry", "enum", "event",
    "exec", "extends", "false", "final", "for", "function", "hint", "if", "import", "in",
    "inlined", "latent", "new", "optional", "out", "parent", "private", "protected",
    "public", "quest", "reward", "saved", "single", "state", "statemachine", "static",
    "storyscene", "struct", "super", "switch", "this", "timer", "true", "var", "virtual",
    "while",
}


def check_identifiers(text, label):
    """Fail early if a declaration uses a reserved word as its name."""
    problems = []

    for match in re.finditer(r"\b(?:var|function)\s+([A-Za-z_][\w, ]*?)\s*[:(]", text):
        for name in (part.strip() for part in match.group(1).split(",")):
            if name.lower() in RESERVED:
                problems.append(name)

    # Parameter lists: `( name : type, other : type )`
    for match in re.finditer(r"\(([^)]*)\)", text):
        for parameter in match.group(1).split(","):
            if ":" in parameter:
                name = parameter.split(":")[0].replace("optional", "").replace("out", "").strip()
                if name.lower() in RESERVED:
                    problems.append(name)

    if problems:
        raise SystemExit(f"{label}: reserved words used as identifiers: {sorted(set(problems))}")

    print(f"{label}: no reserved words used as identifiers")


def convert_master():
    """Convert the UTF-8 master of the reporter into the encoding the game expects."""
    master = HERE / "src/witchertrack.ws"
    text = master.read_text(encoding="utf-8")

    check_identifiers(text, "witchertrack.ws")

    target = OUT.parent / "local/witchertrack.ws"
    write(target, text.replace("\r\n", "\n").split("\n"))
    print(f"witchertrack.ws: converted to UTF-16 LE, {target.stat().st_size} bytes")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--scripts", type=Path, default=DEFAULT_SRC,
        help="the game's vanilla script folder, the one containing game/player/playerWitcher.ws")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT,
                        help="where the patched copies are written")
    args = parser.parse_args()

    global SRC, OUT
    SRC, OUT = args.scripts, args.out

    if not (SRC / "game/player/playerWitcher.ws").exists():
        parser.error(
            f"no vanilla scripts under {SRC}\n"
            "Point --scripts at the game's own script folder "
            "(<game>\\content\\content0\\scripts), or copy it to mod/vanilla/.")

    report = []
    convert_master()

    # --- playerWitcher.ws : a full snapshot on every load ---------------------
    player_path = SRC / "game/player/playerWitcher.ws"
    player = read(player_path)

    brace = find_function(player, r"event\s+OnSpawned\s*\(\s*spawnData")
    at = insert_before_closing_brace(player, brace, "WT_OnPlayerSpawned( this );")
    report.append(("playerWitcher.ws", "OnSpawned", at + 1))

    # --- playerWitcher.ws : the moment a Gwent card enters the collection -----
    #
    # The only live signal there is for a card. Placed at the end of the game's
    # own `cardIndex != -1` block, so the card is reported once the game has
    # decided it is real and added it, not before.
    brace = find_function(player, r"function\s+AddGwentCard\s*\(")
    at = insert_at_block_end(
        player, r"if\s*\(\s*cardIndex\s*!=\s*-1\s*\)",
        "WT_OnGwentCardAdded( cardIndex );", start=brace)
    report.append(("playerWitcher.ws", "AddGwentCard", at + 1))

    # --- playerWitcher.ws : a heartbeat, every time meditation begins --------
    #
    # Not a signal about anything: a re-read taken at a moment that happens
    # often and happens between things. Anchored on the last statement of the
    # successful path, after GotoState and the heading, because the body ends in
    # `return true` and anything appended past that never runs.
    brace = find_function(player, r"function\s+Meditate\s*\(\s*\)")
    at = insert_after_line(
        player, r"medState\.SetMeditationPointHeading\(\s*GetHeading\(\s*\)\s*\);",
        "WT_OnMeditation();", start=brace)
    report.append(("playerWitcher.ws", "Meditate", at + 1))

    write(OUT / "player/playerWitcher.ws", player)

    # --- hudModuleJournalUpdate.ws : the four notification functions ----------
    hud_path = SRC / "game/gui/hud/modules/hudModuleJournalUpdate.ws"
    hud = read(hud_path)

    hooks = [
        (r"function\s+AddQuestUpdate\s*\(", "WT_OnQuestUpdate( journalQuest );"),
        (r"function\s+AddCraftingSchematicUpdate\s*\(", "WT_OnCraftingSchematicUpdate( schematicName );"),
        (r"function\s+AddAlchemySchematicUpdate\s*\(", "WT_OnAlchemySchematicUpdate( schematicName );"),
        (r"function\s+AddMapPinUpdate\s*\(", "WT_OnMapPinUpdate( mapPinName );"),
    ]

    # Insert from the bottom of the file upward so earlier line numbers stay valid.
    resolved = [(find_function(hud, pattern), call, pattern) for pattern, call in hooks]
    for brace, call, pattern in sorted(resolved, key=lambda item: -item[0]):
        at = insert_after_declarations(hud, brace, call)
        name = re.search(r"Add\w+", pattern).group(0)
        report.append(("hudModuleJournalUpdate.ws", name, at + 1))

    write(OUT / "gui/hud/modules/hudModuleJournalUpdate.ws", hud)

    # --- effectManager.ws : a sweep when a Place of Power is used -------------
    # Using one fires none of the four notifications above - it grants an ability
    # point and applies a buff, and that is all. The game's own OnBuffAdded
    # already isolates "the player just gained a shrine buff" for an achievement;
    # the call goes inside that test rather than repeating it.
    effect_path = find_one(SRC, "effectManager.ws")

    if effect_path is None:
        print("effectManager.ws: not found under the scripts folder - skipping the "
              "Place of Power hook (everything else is unaffected)")
    else:
        effects = read(effect_path)
        at = insert_inside_block(
            effects, r"owner\s*==\s*thePlayer\s*&&\s*IsBuffShrine\s*\(", "WT_OnShrineBuff();")
        report.append(("effectManager.ws", "OnBuffAdded", at + 1))

        write(OUT / effect_path.relative_to(SRC).relative_to("game"), effects)

    print(f"{'file':<32}{'function':<30}{'inserted at line'}")
    for file_name, function_name, line in sorted(report):
        print(f"{file_name:<32}{function_name:<30}{line}")


if __name__ == "__main__":
    sys.exit(main())
