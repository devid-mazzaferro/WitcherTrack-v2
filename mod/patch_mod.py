#!/usr/bin/env python3
"""
Regenerate modWitcherTrack from the game's own scripts, and prove the result.

Three vanilla files get one-line hook calls - playerWitcher.ws, hudModuleJournalUpdate.ws
and effectManager.ws; mod/HOOKS.md says what each call is for and why it sits exactly
where it does. The UTF-8 master of the reporter, src/witchertrack.ws, becomes the deployed
copy, local/witchertrack.ws.

Two game versions. The mod is built once for 4.04 and once for 5.0, into
mod/4.04/modWitcherTrack and mod/5.0/modWitcherTrack. Which one a run builds is read from
the sources themselves - the AddGwentCard and Meditate bodies that 5.0 rewrote say which
version they belong to - and --target only confirms it. The reporter is the same file for
both; the handful of calls whose shape differs live in src/compat-<version>.ws, deployed as
local/witchertrack_compat.ws, and every version layer is checked to declare exactly the same
functions, so the shared reporter compiles against either.

4.04 is no longer installed anywhere once the game updates, so --from-shipped rebuilds a
target from the copies already in mod/<version>/: the marked lines are taken back out to
recover vanilla, and the patch is applied again. That is how the 4.04 build follows a
change to the reporter without a 4.04 install.

Encodings. Patch 4.04 shipped its scripts as UTF-16 LE with a byte-order mark and CRLF
throughout. Patch 5.0 ships them as UTF-8 with no byte-order mark, CRLF on most lines, a
bare LF on some, and no terminator on the last one. A patched copy is written back in its
source's own encoding, mark and line endings, line by line, so it differs from vanilla by
the inserted lines and not one byte more; an inserted line ends in the file's prevailing
newline. The deployed reporter is always UTF-16 LE with exactly one byte-order mark and
CRLF, which compiles on both versions.

Anchors. Each call is placed by finding its class, then the function inside that class,
then a statement inside that function, and every step has to match exactly once. Where 5.0
rewrote a body (AddGwentCard, Meditate) the 5.0 anchor is used, and the 4.04 one is kept
for older sources. Anything else stops the run with a message saying what was not found,
rather than settling for a near miss.

WitcherScript requires every `var` declaration to appear at the top of a function body
before any statement, so a call cannot simply be prepended to the body: it is inserted
after the last declaration instead.

After writing, everything written is read back and checked: each patched file differs from
vanilla by exactly its marked lines; the deployed reporter and version layer decode back to
their masters and carry exactly one byte-order mark each; no function calls itself, directly
or through another; every WT_ function called is defined; and every version layer declares
the same functions. Any failure exits non-zero.
"""

import argparse
import re
import sys
from collections import Counter, namedtuple
from pathlib import Path

HERE = Path(__file__).resolve().parent

# Where the vanilla sources are read from and where the patched copies are written.
# Both are overridable, because the vanilla scripts are not in this repository: they
# belong to the game, and every user already has them under
#   <game>\content\content0\scripts
# Extract that folder (or point --scripts straight at it) and the patcher does the rest.
# The output goes to the folder of the version the sources turn out to be.
TARGETS = ("4.04", "5.0")
DEFAULT_SRC = HERE / "vanilla"
DEFAULT_MASTER = HERE / "src/witchertrack.ws"


def shipped_scripts(target):
    """The scripts folder of a target's shipped mod, which is also where it is built."""
    return HERE / target / "modWitcherTrack/content/scripts"


def compat_master(target):
    return HERE / f"src/compat-{target}.ws"

MARK = "  // modWitcherTrack"
UTF16_BOM = b"\xff\xfe"
UTF8_BOM = b"\xef\xbb\xbf"

Block = namedtuple("Block", "open_line open_col close_line close_col")
Hook = namedtuple("Hook", "function index text")


def require(condition, message):
    if not condition:
        raise SystemExit(message)


# --- reading and writing, byte for byte --------------------------------------

def detect_encoding(raw, path):
    """Return (codec, byte-order mark) for a script, from its bytes alone."""
    if raw.startswith(UTF16_BOM):
        return "utf-16-le", UTF16_BOM
    if raw.startswith(UTF8_BOM):
        return "utf-8", UTF8_BOM

    # Without a mark, UTF-16 is all but certain to put a NUL in the first few bytes.
    # Neither version of the game ships that, so it is refused rather than guessed at.
    require(not raw.startswith(b"\xfe\xff") and b"\x00" not in raw[:4096],
            f"{path}: looks like UTF-16 without a little-endian byte-order mark")
    return "utf-8", b""


