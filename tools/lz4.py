def decompress(src, expected):
    """LZ4 block format. Enough for the game's bundles, which store the size."""
    out = bytearray()
    i = 0
    n = len(src)
    while i < n:
        token = src[i]; i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]; i += 1
                lit += b
                if b != 255: break
        out += src[i:i+lit]; i += lit
        if i >= n: break
        off = src[i] | (src[i+1] << 8); i += 2
        match = token & 0x0f
        if match == 15:
            while True:
                b = src[i]; i += 1
                match += b
                if b != 255: break
        match += 4
        start = len(out) - off
        for k in range(match):
            out.append(out[start + k])
    if expected is not None and len(out) != expected:
        raise ValueError(f'lz4: got {len(out)} bytes, expected {expected}')
    return bytes(out)
