"""Generate the app's original navy/mint waveform mark at native Windows sizes."""
from pathlib import Path

from PIL import Image, ImageDraw

target = Path(__file__).resolve().parents[1] / "src" / "Assets"
target.mkdir(parents=True, exist_ok=True)
scale = 8
image = Image.new("RGBA", (256 * scale, 256 * scale))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((8 * scale, 8 * scale, 248 * scale, 248 * scale), radius=60 * scale, fill="#192C3B")
for x, start, end in ((74, 104, 152), (110, 74, 178), (146, 104, 152), (182, 86, 166)):
    width = 12 * scale
    draw.rounded_rectangle((x * scale - width // 2, start * scale, x * scale + width // 2, end * scale), radius=width // 2, fill="#C5F1DF")
image = image.resize((256, 256), Image.Resampling.LANCZOS)
image.save(target / "app.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
