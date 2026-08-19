#!/usr/bin/env python3
"""
Turn a downloaded witcher3map tile pyramid into one small background image per region.

Why not just ship the tiles
---------------------------
The published pyramid is ~800 MB, and three quarters of that is the single deepest
zoom level - a pyramid quadruples in size per level, so z6 alone is 4096 of its 5456
tiles. But a pyramid exists to serve a slippy map that streams tiles as you pan, and
the map view here is not one: it fits a whole region into a box and draws points over
it. It needs one picture, at whatever resolution that box can show.

One 2048px WebP per region is around 1.2 MB, so six regions land near 7 MB - about a
hundredth of the pyramid, for a background that is never displayed larger than the
window anyway.

What it does
------------
For each region folder it finds, this stitches the tiles of one zoom level into a
single image, trims the transparent padding that `-p raster` adds to square the
pyramid off, downscales to the target size, and writes a WebP. It also records the
transform from the game's world coordinates straight to pixels in that image, so the
map view needs to know nothing about zoom levels, tile grids or anyone's CRS.

Verifying it
------------
The tile-to-coordinate binding cannot be read from the download: it was generated with
`-w none`, so there is no tilemapresource.xml or viewer to take bounds from, and the
model below is inferred rather than published. So `--verify` renders each region's
signposts onto its own background as circles. Those are the same control points the
calibration was fitted from, and their real positions are known: they sit on roads and
in settlements. If they land on roads, the model is right. If they are all shifted, or
mirrored, or one region is upside down, that is visible at a glance and fixable here -
which is the point of drawing them rather than trusting the arithmetic.

Usage
-----
    python3 tools/build_map_backgrounds.py --tiles path/to/witcher3map-maps-master
    python3 tools/build_map_backgrounds.py --tiles ... --verify --size 1024

Licence
-------
The tiles are CC BY-NC-SA 4.0 by untamed0 and contributors, built from CD Projekt
Red's map art under section 9.4 of their User Agreement. Anything this produces is a
derivative of them and carries the same terms: attribution, non-commercial, and shared
alike. The map view credits them on screen.
"""

import argparse
import json
import math
import re
import sys
from pathlib import Path

try:
    from PIL import Image, ImageDraw
except ImportError:
    raise SystemExit("This needs Pillow:  pip install pillow")

ROOT = Path(__file__).resolve().parent.parent
MAPDIR = ROOT / "data/map"
TILE = 256

# The region folder in the tile download, per world file. Their "velen" covers what the
# game splits into Novigrad and Velen, and they ship a separate "hos_velen" for the
# Hearts of Stone additions to that same world - which is a different picture of the
# same coordinates, so it is not a region of its own here.
FOLDERS = {
    "novigrad.w2w": "velen",
    "skellige.w2w": "skellige",
    "bob.w2w": "toussaint",
    "kaer_morhen.w2w": "kaer_morhen",
    "prolog_village.w2w": "white_orchard",
}


def load_calibration():
    path = MAPDIR / "calibration.json"
    if not path.exists():
        raise SystemExit(
            f"No {path.relative_to(ROOT)}. Run tools/fit_map_calibration.py first - the "
            "backgrounds are placed using the same fits.")

    return json.loads(path.read_text(encoding="utf-8"))


def zoom_levels(folder):
    """The zoom levels present, deepest first."""
    return sorted(
        (int(p.name) for p in folder.iterdir() if p.is_dir() and re.fullmatch(r"\d+", p.name)),
        reverse=True)


def stitch(folder, zoom):
    """Assemble one zoom level into a single image.

    gdal2tiles numbers rows from the bottom (TMS) unless told otherwise, and this
    download was not told otherwise, so row y sits at `side - 1 - y` from the top.
    """
    side = 2 ** zoom
    canvas = Image.new("RGBA", (side * TILE, side * TILE), (0, 0, 0, 0))
    found = 0

    for column in folder.joinpath(str(zoom)).iterdir():
        if not column.is_dir() or not re.fullmatch(r"\d+", column.name):
            continue

        x = int(column.name)

        for tile in column.iterdir():
            match = re.fullmatch(r"(\d+)\.(png|jpg|jpeg|webp)", tile.name, re.IGNORECASE)
            if not match:
                continue

            y = int(match.group(1))
            with Image.open(tile) as image:
                canvas.paste(image.convert("RGBA"), (x * TILE, (side - 1 - y) * TILE))
            found += 1

    return canvas, found


def to_plane(matrix, projection, x, y):
    """World X/Y to the community map's plane coordinates - the fit's own output."""
    north = x * matrix[0][0] + y * matrix[1][0] + matrix[2][0]
    east = x * matrix[0][1] + y * matrix[1][1] + matrix[2][1]
    return north, east


def to_map(matrix, projection, x, y):
    """World X/Y to the coordinates that project's own data is written in."""
    north, east = to_plane(matrix, projection, x, y)

    if projection == "mercator":
        north = math.degrees(2 * math.atan(math.exp(math.radians(north))) - math.pi / 2)

    return north, east


