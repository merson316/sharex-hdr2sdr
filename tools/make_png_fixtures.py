"""Writes 4x3 RGB PNGs, one per PNG filter type, for decoder tests.
Pixel (x, y) = (x*60, y*100, (x*y*37) % 256)."""
import os, struct, sys, zlib

W, H = 4, 3
PIXELS = [[(x * 60, y * 100, (x * y * 37) % 256) for x in range(W)] for y in range(H)]


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def encode(filter_type, path):
    raw = b""
    prev = [0] * (W * 3)
    for y in range(H):
        cur = [v for px in PIXELS[y] for v in px]
        out = []
        for i, v in enumerate(cur):
            a = cur[i - 3] if i >= 3 else 0
            b = prev[i]
            c = prev[i - 3] if i >= 3 else 0
            pred = {0: 0, 1: a, 2: b, 3: (a + b) // 2, 4: paeth(a, b, c)}[filter_type]
            out.append((v - pred) & 255)
        raw += bytes([filter_type]) + bytes(out)
        prev = cur
    data = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw))
            + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(data)


if __name__ == "__main__":
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    for ft in range(5):
        encode(ft, os.path.join(outdir, f"filter{ft}.png"))
