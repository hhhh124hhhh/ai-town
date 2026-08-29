"""Luanti Builder - json_gen 模块：把 23 种建筑模板输出为 Unity 端 JSON。

与 lua_gen 的体素填充不同，这里输出连续几何体（box/cyl/sphere/cone/dome），
墙体由多块 box 拼装（留门洞），每栋建筑几个到几十个 block，Unity 端 ShapeFactory 直接实例化。
"""
import math

# 乐高/材质 → 十六进制色（与原项目 lego 色系对齐）
COLOR_HEX = {
    "red": "#C0392B", "blue": "#2E86C1", "yellow": "#F4D03F", "green": "#27AE60",
    "white": "#FDFEFE", "black": "#1C2833", "orange": "#E67E22", "purple": "#8E44AD",
    "pink": "#F1948A", "cyan": "#48C9B0", "gray": "#95A5A6",
}
MATERIAL_HEX = {
    "stone": "#95A5A6", "wood": "#935116", "brick": "#943126", "sand": "#D5B895",
    "glass": "#D6EAF8", "iron": "#85929E", "dirt": "#6E2C00", "snow": "#FBFCFC",
}
WOOD = "#935116"
GLOW = "#F7DC6F"
GLASS = "#D6EAF8"
LEAVES = "#1E8449"
WHITE = "#FDFEFE"
SIZE_STEPS = [0.6, 1.0, 1.5, 2.5]


def _color(params):
    if params.get("color") and params["color"] in COLOR_HEX:
        return COLOR_HEX[params["color"]]
    if params.get("material") and params["material"] in MATERIAL_HEX:
        return MATERIAL_HEX[params["material"]]
    return COLOR_HEX["gray"]


def _hex_box(blocks, x, y, z, sx, sy, sz, color):
    """居中 box：x/z 为中心，y 为底边高度。"""
    blocks.append({"shape": "box", "pos": [x, y + sy / 2.0, z], "size": [sx, sy, sz], "color": color})


def _hex_cyl(blocks, x, y, z, radius, height, color):
    blocks.append({"shape": "cyl", "pos": [x, y + height / 2.0, z], "size": [radius * 2, height, radius * 2], "color": color})


def _sphere(blocks, x, y, z, radius, color):
    blocks.append({"shape": "sphere", "pos": [x, y, z], "size": [radius * 2, radius * 2, radius * 2], "color": color})


def _cone(blocks, x, y, z, radius, height, color):
    blocks.append({"shape": "cone", "pos": [x, y + height / 2.0, z], "size": [radius * 2, height, radius * 2], "color": color})


def _hollow_walls(blocks, w, h, color, door=True, door_w=2.0, door_h=3.0):
    """四面空心墙：y 从 0 到 h。door=True 时前墙(z=-w)中部留门洞。"""
    t = 0.5  # 墙厚
    # 前墙 z=-w：左段 / 门楣 / 右段（z 中心 = -w）
    if door:
        side = (2 * w - door_w) / 2.0
        _hex_box(blocks, -(door_w / 2 + side / 2), 0, -w, side, h, t, color)
        _hex_box(blocks, (door_w / 2 + side / 2), 0, -w, side, h, t, color)
        _hex_box(blocks, 0, door_h, -w, door_w, h - door_h, t, color)
    else:
        _hex_box(blocks, 0, 0, -w, 2 * w, h, t, color)
    # 后墙
    _hex_box(blocks, 0, 0, w, 2 * w, h, t, color)
    # 左右墙
    _hex_box(blocks, -w, 0, 0, t, h, 2 * w, color)
    _hex_box(blocks, w, 0, 0, t, h, 2 * w, color)


def _roof_pyramid(blocks, w, h0, color):
    """四坡锥顶屋顶，从 h0 起高 w*0.6。"""
    rh = w * 0.6
    _cone(blocks, 0, h0, 0, w * 1.3, rh, color)


def _roof_flat(blocks, w, h0, color):
    _hex_box(blocks, 0, h0, 0, 2 * w + 0.6, 0.5, 2 * w + 0.6, color)


def _load_prebuilt(btype):
    """castle/paifang/qilou 直读 StreamingAssets 同款 JSON（与场景内建筑一致）。
    utf-8-sig 兼容带 BOM 的资产文件；缺失/解析失败返回 None 走程序化生成。"""
    import json as _json
    import os as _os
    root = _os.path.dirname(_os.path.abspath(__file__))
    path = _os.path.normpath(_os.path.join(
        root, "..", "Assets", "StreamingAssets", "Buildings", f"{btype}.json"))
    if not _os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            data = _json.load(f)
        return data if data.get("blocks") else None
    except (ValueError, OSError):
        return None


