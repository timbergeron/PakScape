"""Build the document-style file association icon from the PakScape logo."""

from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "windows" / "PakStudio.App" / "Assets"
SOURCE = ASSETS / "PakScape.png"
DESTINATION = ASSETS / "PakScape.File.ico"


def main() -> None:
    scale = 4
    canvas_size = 256
    size = canvas_size * scale
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)

    def box(values):
        return tuple(value * scale for value in values)

    # A soft shadow keeps the paper legible against both light and dark Explorer themes.
    draw.rounded_rectangle(box((22, 18, 214, 242)), radius=18 * scale, fill=(0, 0, 0, 42))

    # Cut the upper-right corner out of a rounded page silhouette.
    page_mask = Image.new("L", (size, size), 0)
    page_mask_draw = ImageDraw.Draw(page_mask)
    page_mask_draw.rounded_rectangle(box((18, 12, 210, 236)), radius=18 * scale, fill=255)
    page_mask_draw.polygon(
        [
            (174 * scale, 12 * scale),
            (210 * scale, 48 * scale),
            (210 * scale, 12 * scale),
        ],
        fill=0,
    )
    page = Image.new("RGBA", (size, size), (247, 247, 247, 255))
    canvas.alpha_composite(Image.composite(page, Image.new("RGBA", (size, size)), page_mask))

    # The folded corner gives Explorer the familiar document shape.
    draw = ImageDraw.Draw(canvas)
    draw.polygon(
        [
            (174 * scale, 12 * scale),
            (174 * scale, 48 * scale),
            (210 * scale, 48 * scale),
        ],
        fill=(214, 214, 214, 255),
    )
    draw.line(
        [(174 * scale, 12 * scale), (174 * scale, 48 * scale), (210 * scale, 48 * scale)],
        fill=(190, 190, 190, 255),
        width=scale,
    )

    # Keep the existing app mark intact, but use it as a file-type badge.
    logo = Image.open(SOURCE).convert("RGBA").resize((112 * scale, 112 * scale), Image.Resampling.LANCZOS)
    canvas.alpha_composite(logo, (58 * scale, 78 * scale))

    final = canvas.resize((canvas_size, canvas_size), Image.Resampling.LANCZOS)
    final.save(
        DESTINATION,
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (96, 96), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
