"""生成守护器的图标：应用图标（多尺寸）和三种托盘状态图标。

标志是一道门洞：外面一圈细线拱门，脚下一扇实心小门——“回到出发前的那道门”。
- 应用图标：大理石色场方片（取自界面同一个 Paper Shaders 色场的截图），象牙白拱门、香槟色小门。
- 托盘图标：没有方片，只有拱门剪影；深色门洞在浅色、深色任务栏上都看得见，小门的颜色就是状态。

用法：python scripts/make-icons.py
输入：src/NoReturnGuardian.App/assets/icon-marble.png
输出：src/NoReturnGuardian.App/assets/{guardian,tray-guarding,tray-alert,tray-idle}.ico
      artifacts/ui-redesign-20261001/icon-preview.png（各尺寸在深浅背景上的样子）
"""

import struct
from io import BytesIO
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'src' / 'NoReturnGuardian.App' / 'assets'
PREVIEW = ROOT / 'artifacts' / 'ui-redesign-20261001' / 'icon-preview.png'

IVORY = (237, 231, 220)
CHAMPAGNE = (236, 200, 146)
GARNET = (212, 112, 95)
DIM = (128, 120, 112)
HOLE = (18, 13, 15)

APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
TRAY_SIZES = [16, 20, 24, 28, 32, 40, 48]
SUPER = 8


def arch_mask(size, cx, top, base, width):
    """拱门剪影（矩形 + 顶部半圆）的 L 模式遮罩，坐标都是像素。"""
    mask = Image.new('L', (size, size), 0)
    draw = ImageDraw.Draw(mask)
    radius = width / 2
    draw.rectangle([cx - radius, top + radius, cx + radius, base], fill=255)
    draw.ellipse([cx - radius, top, cx + radius, top + width], fill=255)
    return mask


def outline_mask(size, cx, top, base, width, stroke):
    """开口朝下的拱门细线：外剪影减内剪影，腿落在门槛上。"""
    outer = np.asarray(arch_mask(size, cx, top - stroke / 2, base, width + stroke), dtype=np.int16)
    inner = np.asarray(arch_mask(size, cx, top + stroke / 2, base + stroke, width - stroke), dtype=np.int16)
    return Image.fromarray(np.clip(outer - inner, 0, 255).astype(np.uint8))


def downsample(image, size):
    return image.resize((size, size), Image.LANCZOS)


def marble_tile(size):
    """大理石方片：圆角、轻压暗、内沿一圈极淡的象牙细线。"""
    big = size * SUPER
    source = Image.open(ASSETS / 'icon-marble.png').convert('RGB')
    # 取色场左侧一道灰玫瑰纹斜着掠过暗紫与酒红的那一块。
    w, h = source.size
    side = int(h * 0.62)
    left, top = int(w * 0.02), int(h * 0.30)
    crop = source.crop((left, top, left + side, top + side)).resize((big, big), Image.LANCZOS)
    pixels = np.asarray(crop, dtype=np.float32)
    # 从上到下轻压一层暗，下半部分更沉，标志压得住。
    ramp = np.linspace(0.92, 0.72, big, dtype=np.float32)[:, None, None]
    pixels = pixels * ramp
    tile = Image.fromarray(np.clip(pixels, 0, 255).astype(np.uint8)).convert('RGBA')

    margin = big * 0.055
    radius = big * 0.2
    shape = Image.new('L', (big, big), 0)
    ImageDraw.Draw(shape).rounded_rectangle([margin, margin, big - margin, big - margin], radius=radius, fill=255)
    rim = Image.new('L', (big, big), 0)
    edge = max(SUPER, big * 0.006)
    ImageDraw.Draw(rim).rounded_rectangle(
        [margin + edge / 2, margin + edge / 2, big - margin - edge / 2, big - margin - edge / 2],
        radius=radius - edge / 2, outline=255, width=int(edge))
    tile.putalpha(shape)
    rim_layer = Image.new('RGBA', (big, big), IVORY + (0,))
    rim_layer.putalpha(rim.point(lambda v: v * 0.16))
    tile = Image.alpha_composite(tile, rim_layer)
    return tile, big


def stroke_for(size, ratio, minimum):
    return max(ratio * size, minimum) * SUPER