class Script:
    """One script file, held as lines that keep their own terminators.

    Splitting on LF and keeping whatever precedes it is what lets a CRLF file, an LF file
    and 5.0's mixture of the two come back out exactly as they went in. `code` is the same
    lines with comments and literals blanked out, which is what every search runs on, so a
    brace or a keyword inside a string or a comment is never mistaken for code.
    """

    def __init__(self, path, raw=None):
        self.path = Path(path)
        self.raw = self.path.read_bytes() if raw is None else raw
        self.encoding, self.bom = detect_encoding(self.raw, self.path)

        try:
            text = self.raw[len(self.bom):].decode(self.encoding)
        except UnicodeDecodeError as error:
            raise SystemExit(f"{self.path}: not valid {self.encoding}: {error}")

        parts = text.split("\n")
        self.lines = [part + "\n" for part in parts[:-1]] + ([parts[-1]] if parts[-1] else [])
        require(self.encode(self.lines) == self.raw,
                f"{self.path}: does not survive a decode and re-encode unchanged")

        crlf = sum(line.endswith("\r\n") for line in self.lines)
        bare = sum(line.endswith("\n") for line in self.lines) - crlf
        self.newline = "\n" if bare > crlf else "\r\n"
        self.endings = (crlf, bare)
        self.code = blank_comments_and_literals(self.lines)

    def encode(self, lines):
        return self.bom + "".join(lines).encode(self.encoding)

    def describe(self):
        crlf, bare = self.endings
        if crlf and bare:
            endings = f"CRLF, {bare} lines with a bare LF"
        else:
            endings = "LF" if bare else "CRLF"
        final = "final newline" if self.lines and self.lines[-1].endswith("\n") else "no final newline"
        codec = "UTF-16 LE" if self.encoding == "utf-16-le" else "UTF-8"
        mark = "BOM" if self.bom else "no BOM"
        return f"{codec}, {mark}, {endings}, {final}"


def blank_comments_and_literals(lines):
    """Replace comments and the inside of string and name literals with spaces."""
    code, in_comment = [], False

    for line in lines:
        text = line.rstrip("\r\n")
        # A literal never spans lines, so one always ends with its line; a stray quote
        # cannot swallow the rest of the file. A block comment can, and is carried over.
        out, quote, i = [], None, 0

        while i < len(text):
            if in_comment:
                if text.startswith("*/", i):
                    in_comment = False
                    out.append("  ")
                    i += 2
                else:
                    out.append(" ")
                    i += 1
            elif quote:
                if text[i] == "\\" and i + 1 < len(text):
                    out.append("  ")
                    i += 2
                elif text[i] == quote:
                    out.append(quote)
                    quote = None
                    i += 1
                else:
                    out.append(" ")
                    i += 1
            elif text.startswith("//", i):
                out.append(" " * (len(text) - i))
                break
            elif text.startswith("/*", i):
                in_comment = True
                out.append("  ")
                i += 2
            else:
                if text[i] in "\"'":
                    quote = text[i]
                out.append(text[i])
                i += 1

        code.append("".join(out))

    return code


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


# --- finding things, exactly once -------------------------------------------

def whole(script):
    last = len(script.code) - 1
    return Block(0, -1, last, len(script.code[last]))


def matches(script, scope, pattern):
    """Every (line, match) of `pattern` strictly inside a block."""
    rx = re.compile(pattern)
    found = []

    for i in range(scope.open_line, scope.close_line + 1):
        text = script.code[i]
        lo = scope.open_col + 1 if i == scope.open_line else 0
        hi = scope.close_col if i == scope.close_line else len(text)
        found.extend((i, m) for m in rx.finditer(text, lo, hi))

    return found


def one(found, what):
    where = ", ".join(str(line + 1) for line, _ in found) or "nowhere"
    require(len(found) == 1, f"{what}: expected exactly one match, found {len(found)} (lines: {where})")
    return found[0]


def block_at(script, line, col, what):
    """The block opened by the first `{` at or after (line, col).

    Only the rest of a header may stand in between - a `;` or a `}` first means the
    header found has no block of its own, and that is an error, not a reason to look
    further down the file.
    """
    for i in range(line, min(line + 6, len(script.code))):
        text = script.code[i]
        for j in range(col if i == line else 0, len(text)):
            if text[j] in ";}":
                raise SystemExit(f"{what}: line {line + 1} opens no block")
            if text[j] == "{":
                depth = 0
                for k in range(i, len(script.code)):
                    row = script.code[k]
                    for c in range(j if k == i else 0, len(row)):
                        depth += {"{": 1, "}": -1}.get(row[c], 0)
                        if depth == 0:
                            return Block(i, j, k, c)
                raise SystemExit(f"{what}: unbalanced braces after line {i + 1}")

    raise SystemExit(f"{what}: no opening brace near line {line + 1}")


def depth_at(script, block, line):
    """How deep the start of `line` is nested, counting the block itself as 1."""
    depth = 0
    for i in range(block.open_line, line):
        start = block.open_col if i == block.open_line else 0
        depth += script.code[i].count("{", start) - script.code[i].count("}", start)
    return depth


