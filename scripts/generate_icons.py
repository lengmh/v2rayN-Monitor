#!/usr/bin/env python3
"""Regenerate checked-in icon assets from web/public/icon.svg.

Requires Inkscape and Pillow (development only). Normal builds use committed assets.
"""
from pathlib import Path
import subprocess
import tempfile
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
PUBLIC = ROOT / "web/public"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def main():
    with tempfile.TemporaryDirectory() as directory:
        images = []
        for size in SIZES:
            output = Path(directory) / f"{size}.png"
            subprocess.run([
                "inkscape", str(PUBLIC / "icon.svg"), "--export-type=png",
                f"--export-filename={output}", f"--export-width={size}",
                f"--export-height={size}",
            ], check=True, stdout=subprocess.DEVNULL)
            with Image.open(output) as image:
                images.append(image.convert("RGBA"))
        # Explicit renders retain small-size quality instead of downsampling 256 px.
        images[-1].save(PUBLIC / "icon.ico", format="ICO",
                        sizes=[(size, size) for size in SIZES], append_images=images[:-1])
        images[-1].save(PUBLIC / "icon-256.png")


if __name__ == "__main__":
    main()
