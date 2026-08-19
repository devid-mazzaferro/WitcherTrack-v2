#!/usr/bin/env python3
"""
Fit the transform from the game's world coordinates to a community map's own
coordinates, one region at a time, and write the result out.

Why this exists
---------------
The game gives every point of interest a world X/Y (see `CatalogEntry`), and the
community map project at witcher3map.com gives every marker a position in its own
Leaflet coordinate system. Neither publishes a formula relating the two. This
recovers one by least squares, using the places that appear in both and can be
matched without judgement: fast-travel signposts, which are named on both sides.

What is published is a similarity - rotation, one uniform scale, translation and a
possible axis flip - because that is the only thing two linear renderings of the
same world can differ by. It is fitted directly rather than trimmed down from an
affine, so it cannot absorb an error as skew.

Two checks decide whether to believe it, and neither is the residual, which a
least-squares fit always has:

  * An unconstrained affine is fitted alongside, purely as a diagnostic. It is
    free to skew, so if the relationship really is a similarity it will decline
    to, and its two singular values will come out equal. That it *can* skew and
    does not is the evidence. This needs redundancy to mean anything - below ten
    control points a free affine will soak up ordinary marker-placement error as
    skew and look damning when nothing is wrong.
  * Leave-one-out: refit without each control point and measure how far off that
    point then lands. A fit that has memorised its inputs rather than found the
    relationship shows a small residual and a large error here. On a five-signpost
    region this is the only honest test available, and it is what decides.

Input
-----
`data/catalog.json`             - built by `WitcherTrack catalog`; supplies world
                                  X/Y and, critically, the streamed world each
                                  point came from, which is what makes matching
                                  per region possible at all.
`data/map/<region>-signposts.txt` - one `key|lat|lng` per line, transcribed from
                                  that region's `mapdata-<region>.js` in the
                                  witcher3map repository. Their labels are i18n
                                  keys whose final segment is a camelCase English
                                  name (`stonecuttersSettlement`), which is what
                                  is matched against our own display names.

Output
------
`data/map/calibration.json` - per region: the matrix, the fit's diagnostics, and
                              the control points it was fitted from, so the fit
                              can be audited without re-running anything.
"""

import json
import math
import re
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
CATALOG = ROOT / "data/catalog.json"
MAPDIR = ROOT / "data/map"

# Above this, the fitted 2x2 is not a similarity and the fit is not believable.
# A correct fit lands within a few parts in ten thousand of 1.0; this leaves room
# for the map having been assembled from tiles that do not quite align.
MAX_ANISOTROPY = 1.01

# For a region with too few control points to judge by anisotropy, this is the
# leave-one-out error a fit has to stay under. The four regions that can be judged
# properly all land between 4 and 8 metres, so this is generous rather than lax.
MAX_LOO_METRES = 20.0

# Which streamed world each community-map region corresponds to, and how that
# region's map coordinates relate to a plane.
#
# The mapping is not one-to-one by name: what that project calls "Velen" is the
# world file the game calls novigrad.w2w, and it contains Novigrad and Oxenfurt
# too.
#
# The projection matters. Some regions are drawn on a plain linear CRS, where the
# published lat/lng *are* plane coordinates and a straight affine fits. Others are
# drawn as a web map, where latitude is Mercator-stretched and grows with distance
# from the equator - fitting an affine to raw lat/lng there asks a straight line to
# follow a curve, and it cannot. Skellige spans 130 degrees of that stretch and
# fails the similarity check outright (anisotropy 1.32) until the latitude is
# un-stretched first, which is what "mercator" here means.
REGIONS = {
    "velen": ("novigrad.w2w", "linear"),
    "skellige": ("skellige.w2w", "mercator"),
    "toussaint": ("bob.w2w", "linear"),
    "kaermorhen": ("kaer_morhen.w2w", "linear"),
    "whiteorchard": ("prolog_village.w2w", "mercator"),
}