def gen_building(params):
    """主入口：nlp.parse_input 的 params → Unity JSON dict。"""
    btype = params.get("type") or "house"
    color = _color(params)
    s = SIZE_STEPS[params.get("size", 1)] if 0 <= params.get("size", 1) < 4 else 1.0
    name_zh = {"castle": "老洋楼", "house": "小屋", "tower": "高塔", "pagoda": "宝塔", "pyramid": "金字塔", "qilou": "骑楼", "paifang": "牌坊", "xitai": "戏台", "gulou": "鼓楼"}
    name = name_jh = f"{params.get('color') or params.get('material') or ''}{name_zh.get(btype, btype)}"

    # 三个有 StreamingAssets 成品的模板优先直读（与场景内建筑同款）；默认尺寸时才生效
    if btype in ("castle", "paifang", "qilou") and s == 1.0:
        pre = _load_prebuilt(btype)
        if pre:
            return pre

    blocks = []
    h = int(8 * s)
    w = int(5 * s)

    if btype == "castle":
        h = int(8 * s); w = int(5 * s)
        _hollow_walls(blocks, w, h, color)
        _hex_box(blocks, 0, 0, 0, 2 * w, 0.4, 2 * w, color)  # 地板
        _roof_flat(blocks, w, h, color)
        # 四角塔楼 + 锥顶
        tr = max(1.2, s * 1.5)
        for cx, cz in [(-w, -w), (w, -w), (-w, w), (w, w)]:
            th = h + int(3 * s)
            _hex_cyl(blocks, cx, 0, cz, tr, th, color)
            _cone(blocks, cx, th, cz, tr * 1.3, int(2 * s) + 1, WOOD)
        # 旗杆
        blocks.append({"shape": "box", "pos": [0, h + 2.5, 0], "size": [0.15, 5, 0.15], "color": WOOD})
        _sphere(blocks, 0, h + 5.5, 0, 0.4, GLOW)

    elif btype == "house":
        h = max(3, int(4 * s)); w = max(3, int(5 * s))
        _hollow_walls(blocks, w, h, color)
        _hex_box(blocks, 0, 0, 0, 2 * w, 0.3, 2 * w, color)
        _roof_pyramid(blocks, w, h, WOOD)
        _sphere(blocks, 0, h + 0.2, 0, 0.3, GLOW)

    elif btype == "tower":
        th = int(20 * s); tr = max(2, int(3 * s))
        _hex_cyl(blocks, 0, 0, 0, tr, th, color)
        _cone(blocks, 0, th, 0, tr * 1.4, int(5 * s), WOOD)
        _sphere(blocks, 0, th + int(5 * s) + 1, 0, 0.4, GLOW)

    elif btype == "pagoda":
        tiers = max(3, int(3 * s + 1))
        tw = max(2, int(6 * s)); th = max(3, int(5 * s))
        ty = 0
        for t in range(tiers):
            twd = max(2, tw - t * (tw // tiers) if tw // tiers > 0 else 2)
            _hollow_walls(blocks, twd, th, color, door=(t == 0))
            _hex_box(blocks, 0, ty + th, 0, 2 * twd + 2, 0.5, 2 * twd + 2, WOOD)  # 挑檐
            ty += th + 1
        _cone(blocks, 0, ty, 0, max(1.5, tw * 0.5), 2, WOOD)  # 塔尖
        _sphere(blocks, 0, ty + 2.5, 0, 0.35, GLOW)

    elif btype == "pyramid":
        ph = int(10 * s)
        _cone(blocks, 0, 0, 0, ph, ph, color)  # 四棱锥用 cone 近似（Unity 端可换 4 边金字塔）

    elif btype == "bridge":
        bl = int(20 * s); bh = int(5 * s)
        _hex_box(blocks, bl / 2, bh, 0, bl, 0.5, 6, color)
        _hex_box(blocks, 0, 0, 0, 1.5, bh, 6, color)
        _hex_box(blocks, bl, 0, 0, 1.5, bh - 1, 6, color)

    elif btype == "temple":
        tw = max(3, int(8 * s)); th = int(6 * s)
        for step in range(4):
            sw = tw - step * 1.5
            _hex_box(blocks, 0, step * 0.5, 0, 2 * sw, 0.5, 2 * sw, color)
        _hollow_walls(blocks, tw - 3, th, color)
        _roof_pyramid(blocks, tw - 3, th + 2, color)

    elif btype == "fountain":
        fr = max(3, int(4 * s))
        _hex_cyl(blocks, 0, 0, 0, fr, 1, color)
        _hex_cyl(blocks, 0, 1, 0, 0.8, int(3 * s) + 1, color)
        _hex_cyl(blocks, 0, int(3 * s) + 2, 0, 2, 0.5, color)
        _sphere(blocks, 0, int(3 * s) + 3.5, 0, 0.5, GLASS)

    elif btype == "lighthouse":
        lh = int(15 * s); lr = max(2, int(3 * s))
        _hex_cyl(blocks, 0, 0, 0, lr, lh, color)
        _hex_cyl(blocks, 0, lh, 0, lr - 0.5, 2, GLASS)
        _cone(blocks, 0, lh + 2, 0, lr, 2, WOOD)
        _sphere(blocks, 0, lh + 1, 0, 0.5, GLOW)

    elif btype == "wall":
        wl = int(20 * s); wh = int(4 * s)
        _hex_box(blocks, wl / 2, 0, 0, wl, wh, 1, color)
        for x in range(0, int(wl) + 1, 4):
            _hex_box(blocks, x, wh, 0, 0.8, 0.8, 1.2, color)

    elif btype == "tree":
        th = int(8 * s); tr = max(1.5, int(3 * s))
        _hex_cyl(blocks, 0, 0, 0, 0.6, th, "#6E2C00")
        _sphere(blocks, 0, th, 0, tr, LEAVES)
        _sphere(blocks, 0, th + tr, 0, tr - 1, LEAVES)

    elif btype in ("spaceship", "rocket"):
        rh = int(8 * s)
        _hex_cyl(blocks, 0, 0, 0, 2, rh, color)
        _sphere(blocks, 0, rh + 1, 0, 2, GLASS)
        _hex_box(blocks, 0, 3, 0, int(6 * s), 1, 2, color)
        _hex_cyl(blocks, -1.5, 0, 0, 0.8, 2, "#E74C3C")
        _hex_cyl(blocks, 1.5, 0, 0, 0.8, 2, "#E74C3C")

    elif btype == "mushroom":
        mh = max(3, int(5 * s)); mr = max(2, int(3 * s))
        _hex_cyl(blocks, 0, 0, 0, 0.8, mh, WHITE)
        _sphere(blocks, 0, mh, 0, mr, color)

    elif btype == "heart":
        sc = max(1, int(s * 2))
        pattern = [".##...##.", "#########", "#########", "#########", ".#######.", "..#####..", "...###...", "....#...."]
        rows = len(pattern); cols = len(pattern[0])
        for r, line in enumerate(pattern):
            for c, ch in enumerate(line):
                if ch == "#":
                    x = (c - cols / 2) * sc
                    y = (rows - r) * sc
                    _hex_box(blocks, x, y, 0, sc * 0.95, sc * 0.95, 0.5, color)

    elif btype == "sphere":
        sr = max(3, int(6 * s))
        _sphere(blocks, 0, sr, 0, sr, color)

    elif btype == "statue":
        sh = max(5, int(10 * s))
        _hex_box(blocks, 0, 0, 0, 4, 2, 4, color)
        _hex_box(blocks, 0, 2, 0, 2, sh - 4, 2, color)
        _sphere(blocks, 0, sh - 2, 0, 1.5, color)
        _hex_box(blocks, -2.5, sh / 2, 0, 1.5, 1.5, 1, color)
        _hex_box(blocks, 2.5, sh / 2, 0, 1.5, 1.5, 1, color)

    elif btype == "garden":
        gw = max(4, int(8 * s))
        _hex_cyl(blocks, 0, 0, 0, 0.5, int(4 * s), "#6E2C00")
        _sphere(blocks, 0, int(4 * s), 0, int(3 * s), LEAVES)
        for x in range(-gw, gw + 1, 2):
            _hex_box(blocks, x, 0, -gw, 0.4, 1, 0.4, WOOD)
            _hex_box(blocks, x, 0, gw, 0.4, 1, 0.4, WOOD)
        for z in range(-gw, gw + 1, 2):
            _hex_box(blocks, -gw, 0, z, 0.4, 1, 0.4, WOOD)
            _hex_box(blocks, gw, 0, z, 0.4, 1, 0.4, WOOD)
        for i in range(1, int(10 * s)):
            fx = ((i * 37) % (2 * gw - 2)) - gw + 1
            fz = ((i * 53) % (2 * gw - 2)) - gw + 1
            _sphere(blocks, fx, 0.4, fz, 0.3, ["#E74C3C", "#F4D03F", "#EC87C0", "#48C9B0"][i % 4])

    elif btype == "windmill":
        wh = int(12 * s); wr = max(2, int(3 * s))
        _hex_cyl(blocks, 0, 0, 0, wr, wh, color)
        _cone(blocks, 0, wh, 0, wr * 1.2, 2, WOOD)
        bl = wr * 2
        _hex_box(blocks, 0, wh - 2, 0.3, bl * 2, 0.4, 0.3, WOOD)
        _hex_box(blocks, 0, wh - 2, 0.3, 0.3, 0.4, bl * 2, WOOD)
        _sphere(blocks, 0, wh - 1.5, 0, 0.3, GLOW)

    elif btype == "gazebo":
        gh = max(3, int(5 * s)); gw = max(3, int(4 * s))
        import math as _m
        for i in range(8):
            a = i * _m.pi / 4
            _hex_cyl(blocks, round(_m.cos(a) * gw, 2), 0, round(_m.sin(a) * gw, 2), 0.3, gh, color)
        _hex_cyl(blocks, 0, 0, 0, 0.3, gh, WOOD)
        _cone(blocks, 0, gh, 0, gw * 1.5, 2.5, WOOD)

    elif btype == "skyscraper":
        sh = int(30 * s); sw = max(3, int(5 * s))
        _hollow_walls(blocks, sw, sh, color, door=True, door_w=2.5, door_h=4)
        for y in range(2, sh, 3):
            _hex_box(blocks, 0, y, -sw, 2 * sw - 1, 1.2, 0.3, GLASS)
            _hex_box(blocks, 0, y, sw, 2 * sw - 1, 1.2, 0.3, GLASS)
        _roof_flat(blocks, sw, sh, color)
        _sphere(blocks, 0, sh + 1, 0, 0.4, GLOW)

    elif btype == "village":
        houses = max(3, int(5 * s)); dist = max(9, int(12 * s))
        mats = [MATERIAL_HEX["wood"], MATERIAL_HEX["brick"], MATERIAL_HEX["sand"], MATERIAL_HEX["stone"]]
        for i in range(houses):
            import math as _m
            a = (i / houses) * _m.pi * 2
            hx = round(_m.cos(a) * dist, 2); hz = round(_m.sin(a) * dist, 2)
            hw = max(2, int(3 * s)); hh = max(3, int(4 * s))
            _hex_box(blocks, hx, 0, hz, 2 * hw, 0.3, 2 * hw, mats[i % 4])
            _hex_box(blocks, hx, 0.3, hz - hw, 2 * hw, hh, 0.4, mats[i % 4])
            _hex_box(blocks, hx - hw, 0.3, hz, 0.4, hh, 2 * hw, mats[i % 4])
            _hex_box(blocks, hx + hw, 0.3, hz, 0.4, hh, 2 * hw, mats[i % 4])
            _hex_box(blocks, hx, 0.3, hz + hw, 2 * hw, hh, 0.4, mats[i % 4])
            _roof_pyramid(blocks, hw, hh + 0.3, WOOD)

    elif btype == "spiral":
        sph = int(20 * s)
        for y in range(0, sph):
            a = y * 0.5
            r = max(2, int(5 - y * 2 / sph))
            x = round(math.cos(a) * r, 2); z = round(math.sin(a) * r, 2)
            _hex_box(blocks, x, y, z, 1.2, 1.2, 1.2, color)

    elif btype == "xitai":
        # 民国戏台：台基 + 四角柱 + 歇山式屋顶（大出檐）+ 台口横匾 + 幕布
        if not (params.get("color") or params.get("material")):
            color = "#8A3B2A"  # 戏台朱褐默认
        tw = max(3, int(6 * s))    # 台面半宽
        td = max(2.5, int(4 * s))  # 台面半深
        th = max(1.2, int(2 * s))  # 台基高
        rh = max(2.5, int(4 * s))  # 屋顶高
        # 台基（带勒脚）
        _hex_box(blocks, 0, 0, 0, 2 * tw + 1, 0.4, 2 * td + 1, "#7A8B8B")
        _hex_box(blocks, 0, 0.4, 0, 2 * tw, th - 0.4, 2 * td, color)
        # 四角金柱（通到顶）
        ph = th + rh
        for cx in (-tw + 0.4, tw - 0.4):
            for cz in (-td + 0.4, td - 0.4):
                _hex_cyl(blocks, cx, th, cz, 0.35, ph - th, WOOD)
        # 屋顶：重檐感 = 主檐 + 上檐
        _hex_box(blocks, 0, ph, 0, 2 * tw + 2.4, 0.45, 2 * td + 2.4, WOOD)  # 大出檐
        _cone(blocks, 0, ph + 0.45, 0, max(tw, td) * 1.1, rh * 0.55, color)
        _hex_box(blocks, 0, ph + rh * 0.55 + 0.4, 0, max(tw, td) + 1.5, 0.4, td * 2 + 1.2, WOOD)  # 上檐
        _cone(blocks, 0, ph + rh * 0.55 + 0.8, 0, max(tw, td) * 0.7, rh * 0.4, color)
        # 台口横匾（发光，戏台灵魂）
        _hex_box(blocks, 0, th + rh - 0.8, -td - 0.25, tw * 1.4, 0.7, 0.2, GLOW)
        # 台口幕布（左右各一幅）
        _hex_box(blocks, -tw * 0.55, th, -td - 0.15, tw * 0.5, rh - 1, 0.25, "#7C1F1F")
        _hex_box(blocks, tw * 0.55, th, -td - 0.15, tw * 0.5, rh - 1, 0.25, "#7C1F1F")

    elif btype == "gulou":
        # 民国鼓楼：城台（梯形收分）+ 两层木楼 + 重檐攒尖顶 + 悬匾
        if not (params.get("color") or params.get("material")):
            color = "#6E6A63"  # 城台灰默认
        bw = max(3.5, int(5 * s))   # 台底半宽
        twd = bw * 0.8              # 台顶半宽（收分）
        bh = max(3, int(4 * s))     # 城台高
        fh = max(2.5, int(3 * s))   # 楼层高
        # 城台：三段收分 + 中间门洞
        _hex_box(blocks, 0, 0, 0, 2 * bw, bh * 0.4, 2 * bw, color)
        _hex_box(blocks, 0, bh * 0.4, 0, 2 * (bw + twd) / 2, bh * 0.3, 2 * (bw + twd) / 2, color)
        _hex_box(blocks, 0, bh * 0.7, 0, 2 * twd, bh * 0.3, 2 * twd, color)
        # 门洞（南面）
        _hex_box(blocks, 0, 0, -bw - 0.05, 1.6, 2.2, 0.6, "#2B2620")  # 暗色门洞示意
        # 台面栏杆
        ry = bh
        for x in [x * 0.5 for x in range(-int(2 * twd), int(2 * twd) + 1, 2)]:
            _hex_box(blocks, x, ry, -twd, 0.25, 0.8, 0.25, WOOD)
            _hex_box(blocks, x, ry, twd, 0.25, 0.8, 0.25, WOOD)
        # 一层木楼
        _hex_box(blocks, 0, ry + 0.8, 0, 2 * twd * 0.9, fh, 2 * twd * 0.9, "#935116")
        # 一层檐
        y1 = ry + 0.8 + fh
        _hex_box(blocks, 0, y1, 0, 2 * twd + 1.6, 0.4, 2 * twd + 1.6, WOOD)
        # 二层木楼（收分）
        w2 = twd * 0.7
        _hex_box(blocks, 0, y1 + 0.4, 0, 2 * w2, fh * 0.8, 2 * w2, "#935116")
        # 顶层重檐 + 攒尖
        y2 = y1 + 0.4 + fh * 0.8
        _hex_box(blocks, 0, y2, 0, 2 * w2 + 1.2, 0.4, 2 * w2 + 1.2, WOOD)
        _cone(blocks, 0, y2 + 0.4, 0, w2 * 1.3, fh * 0.9, "#6E4A2A")
        _sphere(blocks, 0, y2 + 0.4 + fh * 0.9, 0, 0.35, GLOW)
        # 悬匾（南面一层檐下）
        _hex_box(blocks, 0, y1 - 1.1, -twd - 0.85, w2 * 1.4, 0.6, 0.2, GLOW)

    elif btype == "shanghai":
        # 东方明珠：主塔 + 双球 + 右侧高楼
        _hex_cyl(blocks, 0, 0, 0, 2, int(30 * s), color)
        _sphere(blocks, 0, int(15 * s), 0, int(4 * s), color)
        _sphere(blocks, 0, int(25 * s), 0, int(3 * s), color)
        bh = int(50 * s)
        _hex_box(blocks, 8, 0, 0, 3, bh, 3, color)

    elif btype == "qilou":
        # 民国骑楼：底层柱廊（沿街步廊）+ 二层骑楼体 + 女儿墙 + 坡檐招牌
        if not (params.get("color") or params.get("material")):
            color = "#9E4B3A"  # 民国砖红默认
        qw = max(4, int(8 * s))        # 面宽（半宽）
        depth = max(3, int(qw * 0.6))  # 进深（半深）
        gf = max(3, int(3.5 * s))      # 底层层高
        fl = max(3, int(3.5 * s))      # 二层层高
        cols = max(4, int(qw / 1.5))   # 前廊柱数
        # 底层：底板 + 后墙 + 侧墙，前面临街开放成柱廊
        _hex_box(blocks, 0, 0, 0, 2 * qw, 0.3, 2 * depth, color)
        _hex_box(blocks, 0, 0, depth, 2 * qw, gf, 0.5, color)
        _hex_box(blocks, -qw, 0, 0, 0.5, gf, 2 * depth, color)
        _hex_box(blocks, qw, 0, 0, 0.5, gf, 2 * depth, color)
        for i in range(cols):
            cx = -qw + (2 * qw) * i / (cols - 1) if cols > 1 else 0
            _hex_box(blocks, round(cx, 2), 0, -depth, 0.6, gf, 0.6, "#7A8B8B")
        # 二层骑楼体：完整围合压在柱廊上，前墙中段留玻璃窗
        y2 = gf + 0.2
        seg = (2 * qw - 2 * fl) / 2  # 前墙左右段宽（中段让窗）
        _hex_box(blocks, 0, y2, depth, 2 * qw, fl, 0.5, color)
        _hex_box(blocks, -qw, y2, 0, 0.5, fl, 2 * depth, color)
        _hex_box(blocks, qw, y2, 0, 0.5, fl, 2 * depth, color)
        _hex_box(blocks, -(qw - fl / 2), y2, -depth, seg, fl, 0.5, color)
        _hex_box(blocks, (qw - fl / 2), y2, -depth, seg, fl, 0.5, color)
        _hex_box(blocks, 0, y2 + 0.6, -depth, 2 * fl, fl - 1.2, 0.4, GLASS)
        # 楼板 + 前女儿墙 + 前坡檐
        y3 = y2 + fl
        _hex_box(blocks, 0, y3, 0, 2 * qw + 0.6, 0.4, 2 * depth + 0.6, color)
        _hex_box(blocks, 0, y3 + 0.4, -depth, 2 * qw + 0.6, 0.8, 0.3, color)
        _hex_box(blocks, 0, y3 + 1.0, -depth - 0.2, 2 * qw + 1.2, 0.3, 0.8, WOOD)
        # 招牌发光条（骑楼灵魂）
        _hex_box(blocks, 0, y3 + 1.6, -depth - 0.3, 2 * qw * 0.6, 0.8, 0.2, GLOW)

    elif btype == "paifang":
        # 民国牌坊：两柱一横梁 + 大檐 + 柱础 + 匾额
        if not (params.get("color") or params.get("material")):
            color = "#7A8B8B"  # 青灰默认
        pw = max(3, int(5 * s))   # 柱距（半距）
        ph = max(5, int(8 * s))   # 柱高
        for cx in (-pw, pw):
            _hex_box(blocks, cx, 0, 0, 1.0, 0.6, 1.4, color)   # 柱础
            _hex_box(blocks, cx, 0.6, 0, 0.7, ph, 0.7, color)  # 柱身
        _hex_box(blocks, 0, ph * 0.55, 0, 2 * pw + 1.5, 0.6, 0.5, color)  # 下横梁
        _hex_box(blocks, 0, ph + 0.6, 0, 2 * pw + 1.5, 0.7, 0.6, color)   # 主横梁
        _hex_box(blocks, 0, ph + 1.3, 0, 2 * pw + 2.6, 0.3, 1.2, WOOD)    # 大檐
        _hex_box(blocks, 0, ph + 2.2, 0, 2 * pw + 1.0, 0.8, 0.4, color)   # 檐上小匾
        _hex_box(blocks, 0, ph + 1.7, -0.5, pw, 0.5, 0.2, GLOW)           # 匾额发光

    else:
        # 默认小屋兜底
        h = max(3, int(4 * s)); w = max(3, int(5 * s))
        _hollow_walls(blocks, w, h, color)
        _roof_pyramid(blocks, w, h, WOOD)

    return {"name": name, "blocks": blocks}