def find_class(script, name, label):
    what = f"{label}: class {name}"
    line, m = one(matches(script, whole(script), rf"\bclass\s+{name}\b"), what)
    return block_at(script, line, m.end(), what)


def find_function(script, scope, pattern, what):
    """Return a function's body and its header, the header as code."""
    line, m = one(matches(script, scope, pattern), what)
    body = block_at(script, line, m.end(), what)

    if body.open_line == line:
        return body, script.code[line][m.start():body.open_col]

    header = [script.code[line][m.start():]] + script.code[line + 1:body.open_line]
    return body, " ".join(header + [script.code[body.open_line][:body.open_col]])


def parameters(header):
    """`( name : type, optional other : type )` as {name: type}.

    Names may share a type, as in vanilla's `Min( a,b : int )`: a name with no type of its
    own takes the type of the next name that has one, so every name in a group is seen.
    """
    inside = header[header.index("(") + 1:header.rindex(")")]
    result, pending = {}, []
    for parameter in inside.split(","):
        name, _, kind = parameter.partition(":")
        name = re.sub(r"^\s*(?:(?:optional|out)\s+)*", "", name).strip()
        if not kind:
            if name:
                pending.append(name)
            continue
        for grouped in pending + [name]:
            result[grouped] = re.sub(r"\s+", "", kind)
        pending = []
    return result


def declarations(script, block):
    """Every `var name : type` inside a block, as {name: type}."""
    text = "\n".join(script.code[block.open_line:block.close_line + 1])
    result = {}
    for m in re.finditer(r"\bvar\s+([\w\s,]+?)\s*:\s*([\w<>\s]+?)\s*[;=]", text):
        for name in m.group(1).split(","):
            result[name.strip()] = re.sub(r"\s+", "", m.group(2))
    return result


def previous_statement(script, line, stop):
    for i in range(line - 1, stop - 1, -1):
        if script.code[i].strip():
            return i
    return None


def indent_of(line):
    return re.match(r"[ \t]*", line).group(0)


# --- where a call goes ---------------------------------------------------------
#
# Each returns (index, indentation): the call is inserted before vanilla line `index`
# (0-based), indented like the code it joins.

def after_declarations(script, block, what):
    """After the run of `var` declarations that opens a function body."""
    require(not script.code[block.open_line][block.open_col + 1:].strip(),
            f"{what}: code follows the opening brace on line {block.open_line + 1}")

    last = None
    for i in range(block.open_line + 1, block.close_line):
        stripped = script.code[i].strip()
        if not stripped:
            continue
        if not re.match(r"var\b", stripped):
            break
        require(stripped.endswith(";"), f"{what}: the declaration on line {i + 1} runs onto the next")
        last = i

    if last is None:
        return block.open_line + 1, indent_of(script.lines[block.open_line]) + "\t"
    return last + 1, indent_of(script.lines[last])


def at_block_start(script, block, what):
    """As the first statement of a block."""
    require(not script.code[block.open_line][block.open_col + 1:].strip(),
            f"{what}: code follows the opening brace on line {block.open_line + 1}")
    return block.open_line + 1, indent_of(script.lines[block.open_line]) + "\t"


def at_block_end(script, block, what):
    """As the last statement of a block, after everything the block does."""
    close = block.close_line
    require(script.code[close].strip() == "}",
            f"{what}: the closing brace on line {close + 1} shares its line with code")

    last = previous_statement(script, close, block.open_line + 1)
    require(last is None or not re.match(r"return\b", script.code[last].strip()),
            f"{what}: the block ends in a return on line {last and last + 1}, "
            "so anything appended after it would never run")

    return close, indent_of(script.lines[close]) + "\t"


def after_statement(script, line, pattern, what):
    """Directly after a statement that has its line to itself.

    For a function whose body ends in a `return`, appending to the end of the body would
    put the call after it, where it can never run. This anchors on the last statement that
    does run instead. The statement must not be the lone body of a brace-less if, else or
    loop: the inserted line would fall outside it and run on every path.
    """
    require(re.fullmatch(rf"\s*{pattern}\s*", script.code[line]),
            f"{what}: line {line + 1} holds more than the anchor statement")

    before = previous_statement(script, line, 0)
    require(before is not None and script.code[before].rstrip().endswith((";", "{", "}")),
            f"{what}: line {line + 1} is the body of a brace-less statement")

    return line + 1, indent_of(script.lines[line])


def after_block(script, block, what):
    """Directly after a block's closing brace, at the block's own level."""
    require(script.code[block.close_line].strip() == "}",
            f"{what}: the closing brace on line {block.close_line + 1} shares its line with code")
    return block.close_line + 1, indent_of(script.lines[block.close_line])


def hook(script, function, place, call):
    index, indent = place
    return Hook(function, index, f"{indent}{call}{MARK}{script.newline}")