def project(lat, lng, projection):
    """Their published coordinates, as a point on a plane.

    Leaflet's spherical Mercator, with the earth radius left out: the fit absorbs
    any constant scale, so only the shape of the stretch matters here. Inverting
    it to get back to lat/lng for display is
    `lat = degrees(2 * atan(exp(radians(y))) - pi / 2)`.
    """
    if projection == "linear":
        return lat, lng

    if projection == "mercator":
        # A latitude past the poles is not a latitude. A region whose coordinates
        # run beyond ±90 is being drawn on a plain plane and simply labelling the
        # axes lat/lng, so asking for Mercator here is a configuration mistake
        # rather than a number to nurse - Toussaint reaches 113 and would return
        # a domain error from the logarithm below.
        if abs(lat) >= 90:
            raise SystemExit(
                f"{lat} is outside a latitude, so this region is not Mercator - "
                "set it to 'linear' in REGIONS")

        stretched = math.degrees(math.log(math.tan(math.pi / 4 + math.radians(lat) / 2)))
        return stretched, lng

    raise SystemExit(f"unknown projection {projection!r}")


def fit_similarity(source, target):
    """Least-squares rotation, uniform scale and translation taking source to target.

    Umeyama's solution. A reflection is allowed rather than corrected away: the
    two coordinate systems disagree about which axis runs which way - their
    latitude tracks our Y and their longitude our X - and that axis swap is a
    reflection, not an error.

    Returns the same 3x2 shape the affine fit produces, so both can be applied as
    `[x, y, 1] @ matrix`, plus the scale factor.
    """
    source_mean = source.mean(axis=0)
    target_mean = target.mean(axis=0)
    centred_source = source - source_mean
    centred_target = target - target_mean

    u, singular, vt = np.linalg.svd(centred_source.T @ centred_target / len(source))
    rotation = (u @ vt).T
    scale = float(singular.sum() / (centred_source ** 2).sum() * len(source))

    linear = (scale * rotation).T
    translation = target_mean - source_mean @ linear

    return np.vstack([linear, translation]), scale


def normalise(text):
    """camelCase or 'Title Case' down to a comparable key."""
    spaced = re.sub(r"([a-z0-9])([A-Z])", r"\1 \2", text)
    return re.sub(r"[^a-z0-9]", "", spaced.lower())


def load_theirs(path):
    points = defaultdict(list)

    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue

        key, lat, lng = line.split("|")
        points[normalise(key)].append((key, float(lat), float(lng)))

    return points


def load_ours(entries, world_suffix):
    points = defaultdict(list)

    for entry in entries:
        if entry.get("region") != "RoadSign" or entry.get("x") is None:
            continue
        if not (entry.get("world") or "").endswith(world_suffix):
            continue

        name = entry.get("displayName") or ""

        # A point whose "name" is just its identifier tidied up was never named
        # by the game, so it cannot be matched by name against anything.
        if not name or normalise(name) == normalise(entry["id"]):
            continue

        points[normalise(name)].append(entry)

    return points


