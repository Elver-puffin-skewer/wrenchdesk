"""Draws the app icon used by the .exe, the desktop shortcut and the browser tab.

Regenerate with:  python build/make-icon.py

It renders the badge from the shop sign — cream hexagon, navy field, red wordmark letter — at
several sizes and packs them into one .ico. Small sizes drop the letter, because at 16px a glyph
turns to mud and the silhouette is what people actually recognise on a taskbar.
"""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

NAVY = (22, 35, 61, 255)
RED = (200, 32, 42, 255)
CREAM = (247, 242, 221, 255)

# Rendered large and shrunk down, which is the cheap way to get clean edges out of PIL.
SUPERSAMPLE = 8
SIZES = [16, 24, 32, 48, 64, 128, 256]

FONT_CANDIDATES = [
    r"C:\Windows\Fonts\georgiab.ttf",
    r"C:\Windows\Fonts\timesbd.ttf",
    r"C:\Windows\Fonts\arialbd.ttf",
]


def load_font(px):
    for path in FONT_CANDIDATES:
        if Path(path).exists():
            try:
                return ImageFont.truetype(path, px)
            except OSError:
                continue
    return ImageFont.load_default()


def hexagon(cx, cy, w, h):
    """Vertical hexagon: points top and bottom, flat-ish sides — the shape of the sign."""
    return [
        (cx, cy - h / 2),
        (cx + w / 2, cy - h / 4),
        (cx + w / 2, cy + h / 4),
        (cx, cy + h / 2),
        (cx - w / 2, cy + h / 4),
        (cx - w / 2, cy - h / 4),
    ]


def render(size, with_letter):
    s = size * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    cx = cy = s / 2

    # Cream border badge, then the navy field inset within it.
    d.polygon(hexagon(cx, cy, s * 0.96, s * 0.99), fill=CREAM)
    d.polygon(hexagon(cx, cy, s * 0.80, s * 0.83), fill=NAVY)

    if with_letter:
        font = load_font(int(s * 0.56))
        box = d.textbbox((0, 0), "E", font=font)
        d.text(
            (cx - (box[0] + box[2]) / 2, cy - (box[1] + box[3]) / 2),
            "E",
            font=font,
            fill=RED,
        )
    else:
        # A red bar keeps the badge reading as the sign when the letter is too small to survive.
        bar_w, bar_h = s * 0.44, s * 0.13
        d.rectangle([cx - bar_w / 2, cy - bar_h / 2, cx + bar_w / 2, cy + bar_h / 2], fill=RED)

    return img.resize((size, size), Image.LANCZOS)


def main():
    out = Path(__file__).resolve().parent.parent / "wwwroot" / "favicon.ico"
    out.parent.mkdir(parents=True, exist_ok=True)

    frames = [render(px, with_letter=px >= 32) for px in SIZES]

    # Pillow writes every requested size from the largest frame, so hand it the biggest and
    # let it carry the rest through append_images.
    frames[-1].save(out, format="ICO", sizes=[(p, p) for p in SIZES], append_images=frames[:-1])

    print(f"Wrote {out} ({out.stat().st_size} bytes, sizes {SIZES})")


if __name__ == "__main__":
    main()
