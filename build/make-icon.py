"""Regenerates src/Magpienest.App/Assets/Magpienest.ico (placeholder artwork).

Usage: python build/make-icon.py   (needs Pillow)
A calm blue rounded square with a white stylised bird sitting in a nest.
"""
import os
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]
BLUE = (45, 108, 178, 255)
BLUE_DARK = (31, 80, 138, 255)
WHITE = (255, 255, 255, 255)


def render(size):
    s = 1024  # draw large, then downsample for smooth edges
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([40, 40, s - 40, s - 40], radius=210, fill=BLUE)
    # nest: a bowl made of two stacked ellipses
    d.ellipse([200, 600, 824, 860], fill=WHITE)
    d.ellipse([260, 640, 764, 820], fill=BLUE_DARK)
    d.ellipse([230, 700, 794, 880], fill=WHITE)
    # bird body, head, tail, beak, eye
    d.ellipse([330, 330, 700, 700], fill=WHITE)
    d.ellipse([560, 230, 780, 450], fill=WHITE)
    d.polygon([(350, 520), (170, 400), (190, 600)], fill=WHITE)
    d.polygon([(760, 310), (880, 350), (760, 390)], fill=WHITE)
    d.ellipse([650, 290, 690, 330], fill=BLUE_DARK)
    # nest rim over the bird's lower body
    d.ellipse([230, 700, 794, 880], fill=WHITE)
    d.arc([300, 720, 724, 860], 20, 160, fill=BLUE, width=22)
    return img.resize((size, size), Image.LANCZOS)


def main():
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Magpienest.App", "Assets", "Magpienest.ico")
    out = os.path.normpath(out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    render(256).save(out, format="ICO", sizes=[(n, n) for n in SIZES])
    # Pillow downsamples the 256 px image to the other sizes.
    print("wrote", out)


if __name__ == "__main__":
    main()