# --- the hooks ----------------------------------------------------------------

def plan_player(script, label, versions):
    cls = find_class(script, "W3PlayerWitcher", label)
    hooks = []

    # --- a full snapshot on every load --------------------------------------
    what = f"{label}: W3PlayerWitcher.OnSpawned"
    body, _ = find_function(script, cls, r"\bevent\s+OnSpawned\s*\(", what)
    hooks.append(hook(script, "OnSpawned", at_block_end(script, body, what),
                      "WT_OnPlayerSpawned( this );"))

    hooks.append(plan_gwent(script, cls, label, versions))
    hooks.append(plan_meditation(script, cls, label, versions))
    return hooks


def plan_gwent(script, cls, label, versions):
    """The moment a Gwent card enters the collection - the only live signal a card has.

    The card is reported once the game has decided it is real and added it, not before.
    In 4.04 that is the end of the game's own `cardIndex != -1` block. 5.0 replaced the
    block with an early return when the card's definition does not resolve, followed by
    the loop that adds it, so the call goes directly after that loop, and reports the
    index the game gives that definition - which is what the catalogue and run.json
    already key a card on.
    """
    what = f"{label}: W3PlayerWitcher.AddGwentCard"
    body, _ = find_function(script, cls, r"\bfunction\s+AddGwentCard\s*\(", what)

    guard = matches(script, body, r"\bif\s*\(\s*!\s*gwintManager\s*\.\s*GetGwentCard\s*\("
                                  r"\s*cardName\s*,\s*cardDefinition\s*\)\s*\)")
    legacy = matches(script, body, r"\bif\s*\(\s*cardIndex\s*!=\s*-1\s*\)")

    if guard and not legacy:
        line, m = one(guard, f"{what}: the card-definition guard")
        require(depth_at(script, body, line) == 1, f"{what}: the guard on line {line + 1} is nested")
        test = block_at(script, line, m.end(), what)
        require(matches(script, test, r"\breturn\b"),
                f"{what}: the guard on line {line + 1} no longer returns, so getting past it "
                "no longer proves the card resolved")

        line, m = one(matches(script, body, r"\bfor\s*\(\s*i\s*=\s*0\s*;\s*i\s*<\s*amount\s*;"
                                            r"\s*i\s*\+=\s*1\s*\)"), f"{what}: the loop adding the card")
        require(line > test.close_line, f"{what}: the loop on line {line + 1} comes before the guard")
        require(depth_at(script, body, line) == 1, f"{what}: the loop on line {line + 1} is nested")
        loop = block_at(script, line, m.end(), what)
        one(matches(script, loop, r"\bgwintManager\s*\.\s*AddCardToCollection\s*\(\s*cardName\s*\)"),
            f"{what}: AddCardToCollection inside the loop")

        declared = declarations(script, body)
        require(declared.get("gwintManager") == "CR4GwintManager"
                and declared.get("cardDefinition") == "SCardDefinition",
                f"{what}: gwintManager or cardDefinition is not declared as the call expects")

        versions.append(("AddGwentCard", "5.0"))
        return hook(script, "AddGwentCard", after_block(script, loop, what),
                    "WT_OnGwentCardAdded( gwintManager.GetIndex( cardDefinition ) );")

    if legacy and not guard:
        line, m = one(legacy, f"{what}: the cardIndex test")
        versions.append(("AddGwentCard", "4.04"))
        return hook(script, "AddGwentCard", at_block_end(script, block_at(script, line, m.end(), what), what),
                    "WT_OnGwentCardAdded( cardIndex );")

    raise SystemExit(f"{what}: found {'both' if guard else 'neither'} the 5.0 card-definition "
                     "guard and the 4.04 `cardIndex != -1` test")


def plan_meditation(script, cls, label, versions):
    """A heartbeat, every time meditation begins.

    Not a signal about anything: a re-read taken at a moment that happens often and
    happens between things. Anchored on the statement that sets the meditation heading,
    the last one on the path that enters the state, because the body ends in
    `return true` and anything appended past that never runs. 4.04 set it through the
    meditation state; 5.0 sets it on the player, from a heading it computes first.
    """
    what = f"{label}: W3PlayerWitcher.Meditate"
    body, _ = find_function(script, cls, r"\bfunction\s+Meditate\s*\(\s*\)", what)

    current = r"(?<![\w.])SetMeditationPointHeading\s*\(\s*heading\s*\)\s*;"
    legacy = r"\bmedState\s*\.\s*SetMeditationPointHeading\s*\(\s*GetHeading\s*\(\s*\)\s*\)\s*;"
    found_current, found_legacy = matches(script, body, current), matches(script, body, legacy)

    require(not (found_current and found_legacy), f"{what}: found both the 5.0 and the 4.04 heading call")
    pattern, found = (current, found_current) if found_current else (legacy, found_legacy)
    line, _ = one(found, f"{what}: SetMeditationPointHeading")
    versions.append(("Meditate", "5.0" if found_current else "4.04"))

    return hook(script, "Meditate", after_statement(script, line, pattern, what), "WT_OnMeditation();")


