#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ai_town_server.py — 民国小镇 AI 后端（纯标准库，零依赖）

Unity ApiClient.cs（http://127.0.0.1:8765）的完整实现：
  POST /api/generate_json      {"description"|"template"} → {"building":{name,blocks[]}}
  POST /api/npc/chat           {"name","message"} → {"name","reply"}
  GET  /api/intro/line         → {"line"}
  GET  /api/npc/memory?name=X  → {"memory":[...]}（最近对话，供调试）
  GET  /api/commission/state   → {"ok","state"}
  POST /api/commission/new     {"npc","npcPos":[x,y,z]} → {"ok","commission","state"}
  POST /api/commission/submit  {"builds":[...],"zoneCenter":[x,z]} → {"ok","pass","grade",...}
  POST /api/commission/abandon {} → {"ok","state"}

可选 LLM：设置环境变量 TOWN_LLM_BASE_URL / TOWN_LLM_API_KEY / TOWN_LLM_MODEL
（OpenAI 兼容 /chat/completions）后，NPC 对话与开场白走大模型；调用失败自动回退内置规则回复。
用法：python3 ai_town_server.py   （地址可用 TOWN_HOST / TOWN_PORT 覆盖）
"""
import json
import math
import os
import random
import threading
import time
import traceback
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = os.environ.get("TOWN_HOST", "127.0.0.1")
PORT = int(os.environ.get("TOWN_PORT", "8765"))
ROOT = os.path.dirname(os.path.abspath(__file__))
STATE_PATH = os.path.join(ROOT, "state.json")
BUILDINGS_DIR = os.path.normpath(os.path.join(ROOT, "..", "Assets", "StreamingAssets", "Buildings"))

LLM_BASE = os.environ.get("TOWN_LLM_BASE_URL", "").rstrip("/")
LLM_KEY = os.environ.get("TOWN_LLM_API_KEY", "")
LLM_MODEL = os.environ.get("TOWN_LLM_MODEL", "gpt-4o-mini")

_LOCK = threading.Lock()

# ── 模板清单（与 BuildingPanel.Templates 一一对应）────────────────────
TEMPLATE_ZH = {
    "castle": "洋楼", "house": "房屋", "tower": "高塔", "pagoda": "宝塔",
    "qilou": "骑楼", "paifang": "牌坊", "xitai": "戏台", "gulou": "鼓楼",
    "temple": "庙宇", "bridge": "桥", "fountain": "喷泉", "wall": "围墙",
    "garden": "花园", "windmill": "风车", "gazebo": "凉亭", "lighthouse": "灯塔",
    "village": "村落", "statue": "雕像", "tree": "树", "pyramid": "金字塔",
    "sphere": "球体", "spiral": "螺旋", "mushroom": "蘑菇", "heart": "心形",
    "skyscraper": "高楼", "spaceship": "飞船", "shanghai": "上海",
}
TEMPLATE_IDS = list(TEMPLATE_ZH.keys())
LOCKED_DEFAULT = ["village", "skyscraper", "spaceship", "shanghai"]
# StreamingAssets 里已有成品的三个模板直接读文件（与场景内建筑同款）
FILE_TEMPLATES = {"castle": "castle.json", "paifang": "paifang.json", "qilou": "qilou.json"}

# 繁荣度等级：(门槛, 称号)
LEVELS = [(0, "荒郊野店"), (30, "逐成集市"), (80, "小有名气"), (160, "商贾云集"), (300, "民国名镇")]


def _level_of(prosperity):
    lv, name = 1, LEVELS[0][1]
    for i, (th, title) in enumerate(LEVELS):
        if prosperity >= th:
            lv, name = i + 1, title
    return lv, name


# ── 玩法状态（持久化到 server/state.json）────────────────────────────
def _default_state():
    return {
        "gold": 50,
        "prosperity": 0,
        "completed": 0,
        "unlocked": [t for t in TEMPLATE_IDS if t not in LOCKED_DEFAULT],
        "active": None,          # 进行中的委托 CommissionInfo
        "affinity": {},          # NPC 好感度 {name: int}
    }


_STATE = _default_state()


def _load_state():
    global _STATE
    try:
        with open(STATE_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        base = _default_state()
        base.update({k: v for k, v in data.items() if k in base})
        _STATE = base
    except Exception:
        _STATE = _default_state()


def _save_state():
    try:
        with open(STATE_PATH, "w", encoding="utf-8") as f:
            json.dump(_STATE, f, ensure_ascii=False, indent=1)
    except Exception as e:
        print(f"[state] 保存失败: {e}")


_load_state()


def _affinity_label(v):
    if v >= 90:
        return "莫逆之交"
    if v >= 60:
        return "老友"
    if v >= 30:
        return "熟人"
    if v >= 10:
        return "点头之交"
    return "陌生"


def _npc_view():
    arr = []
    for name, info in NPCS.items():
        a = int(_STATE["affinity"].get(name, 0))
        arr.append({"name": name, "role": info["role"], "affinity": a, "affinityLabel": _affinity_label(a)})
    return arr


def _state_payload():
    lv, name = _level_of(int(_STATE["prosperity"]))
    return {
        "gold": int(_STATE["gold"]),
        "prosperity": int(_STATE["prosperity"]),
        "level": lv,
        "levelName": name,
        "completed": int(_STATE["completed"]),
        "unlocked": list(_STATE["unlocked"]),
        "lockedDefault": list(LOCKED_DEFAULT),
        "npcs": _npc_view(),
        "active": _STATE["active"],
    }

# ── NPC 人设（与场景 npcName 一致）───────────────────────────────────
NPCS = {
    "面包师老王": {
        "role": "面包师",
        "persona": "热情开朗的面包师，喜欢聊面包、早点和小镇生活，说话带着炉火气。",
        "greet": "老弟来啦！炉子上刚出一炉芝麻烧饼，趁热咬一口？",
    },
    "守卫铁山": {
        "role": "巡捕",
        "persona": "严肃认真的巡捕，警惕性高，关心小镇治安，说话简短干脆。",
        "greet": "站住……哦，是镇民啊。有事直说，我巡逻时间宝贵。",
    },
    "王婶": {
        "role": "街坊",
        "persona": "热心肠的街坊大婶，爱唠家常，全镇的消息最灵通。",
        "greet": "哎哟，来啦？快坐快坐，正好晾着新腌的酱菜。",
    },
    "钱先生": {
        "role": "钱庄掌柜",
        "persona": "精打细算的钱庄掌柜，开口闭口都是行情生意，算盘打得噼啪响。",
        "greet": "稀客稀客。近来银根紧，你来得正是时候。",
    },
}

_MEMORY = {}  # name -> [(who, text)] 最近对话（内存态，调试用）


def _remember(name, who, text):
    arr = _MEMORY.setdefault(name, [])
    arr.append((who, text))
    if len(arr) > 8:
        del arr[0]


# 关键词 → 回复池（规则兜底；按 NPC 加专属条目）
_COMMON_REPLIES = {
    "greet": [
        "嗯，今儿天气不错，镇口河边风一吹，赛过活神仙。",
        "你来得巧，我正要说正事呢。",
        "安好啊。镇上近来太平，就是河边石桥那边人多，当心脚下。",
    ],
    "weather": [
        "天上铺着层薄云，傍晚怕是要落雨，出门带把伞稳妥。",
        "这种天气最好，不晒不冷，正好干活。",
    ],
    "town": [
        "咱们镇子靠河吃河，南来北往的货都要过石桥。近来人气旺，就差几座像样的建筑撑门面。",
        "镇上人口口相传：谁肯出力起楼盖屋，镇长那头有的是赏钱。",
    ],
    "commission": [
        "委托？找对人了。你先把手上的活干完，咱们再谈下一单。",
        "要接活就开口说「接个委托」，我这儿登着好几件呢。",
    ],
    "thanks": ["客气啥，街里街坊的。", "举手之劳，不值一提。"],
    "bye": ["慢走啊，回头见。", "去吧去吧，路上当心。"],
    "default": [
        "这个嘛……你说到点子上了，不过我还得琢磨琢磨。",
        "嗯，有道理。改天寻个由头，咱们细聊。",
        "这话有意思。回头你也去茶馆听听，议论的人不少。",
    ],
}
_NPC_REPLIES = {
    "面包师老王": {
        "bread": ["刚出炉的芝麻烧饼、豆沙包，你要哪个？老面肥是我自个儿养的，别处吃不着这口。",
                  "做面包跟起楼一个理——底子要实，火候要匀。"],
        "work": ["天不亮就得起来发面，一年到头就盼着镇上人多，生意兴拢。"],
    },
    "守卫铁山": {
        "safety": ["近来河上货船多，闲杂人等也杂。你夜里少往桥洞去。",
                   "治安归我管，放心。前些日子溜进来的小毛贼，早叫我请进了班房。"],
        "work": ["巡逻一圈一个时辰，风雨无阻。这份差事，图的就是个心安。"],
    },
    "王婶": {
        "gossip": ["要说传闻——西街钱庄新进了批大洋，成色好得很，排队兑的人从街头排到街尾。",
                   "隔壁镇老李家嫁闺女，聘礼抬了整整十二条街，啧啧。",
                   "听说了吧？镇长要重修河埠头，谁建的楼好，赏钱翻倍。"],
        "work": ["我这把年纪，也就爱操心些街坊琐事。你要闲得慌，去接个委托，帮镇子添点动静。"],
    },
    "钱先生": {
        "money": ["大洋行情稳中看涨。依我看，眼下最值钱的不是银钱，是地皮和门面。",
                  "账要一笔一笔算，楼要一层一层起。急，是生意人的大忌。"],
        "work": ["钱庄的门槛高，可委托的酬劳更高。你有本事，自会有大洋找上门。"],
    },
}
_REPLY_KEYS = [
    (("面包", "烧饼", "早点", "吃", "点心"), "bread"),
    (("治安", "安全", "土匪", "贼", "巡逻", "太平"), "safety"),
    (("传闻", "消息", "新闻", "听说", "打听"), "gossip"),
    (("钱", "大洋", "生意", "行情", "银"), "money"),
    (("委托", "接活", "任务", "订单", "干活"), "commission"),
    (("你好", "您好", "哈喽", "安好", "早上好", "晚上好"), "greet"),
    (("天气", "下雨", "阴天", "晴天"), "weather"),
    (("镇", "小镇", "这里"), "town"),
    (("活", "工作", "差事"), "work"),
    (("谢谢", "多谢", "辛苦"), "thanks"),
    (("再见", "告辞", "回见", "拜拜"), "bye"),
]


def _rule_reply(name, message):
    pool = dict(_COMMON_REPLIES)
    pool.update(_NPC_REPLIES.get(name, {}))
    for keys, key in _REPLY_KEYS:
        if any(k in message for k in keys):
            options = pool.get(key) or pool["default"]
            return random.choice(options)
    return random.choice(pool["default"])


def _llm_chat(system, user, timeout=30):
    """OpenAI 兼容 /chat/completions；未配置或失败返回 None。"""
    if not (LLM_BASE and LLM_KEY):
        return None
    try:
        payload = json.dumps({
            "model": LLM_MODEL,
            "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
            "temperature": 0.8,
            "max_tokens": 200,
        }).encode("utf-8")
        req = urllib.request.Request(
            LLM_BASE + "/chat/completions", data=payload,
            headers={"Content-Type": "application/json", "Authorization": f"Bearer {LLM_KEY}"})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            data = json.loads(r.read().decode("utf-8"))
        text = data["choices"][0]["message"]["content"].strip()
        return text or None
    except Exception as e:
        print(f"[llm] 调用失败，回退规则回复: {e}")
        return None


def handle_npc_chat(name, message):
    info = NPCS.get(name)
    if info is None:
        return None, f"不认识的 NPC：{name}"
    _remember(name, "你", message)
    system = (f"你是民国小镇里的{name}（{info['role']}）。人设：{info['persona']}"
              "用带民国味儿的白话回答，两三句以内，不要出戏，不要提现代事物。")
    reply = _llm_chat(system, message) or _rule_reply(name, message)
    _remember(name, name, reply)
    with _LOCK:
        cur = int(_STATE["affinity"].get(name, 0))
        _STATE["affinity"][name] = min(100, cur + 1)
        _save_state()
    return reply, None


_INTRO_LINES = [
    "民国十二年，暮春。你背着一只旧藤箱走完最后一段官道——河湾处，那座灰瓦青砖的小镇到了。",
    "汽笛在远处拉了一声长音。镇口的牌坊下，卖报的孩童正嚷着时局，而你的新生活，才刚刚开始。",
    "河风带着水汽扑面而来。石桥、骑楼、茶馆的幌子……十余年闯荡，你终于攒够了安家的本钱。",
    "人说这镇子三样出名：河鲜、洋楼、和一堆等着发财的委托。你掂了掂口袋，信了。",
]


def handle_intro_line():
    system = "你是民国题材游戏的开场旁白。写一句不超过四十个字的开场白，画面感强，第二人称，不要现代词汇。"
    line = _llm_chat(system, "写一句开场白。", timeout=20)
    return line or random.choice(_INTRO_LINES)

# ── 建筑生成 ─────────────────────────────────────────────────────────
# 色板（与 Assets/Materials/Generated 的 Block_* 一致）
C_STONE, C_GREY, C_RED = "#A0A0A0", "#989289", "#9E4B3A"
C_WOOD, C_DWOOD = "#8B5A2B", "#5C4033"
C_ROOF, C_GOLD, C_COL = "#B22222", "#DAA520", "#708090"
C_WIN, C_GLOW, C_LEAF, C_TRUNK = "#D6EAF8", "#F7DC6F", "#4C7A3A", "#6E4B2A"
_WALL_COLORS = {C_STONE, C_GREY, C_RED, C_WOOD, C_DWOOD}


def B(shape, x, y, z, sx, sy, sz, color):
    return {"shape": shape, "pos": [round(x, 2), round(y, 2), round(z, 2)],
            "size": [round(sx, 2), round(sy, 2), round(sz, 2)], "color": color}


def _tint(blocks, color):
    """把墙体类颜色替换为指定色（描述里带材质词时用）。"""
    for b in blocks:
        if b["color"] in _WALL_COLORS:
            b["color"] = color
    return blocks


def _house(bl, cx=0, cz=0, w=10, d=10, wall=C_GREY, roof=C_ROOF, floors=2):
    fh = 3.2
    for i in range(floors):
        y = (i + 0.5) * fh
        bl.append(B("box", cx, y, cz, w, fh, d, wall))
        bl.append(B("box", cx, (i + 1) * fh - 0.1, cz - d / 2 - 0.05, w * 0.7, 1.4, 0.1, C_WIN))
        bl.append(B("box", cx, (i + 1) * fh - 0.1, cz + d / 2 + 0.05, w * 0.7, 1.4, 0.1, C_WIN))
    top = floors * fh
    bl.append(B("box", cx, top + 0.25, cz, w + 1, 0.5, d + 1, C_DWOOD))
    bl.append(B("pyramid", cx, top + 2, cz, w + 1, 3, d + 1, roof))
    bl.append(B("box", cx, 1.2, cz - d / 2 - 0.08, 1.8, 2.4, 0.16, C_DWOOD))
    bl.append(B("sphere", cx + w / 4, top + 4, cz, 0.5, 0.5, 0.5, C_GOLD))


def _t_templates():
    """读 StreamingAssets 同款 JSON；缺失返回 None。"""
    out = {}
    for tid, fn in FILE_TEMPLATES.items():
        try:
            # utf-8-sig 兼容带 BOM 的文件（StreamingAssets 部分资产带 BOM）
            with open(os.path.join(BUILDINGS_DIR, fn), "r", encoding="utf-8-sig") as f:
                out[tid] = json.load(f)
        except Exception:
            pass
    return out


def _gen(tid):
    n = TEMPLATE_ZH.get(tid, tid)
    bl = []
    if tid == "house":
        _house(bl, w=11, d=11)
    elif tid == "tower":
        for i in range(6):
            s = 6 - i * 0.6
            bl.append(B("box", 0, 2 + i * 3.4, 0, s, 3.4, s, C_STONE if i % 2 else C_GREY))
            bl.append(B("box", 0, 3.6 + i * 3.4, 0, s + 0.4, 0.3, s + 0.4, C_DWOOD))
        bl.append(B("pyramid", 0, 23, 0, 7, 3.5, 7, C_ROOF))
        bl.append(B("sphere", 0, 25.4, 0, 0.8, 0.8, 0.8, C_GOLD))
    elif tid == "pagoda":
        for i in range(4):
            s = 12 - i * 2.4
            bl.append(B("box", 0, 1.5 + i * 4.5, 0, s, 3, s, C_RED))
            bl.append(B("pyramid", 0, 4.5 + i * 4.5, 0, s + 2.4, 2, s + 2.4, C_DWOOD))
        bl.append(B("cyl", 0, 20.5, 0, 0.4, 3, 0.4, C_GOLD))
        bl.append(B("sphere", 0, 22.2, 0, 0.9, 0.9, 0.9, C_GOLD))
    elif tid == "temple":
        bl.append(B("box", 0, 0.5, 0, 18, 1, 14, C_STONE))
        bl.append(B("box", 0, 3, 0, 15, 4, 11, C_RED))
        for x in (-6.5, -2.2, 2.2, 6.5):
            bl.append(B("cyl", x, 3, 6.2, 0.6, 5, 0.6, C_DWOOD))
        bl.append(B("pyramid", 0, 7.2, 0, 19, 4, 15, C_DWOOD))
        bl.append(B("sphere", 0, 10.2, 0, 1.1, 1.1, 1.1, C_GOLD))
        bl.append(B("box", 0, 1.1, 7.2, 4, 2.2, 0.4, C_DWOOD))
    elif tid == "bridge":
        bl.append(B("box", 0, 3.4, 0, 18, 0.8, 6, C_STONE))
        bl.append(B("arch", 0, 3.4, 0, 18, 6.5, 6, C_GREY))
        bl.append(B("box", -9.4, 4.3, 0, 0.6, 1.2, 6, C_GREY))
        bl.append(B("box", 9.4, 4.3, 0, 0.6, 1.2, 6, C_GREY))
        for x in range(-8, 9, 4):
            bl.append(B("box", x, 4.4, -3, 0.5, 1, 0.5, C_GREY))
            bl.append(B("box", x, 4.4, 3, 0.5, 1, 0.5, C_GREY))
    elif tid == "fountain":
        bl.append(B("cyl", 0, 0.6, 0, 6, 1.2, 6, C_STONE))
        bl.append(B("cyl", 0, 1.4, 0, 5, 0.6, 5, C_WIN))
        bl.append(B("cyl", 0, 2.6, 0, 0.6, 2.5, 0.6, C_COL))
        bl.append(B("sphere", 0, 4.2, 0, 1.6, 1.6, 1.6, C_WIN))
        bl.append(B("sphere", 0, 5.6, 0, 0.7, 0.7, 0.7, C_GLOW))
    elif tid == "wall":
        for x in (-14, 0, 14):
            bl.append(B("box", x, 2.5, 0, 16, 5, 2, C_GREY))
        bl.append(B("box", 0, 3.5, 0, 8, 7, 2.4, C_RED))
        bl.append(B("box", 0, 7.6, 0, 10, 1, 3, C_DWOOD))
        bl.append(B("pyramid", 0, 9.4, 0, 10, 2.4, 3, C_ROOF))
        for x in (-22, 22):
            bl.append(B("cyl", x, 3, 0, 1.6, 6, 1.6, C_GREY))
            bl.append(B("cone", x, 6.8, 0, 2.2, 1.6, 2.2, C_ROOF))
    elif tid == "garden":
        for a in range(12):
            x, z = 10 * math.cos(a * math.pi / 6), 10 * math.sin(a * math.pi / 6)
            bl.append(B("box", x, 1, z, 0.4, 2, 0.4, C_DWOOD))
        bl.append(B("cyl", 0, 0.4, 0, 5, 0.8, 5, C_WIN))
        for x, z in ((-6, -4), (6, 3), (4, -6)):
            bl.append(B("cyl", x, 2, z, 0.5, 4, 0.5, C_TRUNK))
            bl.append(B("sphere", x, 5, z, 3, 2.6, 3, C_LEAF))
        _gazebo_blocks(bl, 0, 5, scale=0.8)
    elif tid == "windmill":
        bl.append(B("cone", 0, 5, 0, 8, 10, 8, C_STONE))
        bl.append(B("box", 0, 10.4, 0, 1, 0.6, 8.5, C_DWOOD))
        bl.append(B("box", 0, 10.4, 0, 8.5, 0.6, 1, C_DWOOD))
        bl.append(B("sphere", 0, 10.4, 0, 0.9, 0.9, 0.9, C_DWOOD))
        bl.append(B("box", 0, 2, 3.4, 1.6, 2.6, 0.3, C_DWOOD))
    elif tid == "lighthouse":
        for i in range(5):
            bl.append(B("cyl", 0, 2.5 + i * 3.4, 0, 3 - i * 0.35, 3.4, 3 - i * 0.35,
                        C_RED if i % 2 == 0 else "#F2F2F2"))
        bl.append(B("cyl", 0, 19.5, 0, 2, 2.4, 2, C_GLOW))
        bl.append(B("cone", 0, 21.8, 0, 3, 2, 3, C_RED))
    elif tid == "village":
        for i, (x, z, r) in enumerate(((-9, -6, 0), (9, -7, 1), (-7, 8, 1), (10, 7, 0), (0, -12, 1))):
            _house(bl, x, z, w=8, d=8, wall=C_GREY if r else C_WOOD, floors=1 + (i % 2))
    elif tid == "statue":
        bl.append(B("box", 0, 1, 0, 4, 2, 4, C_STONE))
        bl.append(B("box", 0, 3.4, 0, 1.6, 2.8, 1.2, C_COL))
        bl.append(B("sphere", 0, 5.6, 0, 1, 1, 1, C_COL))
        bl.append(B("box", -1.2, 4, 0, 0.5, 2, 0.5, C_COL))
        bl.append(B("box", 1.2, 4, 0, 0.5, 2, 0.5, C_COL))
    elif tid == "tree":
        bl.append(B("cyl", 0, 3, 0, 0.8, 6, 0.8, C_TRUNK))
        bl.append(B("sphere", 0, 7.5, 0, 6, 5, 6, C_LEAF))
        bl.append(B("sphere", 1.5, 9.5, 1, 3.5, 3, 3.5, C_LEAF))
    elif tid == "pyramid":
        for i in range(8):
            s = 18 - i * 2.2
            bl.append(B("box", 0, 1 + i * 1.6, 0, s, 1.6, s, C_STONE if i % 2 else C_GOLD))
    elif tid == "sphere":
        bl.append(B("sphere", 0, 4, 0, 8, 8, 8, C_RED))
        bl.append(B("box", 0, 0.4, 0, 6, 0.8, 6, C_STONE))
    elif tid == "spiral":
        for i in range(28):
            a = i * 0.55
            bl.append(B("box", 4.5 * math.cos(a), 0.6 + i * 0.55, 4.5 * math.sin(a),
                        1.4, 0.6, 1.4, C_GOLD if i % 3 == 0 else C_WOOD))
    elif tid == "mushroom":
        bl.append(B("cyl", 0, 2.5, 0, 2.4, 5, 2.4, "#F2EFE6"))
        bl.append(B("sphere", 0, 5.2, 0, 9, 4.5, 9, C_RED))
        for x, z in ((-2, 1), (2.2, -0.5), (0, -2.2)):
            bl.append(B("sphere", x, 6.6, z, 1.2, 0.8, 1.2, "#F2F2F2"))
    elif tid == "heart":
        bl.append(B("sphere", -2.6, 6, 0, 5, 5, 3.4, C_RED))
        bl.append(B("sphere", 2.6, 6, 0, 5, 5, 3.4, C_RED))
        bl.append(B("pyramid", 0, 2.4, 0, 8.6, 5.2, 3.4, C_RED))
        bl.append(B("box", 0, 6.2, 0, 0.4, 8, 0.4, C_DWOOD))
    elif tid == "skyscraper":
        for i in range(10):
            bl.append(B("box", 0, 2 + i * 3.6, 0, 9, 3.6, 9, C_STONE if i % 3 else C_GREY))
            bl.append(B("box", 0, 3.4 + i * 3.6, 4.55, 7, 1.6, 0.1, C_WIN))
        bl.append(B("box", 0, 38.4, 0, 6, 1.2, 6, C_DWOOD))
        bl.append(B("cyl", 0, 41, 0, 0.3, 3.6, 0.3, C_COL))
    elif tid == "spaceship":
        bl.append(B("cyl", 0, 3.2, 0, 10, 1.6, 10, C_COL))
        bl.append(B("cyl", 0, 4.2, 0, 6.4, 0.8, 6.4, C_GREY))
        bl.append(B("dome", 0, 4.6, 0, 5, 3, 5, C_WIN))
        for x, z in ((-6, 0), (6, 0), (0, -6), (0, 6)):
            bl.append(B("cyl", x, 1.1, z, 0.5, 2.2, 0.5, C_DWOOD))
        bl.append(B("sphere", 0, 6.8, 0, 1, 1, 1, C_GLOW))
    elif tid == "shanghai":
        for i, x in enumerate((-8, 0, 8)):
            _house(bl, x, 0, w=7, d=12, wall=C_RED if i == 1 else C_GREY, floors=3)
        bl.append(B("box", 0, 0.4, 6.4, 24, 0.8, 3, C_STONE))
    elif tid == "xitai":
        bl.append(B("box", 0, 1, 0, 14, 2, 10, C_WOOD))
        for x in (-6, -2, 2, 6):
            bl.append(B("cyl", x, 4.5, -3.5, 0.5, 5, 0.5, C_RED))
        bl.append(B("box", 0, 7.1, 0, 15, 0.4, 11, C_DWOOD))
        bl.append(B("pyramid", 0, 9, 0, 16, 3, 12, C_ROOF))
        bl.append(B("box", 0, 4.6, 4.8, 10, 0.3, 0.2, C_RED))
    elif tid == "gulou":
        bl.append(B("box", 0, 2, 0, 12, 4, 12, C_RED))
        bl.append(B("box", 0, 6.2, 0, 10, 3.4, 10, C_RED))
        bl.append(B("cyl", 0, 8.2, 0, 1.4, 0.8, 1.4, C_DWOOD))
        bl.append(B("pyramid", 0, 10.6, 0, 13, 3.4, 13, C_DWOOD))
        bl.append(B("box", 0, 12.6, 0, 9, 2.6, 9, C_RED))
        bl.append(B("pyramid", 0, 15.2, 0, 10, 2.8, 10, C_DWOOD))
        bl.append(B("sphere", 0, 17.4, 0, 0.8, 0.8, 0.8, C_GOLD))
    else:  # gazebo / 兜底
        _gazebo_blocks(bl, 0, 0)
    name = f"民国{n}·{random.randint(1, 99):02d}"
    return {"name": name, "blocks": bl}


def _gazebo_blocks(bl, cx, cz, scale=1.0):
    s = 6 * scale
    bl.append(B("box", cx, 0.5 * scale, cz, s + 1, 1, s + 1, C_STONE))
    for dx in (-1, 1):
        for dz in (-1, 1):
            bl.append(B("cyl", cx + dx * s / 2.4, 3 * scale, cz + dz * s / 2.4,
                        0.35 * scale, 4 * scale, 0.35 * scale, C_RED))
    bl.append(B("pyramid", cx, 6.4 * scale, cz, s + 2, 2.6 * scale, s + 2, C_ROOF))
    bl.append(B("sphere", cx, 8.2 * scale, cz, 0.6 * scale, 0.6 * scale, 0.6 * scale, C_GOLD))


# 描述关键词 → 模板（顺序即优先级）
_DESC_KEYWORDS = [
    (("骑楼",), "qilou"), (("牌坊",), "paifang"), (("洋楼", "西式", "小洋"), "castle"),
    (("宝塔", "佛塔"), "pagoda"), (("戏台",), "xitai"), (("鼓楼",), "gulou"),
    (("庙", "寺", "祠"), "temple"), (("高塔", "塔楼", "钟塔"), "tower"),
    (("桥",), "bridge"), (("喷泉",), "fountain"), (("围墙", "院墙"), "wall"),
    (("花园",), "garden"), (("风车",), "windmill"), (("凉亭", "亭子"), "gazebo"),
    (("灯塔",), "lighthouse"), (("村落", "村子"), "village"), (("雕像", "塑像"), "statue"),
    (("金字塔",), "pyramid"), (("螺旋",), "spiral"), (("蘑菇",), "mushroom"),
    (("心",), "heart"), (("高楼", "大厦", "摩天"), "skyscraper"),
    (("飞船", "飞艇"), "spaceship"), (("上海", "外滩"), "shanghai"),
    (("塔",), "tower"), (("房", "屋", "民居", "宅"), "house"),
]
_COLOR_WORDS = (("青砖", C_GREY), ("灰砖", C_GREY), ("红砖", C_RED), ("木", C_WOOD), ("石", C_STONE))


def handle_generate(payload):
    template = (payload.get("template") or "").strip()
    desc = (payload.get("description") or "").strip()
    tid = None
    if template:
        tid = template if template in TEMPLATE_ZH else None
        if tid is None:
            return None, f"未知模板：{template}"
    elif desc:
        for keys, t in _DESC_KEYWORDS:
            if any(k in desc for k in keys):
                tid = t
                break
        if tid is None:
            tid = "house"
    else:
        return None, "缺少 description/template"

    # 三个英文模板优先读 StreamingAssets 同款（与场景内建筑一致）
    prebuilt = _t_templates().get(tid)
    if prebuilt and prebuilt.get("blocks"):
        building = {"name": prebuilt.get("name") or TEMPLATE_ZH[tid], "blocks": prebuilt["blocks"]}
    else:
        building = _gen(tid)
        if desc:  # 材质词染色（青砖老洋楼 → 青灰墙体）
            for word, color in _COLOR_WORDS:
                if word in desc:
                    _tint(building["blocks"], color)
                    building["name"] = desc if len(desc) <= 12 else f"{word}{TEMPLATE_ZH[tid]}"
                    break
    return building, None

# ── 委托系统 ─────────────────────────────────────────────────────────
# 委托题库：(template, 标题前缀, 委托描述)
_COMMISSION_FLAVOR = {
    "house": ("临河的人家", "河埠上游缺几户人家住着，添些烟火气。"),
    "castle": ("镇东的老洋楼", "镇东头留了块好地，就缺一栋气派的洋楼压阵。"),
    "tower": ("望风的塔", "站得高才看得远，镇上需要一座能望到河湾的塔。"),
    "pagoda": ("镇水的宝塔", "老辈人说塔能镇水，河边起了塔，行船才安稳。"),
    "qilou": ("下南洋的骑楼", "南边流行的骑楼好看又遮雨，街面上就缺这一排。"),
    "xitai": ("酬神的戏台", "庙会快到了，得有座戏台才请得动戏班。"),
    "gulou": ("报时的鼓楼", "晨钟暮鼓，镇上缺一座管时辰的鼓楼。"),
    "temple": ("香火的庙宇", "镇民凑了香火钱，就等着把庙宇起起来。"),
    "bridge": ("连通两岸的桥", "河对岸的地荒着，缺一座桥把它接进来。"),
    "fountain": ("街心的喷泉", "广场正中空落落的，来座喷泉就热闹了。"),
    "gazebo": ("歇脚的凉亭", "官道进镇还有段路，旅人盼个歇脚的凉亭。"),
    "lighthouse": ("指路的灯塔", "夜里行船看不清航道，河口该有座灯塔。"),
    "tree": ("绿荫的树", "夏日毒得很，街边多种几棵大树才好乘凉。"),
    "statue": ("镇标的雕像", "镇口要立一座像样的雕像，让外乡人一眼记住这儿。"),
    "wall": ("护镇的围墙", "年起风沙大，把镇子围起来才踏实。"),
    "windmill": ("磨坊的风车", "新磨坊选址定了，就差一架大风车。"),
    "garden": ("游园", "镇上富户凑份子，想修座像样的游园。"),
}
_GRADES = (("大匠", 1.8), ("佳作", 1.3), ("合格", 1.0))


def _new_commission(npc, npc_pos):
    pool = [t for t in _COMMISSION_FLAVOR if t in _STATE["unlocked"]]
    template = random.choice(pool or list(_COMMISSION_FLAVOR))
    label = TEMPLATE_ZH[template]
    pre, flavor = _COMMISSION_FLAVOR[template]
    diff = random.randint(1, 3)
    min_blocks = 8 + diff * 6
    min_size = 8 + diff * 2
    radius = 30 + diff * 5
    cid = f"c{int(time.time() * 1000) % 10 ** 9:09d}"
    return {
        "id": cid, "npc": npc, "type": template, "typeLabel": label,
        "title": f"{pre}·{label}",
        "desc": f"{flavor}要求：{label}样式（或照着描述建）、方块 ≥ {min_blocks}、占地 ≥ {min_size} 米，"
                f"建在绿圈内（你附近 {radius} 米）。",
        "minBlocks": min_blocks, "minSize": float(min_size),
        "zoneX": round(npc_pos[0], 2), "zoneZ": round(npc_pos[2], 2), "zoneRadius": radius,
        "rewardGold": 10 + diff * 8,
        "unlock": random.choice([t for t in LOCKED_DEFAULT if t not in _STATE["unlocked"]]) \
            if any(t not in _STATE["unlocked"] for t in LOCKED_DEFAULT) else "",
        "difficulty": diff,
    }


def handle_commission_new(payload):
    npc = payload.get("npc", "")
    pos = payload.get("npcPos") or [0, 0, 0]
    if _STATE.get("active"):
        return {"ok": False, "error": "已有进行中的委托，先完成或放弃", "state": _state_payload()}
    commission = _new_commission(npc or "镇民", pos)
    _STATE["active"] = commission
    _save_state()
    return {"ok": True, "commission": commission, "state": _state_payload()}


def _grade_of(bc, min_blocks, size, min_size):
    ratio = min(bc / max(1, min_blocks), size / max(1.0, min_size))
    for grade, th in _GRADES:
        if ratio >= th:
            return grade
    return "合格"


def handle_commission_submit(payload):
    active = _STATE.get("active")
    if not active:
        return {"ok": False, "error": "当前没有进行中的委托"}
    builds = payload.get("builds") or []
    zone_center = payload.get("zoneCenter")
    zx = float(zone_center[0]) if zone_center else float(active["zoneX"])
    zz = float(zone_center[1]) if zone_center else float(active["zoneZ"])
    radius = float(active["zoneRadius"])

    best, best_score = None, -1.0
    for b in builds:
        pos = b.get("pos") or [0, 0, 0]
        ext = b.get("extents") or [0, 0, 0]
        dist = math.hypot(float(pos[0]) - zx, float(pos[2]) - zz)
        in_zone = dist <= radius + 2.0
        bc = int(b.get("blockCount") or 0)
        size = 2.0 * max(float(ext[0]), float(ext[2]))
        type_ok = (b.get("template") == active["type"]) or \
                  (active["typeLabel"] in (b.get("description") or "")) or \
                  (active["typeLabel"] in (b.get("name") or ""))
        score = (int(in_zone) * 1000 + int(type_ok) * 500 + bc) - dist * 0.1
        if score > best_score:
            best = {"name": b.get("name") or "无名建筑", "bc": bc, "size": size,
                    "in_zone": in_zone, "type_ok": type_ok, "dist": dist}
            best_score = score

    reasons = []
    if best is None:
        reasons.append("没有可验收的建筑——先在绿圈内建一栋")
    else:
        if not best["in_zone"]:
            reasons.append(f"最近建筑离绿圈中心 {best['dist']:.0f} 米（要求 {radius:.0f} 米内）")
        if best["bc"] < active["minBlocks"]:
            reasons.append(f"方块数不足：{best['bc']}/{active['minBlocks']}")
        if best["size"] < active["minSize"]:
            reasons.append(f"占地不足：{best['size']:.0f}/{active['minSize']:.0f} 米")
        if not best["type_ok"]:
            reasons.append(f"建筑类型不符：需要「{active['typeLabel']}」样式")

    if reasons:
        return {"ok": True, "pass": False, "grade": "", "buildName": best["name"] if best else "",
                "comment": f"「{active['title']}」还没到时候，{active['npc']} 摇了摇头。",
                "reasons": reasons, "rewardGold": 0, "rewardProsperity": 0, "unlocked": "",
                "state": _state_payload()}

    grade = _grade_of(best["bc"], active["minBlocks"], best["size"], active["minSize"])
    gold = int(active["rewardGold"]) * (2 if grade == "大匠" else 1 if grade == "佳作" else 1)
    prosperity = 4 + int(active["difficulty"]) * 4 + (4 if grade == "大匠" else 0)
    unlocked = active.get("unlock") or ""
    _STATE["gold"] = int(_STATE["gold"]) + gold
    _STATE["prosperity"] = int(_STATE["prosperity"]) + prosperity
    _STATE["completed"] = int(_STATE["completed"]) + 1
    if unlocked and unlocked not in _STATE["unlocked"]:
        _STATE["unlocked"].append(unlocked)
    _STATE["active"] = None
    _save_state()
    comments = {
        "大匠": f"{active['npc']} 绕着「{best['name']}」走了三圈，抚掌大笑：好手艺！镇志上得记你一笔！",
        "佳作": f"{active['npc']} 打量着「{best['name']}」连连点头：有模有样，街坊们都夸呢。",
        "合格": f"{active['npc']} 查验完「{best['name']}」：嗯，交得了差。辛苦钱收好。",
    }
    return {"ok": True, "pass": True, "grade": grade, "buildName": best["name"],
            "comment": comments[grade], "reasons": [],
            "rewardGold": gold, "rewardProsperity": prosperity, "unlocked": unlocked,
            "state": _state_payload()}


def handle_commission_abandon():
    had = bool(_STATE.get("active"))
    _STATE["active"] = None
    _save_state()
    return {"ok": had, "state": _state_payload()}


# ── HTTP 路由 ─────────────────────────────────────────────────────────
class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send(self, obj, code=200):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):  # 安静日志：只留错误与关键请求
        if args and (" 4" in str(args[0])[:3] or " 5" in str(args[0])[:3]):
            super().log_message(fmt, *args)

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path, query = parsed.path, urllib.parse.parse_qs(parsed.query)
        try:
            if path == "/health":
                self._send({"ok": True, "service": "ai-town-server"})
            elif path == "/api/intro/line":
                self._send({"line": handle_intro_line()})
            elif path == "/api/npc/memory":
                name = query.get("name", [""])[0]
                self._send({"memory": [f"{w}: {t}" for w, t in _MEMORY.get(name, [])]})
            elif path == "/api/commission/state":
                self._send({"ok": True, "state": _state_payload()})
            else:
                self._send({"error": f"未知接口 {path}"}, 404)
        except Exception as e:
            traceback.print_exc()
            self._send({"error": str(e)}, 500)

    def do_POST(self):
        path = urllib.parse.urlparse(self.path).path
        try:
            length = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(length) if length else b"{}"
            payload = json.loads(raw.decode("utf-8") or "{}")
        except Exception as e:
            self._send({"error": f"请求体解析失败: {e}"}, 400)
            return
        try:
            if path == "/api/generate_json":
                building, err = handle_generate(payload)
                self._send({"building": building, "error": err}, 200 if err is None else 400)
            elif path == "/api/npc/chat":
                reply, err = handle_npc_chat(payload.get("name", ""), payload.get("message", ""))
                self._send({"name": payload.get("name", ""), "reply": reply or "", "error": err},
                           200 if err is None else 400)
            elif path == "/api/commission/new":
                self._send(handle_commission_new(payload))
            elif path == "/api/commission/submit":
                self._send(handle_commission_submit(payload))
            elif path == "/api/commission/abandon":
                self._send(handle_commission_abandon())
            else:
                self._send({"error": f"未知接口 {path}"}, 404)
        except Exception as e:
            traceback.print_exc()
            self._send({"error": str(e)}, 500)


def main():
    random.seed()
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    print(f"[ai-town] 监听 http://{HOST}:{PORT}  (LLM: {'已配置 ' + LLM_MODEL if LLM_BASE and LLM_KEY else '未配置，使用规则回复'})")
    print("[ai-town] 接口: generate_json / npc/chat / intro/line / commission(state|new|submit|abandon)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[ai-town] 已停止")


if __name__ == "__main__":
    main()
