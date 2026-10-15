"""Renders the Video Trimmer artwork: app.ico (rounded tile, 16-256 px) and hero.jpg (setup wizard side panel).

Same family as SER Player's art: deep-space navy with a violet glow, a glossy white ring with a blue tail - here the
ring is a rotate arrow around the play triangle, above a filmstrip with the app's yellow trim frame.

    python tools/make_art.py            (writes app.ico and hero.jpg next to the project file)
"""
import math
import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

NAVY_DEEP = (5, 8, 20)
NAVY = (11, 19, 46)
VIOLET = (112, 64, 230)
BLUE = (47, 140, 255)
BLUE_LIGHT = (120, 190, 255)
WHITE = (244, 246, 250)
YELLOW = (255, 199, 44)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def background(n, stars=False, seed=7):
    y, x = np.mgrid[0:n, 0:n].astype(np.float32) / n
    # base: navy top-right to deep navy bottom-left
    t = np.clip((x * 0.6 + (1 - y) * 0.7) / 1.3, 0, 1)[..., None]
    img = np.array(NAVY_DEEP, np.float32) * (1 - t) + np.array(NAVY, np.float32) * t
    # violet glow in the top-right corner
    d = np.sqrt((x - 1.02) ** 2 + (y + 0.02) ** 2)
    glow = np.clip(1 - d / 0.62, 0, 1) ** 2.2
    img = img + (np.array(VIOLET, np.float32) - img) * glow[..., None] * 0.85
    # soft blue haze behind the emblem
    d2 = np.sqrt((x - 0.5) ** 2 + (y - 0.48) ** 2)
    haze = np.clip(1 - d2 / 0.55, 0, 1) ** 2
    img = img + (np.array((30, 60, 140), np.float32) - img) * haze[..., None] * 0.35
    out = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    if stars:
        rng = np.random.default_rng(seed)
        dr = ImageDraw.Draw(out)
        for _ in range(int(n * 0.18)):
            sx, sy = rng.random() * n, rng.random() * n
            r = rng.choice([0.5, 0.7, 0.9, 1.3]) * n / 1024
            a = int(rng.uniform(60, 200))
            c = lerp((150, 170, 230), (255, 255, 255), rng.random())
            dr.ellipse((sx - r, sy - r, sx + r, sy + r), fill=lerp(out.getpixel((int(sx), int(sy))), c, a / 255))
    return out.convert("RGBA")


def arc_points(cx, cy, r, a0, a1, steps=400):
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / steps)),
             cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / steps))) for i in range(steps + 1)]


def rounded_polygon(d, pts, r, fill):
    """Convex polygon (clockwise on screen) whose corners are rounded with radius r, tangent to the original edges."""
    n = len(pts)
    outline = []
    for i in range(n):
        p0, p1, p2 = pts[i - 1], pts[i], pts[(i + 1) % n]
        u = (p0[0] - p1[0], p0[1] - p1[1]); lu = math.hypot(*u); u = (u[0] / lu, u[1] / lu)
        v = (p2[0] - p1[0], p2[1] - p1[1]); lv = math.hypot(*v); v = (v[0] / lv, v[1] / lv)
        theta = math.acos(max(-1.0, min(1.0, u[0] * v[0] + u[1] * v[1])))
        bis = (u[0] + v[0], u[1] + v[1]); lb = math.hypot(*bis); bis = (bis[0] / lb, bis[1] / lb)
        dist = r / math.sin(theta / 2)
        c = (p1[0] + bis[0] * dist, p1[1] + bis[1] * dist)
        # tangent points on the two edges, then the arc between them around c
        t0 = (p1[0] + u[0] * r / math.tan(theta / 2), p1[1] + u[1] * r / math.tan(theta / 2))
        t1 = (p1[0] + v[0] * r / math.tan(theta / 2), p1[1] + v[1] * r / math.tan(theta / 2))
        a0 = math.atan2(t0[1] - c[1], t0[0] - c[0])
        a1 = math.atan2(t1[1] - c[1], t1[0] - c[0])
        da = (a1 - a0 + math.pi) % (2 * math.pi) - math.pi
        for k in range(17):
            a = a0 + da * k / 16
            outline.append((c[0] + r * math.cos(a), c[1] + r * math.sin(a)))
    d.polygon(outline, fill=fill)


