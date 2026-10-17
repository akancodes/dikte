"""
Draws Dikte's icons with nothing but the standard library: the app icon (.ico, every size Windows asks for),
the tray icons for each state, and a preview sheet.

    python3 tools/make-icon.py <design> <out-dir>          # dikte.ico, tray-idle/listening/working.ico, icon.png
    python3 tools/make-icon.py --sheet <out.png>           # every design side by side, with tray previews

Shapes are signed-distance functions sampled 4x4 per pixel, so small sizes stay crisp.
"""
import math, struct, sys, zlib

# ---------- geometry ----------

def squircle(x, y, r=0.47, n=5.0):
    """Inside test for a superellipse centred in the unit square (Windows 11 / iOS icon shape)."""
    dx, dy = abs(x - 0.5) / r, abs(y - 0.5) / r
    return dx ** n + dy ** n <= 1

def rrect(x, y, x0, y0, x1, y1, rad):
    cx = min(max(x, x0 + rad), x1 - rad)
    cy = min(max(y, y0 + rad), y1 - rad)
    return x0 <= x <= x1 and y0 <= y <= y1 and (x - cx) ** 2 + (y - cy) ** 2 <= rad * rad

def lerp(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))

def hexc(h):
    return tuple(int(h[i:i + 2], 16) for i in (1, 3, 5))

# ---------- glyphs (white on the tile) ----------

BARS = [0.30, 0.52, 0.74, 0.52, 0.30]   # sound-wave bar heights, as a share of the tile

def bars(x, y, heights=BARS, width=0.085, gap=0.045):
    total = len(heights) * width + (len(heights) - 1) * gap
    x0 = 0.5 - total / 2
    for i, h in enumerate(heights):
        bx = x0 + i * (width + gap)
        if rrect(x, y, bx, 0.5 - h / 2, bx + width, 0.5 + h / 2, width / 2):
            return True
    return False

def mic(x, y):
    if rrect(x, y, 0.385, 0.20, 0.615, 0.58, 0.115):
        return True
    d = math.hypot(x - 0.5, y - 0.44)
    if y >= 0.44 and abs(d - 0.215) <= 0.04:
        return True
    return rrect(x, y, 0.46, 0.64, 0.54, 0.80, 0.02) or rrect(x, y, 0.35, 0.76, 0.65, 0.82, 0.03)

def bubble(x, y):
    """A speech bubble with a tail at the lower left."""
    if rrect(x, y, 0.20, 0.22, 0.80, 0.70, 0.17):
        return True
    # tail: triangle from (0.30,0.62) (0.46,0.68) to (0.26,0.82)
    ax, ay, bx, by, cx, cy = 0.29, 0.60, 0.47, 0.68, 0.25, 0.82
    d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
    l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d
    l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d
    return l1 >= 0 and l2 >= 0 and 1 - l1 - l2 >= 0

# ---------- designs ----------
# Each design: tile colours (top-left, bottom-right) per state, the glyph, and the glyph colour.

STATES = {
    "listening": (hexc("#FF6B5B"), hexc("#D9261C")),
    "working": (hexc("#5B9DFF"), hexc("#2456D6")),
}

DESIGNS = {
    "wave": {   # A: deep violet tile, white sound wave
        "idle": (hexc("#8B5CF6"), hexc("#4C1D95")), "glyph": bars, "ink": (255, 255, 255),
        "label": "A  Wave",
    },
    "mic": {    # B: coral-to-pink tile, white microphone
        "idle": (hexc("#FF8A4C"), hexc("#E11D74")), "glyph": mic, "ink": (255, 255, 255),
        "label": "B  Mic",
    },
    "bubble": { # C: graphite tile, white speech bubble holding a coloured wave
        "idle": (hexc("#3F3F46"), hexc("#111114")), "glyph": None, "ink": (255, 255, 255),
        "label": "C  Bubble",
    },
}

