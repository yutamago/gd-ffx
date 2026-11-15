"""Compares Godot's picture with the original's: a mean absolute difference
per channel (0-255, lower is closer) and a side-by-side PNG (reference,
Godot, difference x4). Usage: diff.py REF GODOT OUT.png"""
import sys

from PIL import Image, ImageChops, ImageStat

ref = Image.open(sys.argv[1]).convert("RGB")
got = Image.open(sys.argv[2]).convert("RGB").resize(ref.size)
diff = ImageChops.difference(ref, got)
w, h = ref.size
mad = sum(ImageStat.Stat(diff).mean) / 3
side = Image.new("RGB", (w * 3, h))
side.paste(ref, (0, 0))
side.paste(got, (w, 0))
side.paste(diff.point(lambda v: min(255, v * 4)), (2 * w, 0))
side.save(sys.argv[3])
print(f"mean absolute difference {mad:.1f} / 255")