# The four functions that draw the on-screen notifications, each with the parameter its
# call passes on. 5.0 gave AddMapPinUpdate a second parameter; the call does not need it.
JOURNAL_HOOKS = [
    ("AddQuestUpdate", "journalQuest", "CJournalQuest", "WT_OnQuestUpdate( journalQuest );"),
    ("AddCraftingSchematicUpdate", "schematicName", "name", "WT_OnCraftingSchematicUpdate( schematicName );"),
    ("AddAlchemySchematicUpdate", "schematicName", "name", "WT_OnAlchemySchematicUpdate( schematicName );"),
    ("AddMapPinUpdate", "mapPinName", "name", "WT_OnMapPinUpdate( mapPinName );"),
]


def plan_journal(script, label, versions):
    cls = find_class(script, "CR4HudModuleJournalUpdate", label)
    hooks = []

    for function, parameter, kind, call in JOURNAL_HOOKS:
        what = f"{label}: CR4HudModuleJournalUpdate.{function}"
        body, header = find_function(script, cls, rf"\bfunction\s+{function}\s*\(", what)
        require(parameters(header).get(parameter) == kind,
                f"{what}: has no parameter `{parameter} : {kind}` any more: {' '.join(header.split())}")
        hooks.append(hook(script, function, after_declarations(script, body, what), call))

    return hooks


def plan_effects(script, label, versions):
    """A sweep when a Place of Power is used.

    Using one fires none of the four notifications - it grants an ability point and
    applies a buff, and that is all. The game's own OnBuffAdded already isolates "the
    player just gained a shrine buff" for an achievement; the call goes inside that test
    rather than repeating it.
    """
    cls = find_class(script, "W3EffectManager", label)
    what = f"{label}: W3EffectManager.OnBuffAdded"
    body, _ = find_function(script, cls, r"\bfunction\s+OnBuffAdded\s*\(", what)

    line, m = one(matches(script, body, r"\bif\s*\(\s*owner\s*==\s*thePlayer\s*&&\s*IsBuffShrine\s*\("),
                  f"{what}: the player-and-shrine test")
    block = block_at(script, line, m.end(), what)
    return [hook(script, "OnBuffAdded", at_block_start(script, block, what), "WT_OnShrineBuff();")]


def apply(script, hooks):
    """Insert every hook, and return the patched lines and each hook's final line number."""
    indexes = [h.index for h in hooks]
    require(len(set(indexes)) == len(indexes), f"{script.path}: two hooks landed on the same line")

    lines = list(script.lines)
    for h in sorted(hooks, key=lambda h: -h.index):
        lines.insert(h.index, h.text)

    placed = {h: h.index + sum(other < h.index for other in indexes) for h in hooks}
    return lines, placed


# --- the reporter ---------------------------------------------------------------

# Words the WitcherScript compiler treats as tokens. Using one as a parameter or variable
# name produces a syntax error that names the token rather than the identifier, which is
# easy to misread, so the names are checked here instead of in the game. The list is the
# compiler's own keyword table, as it reads in the 5.0 executable - which is where `set`
# became one: vanilla renamed `SetCannotUseUndyingSkill( set : bool )` for it, and a 4.04
# copy of playerWitcher.ws stops with "unexpected TOKEN_SET". The type names and the
# `the...` globals are tokens too, and so are the four annotation keywords 5.0 added for
# @addMethod and its siblings. Every one of set, defaults, savepoint, local, map, any, def,
# enter, NULL and the four annotation words was confirmed by compiling a declaration with
# it. The last row is not refused as an identifier - scene, at least, compiles - and is
# kept from the 4.04 list; avoiding a word for nothing costs nothing. Compared without case.
RESERVED = {
    "abstract", "autobind", "class", "const", "def", "default", "defaults", "editable",
    "entry", "enum", "event", "exec", "extends", "final", "function", "hint", "import",
    "inlined", "latent", "local", "optional", "out", "private", "protected", "public",
    "quest", "reward", "saved", "savepoint", "single", "state", "statemachine",
    "storyscene", "struct", "timer", "var",
    "break", "case", "cleanup", "continue", "delete", "do", "else", "enter", "false", "for",
    "if", "in", "new", "null", "parent", "return", "super", "switch", "this", "true",
    "virtual_parent", "while",
    "any", "array", "bool", "byte", "float", "int", "map", "name", "set", "string", "void",
    "thecamera", "thedebug", "thegame", "theinput", "theplayer", "thesound", "thetelemetry",
    "thetimer",
    "addfield", "addmethod", "replacemethod", "wrapmethod",
    "scene", "static", "virtual",
}


