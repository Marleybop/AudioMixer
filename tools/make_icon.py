"""Draws the AudioMixer app icon at every Windows icon size and writes a multi-size .ico.

Each size is laid out on whole pixels (drawn 4x and scaled down on 4-pixel boundaries),
so the bars stay sharp at 16-32 px instead of being a blurry downscale of the 256 px art.

usage: python make_icon.py <out.ico> [preview.png]
"""
import sys
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 4  # supersampling factor

BG = (27, 28, 32, 255)        # #1b1c20, panel colour from the v2 design
EDGE = (52, 55, 62, 255)      # #34373e
TRACK = (46, 48, 54, 255)     # #2e3036
FILL = (232, 163, 61, 255)    # #e8a33d, the accent
CAP = (235, 232, 225, 255)    # #ebe8e1
LEVELS4 = [0.55, 0.92, 0.38, 0.72]
LEVELS3 = [0.45, 0.92, 0.7]


def draw(size: int) -> Image.Image:
    S = size * SS
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    inset = 0 if size <= 20 else round(size / 16)
    radius = size * 0.22
    box = [inset * SS, inset * SS, (size - inset) * SS - 1, (size - inset) * SS - 1]
    d.rounded_rectangle(box, radius=radius * SS, fill=BG,
                        outline=EDGE if size >= 32 else None, width=max(1, size // 64) * SS)

    levels = LEVELS3 if size < 24 else LEVELS4
    n = len(levels)
    pad = max(3, round(size * 0.22)) if size > 20 else 3
    avail = size - 2 * pad
    gap = max(1, round(avail * 0.11))
    bar = (avail - (n - 1) * gap) // n
    left = (size - (n * bar + (n - 1) * gap)) // 2
    top, bottom = pad, size - pad
    r = bar * 0.25 * SS if size >= 48 else 0

    for i, lv in enumerate(levels):
        x0 = (left + i * (bar + gap)) * SS
        x1 = x0 + bar * SS - 1
        d.rounded_rectangle([x0, top * SS, x1, bottom * SS - 1], radius=r, fill=TRACK)
        fill_top = bottom - max(1, round((bottom - top) * lv))
        d.rounded_rectangle([x0, fill_top * SS, x1, bottom * SS - 1], radius=r, fill=FILL)
        if size >= 32:
            cap_h = max(1, size // 32)
            d.rectangle([x0, fill_top * SS, x1, (fill_top + cap_h) * SS - 1], fill=CAP)

    return img.resize((size, size), Image.BOX)


def main() -> None:
    out = sys.argv[1]
    images = {s: draw(s) for s in SIZES}
    images[256].save(out, format="ICO", sizes=[(s, s) for s in SIZES],
                     append_images=[images[s] for s in SIZES if s != 256])
    if len(sys.argv) > 2:
        sheet = Image.new("RGBA", (sum(SIZES) + 10 * len(SIZES) + 10, 276), (240, 240, 240, 255))
        x = 10
        for s in SIZES:
            sheet.alpha_composite(images[s], (x, 266 - s))
            x += s + 10
        sheet.save(sys.argv[2])
        images[256].save(sys.argv[2].replace(".png", "_256.png"))


if __name__ == "__main__":
    main()