def fit_region(region, world_suffix, projection, entries):
    source = MAPDIR / f"{region}-signposts.txt"
    if not source.exists():
        return None, f"no {source.name} on file"

    theirs = load_theirs(source)
    ours = load_ours(entries, world_suffix)

    # Only names that appear exactly once on each side. A name occurring twice
    # (there are several "Grotto"s) carries no information about which is which,
    # and guessing would quietly poison the fit.
    shared = set(theirs) & set(ours)
    usable = [k for k in shared if len(theirs[k]) == 1 and len(ours[k]) == 1]

    # Three points fit an affine exactly and prove nothing. Five is the smallest
    # set with real redundancy, and it is what Kaer Morhen has - the whole region
    # carries five signposts. The similarity check below is what actually decides
    # whether a small fit is believable, not the count.
    if len(usable) < 5:
        return None, f"only {len(usable)} unambiguous control points, too few to fit"

    control = [
        {
            "name": ours[k][0].get("displayName"),
            "id": ours[k][0]["id"],
            "world": [ours[k][0]["x"], ours[k][0]["y"]],
            "map": [theirs[k][0][1], theirs[k][0][2]],
            "plane": list(project(theirs[k][0][1], theirs[k][0][2], projection)),
        }
        for k in sorted(usable)
    ]

    world = np.array([c["world"] for c in control], float)
    target = np.array([c["plane"] for c in control], float)
    design = np.column_stack([world, np.ones(len(world))])

    # Two fits, for two different jobs.
    #
    # The unconstrained affine is the diagnostic: it is free to skew, so if the
    # relationship really is a rotation and a uniform scale, it will decline to
    # skew and its two singular values will come out equal. That it *can* and
    # does not is the evidence. Its anisotropy is reported below and nothing is
    # published from it.
    #
    # What is published is a similarity - rotation, one scale, translation, and
    # a possible axis flip - fitted directly. It is the physically correct model,
    # it cannot absorb an error as skew, and with four free parameters instead of
    # six it stays honest on a region with only a handful of signposts. Kaer
    # Morhen has five in total, and an affine fitted to those reads a convincing
    # 2.3 m residual while quietly skewing by 2.4%.
    loose, *_ = np.linalg.lstsq(design, target, rcond=None)
    singular = np.linalg.svd(loose[:2, :], compute_uv=False)
    anisotropy = float(singular[0] / singular[1])

    matrix, scale = fit_similarity(world, target)
    residual = np.linalg.norm(design @ matrix - target, axis=1)
    metres = float(1 / scale)

    # Leave-one-out: refit without each point and measure how far off that point
    # then lands. A fit that has memorised its control points rather than found
    # the relationship shows a small residual and a large error here. On a region
    # with five signposts this is the only honest test there is - the anisotropy
    # diagnostic above needs more redundancy than five points can give before its
    # verdict means anything.
    loo = []
    for i in range(len(world)):
        keep = [j for j in range(len(world)) if j != i]
        held, _ = fit_similarity(world[keep], target[keep])
        loo.append(float(np.linalg.norm(np.append(world[i], 1.0) @ held - target[i])))
    loo = np.array(loo)

    return {
        "world": world_suffix,
        "projection": projection,
        # Row-major, applied as [north, east] = [x, y, 1] @ matrix, where those
        # are plane coordinates: for a "mercator" region they must be inverted
        # back to latitude before Leaflet is given them (see project()).
        # This is the constrained similarity, not the affine.
        "matrix": matrix.tolist(),
        "model": "similarity",
        "controlPoints": len(control),
        "ambiguousNamesSkipped": sorted(k for k in shared if k not in usable),
        "residual": {
            "mean": float(residual.mean()),
            "median": float(np.median(residual)),
            "p95": float(np.percentile(residual, 95)),
            "max": float(residual.max()),
            "meanMetres": float(residual.mean() * metres),
        },
        "anisotropy": anisotropy,
        # The anisotropy diagnostic needs redundancy before it says anything. With
        # only a handful of control points a free affine can soak up ordinary
        # marker-placement error as skew and look damning when nothing is wrong,
        # so below this many points the leave-one-out error decides instead.
        "anisotropyReliable": len(control) >= 10,
        "leaveOneOut": {
            "mean": float(loo.mean()),
            "max": float(loo.max()),
            "meanMetres": float(loo.mean() * metres),
        },
        "metresPerMapUnit": metres,
        "trusted": (anisotropy <= MAX_ANISOTROPY if len(control) >= 10
                    else float(loo.mean()) * metres <= MAX_LOO_METRES),
        "points": control,
    }, None


def main():
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    entries = catalog if isinstance(catalog, list) else catalog.get("entries", catalog)

    results = {}
    failures = 0

    for region, (world_suffix, projection) in REGIONS.items():
        fit, why = fit_region(region, world_suffix, projection, entries)

        if fit is None:
            print(f"{region:14} skipped - {why}")
            continue

        verdict = "ok" if fit["trusted"] else "NOT A SIMILARITY - do not use"
        print(
            f"{region:14} {fit['controlPoints']:3} points  "
            f"mean {fit['residual']['mean']:.3f} map units "
            f"({fit['residual']['meanMetres']:.1f} m)  "
            f"anisotropy {fit['anisotropy']:.5f}"
            f"{'' if fit['anisotropyReliable'] else ' (n/a)'}  "
            f"loo {fit['leaveOneOut']['meanMetres']:5.1f} m  {projection:9} {verdict}"
        )

        if not fit["trusted"]:
            failures += 1

        results[region] = fit

    if not results:
        print("\nNothing fitted. Transcribe a region's signposts into "
              f"{MAPDIR}/<region>-signposts.txt first.")
        return 1

    out = MAPDIR / "calibration.json"
    out.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    print(f"\nWritten to {out.relative_to(ROOT)}")

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