def check_identifiers(code, label):
    """Fail early if a declaration uses a reserved word as its name."""
    problems = []

    # A var list may run onto the next line (`var statVitality,` in vanilla's temp.ws), so
    # any whitespace may sit between the names, not only spaces.
    for match in re.finditer(r"\b(?:var|function|event)\s+([A-Za-z_][\w,\s]*?)\s*[:(]", code):
        for name in (part.strip() for part in match.group(1).split(",")):
            if name.lower() in RESERVED:
                problems.append(name)

    # Parameter lists: `( name : type, optional other : type )`
    for match in re.finditer(r"\(([^()]*)\)", code):
        for name in parameters(f"({match.group(1)})"):
            if name.lower() in RESERVED:
                problems.append(name)

    if problems:
        raise SystemExit(f"{label}: reserved words used as identifiers: {sorted(set(problems))}")

    print(f"{label}: no reserved words used as identifiers")


def definitions(code):
    """(name, body) for every function defined (not merely declared) in `code`, in order."""
    found = []
    for m in re.finditer(r"\bfunction\s+(\w+)\s*\(", code):
        brace, stop = code.find("{", m.end()), code.find(";", m.end())
        if brace < 0 or 0 <= stop < brace:
            continue  # declared without a body

        depth = 0
        for end in range(brace, len(code)):
            depth += {"{": 1, "}": -1}.get(code[end], 0)
            if depth == 0:
                break
        found.append((m.group(1), code[brace:end + 1]))

    return found


def function_bodies(code):
    """{name: body} for every function defined in `code`.

    A name defined twice keeps only its last body here, which is why duplicates() exists:
    every check built on this dictionary would otherwise see one function where there are
    two, and a duplicate in the 4.04 layer - which nothing here can compile - would ship.
    """
    return dict(definitions(code))


def duplicates(code):
    """Every function name `code` defines more than once."""
    counts = Counter(name for name, _ in definitions(code))
    return sorted(name for name, count in counts.items() if count > 1)


def call_cycles(code):
    """Every way a function in `code` can end up calling itself.

    A `sed -i` once rewrote the body of WT_Sweep() while renaming its call sites, and
    the game recursed until it died. Indirect recursion is as fatal, so the whole call
    graph is walked rather than each body searched for its own name.
    """
    bodies = function_bodies(code)
    calls = {name: set(re.findall(r"(?<![\w.])(\w+)\s*\(", body)) & bodies.keys()
             for name, body in bodies.items()}

    cycles, state, stack = [], {}, []

    def visit(name):
        state[name] = "open"
        stack.append(name)
        for callee in sorted(calls[name]):
            if state.get(callee) == "open":
                cycles.append(stack[stack.index(callee):] + [callee])
            elif callee not in state:
                visit(callee)
        stack.pop()
        state[name] = "closed"

    for name in sorted(calls):
        if name not in state:
            visit(name)

    return len(bodies), cycles


def signatures(code):
    """{name: (parameters, return type)} for every function a file defines."""
    result = {}
    for m in re.finditer(r"\bfunction\s+(\w+)\s*(\([^)]*\))\s*(?::\s*([\w<>\s]+?))?\s*\{", code):
        result[m.group(1)] = (tuple(parameters(m.group(2)).items()),
                              re.sub(r"\s+", "", m.group(3) or "void"))
    return result


# --- proving the result -------------------------------------------------------

def verify_patched(label, vanilla, path, placed):
    """Everything that differs from vanilla is one of the planned lines, and nothing else."""
    raw = path.read_bytes()
    patched = Script(path, raw)
    marker = MARK.strip()
    problems = []

    if (patched.encoding, patched.bom) != (vanilla.encoding, vanilla.bom):
        problems.append(f"written as {patched.describe()}, vanilla is {vanilla.describe()}")

    marked = [(i, line) for i, line in enumerate(patched.lines) if marker in line]
    expected = sorted((at, h.text) for h, at in placed.items())
    if marked != expected:
        problems.append(f"marked lines {[i + 1 for i, _ in marked]}, "
                        f"planned {[i + 1 for i, _ in expected]}")
    if any(not line.rstrip("\r\n").endswith(MARK) for _, line in marked):
        problems.append("a marked line does not end in the marker")

    rest = [line for line in patched.lines if marker not in line]
    if rest != vanilla.lines:
        first = next((i for i, (a, b) in enumerate(zip(rest, vanilla.lines)) if a != b),
                     min(len(rest), len(vanilla.lines)))
        problems.append(f"unmarked lines differ from vanilla, first at vanilla line {first + 1}")
    elif vanilla.encode(rest) != vanilla.raw:
        problems.append("the bytes outside the marked lines differ from vanilla")

    if "".join(patched.lines).count("﻿") != "".join(vanilla.lines).count("﻿"):
        problems.append("a stray U+FEFF was added")

    count = f"{len(marked)} marked line{'' if len(marked) == 1 else 's'}"
    good = f"differs from vanilla by exactly its {count}, byte for byte"
    return [(label, good, problems)]


