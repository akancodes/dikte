"""Draws dikte.ico (a microphone on a dark rounded square) with no dependencies beyond the standard library."""
import math, struct, sys, zlib

def coverage(x, y, s):
    """RGBA of one pixel centre (x, y) in a unit square, s = pixel size for anti-aliasing via 4x4 supersampling."""
    acc = [0.0, 0.0, 0.0, 0.0]
    n = 4
    for i in range(n):
        for j in range(n):
            px, py = x + (i + 0.5) / n * s, y + (j + 0.5) / n * s
            c = shade(px, py)
            if c:
                a = c[3]
                for k in range(3):
                    acc[k] += c[k] * a
                acc[3] += a
    if acc[3] == 0:
        return (0, 0, 0, 0)
    return tuple(int(acc[k] / acc[3]) for k in range(3)) + (int(255 * acc[3] / (n * n)),)

def in_round_rect(px, py, x0, y0, x1, y1, r):
    cx = min(max(px, x0 + r), x1 - r)
    cy = min(max(py, y0 + r), y1 - r)
    return (px - cx) ** 2 + (py - cy) ** 2 <= r * r and x0 <= px <= x1 and y0 <= py <= y1

def shade(px, py):
    white = (250, 250, 250, 1.0)
    red = (239, 68, 68, 1.0)
    # mic capsule
    if in_round_rect(px, py, 0.39, 0.16, 0.61, 0.58, 0.11):
        return red
    # holder arc: ring centred at (0.5, 0.45), radius 0.2, lower half
    d = math.hypot(px - 0.5, py - 0.45)
    if py >= 0.45 and abs(d - 0.2) <= 0.032:
        return white
    # stem and base
    if 0.47 <= px <= 0.53 and 0.64 <= py <= 0.78:
        return white
    if in_round_rect(px, py, 0.36, 0.76, 0.64, 0.82, 0.03):
        return white
    # background
    if in_round_rect(px, py, 0.02, 0.02, 0.98, 0.98, 0.22):
        return (24, 24, 27, 1.0)
    return None

def png(size):
    rows = []
    for y in range(size):
        row = bytearray([0])
        for x in range(size):
            row += bytes(coverage(x / size, y / size, 1 / size))
        rows.append(bytes(row))
    raw = b"".join(rows)
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")

sizes = [16, 24, 32, 48, 64, 256]
images = [png(s) for s in sizes]
header = struct.pack("<HHH", 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
entries = b""
for s, data in zip(sizes, images):
    entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
open(sys.argv[1], "wb").write(header + entries + b"".join(images))
open(sys.argv[1].replace(".ico", ".png"), "wb").write(images[-1])