def emblem(n, cx, cy, radius, stroke, strip_y, strip_h, strip_x0, strip_x1, small=False):
    """The ring arrow + play triangle + filmstrip, on a transparent layer (supersampled by the caller)."""
    layer = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx, cy, radius, stroke = cx * n, cy * n, radius * n, stroke * n

    # Ring: clockwise from the gap at the top-right round to it, white turning blue towards the arrowhead.
    start, end = -38.0, 258.0  # degrees from 3 o'clock, clockwise (PIL's convention)
    mask = Image.new("L", (n, n), 0)
    md = ImageDraw.Draw(mask)
    half = stroke / 2
    md.arc((cx - radius - half, cy - radius - half, cx + radius + half, cy + radius + half), start, end, fill=255, width=int(stroke))
    sx, sy = cx + radius * math.cos(math.radians(start)), cy + radius * math.sin(math.radians(start))
    md.ellipse((sx - half, sy - half, sx + half, sy + half), fill=255)  # round cap at the start
    yy, xx = np.mgrid[0:n, 0:n].astype(np.float32)
    ang = (np.degrees(np.arctan2(yy - cy, xx - cx)) - start) % 360
    span = end - start
    t = np.where(ang <= span, ang / span, np.where(ang > (span + 360) / 2, 0.0, 1.0))  # the gap: nearest end
    w2b = np.clip((t - 0.58) / 0.32, 0, 1)[..., None]
    b2l = np.clip((t - 0.9) / 0.1, 0, 1)[..., None]
    col = np.array(WHITE, np.float32) * (1 - w2b) + np.array(BLUE, np.float32) * w2b
    col = col * (1 - b2l) + np.array(BLUE_LIGHT, np.float32) * b2l
    ring = Image.fromarray(np.clip(col, 0, 255).astype(np.uint8)).convert("RGBA")
    ring.putalpha(mask)
    del yy, xx, ang, t, w2b, b2l, col
    layer = Image.alpha_composite(layer, ring)
    d = ImageDraw.Draw(layer)
    # arrowhead at the end, centred on the ring, pointing along the clockwise tangent
    ea = math.radians(end)
    ex, ey = cx + radius * math.cos(ea), cy + radius * math.sin(ea)
    tx, ty = -math.sin(ea), math.cos(ea)  # clockwise tangent
    nx, ny = math.cos(ea), math.sin(ea)   # outward normal
    head = stroke * (1.75 if not small else 1.45)
    tip = (ex + tx * head * 1.25, ey + ty * head * 1.25)
    b1 = (ex + nx * head - tx * head * 0.05, ey + ny * head - ty * head * 0.05)
    b2 = (ex - nx * head - tx * head * 0.05, ey - ny * head - ty * head * 0.05)
    rounded_polygon(d, [tip, b1, b2], head * 0.12, BLUE_LIGHT + (255,))

    # Play triangle with rounded corners
    s = radius * 0.86
    px = cx + s * 0.1
    tri = [(px - s * 0.4, cy - s * 0.48), (px + s * 0.48, cy), (px - s * 0.4, cy + s * 0.48)]
    rounded_polygon(d, tri, s * 0.07, WHITE + (255,))

    # Filmstrip with the yellow trim frame and a playhead
    y0, y1 = strip_y * n, (strip_y + strip_h) * n
    x0, x1 = strip_x0 * n, strip_x1 * n
    h = y1 - y0
    rad = h * 0.22
    d.rounded_rectangle((x0, y0, x1, y1), radius=rad, fill=(28, 36, 62, 235))
    if not small:
        tiles = 9
        tw = (x1 - x0) / tiles
        for i in range(tiles):
            tx0 = x0 + i * tw
            c = lerp((52, 78, 140), (96, 70, 170), i / (tiles - 1))
            d.rounded_rectangle((tx0 + tw * 0.08, y0 + h * 0.16, tx0 + tw * 0.92, y1 - h * 0.16), radius=h * 0.08, fill=c + (255,))
            # a little horizon in each frame
            d.rectangle((tx0 + tw * 0.08, y0 + h * 0.56, tx0 + tw * 0.92, y1 - h * 0.16), fill=lerp(c, (20, 26, 50), 0.45) + (255,))
    sel0, sel1 = x0 + (x1 - x0) * 0.27, x0 + (x1 - x0) * 0.73
    hw = h * (0.30 if not small else 0.36)
    bar = h * (0.12 if not small else 0.18)
    frame = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    fd = ImageDraw.Draw(frame)
    fd.rounded_rectangle((sel0 - hw, y0 - bar * 0.2, sel1 + hw, y1 + bar * 0.2), radius=h * 0.2, fill=YELLOW + (255,))
    fd.rectangle((sel0, y0 + bar, sel1, y1 - bar), fill=(0, 0, 0, 0))
    # dim film outside the selection
    shade = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shade)
    sd.rectangle((x0, y0, sel0 - hw, y1), fill=(4, 6, 14, 150))
    sd.rectangle((sel1 + hw, y0, x1, y1), fill=(4, 6, 14, 150))
    layer = Image.alpha_composite(layer, shade)
    # punch the frame's hole (keep the film visible inside the selection)
    hole = Image.new("L", (n, n), 0)
    ImageDraw.Draw(hole).rectangle((sel0, y0 + bar, sel1, y1 - bar), fill=255)
    frame_alpha = np.array(frame.split()[3], np.float32) * (1 - np.array(hole, np.float32) / 255)
    frame.putalpha(Image.fromarray(frame_alpha.astype(np.uint8)))
    layer = Image.alpha_composite(layer, frame)
    d = ImageDraw.Draw(layer)
    # chevrons on the grips
    for gx, sign in ((sel0 - hw / 2, 1), (sel1 + hw / 2, -1)):
        cs = h * 0.16
        d.line([(gx + sign * cs * 0.5, (y0 + y1) / 2 - cs), (gx - sign * cs * 0.5, (y0 + y1) / 2), (gx + sign * cs * 0.5, (y0 + y1) / 2 + cs)],
               fill=(70, 50, 0, 255), width=max(1, int(h * 0.07)), joint="curve")
    # playhead
    phx = sel0 + (sel1 - sel0) * 0.42
    pw = h * (0.08 if not small else 0.12)
    d.rounded_rectangle((phx - pw / 2, y0 - h * 0.22, phx + pw / 2, y1 + h * 0.22), radius=pw / 2, fill=WHITE + (255,))
    return layer


