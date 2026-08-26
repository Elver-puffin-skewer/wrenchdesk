"""Draws the app icon used by the .exe, the desktop shortcut and the browser tab.

Regenerate with:  python build/make-icon.py

It renders the badge from the shop sign — cream hexagon, navy field, red monogram — at several
sizes and packs them into one .ico.

The monogram simplifies as the icon shrinks. "WSE" needs about 48px to stay legible; below that
three letters collapse into a smear, so it steps down to a single "W", and at the very smallest
size to a plain bar. That is deliberate: on a taskbar what people actually recognise is the
silhouette and the colours, not glyphs they cannot resolve.
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


def fit_font(draw, text, max_w, max_h, start_px):
    """Largest font size at which the text fits the space available inside the badge."""
    px = start_px
    while px > 4:
        font = load_font(px)
        box = draw.textbbox((0, 0), text, font=font)
        if (box[2] - box[0]) <= max_w and (box[3] - box[1]) <= max_h:
            return font
        px -= max(1, px // 20)
    return load_font(4)


def draw_centred(draw, cx, cy, text, font, fill):
    box = draw.textbbox((0, 0), text, font=font)
    draw.text((cx - (box[0] + box[2]) / 2, cy - (box[1] + box[3]) / 2), text, font=font, fill=fill)


def monogram_for(size):
    """How much of the monogram survives at this size."""
    if size >= 48:
        return "WSE"
    if size >= 24:
        return "W"
    return None


def render(size):
    s = size * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    cx = cy = s / 2

    # Cream border badge, then the navy field inset within it.
    d.polygon(hexagon(cx, cy, s * 0.96, s * 0.99), fill=CREAM)
    d.polygon(hexagon(cx, cy, s * 0.80, s * 0.83), fill=NAVY)

    text = monogram_for(size)

    if text:
        # The navy field narrows towards its points, so the wordmark gets the flat middle band
        # only — wide enough to read, short enough to clear the sloping top and bottom edges.
        # A lone letter can be set larger than three, which would otherwise look lost in the field.
        width_limit = s * (0.62 if len(text) > 1 else 0.50)
        height_limit = s * (0.34 if len(text) > 1 else 0.46)
        font = fit_font(d, text, max_w=width_limit, max_h=height_limit, start_px=int(s * 0.6))
        draw_centred(d, cx, cy, text, font, RED)
    else:
        # A red bar keeps the badge reading as the sign when no glyph would survive.
        bar_w, bar_h = s * 0.44, s * 0.13
        d.rectangle([cx - bar_w / 2, cy - bar_h / 2, cx + bar_w / 2, cy + bar_h / 2], fill=RED)

    return img.resize((size, size), Image.LANCZOS)


def main():
    out = Path(__file__).resolve().parent.parent / "wwwroot" / "favicon.ico"
    out.parent.mkdir(parents=True, exist_ok=True)

    frames = [render(px) for px in SIZES]

    # Pillow writes every requested size from the largest frame, so hand it the biggest and
    # let it carry the rest through append_images.
    frames[-1].save(out, format="ICO", sizes=[(p, p) for p in SIZES], append_images=frames[:-1])

    print(f"Wrote {out} ({out.stat().st_size} bytes, sizes {SIZES})")


if __name__ == "__main__":
    main()