def verify_deployed(label, target, payload, master_text):
    """A deployed UTF-16 copy is its UTF-8 master, with exactly one byte-order mark."""
    raw = target.read_bytes()
    results = []

    # Decoded without the mark, so a second one would surface as a U+FEFF character.
    try:
        text = raw[len(UTF16_BOM):].decode("utf-16-le")
    except UnicodeDecodeError as error:
        text = f"<not valid UTF-16 LE: {error}>"

    problems = []
    if raw != payload:
        problems.append("the file on disk is not what was written")
    if text.replace("\r\n", "\n") != master_text:
        problems.append("does not decode back to the master's text")
    results.append((label, "the UTF-16 copy decodes back to the UTF-8 master", problems))

    problems = []
    if not raw.startswith(UTF16_BOM):
        problems.append("no UTF-16 LE byte-order mark")
    if "﻿" in text:
        problems.append(f"{text.count(chr(0xfeff))} U+FEFF after the byte-order mark")
    results.append((label, "exactly one byte-order mark, no stray U+FEFF", problems))

    return results


def verify_code(master_code, layer_code, layer_label):
    """The reporter and the version layer it ships with, taken as one program."""
    results = []
    label = f"witchertrack.ws + {layer_label}"

    functions, cycles = call_cycles(master_code + "\n" + layer_code)
    results.append((label, f"{functions} functions, none calls itself, directly or through another",
                    [" -> ".join(cycle) for cycle in cycles]))

    master, layer = function_bodies(master_code), function_bodies(layer_code)
    called = set(re.findall(r"(?<![\w.])(WT_\w+)\s*\(", master_code + "\n" + layer_code))
    problems = [f"{name} is defined in both" for name in sorted(master.keys() & layer.keys())]
    problems += [f"{name} is defined twice in witchertrack.ws" for name in duplicates(master_code)]
    problems += [f"{name} is defined twice in {layer_label}" for name in duplicates(layer_code)]
    problems += [f"{name} is called but defined nowhere"
                 for name in sorted(called - master.keys() - layer.keys())]
    results.append((label, f"each of the {len(called)} WT_ functions called is defined exactly once",
                    problems))

    return results


def verify_layers(master_code):
    """Every version layer gives the reporter the same functions, so it fits each one.

    Only the build for the installed game can be compiled, and once the game updates that
    is never 4.04 again; this is what stands in for compiling the other one. The interface
    is whatever the reporter calls without defining it. Each layer has to define all of it
    with the same signatures as every other layer; what a layer keeps to itself, such as
    5.0's area-name helpers, is its own business, as long as the reporter never calls it.
    """
    master = function_bodies(master_code)
    interface = set(re.findall(r"(?<![\w.])(WT_\w+)\s*\(", master_code)) - master.keys()
    codes = {target: "\n".join(Script(compat_master(target)).code) for target in TARGETS}
    layers = {target: signatures(code) for target, code in codes.items()}
    reference_target = TARGETS[-1]
    reference, problems = layers[reference_target], []

    for target, code in codes.items():
        problems += [f"{name} is defined twice in compat-{target}.ws" for name in duplicates(code)]

    for name in sorted(interface):
        for target, found in layers.items():
            if name not in found:
                problems.append(f"compat-{target}.ws lacks {name}")
            elif name in reference and found[name] != reference[name]:
                problems.append(f"{name} differs: compat-{target}.ws has {found[name]}, "
                                f"compat-{reference_target}.ws has {reference[name]}")

    files = " and ".join(f"compat-{target}.ws" for target in TARGETS)
    return [("version layers", f"{files} both define the {len(interface)} functions the reporter "
             f"takes from them, with the same signatures ({', '.join(sorted(interface))})", problems)]


def deployable(text):
    """The deployed form of a UTF-8 master: UTF-16 LE, one byte-order mark, CRLF."""
    return UTF16_BOM + text.replace("\n", "\r\n").encode("utf-16-le")


# --- main ---------------------------------------------------------------------