def shadow(layer, n, blur, alpha=0.55, offset=(0, 0.012)):
    a = np.array(layer.split()[3], np.float32) * alpha
    sh = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    sh.putalpha(Image.fromarray(a.astype(np.uint8)))
    sh = sh.filter(ImageFilter.GaussianBlur(blur * n))
    out = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    out.paste(sh, (int(offset[0] * n), int(offset[1] * n)))
    return out


def render(size, hero=False, small=False):
    ss = 4 if size <= 256 else 2
    n = size * ss
    bg = background(n, stars=hero)
    if hero:
        layer = emblem(n, 0.5, 0.40, 0.235, 0.032, 0.70, 0.085, 0.24, 0.76)
    elif small:
        layer = emblem(n, 0.5, 0.40, 0.27, 0.075, 0.76, 0.15, 0.12, 0.88, small=True)
    else:
        layer = emblem(n, 0.5, 0.41, 0.265, 0.045, 0.765, 0.13, 0.13, 0.87)
    glow = layer.filter(ImageFilter.GaussianBlur(0.03 * n))
    ga = np.array(glow.split()[3], np.float32) * 0.35
    glow.putalpha(Image.fromarray(ga.astype(np.uint8)))
    img = Image.alpha_composite(bg, shadow(layer, n, 0.012))
    img = Image.alpha_composite(img, glow)
    img = Image.alpha_composite(img, layer)
    if not hero:
        mask = Image.new("L", (n, n), 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, n - 1, n - 1), radius=n * 0.21, fill=255)
        img.putalpha(mask)
    return img.resize((size, size), Image.LANCZOS)


def main():
    sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
    frames = [render(s, small=s <= 32) for s in sizes]
    frames[0].save(os.path.join(ROOT, "app.ico"), format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[1:])
    frames[0].save(os.path.join(HERE, "icon-256.png"))
    render(1024).save(os.path.join(HERE, "icon-1024.png"))
    render(1024, hero=True).convert("RGB").save(os.path.join(ROOT, "hero.jpg"), quality=92)
    print("wrote app.ico, hero.jpg")


if __name__ == "__main__":
    main()
