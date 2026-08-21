"""Pull the inventory icons the catalogue needs out of the game's texture caches.

The chain, all of it the game's own data:

    catalogue id -> <schematic>/<recipe> -> crafted item -> icon_path -> texture.cache

Each cache entry is a zlib stream behind a nine-byte header, holding a BC3 (DXT5)
surface: sixteen bytes per four-by-four block, which is one byte per pixel.
"""
import json
import os
import struct
import sys
import zlib
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scan_items
from texcache import read_index, read_payload

BC3 = 1032


# --------------------------------------------------------------------- BC3

def _colours(c0, c1):
    def rgb(c):
        return (((c >> 11) & 31) * 255 // 31, ((c >> 5) & 63) * 255 // 63, (c & 31) * 255 // 31)

    a, b = rgb(c0), rgb(c1)
    # BC3 always uses the four-colour mode; the three-colour punch-through is BC1 only.
    return [
        a,
        b,
        tuple((2 * a[i] + b[i]) // 3 for i in range(3)),
        tuple((a[i] + 2 * b[i]) // 3 for i in range(3)),
    ]


def _alphas(a0, a1):
    if a0 > a1:
        return [a0, a1] + [((7 - i) * a0 + i * a1) // 7 for i in range(1, 7)]
    return [a0, a1] + [((5 - i) * a0 + i * a1) // 5 for i in range(1, 5)] + [0, 255]


def decode_bc3(data, width, height):
    """BC3 to straight RGBA rows."""
    out = bytearray(width * height * 4)
    blocks_x = (width + 3) // 4
    blocks_y = (height + 3) // 4

    for by in range(blocks_y):
        for bx in range(blocks_x):
            off = (by * blocks_x + bx) * 16
            a0, a1 = data[off], data[off + 1]
            alpha = _alphas(a0, a1)
            bits = int.from_bytes(data[off + 2:off + 8], "little")

            c0, c1 = struct.unpack_from("<HH", data, off + 8)
            colour = _colours(c0, c1)
            (idx,) = struct.unpack_from("<I", data, off + 12)

            for py in range(4):
                y = by * 4 + py
                if y >= height:
                    break
                for px in range(4):
                    x = bx * 4 + px
                    if x >= width:
                        continue
                    k = py * 4 + px
                    r, g, b = colour[(idx >> (2 * k)) & 3]
                    p = (y * width + x) * 4
                    out[p] = r
                    out[p + 1] = g
                    out[p + 2] = b
                    out[p + 3] = alpha[(bits >> (3 * k)) & 7]
    return bytes(out)


# --------------------------------------------------------------------- PNG

def _filtered(row, prior, stride):
    """The five PNG row filters, and the one the standard heuristic prefers.

    Smallest sum of absolute signed differences, which is what every encoder uses. On
    this set it loses to no filtering at all on every single icon - see write_png - and
    it is kept only so that the choice is measured rather than assumed.
    """
    best = None
    for kind in range(5):
        out = bytearray(len(row) + 1)
        out[0] = kind
        for i, x in enumerate(row):
            a = row[i - 4] if i >= 4 else 0
            b = prior[i]
            c = prior[i - 4] if i >= 4 else 0
            if kind == 0:
                v = x
            elif kind == 1:
                v = x - a
            elif kind == 2:
                v = x - b
            elif kind == 3:
                v = x - (a + b) // 2
            else:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                v = x - (a if pa <= pb and pa <= pc else b if pb <= pc else c)
            out[i + 1] = v & 0xff
        score = sum(v if v < 128 else 256 - v for v in out[1:])
        if best is None or score < best[0]:
            best = (score, out)
    return best[1]


def write_png(path, width, height, rgba):
    stride = width * 4

    # Both, and keep whichever ends up smaller. Adaptive filtering is the usual win and
    # here it usually loses: an icon is mostly transparent, and a run of identical zero
    # pixels compresses to nothing until a filter turns it into something else. Measured
    # over the whole set, guessing either way costs about a megabyte.
    flat = bytearray()
    adaptive = bytearray()
    prior = bytes(stride)
    for y in range(height):
        row = rgba[y * stride:(y + 1) * stride]
        flat.append(0)
        flat += row
        adaptive += _filtered(row, prior, stride)
        prior = row

    a = zlib.compress(bytes(flat), 9)
    b = zlib.compress(bytes(adaptive), 9)
    body = a if len(a) <= len(b) else b

    def chunk(tag, body):
        return (struct.pack(">I", len(body)) + tag + body
                + struct.pack(">I", zlib.crc32(tag + body) & 0xffffffff))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", body)
           + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(png)


# -------------------------------------------------------------------- main

def payload(cache, entry):
    blob = read_payload(cache, entry)
    zsize, size, _flag = struct.unpack_from("<IIB", blob, 0)
    data = zlib.decompress(blob[9:9 + zsize])
    if len(data) != size:
        raise ValueError(f"{entry.name}: got {len(data)} bytes, expected {size}")
    return data


def mapping(game, catalog_path):
    """Which icon belongs to each diagram and formula, asked of the game itself.

    A schematic's own icon is the same scroll for all of them, which is why every diagram
    looks alike in the inventory. The icon worth showing belongs to the item the
    schematic *makes*, and the game's own definitions say which that is.
    """
    data = scan_items.scan(game)
    items, sch, rec = data["items"], data["schematics"], data["recipes"]
    catalog = json.load(open(catalog_path, encoding="utf-8"))

    out = {}
    for entry in catalog:
        if entry["kind"] not in ("Diagram", "Formula"):
            continue
        made = sch.get(entry["id"]) or rec.get(entry["id"])
        icon = items.get(made) if made else None
        icon = icon or items.get(entry["id"])
        if icon:
            out[entry["id"]] = icon
    print(f"catalogue: {len(out)} diagrams and formulae resolved to an icon")
    return out


def main(game, catalog_path, outdir):
    wanted = mapping(game, catalog_path)
    keys = {}
    for cid, icon in wanted.items():
        key = ("gameplay/gui_new/" + icon).replace("/", chr(92)).lower()
        keys.setdefault(key, []).append(cid)
    print(f"{len(wanted)} entries want {len(keys)} distinct icons")

    caches = [
        os.path.join(game, "content", "content0", "texture.cache"),
        os.path.join(game, "dlc", "bob", "content", "texture.cache"),
        os.path.join(game, "dlc", "ep1", "content", "texture.cache"),
        os.path.join(game, "dlc", "dlc10", "content", "texture.cache"),
    ]

    os.makedirs(outdir, exist_ok=True)
    done, types, failed = {}, Counter(), []

    for cache in caches:
        if not os.path.exists(cache):
            print("  missing:", cache)
            continue
        _foot, entries = read_index(cache)
        got = 0
        for e in entries:
            # An expansion files its resources under its own root -
            # dlc\bob\data\gameplay\gui_new\... - while the base game starts at
            # gameplay\. The tail is what identifies the icon either way.
            low = e.name.lower()
            key = next((k for k in keys if low.endswith(k)), None)
            if key is None or key in done:
                continue
            types[e.type] += 1
            if e.type != BC3:
                failed.append((e.name, f"type {e.type}, {e.size / (e.width * e.height):.2f} bytes per pixel"))
                continue
            try:
                data = payload(cache, e)
                rgba = decode_bc3(data, e.width, e.height)
                name = os.path.basename(e.name).rsplit(".", 1)[0] + ".png"
                write_png(os.path.join(outdir, name), e.width, e.height, rgba)
                done[key] = name
                got += 1
            except Exception as ex:
                failed.append((e.name, str(ex)[:60]))
        print(f"  {os.path.relpath(cache, game)}: {got}")

    print(f"\nextracted {len(done)} of {len(keys)}")
    print("types seen:", types.most_common())
    for name, why in failed[:10]:
        print("  failed:", name[-60:], "|", why)
    for key in sorted(set(keys) - set(done))[:10]:
        print("  never found:", key)

    index = {cid: done[k] for k, cids in keys.items() if k in done for cid in cids}
    json.dump(index, open(os.path.join(outdir, "index.json"), "w", encoding="utf-8"),
              indent=0, sort_keys=True)
    print("index.json:", len(index), "catalogue ids")


if __name__ == "__main__":
    if len(sys.argv) < 4:
        raise SystemExit(
            "usage: extract_icons.py <game folder> <catalog.json> <output folder>\n"
            "  e.g. python tools/extract_icons.py "
            '"C:/GOG Games/The Witcher 3" data/catalog.json data/icons')
    main(sys.argv[1], sys.argv[2], sys.argv[3])
