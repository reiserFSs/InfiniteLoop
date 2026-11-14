#!/usr/bin/env python3
"""Renders AscNet.icns (macOS) and ascnet.ico + ascnet-256.png (Windows / Slint window icon) from ascnet.svg and ascnet-small.svg.

Renderer: headless Brave/Chrome (`--screenshot` with a transparent background), because rsvg-convert is not installed here and
qlmanage paints an opaque white background. Set BROWSER to override. Each SVG is rendered once at 1024 px, then downscaled with
Pillow (LANCZOS). Sizes below 64 px use ascnet-small.svg (no slash). Needs Pillow and macOS iconutil (icns only).
The Windows variant is the same art with the tile edge to edge (full square, small corner radius) and the glyph scaled to fill it.
"""
import io, os, pathlib, re, struct, subprocess, tempfile
from PIL import Image

here = pathlib.Path(__file__).resolve().parent
browser = os.environ.get("BROWSER", "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser")
SMALL_BELOW = 64
TILE_SCALE = 1024 / 824


def windows_svg(svg):
    # Drop the inset macOS tile, draw a full-bleed one, and scale the glyph about the centre by the same factor.
    body = re.sub(r"\s*<rect[^>]*/>|\s*<!--.*?-->", "", re.search(r"<svg[^>]*>(.*)</svg>", svg, re.S).group(1))
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">'
            '<rect width="1024" height="1024" rx="96" fill="#1a1d22"/>'
            f'<g transform="translate(512 512) scale({TILE_SCALE:.4f}) translate(-512 -512)">{body}</g></svg>')


def render(svg, tmp, name):
    (tmp / f"{name}.svg").write_text(svg)
    (tmp / f"{name}.html").write_text(
        f'<html><body style="margin:0;background:transparent"><img src="{name}.svg" width=1024 height=1024 style="display:block"></body></html>')
    out = tmp / f"{name}.png"
    subprocess.run([browser, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--default-background-color=00000000",
                    "--window-size=1024,1024", f"--screenshot={out}", (tmp / f"{name}.html").as_uri()],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    img = Image.open(out).convert("RGBA")
    assert img.size == (1024, 1024), name
    return img


def write_ico(path, frames):
    # PNG-compressed entries; Pillow's ICO writer only emits sizes of a single source image.
    blobs = []
    for img in frames:
        buf = io.BytesIO()
        img.save(buf, "PNG")
        blobs.append(buf.getvalue())
    head = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = b""
    for img, blob in zip(frames, blobs):
        w = img.width % 256  # 256 is stored as 0
        entries += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    path.write_bytes(head + entries + b"".join(blobs))


def sized(masters, px):
    return masters[px < SMALL_BELOW].resize((px, px), Image.LANCZOS)


with tempfile.TemporaryDirectory() as t:
    tmp = pathlib.Path(t)
    full, small = (here / "ascnet.svg").read_text(), (here / "ascnet-small.svg").read_text()
    # masters[True] is used for sizes below SMALL_BELOW
    mac = {True: render(small, tmp, "mac-small"), False: render(full, tmp, "mac-full")}
    win = {True: render(windows_svg(small), tmp, "win-small"), False: render(windows_svg(full), tmp, "win-full")}

    iconset = tmp / "AscNet.iconset"
    iconset.mkdir()
    for base in (16, 32, 128, 256, 512):
        sized(mac, base).save(iconset / f"icon_{base}x{base}.png")
        sized(mac, base * 2).save(iconset / f"icon_{base}x{base}@2x.png")
    subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(here.parent / "macos/AscNet.icns")], check=True)

    write_ico(here / "ascnet.ico", [sized(win, px) for px in (16, 24, 32, 48, 64, 128, 256)])
    sized(win, 256).save(here / "ascnet-256.png")
