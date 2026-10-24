"""Makes the shipped, compressed copy of the plugin's images from their lossless sources.

assets-src/{avatar,video}/**.png (lossless RGBA, the raw base to edit or regenerate from) -> assets/{avatar,video}/**.png
(256-colour palette PNGs with alpha, quantized by libimagequant with dithering: about a third of the size and no visible
difference in game, where the plugin block-compresses them anyway). The package and e2e scripts copy assets/.
Only files whose source is newer than the copy are redone; copies without a source are removed.

usage: pack_assets.py [--all]        (needs: pip install pillow imagequant)
"""
import os
import sys

import imagequant
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SRC = os.path.join(ROOT, "assets-src")
OUT = os.path.join(ROOT, "assets")
FOLDERS = ("avatar", "video")


def pack(src, dst):
    image = Image.open(src).convert("RGBA")
    packed = imagequant.quantize_pil_image(image, dithering_level=1.0, max_colors=256, min_quality=0, max_quality=100)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    packed.save(dst, optimize=True)


def main():
    redo = "--all" in sys.argv[1:]
    done = kept = removed = 0
    before = after = 0
    wanted = set()
    for folder in FOLDERS:
        for base, _, files in os.walk(os.path.join(SRC, folder)):
            for name in sorted(files):
                if not name.lower().endswith(".png"):
                    continue
                src = os.path.join(base, name)
                dst = os.path.join(OUT, os.path.relpath(src, SRC))
                wanted.add(os.path.normcase(dst))
                if redo or not os.path.exists(dst) or os.path.getmtime(dst) < os.path.getmtime(src):
                    pack(src, dst)
                    done += 1
                else:
                    kept += 1
                before += os.path.getsize(src)
                after += os.path.getsize(dst)
    for folder in FOLDERS:
        for base, _, files in os.walk(os.path.join(OUT, folder)):
            for name in files:
                path = os.path.join(base, name)
                if name.lower().endswith(".png") and os.path.normcase(path) not in wanted:
                    os.remove(path)
                    removed += 1
    print(f"{done} packed, {kept} up to date, {removed} removed; {before / 2**20:.1f} MB -> {after / 2**20:.1f} MB")


if __name__ == "__main__":
    main()