def map_to_pixel(north, east, projection, zoom):
    """Their coordinates to a pixel in the stitched image of that zoom level.

    Two models, matching the two the fits found:

    * A region on a plain plane is drawn with Leaflet's Simple CRS, where a coordinate
      is a pixel scaled by the zoom and the vertical axis runs the other way from the
      screen's. The pyramid is 256 * 2**zoom across, and these regions' coordinates run
      inside 0..256, which is what that scaling implies and is the first thing --verify
      would show to be wrong.
    * A region drawn as a web map uses the usual spherical Mercator, where longitude
      spans -180..180 across the pyramid and latitude is folded in the same way.
    """
    span = TILE * 2 ** zoom

    if projection == "mercator":
        px = (east + 180.0) / 360.0 * span
        sin_lat = math.sin(math.radians(north))
        py = (0.5 - math.log((1 + sin_lat) / (1 - sin_lat)) / (4 * math.pi)) * span
        return px, py

    return east * 2 ** zoom, span - north * 2 ** zoom


def build(region, world, fit, tiles_root, size, verify):
    folder = tiles_root / FOLDERS[world]
    if not folder.is_dir():
        return None, f"no {FOLDERS[world]}/ folder under {tiles_root}"

    levels = zoom_levels(folder)
    if not levels:
        return None, "no zoom-level folders inside it"

    # The shallowest level that still has more pixels than the target, so the downscale
    # below is always shrinking - enlarging a lower level would only invent detail.
    zoom = next((z for z in reversed(levels) if TILE * 2 ** z >= size), levels[0])

    canvas, tiles = stitch(folder, zoom)
    if not tiles:
        return None, f"no tiles in {folder}/{zoom}"

    # `-p raster` pads the source out to a square power of two, so most of the canvas is
    # usually empty. Trimming it is most of the size saving, and it has to be accounted
    # for in the transform below rather than silently shifting everything.
    box = canvas.getbbox()
    cropped = canvas.crop(box)
    scale = min(size / cropped.width, size / cropped.height, 1.0)
    final = cropped.resize(
        (max(1, round(cropped.width * scale)), max(1, round(cropped.height * scale))),
        Image.LANCZOS)

    MAPDIR.mkdir(parents=True, exist_ok=True)
    out = MAPDIR / f"{region}.webp"
    final.save(out, "WEBP", quality=82, method=6)

    matrix = fit["matrix"]
    projection = fit["projection"]

    def world_to_pixel(x, y):
        north, east = to_map(matrix, projection, x, y)
        px, py = map_to_pixel(north, east, projection, zoom)
        return (px - box[0]) * scale, (py - box[1]) * scale

    # The composition of fit, projection, crop and downscale is linear, so it collapses
    # into one 3x2 the map view can apply on its own. Recovered from where three probe
    # points land rather than by multiplying the pieces out, which keeps this honest if
    # any one of them is ever changed.
    origin = world_to_pixel(0.0, 0.0)
    along_x = world_to_pixel(1000.0, 0.0)
    along_y = world_to_pixel(0.0, 1000.0)
    pixel_matrix = [
        [(along_x[0] - origin[0]) / 1000.0, (along_x[1] - origin[1]) / 1000.0],
        [(along_y[0] - origin[0]) / 1000.0, (along_y[1] - origin[1]) / 1000.0],
        [origin[0], origin[1]],
    ]

    if verify:
        proof = final.convert("RGB")
        draw = ImageDraw.Draw(proof)

        for point in fit["points"]:
            px, py = world_to_pixel(point["world"][0], point["world"][1])
            draw.ellipse([px - 6, py - 6, px + 6, py + 6], outline=(255, 64, 64), width=3)

        proof.save(MAPDIR / f"{region}-verify.jpg", quality=88)

    return {
        "image": out.name,
        "zoom": zoom,
        "tiles": tiles,
        "width": final.width,
        "height": final.height,
        "bytes": out.stat().st_size,
        # Applied as [px, py] = [x, y, 1] * pixelMatrix, straight from world coordinates.
        "pixelMatrix": pixel_matrix,
    }, None


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--tiles", type=Path, required=True,
                        help="the extracted witcher3map-maps folder, the one holding "
                             "velen/, skellige/ and so on")
    parser.add_argument("--size", type=int, default=2048,
                        help="longest edge of each background, in pixels (default 2048)")
    parser.add_argument("--verify", action="store_true",
                        help="also write <region>-verify.jpg with the signposts circled")
    args = parser.parse_args()

    if not args.tiles.is_dir():
        raise SystemExit(f"No such folder: {args.tiles}")

    calibration = load_calibration()
    built = {}
    total = 0

    for region, fit in calibration.items():
        world = fit["world"]

        if world not in FOLDERS:
            print(f"{region:14} skipped - no tile folder is known for {world}")
            continue

        result, why = build(region, world, fit, args.tiles, args.size, args.verify)

        if result is None:
            print(f"{region:14} skipped - {why}")
            continue

        built[region] = result
        total += result["bytes"]
        print(f"{region:14} z{result['zoom']} {result['tiles']:5} tiles -> "
              f"{result['width']}x{result['height']}  {result['bytes']/1e6:5.2f} MB")

    if not built:
        raise SystemExit("Nothing built.")

    out = MAPDIR / "backgrounds.json"
    out.write_text(json.dumps(built, indent=2) + "\n", encoding="utf-8")

    print(f"\n{total/1e6:.1f} MB in total, written to {MAPDIR.relative_to(ROOT)}/")
    print(f"Index: {out.relative_to(ROOT)}")

    if args.verify:
        print("\nCheck the -verify.jpg files before trusting these: every circle is a "
              "signpost and belongs on a road or in a settlement.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
