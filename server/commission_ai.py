"""ai-town 委托建造系统：NPC 发单 → 玩家建造 → 规则验收 + LLM 点评。

核心循环（演示主线）：
  请求委托(/new) → Tab 面板建造 → 提交验收(/submit) → 判分发奖 → NPC 记忆写入。

验收是确定性的规则判分（类型/占地/方块数/距离），LLM 只负责风味文本
（发单话术 + 角色化点评），无 Key 时走固定话术，离线演示完整可玩。
"""
import json
import math
import os

from nlp import parse_input
from npc_ai import manager as npc_manager, llm_available, call_llm_chat

# ── 状态持久化（2026-08-29：python 重启清零曾致 UI 谎报有单/引导丢失）────────
# 保存文件与脚本同目录（server/state.json）；每个写操作后落盘，启动时恢复。
# 只持久化可安全 JSON 化的游戏进度（active/gold/prosperity/completed/affinity/
# unlocked/_issued/_next_id），NPC 记忆仍归 npc_ai 管（其自有体系）。
_STATE_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "state.json")


def _load_saved_state():
    try:
        with open(_STATE_PATH, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None  # 首次运行/文件损坏：从零开始（容错优先）

# 初始锁定的模板（委托奖励 + 繁荣度里程碑解锁）
LOCKED_DEFAULT = ["temple", "lighthouse", "windmill", "xitai", "gulou", "paifang", "skyscraper", "spaceship", "shanghai"]
# 繁荣度里程碑解锁：等级 → 模板（民国世界观：戏台/鼓楼为终点）
MILESTONE_UNLOCK = {3: "xitai", 4: "gulou", 5: "paifang"}

PROSPERITY_LEVELS = [
    (0, "荒地聚落"), (100, "边陲小村"), (250, "热闹小镇"), (450, "繁荣市镇"), (700, "传奇之城"),
]

# 每个 NPC 的委托序列（按发单次数循环取用）
ARCHETYPES = {
    "面包师老王": [
        {"title": "新烤炉房", "type": "house", "type_label": "房屋", "min_blocks": 6, "min_size": 6.0,
         "reward_gold": 60, "unlock": "",
         "hint": "老王的烤炉塌了！想要一座宽敞的新房屋当烤炉房，占地至少 6 米，就建在我小屋旁边。"},
        {"title": "香料花园", "type": "garden", "type_label": "花园", "min_blocks": 6, "min_size": 6.0,
         "reward_gold": 80, "unlock": "",
         "hint": "老王想种罗勒和迷迭香，给我围一座花园吧，占地至少 6 米，建在我附近才好照看。"},
        {"title": "老磨坊", "type": "windmill", "type_label": "风车", "min_blocks": 4, "min_size": 4.0,
         "reward_gold": 100, "unlock": "windmill",
         "hint": "面粉快磨不过来了！建一座风车磨坊，我来教你风车模板的手艺（解锁模板）。"},
        {"title": "镇心喷泉", "type": "fountain", "type_label": "喷泉", "min_blocks": 4, "min_size": 6.0,
         "reward_gold": 80, "unlock": "",
         "hint": "广场太干了，建一座喷泉，镇民等面包时也有个看头。占地至少 6 米，建在我附近。"},
        {"title": "运货小桥", "type": "bridge", "type_label": "桥", "min_blocks": 3, "min_size": 8.0,
         "reward_gold": 90, "unlock": "",
         "hint": "运面粉的车得绕远路，建一座桥吧，跨度至少 8 米，就在我这片区。"},
    ],
    "守卫铁山": [
        {"title": "南瞭望塔", "type": "tower", "type_label": "高塔", "min_blocks": 3, "min_size": 4.0,
         "reward_gold": 60, "unlock": "",
         "hint": "南边是视野死角。建一座高塔当瞭望塔，占地至少 4 米，建在洋楼这片防区。"},
        {"title": "新城墙", "type": "wall", "type_label": "围墙", "min_blocks": 4, "min_size": 10.0,
         "reward_gold": 80, "unlock": "",
         "hint": "木栅栏挡不住野猪。砌一段围墙，长度至少 10 米，就建在洋楼附近。"},
        {"title": "巷口灯塔", "type": "lighthouse", "type_label": "灯塔", "min_blocks": 4, "min_size": 4.0,
         "reward_gold": 100, "unlock": "lighthouse",
         "hint": "夜里巡逻最怕看不见路。码头那边正缺一座灯塔，图纸我给你（解锁模板）。"},
        {"title": "英雄纪念像", "type": "statue", "type_label": "雕像", "min_blocks": 4, "min_size": 3.0,
         "reward_gold": 80, "unlock": "",
         "hint": "老队长牺牲十年了。立一座雕像纪念他，建在洋楼附近，让每个路过的人都看见。"},
        {"title": "关帝庙", "type": "temple", "type_label": "庙宇", "min_blocks": 8, "min_size": 8.0,
         "reward_gold": 120, "unlock": "temple",
         "hint": "镇长说镇里该有座庙了。占地至少 8 米，建在洋楼防区内。图纸我给你（解锁模板）。"},
    ],
}

# json_gen 支持的全部类型（template 参数合法性校验 + rocket 归一化）
KNOWN_TYPES = {
    "castle", "house", "tower", "pagoda", "pyramid", "bridge", "temple", "fountain",
    "lighthouse", "wall", "tree", "spaceship", "rocket", "mushroom", "heart", "sphere",
    "statue", "garden", "windmill", "gazebo", "skyscraper", "village", "spiral", "shanghai",
    "qilou", "paifang", "xitai", "gulou",
}


def _affinity_label(v):
    if v >= 25:
        return "挚友"
    if v >= 10:
        return "熟识"
    return "陌生"


def _prosperity_level(p):
    lvl, name = 1, PROSPERITY_LEVELS[0][1]
    for i, (th, n) in enumerate(PROSPERITY_LEVELS):
        if p >= th:
            lvl, name = i + 1, n
    return lvl, name


def _resolve_type(template, description):
    """建筑实际类型：template 直用（rocket 归一为 spaceship），描述走 nlp 重解析。"""
    t = str(template or "").strip()
    if t:
        return "spaceship" if t == "rocket" else (t if t in KNOWN_TYPES else None)
    d = str(description or "").strip()
    if d:
        got = parse_input(d).get("type")
        return "spaceship" if got == "rocket" else got
    return None


class CommissionManager:
    def __init__(self):
        self.active = None
        self.completed = 0
        self.gold = 0
        self.prosperity = 0
        self.affinity = {}      # npc -> int
        self.unlocked = []      # 已解锁模板
        self._issued = {}       # npc -> 发单次数
        self._next_id = 1
        self._load()            # 有存档则恢复（服务器重启不再丢单）

    # ── 持久化 ───────────────────────────────────────────
    def _save(self):
        try:
            data = {
                "active": self.active,
                "completed": self.completed,
                "gold": self.gold,
                "prosperity": self.prosperity,
                "affinity": self.affinity,
                "unlocked": self.unlocked,
                "issued": self._issued,
                "nextId": self._next_id,
            }
            tmp = _STATE_PATH + ".tmp"  # 原子写：先 tmp 后 rename，防写一半损坏
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=1)
            os.replace(tmp, _STATE_PATH)
        except OSError:
            pass  # 落盘失败不阻断游戏（内存态仍可用，下一写操作再试）

    def _load(self):
        data = _load_saved_state()
        if not data:
            return
        try:
            # 2026-08-29 修复：不恢复 active 委托（开局干净，玩家主动找 NPC 接）
            # 只恢复进度数据（completed/gold/prosperity/affinity/unlocked）
            self.active = None  # 强制清空，不恢复
            self.completed = int(data.get("completed", 0))
            self.gold = int(data.get("gold", 0))
            self.prosperity = int(data.get("prosperity", 0))
            self.affinity = {k: int(v) for k, v in data.get("affinity", {}).items()}
            self.unlocked = list(data.get("unlocked", []))
            self._issued = {k: int(v) for k, v in data.get("issued", {}).items()}
            self._next_id = int(data.get("nextId", 1))
        except (TypeError, ValueError):
            pass  # 存档字段异常：用默认值，别让坏档挡启动

    # ── 状态 ─────────────────────────────────────────────
    def state(self):
        lvl, lname = _prosperity_level(self.prosperity)
        return {
            "gold": self.gold,
            "prosperity": self.prosperity,
            "level": lvl,
            "levelName": lname,
            "completed": self.completed,
            "unlocked": list(self.unlocked),
            "lockedDefault": list(LOCKED_DEFAULT),
            "npcs": [
                {"name": n, "role": npc["role"], "affinity": self.affinity.get(n, 0),
                 "affinityLabel": _affinity_label(self.affinity.get(n, 0))}
                for n, npc in npc_manager.npcs.items()
            ],
            "active": dict(self.active) if self.active else None,
        }

    # ── 发单 ─────────────────────────────────────────────
    def new(self, npc_name, npc_pos):
        npc = npc_manager.npcs.get(npc_name)
        if not npc:
            return None, f"找不到 NPC：{npc_name}"
        if self.active:
            return None, f"已有进行中的委托「{self.active['title']}」（先完成或放弃）"

        seq = ARCHETYPES.get(npc_name)
        if not seq:
            return None, f"{npc_name} 目前没有委托可发"
        idx = self._issued.get(npc_name, 0)
        self._issued[npc_name] = idx + 1
        a = seq[idx % len(seq)]

        x, z = float(npc_pos[0]), float(npc_pos[2])
        # 难度随完成数收紧：验收半径 25m → 最低 12m（2026-08-29 从 18m 增大，场景建筑多需要更大空间）
        radius = max(25 - 2 * self.completed, 12)
        c = {
            "id": f"c{self._next_id:03d}",
            "npc": npc_name,
            "title": a["title"],
            "desc": self._flavor_desc(npc, a, radius),
            "type": a["type"],
            "typeLabel": a["type_label"],
            "minBlocks": a["min_blocks"],
            "minSize": a["min_size"],
            "zoneX": x, "zoneZ": z,
            "zoneRadius": float(radius),
            "rewardGold": a["reward_gold"],
            "unlock": a.get("unlock", ""),
            "difficulty": self.completed + 1,
        }
        self._next_id += 1
        self.active = c
        self._save()
        return c, None

    def _flavor_desc(self, npc, a, radius):
        if llm_available():
            try:
                sys = (
                    f"你是 {npc['name']}，AI 小镇里的{npc['role']}。性格：{npc['personality']}\n"
                    "玩家是一名建造师，你想委托他帮你建东西。用第一人称说 2-3 句发单的话，"
                    "自然地提清要求（要建什么、占地大小、建在你附近），口语化，别用列表。"
                )
                user = (f"委托：{a['title']}（{a['type_label']}），占地至少 {a['min_size']:.0f} 米，"
                        f"建在我 {radius:.0f} 米范围内。")
                return call_llm_chat(sys, user)
            except Exception:
                pass  # LLM 失败回退固定话术
        return a["hint"]

    # ── 验收 ─────────────────────────────────────────────
    def submit(self, builds, zone_center=None):
        if not self.active:
            return None, "当前没有进行中的委托（先向 NPC 请求委托）"
        if not builds:
            return None, "接单后还没有建造任何建筑（用 Tab 面板生成）"

        c = self.active
        # 绿圈跟随建筑落位：客户端上报的放置点覆盖判分圆心（重复提交口径一致）
        if zone_center and len(zone_center) >= 2:
            try:
                c["zoneX"], c["zoneZ"] = float(zone_center[0]), float(zone_center[1])
            except (TypeError, ValueError):
                pass
        best = None  # (score, reasons, entry, facts)
        for b in builds:
            got, reasons, score, facts = self._judge_one(c, b)
            if best is None or score > best[0]:
                best = (score, reasons, b, facts, got)
        score, reasons, entry, facts, got_type = best
        passed = all(not r.startswith("✗") for r in reasons)

        result = {
            "pass": passed,
            "grade": "F",
            "comment": "",
            "reasons": reasons,
            "buildName": str(entry.get("name", "")),
            "rewardGold": 0,
            "rewardProsperity": 0,
            "unlocked": "",
        }

        if passed:
            result["grade"] = "S" if facts["size"] >= c["minSize"] * 1.4 and \
                facts["blocks"] >= c["minBlocks"] * 1.5 else "A"
            result["rewardGold"] = c["rewardGold"] + (30 if result["grade"] == "S" else 0)
            result["rewardProsperity"] = 100 if result["grade"] == "S" else 60
            self._settle(c, result)
        else:
            result["comment"] = self._flavor_comment(c["npc"], c, got_type, facts, "F")

        result["state"] = self.state()
        return result, None

    def _judge_one(self, c, b):
        got_type = _resolve_type(b.get("template"), b.get("description"))
        try:
            blocks = int(b.get("blockCount", 0))
            pos = [float(v) for v in b.get("pos", [0, 0, 0])]
            ext = [float(v) for v in b.get("extents", [0, 0, 0])]
        except (TypeError, ValueError):
            return None, ["✗ 建筑数据不完整"], -1, {}

        size = max(ext[0], ext[2]) if len(ext) >= 3 else 0
        dist = math.hypot(pos[0] - c["zoneX"], pos[2] - c["zoneZ"]) if len(pos) >= 3 else 1e9
        facts = {"size": size, "blocks": blocks, "dist": dist, "type": got_type}

        reasons, score = [], 0
        if got_type == c["type"]:
            reasons.append(f"✓ 类型：{c['typeLabel']}")
            score += 2
        else:
            shown = got_type or "未识别"
            reasons.append(f"✗ 类型不符：建的是「{shown}」，要求「{c['typeLabel']}」")
        if size >= c["minSize"] - 0.5:
            reasons.append(f"✓ 占地 {size:.1f}m ≥ {c['minSize']:.0f}m")
            score += 1
        else:
            reasons.append(f"✗ 占地 {size:.1f}m < {c['minSize']:.0f}m（输入\"大\"字可放大）")
        if blocks >= c["minBlocks"]:
            reasons.append(f"✓ 方块数 {blocks} ≥ {c['minBlocks']}")
            score += 1
        else:
            reasons.append(f"✗ 方块数 {blocks} < {c['minBlocks']}")
        if dist <= c["zoneRadius"]:
            reasons.append(f"✓ 距离 {dist:.0f}m ≤ {c['zoneRadius']:.0f}m")
            score += 2
        else:
            reasons.append(f"✗ 距离 {dist:.0f}m 超出 {c['zoneRadius']:.0f}m（要建在绿圈内）")
        return got_type, reasons, score, facts

    def _settle(self, c, result):
        """发奖 + 繁荣度里程碑解锁 + 写入 NPC 记忆（后续对话可引用这单）。"""
        npc_name = c["npc"]
        self.gold += result["rewardGold"]
        self.prosperity += result["rewardProsperity"]
        self.completed += 1
        self.affinity[npc_name] = self.affinity.get(npc_name, 0) + 10

        if c.get("unlock") and c["unlock"] not in self.unlocked:
            self.unlocked.append(c["unlock"])
            result["unlocked"] = c["unlock"]

        old_lvl, _ = _prosperity_level(self.prosperity - result["rewardProsperity"])
        new_lvl, _ = _prosperity_level(self.prosperity)
        m = MILESTONE_UNLOCK.get(new_lvl)
        if new_lvl > old_lvl and m and m not in self.unlocked:
            self.unlocked.append(m)
            result["unlocked"] = (result["unlocked"] + "," + m) if result["unlocked"] else m

        result["comment"] = self._flavor_comment(npc_name, c, c["type"],
                                                 {"size": 0, "blocks": 0, "dist": 0},
                                                 result["grade"], settled=True)

        # NPC 记忆：这单交付对话，之后闲聊 NPC 能"记得"
        npc = npc_manager.npcs.get(npc_name)
        if npc is not None:
            npc["memory"].append({"user": f"我完成了你的委托「{c['title']}」！",
                                  "npc": result["comment"]})
            if len(npc["memory"]) > 50:
                npc["memory"] = npc["memory"][-50:]

        self.active = None
        self._save()

    def _flavor_comment(self, npc_name, c, got_type, facts, grade, settled=False):
        npc = npc_manager.npcs.get(npc_name)
        if npc is None:
            return "……"
        if llm_available():
            try:
                sys = (
                    f"你是 {npc['name']}，AI 小镇里的{npc['role']}。性格：{npc['personality']}\n"
                    "玩家刚为你建完委托的建筑，请你对交付结果做 2-3 句角色化点评，"
                    "提到具体的建筑和数据，口语化。"
                )
                if settled:
                    user = (f"委托「{c['title']}」验收通过，评级 {grade}，"
                            f"占地 {facts.get('size', 0):.0f}m、{facts.get('blocks', 0)} 个方块、"
                            f"离我 {facts.get('dist', 0):.0f} 米。酬劳 {c['rewardGold']} 大洋。"
                            "另外镇上的路工已经自动把一条青石板路从主街铺到了新建筑门口，"
                            "点评里顺口提一句这条路。")
                else:
                    user = (f"委托「{c['title']}」（要求{c['typeLabel']}）验收没过："
                            f"玩家建的是「{got_type or '未识别'}」。委婉点出问题，鼓励他重试。"
                            "不要骂人。")
                return call_llm_chat(sys, user)
            except Exception:
                pass
        # 离线回退话术（修路是自动接路系统送的，点评里点一句）
        if settled:
            if grade == "S":
                return f"好家伙！这「{c['title']}」比我梦里想的还气派！连青石板路都一路铺到我门口喽，大洋收好。"
            return f"成了！「{c['title']}」总算建起来了，门口那条新路我改天就带镇民走两步。大洋拿好。"
        return f"嗯……这不是我要的{c['typeLabel']}呀。再试一次？要求我再说一遍都行。"

    # ── 放弃 ─────────────────────────────────────────────
    def abandon(self):
        if not self.active:
            return None, "当前没有进行中的委托"
        title = self.active["title"]
        self.active = None
        self._save()
        return {"abandoned": title, "state": self.state()}, None


manager = CommissionManager()
