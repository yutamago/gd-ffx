"""Compares the colour balance of two pictures of the same view (the classic
look and the remastered one): mean OKLab lightness, chroma and hue over the
whole picture and over a grid of cells, and whether they stay within the
tolerances of docs/remaster-editor.md ("The colour check"). The same numbers
as the game's --selftest-lighting (godot/src/Remaster/ColourBalance.cs).

Usage: colour.py CLASSIC.png REMASTERED.png [--json]
Exit status 1 when a tolerance is broken."""
import json
import math
import sys

from PIL import Image

# the tolerances (ColourBalance.cs keeps the same)
LIGHTNESS = 0.06      # |mean L remastered - classic| (OKLab L, 0..1)
CHROMA = (0.75, 1.35)  # mean chroma remastered / classic
HUE = 12.0            # chroma-weighted mean hue difference, degrees
CELL_LIGHTNESS = 0.15  # the cells' median |dL|
CELL_HUE = 25.0       # the cells' median hue difference (cells with colour in both)
COLS, ROWS = 4, 3


def lin(c):
    c /= 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


LUT = [lin(float(i)) for i in range(256)]


def oklab(r, g, b):
    r, g, b = LUT[r], LUT[g], LUT[b]
    l = (0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b) ** (1 / 3)
    m = (0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b) ** (1 / 3)
    s = (0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b) ** (1 / 3)
    return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s)


class Acc:
    def __init__(self):
        self.n = self.l = self.c = self.a = self.b = 0.0

    def add(self, lab):
        l, a, b = lab
        self.n += 1
        self.l += l
        self.c += math.hypot(a, b)
        self.a += a
        self.b += b

    def stats(self):
        n = max(self.n, 1)
        return {"L": self.l / n, "C": self.c / n,
                "hue": math.degrees(math.atan2(self.b, self.a)) % 360,
                "hueChroma": math.hypot(self.a, self.b) / n}


def measure(path, size):
    img = Image.open(path).convert("RGB").resize(size)
    w, h = size
    px = img.load()
    total = Acc()
    cells = [Acc() for _ in range(COLS * ROWS)]
    cache = {}
    for y in range(0, h, 2):
        for x in range(0, w, 2):
            p = px[x, y]
            lab = cache.get(p)
            if lab is None:
                lab = cache[p] = oklab(*p)
            total.add(lab)
            cells[min(ROWS - 1, y * ROWS // h) * COLS + min(COLS - 1, x * COLS // w)].add(lab)
    return total.stats(), [c.stats() for c in cells]


def hue_diff(a, b):
    d = abs(a - b) % 360
    return min(d, 360 - d)


def median(v):
    v = sorted(v)
    return v[len(v) // 2] if v else 0.0


def compare(classic, remastered):
    size = Image.open(classic).size
    (c, cc), (r, rc) = measure(classic, size), measure(remastered, size)
    dl = r["L"] - c["L"]
    ratio = r["C"] / max(c["C"], 1e-6)
    dh = hue_diff(r["hue"], c["hue"])
    cell_dl = median([abs(b["L"] - a["L"]) for a, b in zip(cc, rc)])
    cell_dh = median([hue_diff(a["hue"], b["hue"]) for a, b in zip(cc, rc)
                      if a["hueChroma"] > 0.01 and b["hueChroma"] > 0.01])
    fails = []
    # a remastered picture that is the classic one measures nothing (a frame not drawn: a lost GPU device)
    if Image.open(classic).convert("RGB").tobytes() == Image.open(remastered).convert("RGB").resize(size).tobytes():
        fails.append("the remastered picture is the classic one")
    if abs(dl) > LIGHTNESS:
        fails.append(f"lightness {dl:+.3f} (limit {LIGHTNESS})")
    if not CHROMA[0] <= ratio <= CHROMA[1]:
        fails.append(f"chroma x{ratio:.2f} (limit {CHROMA[0]}..{CHROMA[1]})")
    if dh > HUE:
        fails.append(f"hue {dh:.1f} deg (limit {HUE})")
    if cell_dl > CELL_LIGHTNESS:
        fails.append(f"cells' lightness {cell_dl:.3f} (limit {CELL_LIGHTNESS})")
    if cell_dh > CELL_HUE:
        fails.append(f"cells' hue {cell_dh:.1f} deg (limit {CELL_HUE})")
    return {"classic": c, "remastered": r, "dL": dl, "chromaRatio": ratio, "dHue": dh,
            "cellsDL": cell_dl, "cellsDHue": cell_dh, "fails": fails}


def main():
    res = compare(sys.argv[1], sys.argv[2])
    if "--json" in sys.argv:
        print(json.dumps(res, indent=1))
    c, r = res["classic"], res["remastered"]
    print(f"classic    L {c['L']:.3f} C {c['C']:.3f} hue {c['hue']:.0f}")
    print(f"remastered L {r['L']:.3f} C {r['C']:.3f} hue {r['hue']:.0f}")
    print(f"dL {res['dL']:+.3f}, chroma x{res['chromaRatio']:.2f}, hue {res['dHue']:.1f} deg; "
          f"cells: |dL| {res['cellsDL']:.3f}, hue {res['cellsDHue']:.1f} deg")
    print("colour balance: " + ("ok" if not res["fails"] else "OFF: " + "; ".join(res["fails"])))
    sys.exit(1 if res["fails"] else 0)


if __name__ == "__main__":
    main()
