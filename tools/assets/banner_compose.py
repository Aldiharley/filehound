"""Composes the GitHub banners from the Higgsfield renders: crop, cream normalisation, text overlay.

Spec: docs/superpowers/specs/2026-10-09-github-banner.md
Usage: python tools/assets/banner_compose.py <repo root>
Inputs:  banner-social.png (16:9 render), banner-wide.png (21:9 render), icon-paw-cut.png, each taken from assets-src/
         (local generation output, git-ignored) or else from docs/banner/src/ (the committed copies)
Outputs: docs/banner/social-preview.png (1280x640), docs/banner/readme-banner.png (1600x500)
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageStat

ROOT = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
OUT = ROOT / "docs" / "banner"


def src(name: str) -> Path:
    """A render from the local generation folder, else the copy committed next to the banners."""
    for folder in (ROOT / "assets-src", OUT / "src"):
        if (folder / name).exists():
            return folder / name
    raise FileNotFoundError(name)
FONTS = Path("C:/Windows/Fonts")

CANVAS = (0xFB, 0xF4, 0xEA)
SURFACE = (0xFF, 0xFB, 0xF5)
INK = (0x3F, 0x3A, 0x36)
INK_SOFT = (0x7A, 0x71, 0x6A)
PEACH_DEEP = (0xEE, 0x8F, 0x6E)
SHADOW = (0x8C, 0x6E, 0x55)

BOLD = FONTS / "segoeuib.ttf"
SEMIBOLD = FONTS / "seguisb.ttf"

WORDMARK = "FileHound"
TAGLINE = ("Find any file on any drive as you type.", "Bring deleted ones back.")
CHIPS = ("Open source", "Windows 11", ".NET 10")


def font(path: Path, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(path), size)


def crop_band(im: Image.Image, width: int, height: int) -> Image.Image:
    """Centre crop to the target aspect ratio, then scale to the target size."""
    target = width / height
    w, h = im.size
    if w / h > target:
        nw = round(h * target)
        box = ((w - nw) // 2, 0, (w - nw) // 2 + nw, h)
    else:
        nh = round(w / target)
        box = (0, (h - nh) // 2, w, (h - nh) // 2 + nh)
    return im.crop(box).resize((width, height), Image.LANCZOS)


def match_cream(im: Image.Image, sample_right: int) -> Image.Image:
    """Shifts the render so its flat background is exactly Canvas cream (sampled from the empty left part)."""
    im = im.convert("RGB")
    median = ImageStat.Stat(im.crop((0, 0, sample_right, im.height))).median
    delta = [CANVAS[i] - median[i] for i in range(3)]
    if not any(abs(d) > 1 for d in delta):
        return im
    lut = []
    for c in range(3):
        lut += [max(0, min(255, v + delta[c])) for v in range(256)]
    return im.point(lut)


def fit_illustration(im: Image.Image, scale: float, feather: int = 40) -> Image.Image:
    """Shrinks the whole scene toward the right edge (vertically centred) and fills the rest with cream.

    The render's background is flat, so only the top and bottom edges can show a seam (the peach glow reaches
    them); those are feathered into the cream.
    """
    if scale >= 1:
        return im
    w, h = im.size
    sw, sh = round(w * scale), round(h * scale)
    small = im.resize((sw, sh), Image.LANCZOS)
    mask = Image.new("L", (sw, sh), 255)
    d = ImageDraw.Draw(mask)
    for i in range(feather):
        a = round(255 * i / feather)
        d.line((0, i, sw, i), fill=a)
        d.line((0, sh - 1 - i, sw, sh - 1 - i), fill=a)
    out = Image.new("RGB", (w, h), CANVAS)
    out.paste(small, (w - sw, (h - sh) // 2), mask)
    return out


def flatten_text_zone(im: Image.Image, zone_right: int, ramp: int = 60) -> Image.Image:
    """Pure cream under the text: solid up to zone_right - ramp, then a linear blend into the render."""
    mask = Image.new("L", im.size, 0)
    d = ImageDraw.Draw(mask)
    d.rectangle((0, 0, zone_right - ramp, im.height), fill=255)
    for x in range(ramp):
        d.line((zone_right - ramp + x, 0, zone_right - ramp + x, im.height), fill=255 - round(255 * x / ramp))
    flat = Image.new("RGB", im.size, CANVAS)
    return Image.composite(flat, im, mask)


def draw_tracked(draw: ImageDraw.ImageDraw, xy, text: str, fnt, fill, tracking: float) -> float:
    """Draws text with a letter-spacing adjustment; returns the end x."""
    x, y = xy
    for ch in text:
        draw.text((x, y), ch, font=fnt, fill=fill)
        x += fnt.getlength(ch) + tracking
    return x - tracking


def warm_shadow(size, radius: int, blur: int, offset: int, opacity: float) -> Image.Image:
    """The clay drop shadow: warm brown, blurred, straight down."""
    pad = blur * 2
    w, h = size
    layer = Image.new("RGBA", (w + pad * 2, h + pad * 2), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.rounded_rectangle((pad, pad + offset, pad + w, pad + h + offset), radius=radius, fill=SHADOW + (round(255 * opacity),))
    return layer.filter(ImageFilter.GaussianBlur(blur / 2))


def overlay(im: Image.Image, s: dict) -> Image.Image:
    """The text layer: wordmark with peach underline, two tagline lines, three clay chips."""
    im = im.convert("RGBA")
    draw = ImageDraw.Draw(im)
    wm_font = font(BOLD, s["wm_size"])
    x0, y0 = s["wm_xy"]
    end_x = draw_tracked(draw, (x0, y0), WORDMARK, wm_font, INK, -2)
    ascent, _ = wm_font.getmetrics()
    baseline = y0 + ascent
    draw.rounded_rectangle((x0 + 4, baseline + 14, x0 + 4 + 120, baseline + 14 + 6), radius=3, fill=PEACH_DEEP)

    tag_font = font(SEMIBOLD, s["tag_size"])
    for line, y in zip(TAGLINE, s["tag_ys"]):
        draw.text((x0, y), line, font=tag_font, fill=INK)

    chip_font = font(SEMIBOLD, s["chip_size"])
    paw = Image.open(src("icon-paw-cut.png")).convert("RGBA")
    h = s["chip_h"]
    pad = s["chip_pad"]
    x = x0
    y = s["chip_y"]
    for i, label in enumerate(CHIPS):
        icon_w = h - 12 if i == 0 else 0
        tw = chip_font.getlength(label)
        w = round(pad * 2 + tw + (icon_w + 6 if icon_w else 0))
        sh = warm_shadow((w, h), h // 2, 18, 4, 0.14)
        im.alpha_composite(sh, (x - 36, y - 36))
        draw = ImageDraw.Draw(im)
        draw.rounded_rectangle((x, y, x + w, y + h), radius=h // 2, fill=SURFACE)
        tx = x + pad
        if icon_w:
            ic = paw.resize((icon_w, icon_w), Image.LANCZOS)
            im.alpha_composite(ic, (tx, y + 6))
            tx += icon_w + 6
        asc, desc = chip_font.getmetrics()
        draw.text((tx, y + (h - (asc + desc)) // 2), label, font=chip_font, fill=INK_SOFT)
        x += w + s["chip_gap"]
    return im


def round_card(im: Image.Image, radius: int) -> Image.Image:
    """Transparent rounded corners plus a 1 px white rim at 60% along the top edge (the clay card look)."""
    im = im.convert("RGBA")
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, im.width - 1, im.height - 1), radius=radius, fill=255)
    im.putalpha(mask)
    rim = Image.new("RGBA", im.size, (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle((0, 0, im.width - 1, im.height - 1), radius=radius, outline=(255, 255, 255, 153), width=1)
    rim_mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(rim_mask).rectangle((0, 0, im.width, radius), fill=255)
    rim.putalpha(Image.composite(rim.getchannel("A"), rim_mask, rim_mask))
    im.alpha_composite(rim)
    return im


# zone: right edge of the cream ramp; nothing of the scene may cross it. scale: shrink toward the right edge.
SOCIAL = dict(size=(1280, 640), zone=690, scale=0.86, wm_size=96, wm_xy=(96, 184), tag_size=34, tag_ys=(310, 354),
              chip_y=426, chip_h=36, chip_pad=16, chip_gap=10, chip_size=18)
WIDE = dict(size=(1600, 500), zone=800, scale=1.0, wm_size=88, wm_xy=(88, 128), tag_size=30, tag_ys=(236, 274),
            chip_y=336, chip_h=34, chip_pad=14, chip_gap=10, chip_size=17)


def build(render: str, s: dict, rounded: bool) -> Image.Image:
    im = Image.open(src(render))
    im = crop_band(im, *s["size"])
    im = match_cream(im, s["zone"] // 2)
    im = fit_illustration(im, s["scale"])
    im = flatten_text_zone(im, s["zone"])
    im = overlay(im, s)
    return round_card(im, 40) if rounded else im.convert("RGB")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    build("banner-social.png", SOCIAL, rounded=False).save(OUT / "social-preview.png", optimize=True)
    build("banner-wide.png", WIDE, rounded=True).save(OUT / "readme-banner.png", optimize=True)
    for p in ("social-preview.png", "readme-banner.png"):
        print(f"  {OUT / p}  {(OUT / p).stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
