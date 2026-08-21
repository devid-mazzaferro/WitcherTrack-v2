import struct, sys, zlib, collections

def entries(path):
    d = open(path, 'rb').read()
    assert d[:8] == b'POTATO70', d[:8]
    bundle_size, dummy, toc_size = struct.unpack_from('<III', d, 8)
    base = 0x20
    n = toc_size // 0x140
    out = []
    for i in range(n):
        e = base + i * 0x140
        name = d[e:e+0x100].split(b'\x00')[0].decode('latin1')
        size, zsize, offset = struct.unpack_from('<III', d, e + 0x114)
        comp = struct.unpack_from('<I', d, e + 0x13c)[0]
        out.append((name, size, zsize, offset, comp))
    return d, out

if __name__ == '__main__':
    d, es = entries(sys.argv[1])
    print('entries:', len(es))
    print('compression types:', collections.Counter(e[4] for e in es))
    pat = sys.argv[2] if len(sys.argv) > 2 else ''
    for name, size, zsize, offset, comp in es:
        if pat and pat.lower() not in name.lower(): continue
        print(f'{comp} {size:9} {zsize:9} {offset:10}  {name}')
