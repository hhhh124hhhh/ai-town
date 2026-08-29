# -*- coding: utf-8 -*-
"""委托系统逻辑测试：发单→错误提交→正确提交→里程碑→放弃。直接调 CommissionManager，不走 HTTP。"""
import io
import sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

from commission_ai import CommissionManager, _resolve_type

m = CommissionManager()
ok = fail = 0


def check(label, cond, extra=""):
    global ok, fail
    if cond:
        ok += 1
        print(f"  PASS {label} {extra}")
    else:
        fail += 1
        print(f"  FAIL {label} {extra}")


print("== 类型解析 ==")
check("描述'建一个大房屋'→house", _resolve_type("", "建一个大房屋") == "house")
check("描述'建一个红色大城堡'→castle", _resolve_type("", "建一个红色大城堡") == "castle")
check("template windmill→windmill", _resolve_type("windmill", "") == "windmill")
check("template rocket→spaceship", _resolve_type("rocket", "") == "spaceship")
check("乱模板→None", _resolve_type("ta城堡", "") is None)

print("== 发单（老王第1单=烤炉房 house）==")
c, err = m.new("面包师老王", [3.75, 0, -4.2])
check("发单成功", c is not None and err is None, err or c["title"])
check("类型 house", c["type"] == "house")
check("zoneCenter=NPC坐标", abs(c["zoneX"] - 3.75) < 0.01 and abs(c["zoneZ"] + 4.2) < 0.01)
check("初始半径18", c["zoneRadius"] == 18.0)
check("有风味文本", len(c["desc"]) > 5)
c2, err2 = m.new("守卫铁山", [0, 0, -3.2])
check("进行中不可再发单", c2 is None and "进行中" in (err2 or ""))

print("== 提交：类型错+距离远 → 不过 ==")
r, e = m.submit([
    {"name": "红色城堡", "description": "建一个红色大城堡", "template": "",
     "blockCount": 18, "pos": [40, 0, 40], "extents": [11, 9, 11]},
])
check("返回结果", r is not None and e is None)
check("未通过", r["pass"] is False)
check("reasons 指出类型/距离", any("类型" in x for x in r["reasons"]) and any("距离" in x for x in r["reasons"]), r["reasons"])
check("无奖励", r["rewardGold"] == 0)

print("== 提交：正确（大房屋 建在 NPC 附近）→ 过 ==")
r, e = m.submit([
    {"name": "红色城堡", "description": "建一个红色大城堡", "template": "",
     "blockCount": 18, "pos": [40, 0, 40], "extents": [11, 9, 11]},  # 干扰项：应被忽略
    {"name": "大房屋", "description": "建一个大的房屋", "template": "",
     "blockCount": 12, "pos": [5.5, 0, -2.0], "extents": [10, 5, 10]},
])
check("通过（多建筑取最优）", r["pass"] is True, r["reasons"])
check("评级 S（占地10>=8.4, 方块12>=9）", r["grade"] == "S", r["grade"])
check("金币 60+30", r["rewardGold"] == 90, r["rewardGold"])
check("繁荣 +100", r["rewardProsperity"] == 100)
s = r["state"]
check("state.completed=1", s["completed"] == 1)
check("好感+10=10 熟识", s["npcs"][0]["affinity"] == 10 and s["npcs"][0]["affinityLabel"] == "熟识")
check("active 已清空", s["active"] is None)
check("NPC 记忆已写入", any("委托" in mm.get("user", "") for mm in __import__("npc_ai").manager.npcs["面包师老王"]["memory"]))

print("== 铁山第1单=瞭望塔 tower，半径随完成数收紧 18-2=16 ==")
c, err = m.new("守卫铁山", [0, 0, -3.2])
check("发单成功", c is not None, err or "")
check("类型 tower", c["type"] == "tower")
check("半径16", c["zoneRadius"] == 16.0, c["zoneRadius"])
r, e = m.submit([{"name": "高塔", "description": "", "template": "tower",
                  "blockCount": 3, "pos": [0, 0, -10], "extents": [6, 25, 6]}])
check("模板通道 tower 通过", r["pass"] is True, r["reasons"])
check("繁荣120→等级2", r["state"]["level"] == 2, r["state"]["level"])

print("== 放弃 ==")
c, err = m.new("面包师老王", [3.75, 0, -4.2])
check("第2单=花园", c["title"] == "香料花园", c["title"])
res, err = m.abandon()
check("放弃成功", res is not None and res["abandoned"] == "香料花园")
res2, err2 = m.abandon()
check("无单可放弃", res2 is None and err2)

print("== 锁定/解锁表 ==")
s = m.state()
check("lockedDefault 9 项", len(s["lockedDefault"]) == 9)

print(f"\n结果: {ok} pass / {fail} fail")
sys.exit(1 if fail else 0)