def shade(design, state, x, y, small):
    d = DESIGNS[design]
    if not squircle(x, y):
        return None
    top, bottom = STATES.get(state, d["idle"])
    tile = lerp(top, bottom, max(0.0, min(1.0, (x + y) / 2)))
    if design == "bubble":
        heights = [0.13, 0.23, 0.32, 0.23, 0.13]
        if bubble(x, y):
            inner = 0.46
            # the wave sits inside the bubble, shifted up to its centre
            if bars(x, y + (0.5 - inner), heights, 0.07, 0.04):
                return lerp(hexc("#FF6B5B"), hexc("#8B5CF6"), (x - 0.3) / 0.4) if state == "idle" else tile
            return d["ink"]
        return tile
    glyph = d["glyph"]
    if design == "wave" and small:
        # three fat bars read better at 16 px than five thin ones
        hit = bars(x, y, [0.36, 0.62, 0.36], 0.15, 0.08)
    else:
        hit = glyph(x, y)
    return d["ink"] if hit else tile

def render(design, state, size):
    small = size <= 24
    px = []
    n = 4
    for j in range(size):
        row = []
        for i in range(size):
            acc = [0.0, 0.0, 0.0]
            cover = 0
            for a in range(n):
                for b in range(n):
                    c = shade(design, state, (i + (a + 0.5) / n) / size, (j + (b + 0.5) / n) / size, small)
                    if c:
                        cover += 1
                        for k in range(3):
                            acc[k] += c[k]
            if cover:
                row.append(tuple(int(acc[k] / cover) for k in range(3)) + (int(255 * cover / (n * n)),))
            else:
                row.append((0, 0, 0, 0))
        px.append(row)
    return px

# ---------- encoders ----------

def png(px):
    h, w = len(px), len(px[0])
    raw = b"".join(b"\x00" + bytes(v for p in row for v in p) for row in px)
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))

def ico(images):
    """images: list of (size, png bytes)."""
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data
    return header + entries + blobs

APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
TRAY_SIZES = [16, 20, 24, 32, 40, 48]

def build(design, out):
    with open(f"{out}/dikte.ico", "wb") as f:
        f.write(ico([(s, png(render(design, "idle", s))) for s in APP_SIZES]))
    for state in ("idle", "listening", "working"):
        with open(f"{out}/tray-{state}.ico", "wb") as f:
            f.write(ico([(s, png(render(design, state, s))) for s in TRAY_SIZES]))
    with open(f"{out}/icon.png", "wb") as f:
        f.write(png(render(design, "idle", 256)))

# ---------- preview sheet ----------

def sheet(out):
    W, rowh = 900, 300
    canvas = [[(246, 246, 247, 255)] * W for _ in range(rowh * len(DESIGNS))]
    def blit(img, x0, y0, scale=1):
        for j, row in enumerate(img):
            for i, p in enumerate(row):
                if p[3] == 0:
                    continue
                for sy in range(scale):
                    for sx in range(scale):
                        X, Y = x0 + i * scale + sx, y0 + j * scale + sy
                        bg = canvas[Y][X]
                        a = p[3] / 255
                        canvas[Y][X] = tuple(int(p[k] * a + bg[k] * (1 - a)) for k in range(3)) + (255,)
    for r, design in enumerate(DESIGNS):
        y = r * rowh
        blit(render(design, "idle", 224), 30, y + 38)
        # a dark taskbar strip with the three tray states at real 16 and 24 px, magnified 4x
        for X in range(290, W - 20):
            for Y in range(y + 60, y + 240):
                canvas[Y][X] = (32, 32, 36, 255)
        for k, state in enumerate(("idle", "listening", "working")):
            blit(render(design, state, 16), 320 + k * 190, y + 80, 4)
            blit(render(design, state, 24), 320 + k * 190, y + 150, 3)
            blit(render(design, state, 16), 410 + k * 190, y + 95)
            blit(render(design, state, 24), 440 + k * 190, y + 90)
    with open(out, "wb") as f:
        f.write(png(canvas))

if __name__ == "__main__":
    if sys.argv[1] == "--sheet":
        sheet(sys.argv[2])
    else:
        build(sys.argv[1], sys.argv[2])
