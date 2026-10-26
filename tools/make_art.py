"""Makes Splicia's icon and setup artwork from the supplied artwork (art/splicia-source.png, 1254 px square).

    app.ico   rounded tile, 16-256 px: the whole emblem (filmstrip S + trim bar) from 48 px up; just the S below that,
              where the bar would be a smudge
    hero.jpg  portrait crop for the setup wizard's side panel (drawn at the panel's width, top-aligned)

    python tools/make_art.py
"""
import os

from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, "art", "splicia-source.png")

# Measured on the source (luminance > 70 after a 5 px blur, which drops the stars):
#   S        x 355..884, y 154..878
#   trim bar x 208..1045, y 887..1094 (playhead included)
EMBLEM = (0, 0, 1254, 1254)      # the whole artwork: the emblem is centred and fills ~75% of it
S_ONLY = (170, 66, 1070, 966)    # square around the S, which fills ~80% of it
HERO = (150, 0, 1104, 1254)      # portrait 954 x 1254: the whole emblem with a little sky either side


def tile(src, box, size, radius=0.22, sharpen=0.0):
    """Crop, scale with supersampling, round the corners."""
    ss = 4
    big = src.crop(box).resize((size * ss, size * ss), Image.LANCZOS)
    mask = Image.new("L", big.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, big.width - 1, big.height - 1), radius=int(big.width * radius), fill=255)
    big.putalpha(mask)
    im = big.resize((size, size), Image.LANCZOS)
    if sharpen:
        rgb = im.convert("RGB").filter(ImageFilter.UnsharpMask(radius=0.8, percent=int(sharpen * 100), threshold=0))
        rgb.putalpha(im.getchannel("A"))
        im = rgb
    return im


def main():
    src = Image.open(SRC).convert("RGB")
    sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
    frames = [tile(src, EMBLEM if s >= 48 else S_ONLY, s, sharpen=0 if s >= 64 else 0.6 if s >= 32 else 0.9) for s in sizes]
    frames[0].save(os.path.join(ROOT, "app.ico"), format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[1:])
    tile(src, EMBLEM, 1024).save(os.path.join(HERE, "icon-1024.png"))
    hero = src.crop(HERO)
    hero.resize((600, round(600 * hero.height / hero.width)), Image.LANCZOS).save(os.path.join(ROOT, "hero.jpg"), quality=92)
    print("wrote app.ico, hero.jpg, tools/icon-1024.png")


if __name__ == "__main__":
    main()
