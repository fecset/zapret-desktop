"""Rasterize the flat SVG master for Windows. Requires the existing Pillow tool."""
from pathlib import Path
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw

assets = Path(__file__).resolve().parents[1] / "src" / "Zapret.Desktop" / "Assets"
root = ET.parse(assets / "app.svg").getroot()
namespace = {"svg": "http://www.w3.org/2000/svg"}
surface = root.find("svg:rect", namespace)
bolt = root.find("svg:polygon", namespace)
scale = 4
art = Image.new("RGBA", (256 * scale, 256 * scale), (0, 0, 0, 0))
draw = ImageDraw.Draw(art)
draw.rounded_rectangle((0, 0, 256 * scale - 1, 256 * scale - 1),
                       radius=float(surface.attrib["rx"]) * scale, fill=surface.attrib["fill"])
points = [tuple(float(part) * scale for part in point.split(","))
          for point in bolt.attrib["points"].split()]
draw.polygon(points, fill=bolt.attrib["fill"])
art.resize((256, 256), Image.Resampling.LANCZOS).save(assets / "app.png")
art.resize((32, 32), Image.Resampling.LANCZOS).save(assets / "app-32.png")
art.save(assets / "app.ico", format="ICO",
         sizes=[(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
print("Updated app.png, app-32.png and app.ico from app.svg")
