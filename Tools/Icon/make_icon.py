#!/usr/bin/env python3
"""Eigenes App-Icon (keine Spielgrafik): Serpentine auf dunklem Quadrat.
Schreibt icon.png (1024), icon.ico (Windows) und, auf macOS, icon.icns. Braucht Pillow."""
import math, os, shutil, subprocess, sys, tempfile
from PIL import Image, ImageDraw, ImageFont

S = 1024
here = os.path.dirname(os.path.abspath(__file__))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle((40, 40, S - 40, S - 40), 200, fill=255)
img = Image.new("RGBA", (S, S), (18, 20, 32, 255))
d = ImageDraw.Draw(img)
# hairpin road: sine snake from bottom to top, asphalt with white edges and a yellow centre line
pts = [(S / 2 + 260 * math.sin(t / 40 * 2.6 * math.pi), 1060 - t / 40 * 1100) for t in range(41)]
d.line(pts, fill=(235, 235, 240), width=150, joint="curve")
d.line(pts, fill=(52, 54, 66), width=120, joint="curve")
d.line(pts, fill=(255, 196, 0), width=10, joint="curve")
img.putalpha(mask)
d = ImageDraw.Draw(img)
try:
    font = ImageFont.truetype(os.path.join(here, "../../Touge/Assets/Fonts/Rajdhani-Bold.ttf"), 210)
    d.text((S / 2, 880), "TOUGE", font=font, anchor="ms", fill=(255, 255, 255), stroke_width=12, stroke_fill=(18, 20, 32))
except OSError:
    pass
img.save(os.path.join(here, "icon.png"))
img.save(os.path.join(here, "icon.ico"), sizes=[(256, 256), (64, 64), (48, 48), (32, 32), (16, 16)])
if sys.platform == "darwin":
    tmp = tempfile.mkdtemp(suffix=".iconset")
    for n in (16, 32, 128, 256, 512):
        img.resize((n, n), Image.LANCZOS).save(f"{tmp}/icon_{n}x{n}.png")
        img.resize((n * 2, n * 2), Image.LANCZOS).save(f"{tmp}/icon_{n}x{n}@2x.png")
    subprocess.run(["iconutil", "-c", "icns", tmp, "-o", os.path.join(here, "icon.icns")], check=True)
    shutil.rmtree(tmp)