def app_frame(size):
    tile, big = marble_tile(size)
    cx = big / 2
    small = size <= 32
    width = big * (0.32 if small else 0.30)
    top = big * (0.21 if small else 0.20)
    base = big * (0.79 if small else 0.78)
    stroke = stroke_for(size, 0.030, 1.35 if size <= 24 else 1.2)
    outline = outline_mask(big, cx, top, base, width, stroke)
    door_w = big * (0.15 if small else 0.11)
    door_h = big * (0.23 if small else 0.17)
    door = arch_mask(big, cx, base - door_h, base, door_w)

    # 标志下面一层很淡的暗影，让细线在亮纹上也读得清。
    shade = Image.new('RGBA', (big, big), (8, 4, 6, 0))
    shade.putalpha(outline.filter(ImageFilter.GaussianBlur(big * 0.02)).point(lambda v: v * 0.55))
    tile = Image.alpha_composite(tile, shade)
    ink = Image.new('RGBA', (big, big), IVORY + (0,))
    ink.putalpha(outline)
    tile = Image.alpha_composite(tile, ink)
    gold = Image.new('RGBA', (big, big), CHAMPAGNE + (0,))
    gold.putalpha(door)
    tile = Image.alpha_composite(tile, gold)
    return downsample(tile, size)


def tray_frame(size, door_color):
    big = size * SUPER
    cx = big / 2
    width = big * 0.6
    stroke = stroke_for(size, 0.085, 1.35)
    top = big * 0.05 + stroke / 2
    base = big * 0.97
    frame = Image.new('RGBA', (big, big), (0, 0, 0, 0))
    hole = Image.new('RGBA', (big, big), HOLE + (0,))
    hole.putalpha(arch_mask(big, cx, top, base, width).point(lambda v: v * 0.94))
    frame = Image.alpha_composite(frame, hole)
    ink = Image.new('RGBA', (big, big), IVORY + (0,))
    ink.putalpha(outline_mask(big, cx, top, base, width, stroke))
    frame = Image.alpha_composite(frame, ink)
    door_w = big * 0.26
    door_h = big * 0.44
    door = Image.new('RGBA', (big, big), door_color + (0,))
    door.putalpha(arch_mask(big, cx, base - door_h, base, door_w))
    frame = Image.alpha_composite(frame, door)
    return downsample(frame, size)


def dib_entry(image):
    """32 位 BGRA 位图帧（自下而上）加 1 位 AND 遮罩，老接口也能读。"""
    size = image.size[0]
    rgba = np.asarray(image.convert('RGBA'), dtype=np.uint8)
    bgra = rgba[:, :, [2, 1, 0, 3]][::-1].tobytes()
    row = ((size + 31) // 32) * 4
    mask = bytearray()
    for y in range(size - 1, -1, -1):
        line = bytearray(row)
        for x in range(size):
            if rgba[y, x, 3] == 0:
                line[x // 8] |= 0x80 >> (x % 8)
        mask += line
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, len(bgra) + len(mask), 0, 0, 0, 0)
    return header + bgra + bytes(mask)


def png_entry(image):
    buffer = BytesIO()
    image.save(buffer, format='PNG')
    return buffer.getvalue()


def write_ico(path, frames):
    entries = [(image.size[0], png_entry(image) if image.size[0] >= 256 else dib_entry(image)) for image in frames]
    offset = 6 + 16 * len(entries)
    out = bytearray(struct.pack('<HHH', 0, 1, len(entries)))
    for size, data in entries:
        out += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    path.write_bytes(bytes(out))


def preview(app, trays):
    """把各尺寸放在深色、浅色两种任务栏底色上并排，放大两倍便于检查。"""
    columns = APP_SIZES
    cell = 280
    sheet = Image.new('RGBA', (cell * len(columns) // 2 + 40, 900), (0, 0, 0, 255))
    draw = ImageDraw.Draw(sheet)
    for band, color in enumerate([(32, 32, 32), (243, 243, 243)]):
        draw.rectangle([0, band * 450, sheet.size[0], band * 450 + 450], fill=color + (255,))
        x = 20
        for image in app:
            scaled = image.resize((image.size[0] * 2, image.size[1] * 2), Image.NEAREST) if image.size[0] <= 64 else image
            sheet.alpha_composite(scaled, (x, band * 450 + 30))
            x += scaled.size[0] + 18
        x = 20
        for frames in trays:
            for image in frames:
                scaled = image.resize((image.size[0] * 3, image.size[1] * 3), Image.NEAREST)
                sheet.alpha_composite(scaled, (x, band * 450 + 300))
                x += scaled.size[0] + 10
            x += 30
    PREVIEW.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(PREVIEW)


def main():
    app = [app_frame(size) for size in APP_SIZES]
    write_ico(ASSETS / 'guardian.ico', app)
    states = {'tray-guarding': CHAMPAGNE, 'tray-alert': GARNET, 'tray-idle': DIM}
    trays = []
    for name, color in states.items():
        frames = [tray_frame(size, color) for size in TRAY_SIZES]
        write_ico(ASSETS / (name + '.ico'), frames)
        trays.append([frames[0], frames[4]])
    preview(app, trays)
    print('icons written to', ASSETS)


if __name__ == '__main__':
    main()
