"""生成 MonitorAlign 应用图标（圆角矩形 / 简约风格）。

构图（全部由圆角矩形构成，无渐变噪声、无细碎装饰）：
    ① 外层圆角方形徽标（深蓝）——保证 16px 时也有可辨识的块面；
    ② 左侧横向圆角矩形 = 主屏（实心白，固定基准）；
    ③ 右侧纵向圆角矩形 = 副屏（半透明白，待调整）；
    ④ 一条琥珀色横线贯穿两者 = 对齐基准线。

小尺寸（<32px）自动简化：去掉描边层次，保证不糊成一团。
"""
from PIL import Image, ImageDraw
import io
import os
import struct

# ---------- Fluent 配色 ----------
BADGE = (14, 95, 168, 255)          # 深蓝徽标
MON_PRIMARY = (255, 255, 255, 255)  # 主屏：实心白
MON_SECONDARY = (255, 255, 255, 140)  # 副屏：半透明白（叠在深蓝上呈浅蓝）
ACCENT = (255, 185, 0, 255)         # 琥珀：对齐基准线


def draw(size: int) -> Image.Image:
    s = size
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # 半透明白要合成在徽标上，先用不透明画布画徽标，再叠加
    base = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    bd = ImageDraw.Draw(base)
    bd.rounded_rectangle([0, 0, s - 1, s - 1], radius=round(s * 0.24), fill=BADGE)
    img = Image.alpha_composite(img, base)

    d = ImageDraw.Draw(img)

    small = s < 32                       # 16/20/24px：细节会被像素栅格吃掉，需要另做简化
    gap = 1 if small else max(1, round(s * 0.045))       # 两屏之间的缝
    rad = 1 if small else max(1, round(s * 0.042))       # 屏幕圆角——太大就会退化成"胶囊"

    # 左：主屏（横屏）　右：副屏（竖屏）。小尺寸下把图形放大、缝隙压到 1px。
    if small:
        lx0, lx1 = round(s * 0.14), round(s * 0.52)
        rx0, rx1 = lx1 + gap, round(s * 0.87)
        ly0, ly1 = round(s * 0.31), round(s * 0.69)
        ry0, ry1 = round(s * 0.19), round(s * 0.81)
    else:
        lx0, lx1 = round(s * 0.165), round(s * 0.525)
        rx0, rx1 = lx1 + gap, round(s * 0.845)
        ly0, ly1 = round(s * 0.355), round(s * 0.645)
        ry0, ry1 = round(s * 0.255), round(s * 0.745)

    d.rounded_rectangle([lx0, ly0, lx1, ly1], radius=rad, fill=MON_PRIMARY)
    d.rounded_rectangle([rx0, ry0, rx1, ry1], radius=rad, fill=MON_SECONDARY)

    # 对齐基准线：贯穿两块屏，两端伸出一点，强调"同一条线"
    lw = max(2, round(s * (0.10 if small else 0.062)))
    y = s // 2
    d.rounded_rectangle(
        [round(s * (0.09 if small else 0.115)), y - lw // 2,
         round(s * (0.91 if small else 0.885)), y - lw // 2 + lw - 1],
        radius=lw // 2, fill=ACCENT)

    return img


def write_ico(path: str, images):
    """自己写 ICO 容器（PNG 载荷）。

    不用 Pillow 的 ICO 保存：它要么从最大尺寸缩放（几何边缘变糊），
    要么在 append_images 模式下会莫名其妙丢掉 20/40/96 这几帧。
    """
    n = len(images)
    blobs = []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        blobs.append(buf.getvalue())

    offset = 6 + 16 * n
    header = struct.pack("<HHH", 0, 1, n)          # reserved / type=icon / count
    entries = b""
    for im, blob in zip(images, blobs):
        w, h = im.size
        # 尺寸 >=256 时目录项里写 0（ICO 规范）
        entries += struct.pack("<BBBBHHII",
                               w % 256, h % 256, 0, 0,          # 宽、高、调色板数、保留
                               1, 32,                            # 平面数、位深
                               len(blob), offset)
        offset += len(blob)

    with open(path, "wb") as f:
        f.write(header)
        f.write(entries)
        for blob in blobs:
            f.write(blob)


def main():
    out_dir = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Resources")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, "app.ico")

    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    write_ico(out_path, [draw(n) for n in sizes])   # 每个尺寸独立绘制，边缘最锐利

    preview = os.path.join(out_dir, "app_256.png")
    draw(256).save(preview, format="PNG")
    # 小尺寸拼一排，便于人工检查在小图标下的可读性
    strip = Image.new("RGBA", (16 + 20 + 24 + 32 + 40 + 48 + 5 * 6, 48), (0, 0, 0, 0))
    x = 0
    for n in (16, 20, 24, 32, 40, 48):
        im = draw(n)
        strip.paste(im, (x, 48 - n))
        x += n + 6
    strip.save(os.path.join(out_dir, "app_small.png"), format="PNG")

    print("icon ->", out_path)
    print("sizes:", ", ".join(str(n) for n in sizes))
    print("bytes:", os.path.getsize(out_path))


if __name__ == "__main__":
    main()
