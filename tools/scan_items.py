"""Read the game's own item, schematic and recipe definitions out of its bundles."""
import sys, re, zlib, glob, os, json, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bundle import entries
import lz4

def bundles(game):
    out  = glob.glob(os.path.join(game, 'content', 'content*', 'bundles', '*.bundle'))
    out += glob.glob(os.path.join(game, 'dlc', '*', 'content', 'bundles', '*.bundle'))
    out += glob.glob(os.path.join(game, 'dlc', '*', 'bundles', '*.bundle'))
    return sorted(out)

def payload(d, size, zsize, offset, comp):
    raw = d[offset:offset+zsize]
    if comp == 0: return raw
    if comp == 1:
        try: return zlib.decompress(raw)
        except zlib.error: return zlib.decompress(raw, -15)
    if comp in (4, 5): return lz4.decompress(raw, size)
    return None                      # doboz and snappy: nothing needed uses them

def scan(game):
    items, schematics, recipes = {}, {}, {}
    files, skipped = 0, collections.Counter()

    for path in bundles(game):
        try: d, es = entries(path)
        except Exception: continue
        for name, size, zsize, offset, comp in es:
            if not name.lower().endswith('.xml') or 'items' not in name.lower(): continue
            blob = payload(d, size, zsize, offset, comp)
            if blob is None: skipped[comp] += 1; continue
            try: x = blob.decode('utf-16')
            except Exception: skipped['decode'] += 1; continue
            files += 1

            for m in re.finditer(r'<item\s([^>]*?)>', x, re.S):
                a = m.group(1)
                nm = re.search(r'name\s*=\s*"([^"]*)"', a)
                ic = re.search(r'icon_path\s*=\s*"([^"]*)"', a)
                if nm and ic: items.setdefault(nm.group(1), ic.group(1))

            for m in re.finditer(r'<schematic\s([^>]*?)>', x, re.S):
                a = m.group(1)
                nm = re.search(r'name_name\s*=\s*"([^"]*)"', a)
                cr = re.search(r'craftedItem_name\s*=\s*"([^"]*)"', a)
                if nm and cr: schematics.setdefault(nm.group(1), cr.group(1))

            for m in re.finditer(r'<recipe\s([^>]*?)>', x, re.S):
                a = m.group(1)
                nm = re.search(r'name_name\s*=\s*"([^"]*)"', a)
                ck = re.search(r'cookedItem_name\s*=\s*"([^"]*)"', a)
                if nm and ck: recipes.setdefault(nm.group(1), ck.group(1))

    return dict(items=items, schematics=schematics, recipes=recipes,
                stats=dict(files=files, skipped=dict(skipped)))

if __name__ == '__main__':
    out = scan(sys.argv[1])
    print('xml files read', out['stats']['files'], 'skipped', out['stats']['skipped'])
    print('items with an icon', len(out['items']))
    print('schematics        ', len(out['schematics']))
    print('alchemy recipes   ', len(out['recipes']))
    here = os.path.dirname(os.path.abspath(__file__))
    json.dump(out, open(os.path.join(here, 'gamedata.json'), 'w', encoding='utf-8'))