def stripped(path):
    """A shipped copy with its marked lines taken out: the vanilla file it was made from."""
    shipped = Script(path)
    marker = MARK.strip()
    kept = [line for line in shipped.lines if marker not in line]

    require(len(kept) < len(shipped.lines), f"{path}: carries no {marker} lines, so it is not a shipped copy")
    require(all(line.rstrip("\r\n").endswith(MARK) for line in shipped.lines if marker in line),
            f"{path}: a line mentions {marker} without ending in the marker")
    return Script(path, shipped.encode(kept))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--scripts", type=Path,
        help="the game's vanilla script folder, the one containing game/player/playerWitcher.ws "
             f"(default: {DEFAULT_SRC})")
    parser.add_argument("--from-shipped", action="store_true",
                        help="rebuild --target from its own shipped copies, with the marked lines "
                             "taken back out, instead of from vanilla scripts")
    parser.add_argument("--target", choices=TARGETS,
                        help="the game version the sources must turn out to be; "
                             "required with --from-shipped, otherwise read from the sources")
    parser.add_argument("--out", type=Path,
                        help="where the patched copies are written "
                             "(default: mod/<version>/modWitcherTrack/content/scripts/game)")
    parser.add_argument("--master", type=Path, default=DEFAULT_MASTER,
                        help="the UTF-8 master of the reporter")
    args = parser.parse_args()

    if args.from_shipped:
        if args.target is None or args.scripts is not None:
            parser.error("--from-shipped takes --target and no --scripts")
        src = shipped_scripts(args.target)
    else:
        src = args.scripts or DEFAULT_SRC

    if not (src / "game/player/playerWitcher.ws").exists():
        parser.error(
            f"no vanilla scripts under {src}\n"
            "Point --scripts at the game's own script folder "
            "(<game>\\content\\content0\\scripts), or copy it to mod/vanilla/.")

    effect_path = find_one(src, "effectManager.ws")
    require(effect_path is not None, f"effectManager.ws: not found under {src}")

    plans = [
        ("playerWitcher.ws", src / "game/player/playerWitcher.ws", plan_player),
        ("hudModuleJournalUpdate.ws", src / "game/gui/hud/modules/hudModuleJournalUpdate.ws", plan_journal),
        ("effectManager.ws", effect_path, plan_effects),
    ]

    # Everything is planned against pristine sources before anything is written, so a
    # missing anchor leaves the output folder exactly as it was - and a rebuild from the
    # shipped copies can write over the very files it read.
    origin = " (shipped copies, marked lines taken out)" if args.from_shipped else ""
    print(f"sources: {src}{origin}")
    planned, versions = [], []
    for label, path, plan in plans:
        if args.from_shipped:
            vanilla = stripped(path)
        else:
            vanilla = Script(path)
            require(not any(MARK.strip() in line for line in vanilla.lines),
                    f"{path}: already carries modWitcherTrack lines; point --scripts at the game's "
                    "own scripts, or rebuild with --from-shipped")

        lines, placed = apply(vanilla, plan(vanilla, label, versions))
        planned.append((label, vanilla, path, lines, placed))
        print(f"  {label:<30}{vanilla.describe()}")

    shapes = ", ".join(f"{function} is {version}" for function, version in versions)
    found = {version for _, version in versions}
    require(len(found) == 1, f"the sources mix game versions: {shapes}")
    target = found.pop()
    require(args.target in (None, target), f"--target {args.target}, but the sources are {target}: {shapes}")
    print(f"game version: {target} ({shapes})")

    out = args.out or shipped_scripts(target) / "game"
    patched = []
    for label, vanilla, path, lines, placed in planned:
        destination = out / path.relative_to(src / "game")
        require(args.from_shipped or not destination.exists() or not destination.samefile(path),
                f"{destination}: would overwrite its own source")
        patched.append((label, vanilla, destination, lines, placed))

    master = Script(args.master)
    layer = Script(compat_master(target))
    deployed = []
    for source, name in ((master, "witchertrack.ws"), (layer, "witchertrack_compat.ws")):
        require(source.encoding == "utf-8" and not source.bom, f"{source.path}: a master is kept in UTF-8")
        check_identifiers("\n".join(source.code), source.path.name)
        text = "".join(source.lines).replace("\r\n", "\n")
        deployed.append((name, out.parent / "local" / name, deployable(text), text))

    for label, vanilla, destination, lines, placed in patched:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(vanilla.encode(lines))
    for name, destination, payload, _ in deployed:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(payload)
        print(f"{name}: converted to UTF-16 LE, {len(payload)} bytes")

    print()
    print(f"{'file':<32}{'function':<30}{'inserted at line'}")
    report = sorted((label, h.function, at + 1) for label, _, _, _, placed in patched for h, at in placed.items())
    for file_name, function_name, line in report:
        print(f"{file_name:<32}{function_name:<30}{line}")

    results = []
    for label, vanilla, destination, _, placed in patched:
        results += verify_patched(label, vanilla, destination, placed)
    for name, destination, payload, text in deployed:
        results += verify_deployed(name, destination, payload, text)
    results += verify_code("\n".join(master.code), "\n".join(layer.code), layer.path.name)
    results += verify_layers("\n".join(master.code))

    print()
    print(f"verification ({target} build in {out.parent})")
    failed = 0
    for label, good, problems in results:
        if problems:
            failed += 1
            print(f"  FAIL  {label}: {'; '.join(problems)}")
        else:
            print(f"  ok    {label}: {good}")

    print("all checks passed" if not failed else f"{failed} check(s) failed - do not ship this output")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
