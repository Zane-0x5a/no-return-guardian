"""在截图里量文字墨迹偏离中线多少。

用法：python measure-align.py <截图.png> <截图模式的 stderr 日志>
日志里要有 ui-align-probe.js 发回的 command {"name":"probe",...}。每个部件只在自己的横向范围、
所属框的纵向范围内找墨迹行（与该行背景的中位亮度相差明显的像素），报告墨迹中线相对框中线的偏移，单位 CSS px。
"""
import json
import sys

from PIL import Image


def lum(pixel):
    return 0.299 * pixel[0] + 0.587 * pixel[1] + 0.114 * pixel[2]


def ink_rows(image, scale, x0, x1, y0, y1, inset=3, delta=40):
    rows = []
    for y in range(int(y0 * scale) + inset, int(y1 * scale) - inset):
        values = sorted(lum(image.getpixel((x, y))) for x in range(int(x0 * scale), int(x1 * scale)))
        background = values[len(values) // 2]
        if abs(values[0] - background) > delta or abs(values[-1] - background) > delta:
            rows.append(y)
    return rows


def main():
    shot, log = sys.argv[1], sys.argv[2]
    probe = None
    with open(log, encoding='utf-8') as handle:
        for line in handle:
            if line.startswith('command ') and '"probe"' in line:
                probe = json.loads(line[len('command '):])
    if probe is None:
        sys.exit('no probe message in the log')
    image = Image.open(shot).convert('RGB')
    scale = image.size[0] / probe['width']
    print('step', probe['step'], 'scale', round(scale, 3))
    for item in probe['items']:
        x, y, w, h = item['box']
        center = y + h / 2
        result = {}
        for name, (x0, x1) in item['parts'].items():
            rows = ink_rows(image, scale, x0, x1, y, y + h)
            result[name] = round((rows[0] + rows[-1] + 1) / 2 / scale - center, 2) if rows else None
        print(' ', item['name'], result)


if __name__ == '__main__':
    main()
