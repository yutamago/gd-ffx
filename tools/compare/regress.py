"""R8, the sweep's regression pictures: a picture against its baseline.

Usage: regress.py BASELINE PICTURE DIFF.png [THRESHOLD]

Prints the mean absolute difference per channel (0..255), the share of
pixels off by more than 24 in any channel, and the worst cell of a 4 x 3 grid;
writes DIFF.png (baseline, picture, difference x4 side by side). Exit 1 when
the mean is over THRESHOLD (default 2.0), 2 when the sizes differ."""
import sys

from PIL import Image, ImageChops, ImageStat

base = Image.open(sys.argv[1]).convert("RGB")
got = Image.open(sys.argv[2]).convert("RGB")
limit = float(sys.argv[4]) if len(sys.argv) > 4 else 2.0
if base.size != got.size:
    print(f"size {got.size[0]}x{got.size[1]}, the baseline's {base.size[0]}x{base.size[1]}: REGRESSED")
    sys.exit(2)
diff = ImageChops.difference(base, got)
w, h = base.size
mad = sum(ImageStat.Stat(diff).mean) / 3
r, g, b = diff.split()
most = ImageChops.lighter(ImageChops.lighter(r, g), b)
off = ImageStat.Stat(most.point(lambda v: 255 if v > 24 else 0)).mean[0] / 255
worst, at = 0.0, ""
for gy in range(3):
    for gx in range(4):
        box = (gx * w // 4, gy * h // 3, (gx + 1) * w // 4, (gy + 1) * h // 3)
        m = sum(ImageStat.Stat(diff.crop(box)).mean) / 3
        if m > worst:
            worst, at = m, f"{gx},{gy}"
side = Image.new("RGB", (w * 3, h))
side.paste(base, (0, 0))
side.paste(got, (w, 0))
side.paste(diff.point(lambda v: min(255, v * 4)), (2 * w, 0))
side.save(sys.argv[3])
verdict = "REGRESSED" if mad > limit else "ok"
print(f"mean {mad:.2f}/255 (threshold {limit:g}), {off * 100:.2f} % of pixels off by > 24, worst cell {at} {worst:.2f}: {verdict}")
sys.exit(1 if mad > limit else 0)
