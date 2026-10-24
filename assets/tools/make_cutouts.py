"""Turns cut-out figures into avatar cutouts the plugin loads (Game/Presence/DirectionalAvatar.cs).

Each input is an RGBA PNG of one figure on a transparent background (cropped or not). Each output is a canvas
CANVAS_PX tall standing for AvatarSprites.CanvasHeightMeters (2 m): the figure scaled so its height is the pose's
height, the soles of the shoes on the bottom edge, legs centred horizontally (so the sprite turns and mirrors about the feet).

usage: make_cutouts.py OUT_DIR POSE HEIGHT_M WIDTH_PX direction=figure.png [direction=figure.png ...]
"""
import os
import sys

from PIL import Image

CANVAS_PX = 768
CANVAS_M = 2.0


def legs_centre(alpha):
    """Horizontal centre of mass of the opaque pixels between 55 % and 92 % of the figure's height (the legs; a club
    shaft there is thin enough not to move it much)."""
    w, h = alpha.size
    px = alpha.load()
    total = weighted = 0
    for y in range(int(h * 0.55), int(h * 0.92)):
        for x in range(w):
            a = px[x, y]
            if a > 128:
                total += 1
                weighted += x
    return weighted / total if total else w / 2


def feet_row(alpha):
    """The lowest row of the shoes: the lowest row whose opaque width is at least 40 % of the widest row in the bottom
    tenth (a club head lying lower in front of the feet is narrower and is left hanging below the canvas)."""
    w, h = alpha.size
    px = alpha.load()
    counts = [sum(1 for x in range(w) if px[x, y] > 128) for y in range(h)]
    bottom = counts[int(h * 0.9):]
    widest = max(bottom) if bottom else 0
    for y in range(h - 1, int(h * 0.9) - 1, -1):
        if counts[y] >= widest * 0.4:
            return y
    return h - 1


def main():
    out_dir, pose, height_m, width_px = sys.argv[1], sys.argv[2], float(sys.argv[3]), int(sys.argv[4])
    os.makedirs(out_dir, exist_ok=True)
    for arg in sys.argv[5:]:
        direction, path = arg.split("=", 1)
        fig = Image.open(path).convert("RGBA")
        fig = fig.crop(fig.getchannel("A").point(lambda a: 255 if a > 20 else 0).getbbox())
        target_h = round(height_m / CANVAS_M * CANVAS_PX)
        scale = target_h / fig.height
        fig = fig.resize((max(1, round(fig.width * scale)), target_h), Image.LANCZOS)
        cx = legs_centre(fig.getchannel("A"))
        canvas = Image.new("RGBA", (width_px, CANVAS_PX), (0, 0, 0, 0))
        x = round(width_px / 2 - cx)
        if x < 0 or x + fig.width > width_px:
            print(f"warning: {direction} is clipped by the {width_px}px canvas", file=sys.stderr)
        canvas.paste(fig, (x, CANVAS_PX - 1 - feet_row(fig.getchannel("A"))), fig)
        name = f"{pose}_{direction}.png"
        canvas.save(os.path.join(out_dir, name), optimize=True)
        print(name, fig.size)


if __name__ == "__main__":
    main()
