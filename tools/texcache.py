"""Read a Witcher 3 texture.cache: its index, and the pixels of one entry.

Layout, from the end of the file backwards:

    [ pixel data, addressed in 4096-byte pages ]
    [ MipsOffsetTable   MipEntryCount * 4 bytes ]
    [ NamesTable        StringTableSize bytes, null-separated paths ]
    [ EntryInfoTable    EntryCount * 52 bytes ]
    [ Footer            32 bytes ]
"""
import os
import struct
from collections import namedtuple

FOOTER = 32
ENTRY = 52
PAGE = 4096

Footer = namedtuple("Footer", "crc usedPages entryCount stringTableSize mipEntryCount magic version")
Entry = namedtuple(
    "Entry",
    "hash pathIndex pageOffset zsize size baseAlignment width height mips slices "
    "mipOffsetIndex mipsCount timestamp type isCube name",
)


def read_footer(f, size):
    f.seek(size - FOOTER)
    crc, pages, entries, tbl, mips = struct.unpack("<QIIII", f.read(24))
    magic = f.read(4)
    (version,) = struct.unpack("<I", f.read(4))
    return Footer(crc, pages, entries, tbl, mips, magic, version)


def read_index(path):
    size = os.path.getsize(path)
    with open(path, "rb") as f:
        foot = read_footer(f, size)
        if foot.magic != b"HCXT":
            raise ValueError(f"{path}: not a texture cache ({foot.magic!r})")

        entries_start = size - FOOTER - foot.entryCount * ENTRY
        names_start = entries_start - foot.stringTableSize

        f.seek(names_start)
        table = f.read(foot.stringTableSize)

        f.seek(entries_start)
        raw = f.read(foot.entryCount * ENTRY)

    def at(offset):
        """PathStringIndex is a byte offset into the table, not an index into it."""
        if not 0 <= offset < len(table):
            return ""
        end = table.find(b"\x00", offset)
        return table[offset:end if end >= 0 else None].decode("latin1")

    out = []
    for i in range(foot.entryCount):
        v = struct.unpack_from("<iiIIIIHHHHiiqhh", raw, i * ENTRY)
        out.append(Entry(*v, name=at(v[1])))
    return foot, out


def read_payload(path, entry):
    """The entry's own bytes: the top mip, still in whatever the game cooked it as."""
    with open(path, "rb") as f:
        f.seek(entry.pageOffset * PAGE)
        return f.read(entry.zsize)


if __name__ == "__main__":
    import sys

    cache = sys.argv[1]
    foot, entries = read_index(cache)
    print(foot._replace(crc=hex(foot.crc)))
    print("entries:", len(entries))
    named = sum(1 for e in entries if e.name)
    print("entries with a name:", named)
    for e in entries[:3]:
        print(" ", e.width, "x", e.height, "mips", e.mips, "type", e.type, "|", e.name[:70])

    want = sys.argv[2].lower() if len(sys.argv) > 2 else None
    if want:
        for e in entries:
            if want in e.name.lower():
                print("MATCH", e.name, "|", e.width, "x", e.height,
                      "| zsize", e.zsize, "size", e.size, "| type", e.type,
                      "| mips", e.mips, "| page", e.pageOffset)
