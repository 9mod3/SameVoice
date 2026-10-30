from PIL import Image, ImageDraw

SIZE = 1024
img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Rounded dark tile
margin = 60
radius = 230
d.rounded_rectangle((margin, margin, SIZE - margin, SIZE - margin), radius=radius, fill=(13, 17, 30, 255))

# Gradient orb clipped to tile
orb = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
od = ImageDraw.Draw(orb)
for y in range(SIZE):
    t = y / SIZE
    r = int(92 + (42 - 92) * t)
    g = int(92 + (212 - 92) * t)
    b = int(255 + (190 - 255) * t)
    od.line((0, y, SIZE, y), fill=(r, g, b, 84))
mask = Image.new("L", (SIZE, SIZE), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle((margin, margin, SIZE - margin, SIZE - margin), radius=radius, fill=255)
img.alpha_composite(Image.composite(orb, Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0)), mask))

# Waveform bars
bars = [
    (330, 430, 760), (430, 350, 790), (530, 290, 815), (630, 365, 760), (730, 455, 705)
]
for x, top, bottom in bars:
    d.rounded_rectangle((x, top, x + 58, bottom), radius=29, fill=(240, 247, 255, 245))

# Accent underline
d.rounded_rectangle((330, 705, 610, 747), radius=21, fill=(103, 232, 193, 255))

# Save Windows ico
sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
img.save("src/windows/SameVoice.App/Assets/SameVoice.ico", format="ICO", sizes=sizes)

# Android launcher foreground (transparent background, safe area)
fg = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
fd = ImageDraw.Draw(fg)
for x, top, bottom in [(110, 196, 340), (190, 150, 370), (270, 120, 390), (350, 165, 340), (430, 210, 300)]:
    fd.rounded_rectangle((x, top, x + 34, bottom), radius=17, fill=(240, 247, 255, 255))
fd.rounded_rectangle((110, 410, 278, 436), radius=13, fill=(103, 232, 193, 255))
fg.save("src/android/app/src/main/res/drawable-nodpi/ic_launcher_foreground.png")

large = img.resize((512, 512), Image.LANCZOS)
large.save("src/windows/SameVoice.App/Assets/SameVoice.png")

# Android legacy launcher icons
for density, size in [("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)]:
    icon = img.resize((size, size), Image.LANCZOS)
    icon.save(f"src/android/app/src/main/res/mipmap-{density}/ic_launcher.png")
    icon.save(f"src/android/app/src/main/res/mipmap-{density}/ic_launcher_round.png")

print("icons written")
