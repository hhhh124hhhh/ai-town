"""ai-town NPC AI 模块：角色对话 + 短期记忆。

LLM 通道（可选）：读取 llm_config.json 或环境变量，OpenAI 兼容接口（DeepSeek 等）。
无 Key 时回退到角色化规则回复（仍带记忆，保证离线演示完整）。

配置文件 server/llm_config.json：
{
  "api_key": "sk-...",
  "base_url": "https://api.deepseek.com/v1",
  "model": "deepseek-chat"
}
"""
import json
import os
import re
import subprocess

CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "llm_config.json")
MEMORY_LIMIT = 50


def _load_config():
    cfg = {}
    if os.path.exists(CONFIG_PATH):
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                cfg = json.load(f)
        except (ValueError, OSError):
            pass
    return {
        "api_key": cfg.get("api_key") or os.environ.get("DEEPSEEK_API_KEY") or os.environ.get("LLM_API_KEY") or "",
        "base_url": cfg.get("base_url", "https://api.deepseek.com/v1"),
        "model": cfg.get("model", "deepseek-chat"),
    }


def llm_available():
    return bool(_load_config()["api_key"])


def call_llm_chat(system_prompt, user_message, history=None):
    """通用对话调用（OpenAI 兼容）。失败抛异常。"""
    cfg = _load_config()
    messages = [{"role": "system", "content": system_prompt}]
    for h in (history or [])[-10:]:
        messages.append({"role": "user", "content": h.get("user", "")})
        messages.append({"role": "assistant", "content": h.get("npc", "")})
    messages.append({"role": "user", "content": user_message})

    payload = {"model": cfg["model"], "messages": messages,
               "temperature": 0.8, "max_tokens": 300, "stream": False}
    url = cfg["base_url"].rstrip("/") + "/chat/completions"
    curl_cmd = ["curl", "-s", "-X", "POST", url,
                "-H", "Content-Type: application/json",
                "-H", f"Authorization: Bearer {cfg['api_key']}",
                "-d", json.dumps(payload, ensure_ascii=False),
                "--connect-timeout", "10", "--max-time", "60"]
    result = subprocess.run(curl_cmd, capture_output=True, text=True, timeout=70,
                            encoding="utf-8", errors="replace")
    resp = json.loads(result.stdout)
    if "error" in resp:
        raise RuntimeError(resp["error"].get("message", str(resp["error"]))[:200])
    return resp["choices"][0]["message"]["content"].strip()


class NPCManager:
    def __init__(self):
        self.npcs = {}

    def register_npc(self, name, role, personality, greeting):
        self.npcs[name] = {
            "name": name, "role": role, "personality": personality,
            "greeting": greeting, "memory": [],
        }

    def get(self, name):
        return self.npcs.get(name)

    def chat(self, name, user_message):
        npc = self.npcs.get(name)
        if not npc:
            return f"（找不到 {name}）", False

        reply = None
        if llm_available():
            try:
                system_prompt = self._build_prompt(npc)
                reply = call_llm_chat(system_prompt, user_message, npc["memory"])
            except Exception as e:
                reply = self._fallback(npc, user_message) + f"（大模型暂不可用: {str(e)[:60]}）"
        else:
            reply = self._fallback(npc, user_message) + "（离线模式，配置 llm_config.json 可接入大模型）"

        npc["memory"].append({"user": user_message, "npc": reply})
        if len(npc["memory"]) > MEMORY_LIMIT:
            npc["memory"] = npc["memory"][-MEMORY_LIMIT:]
        return reply, llm_available()

    def get_memory(self, name):
        npc = self.npcs.get(name)
        return npc["memory"] if npc else []

    def _build_prompt(self, npc):
        mem_lines = "\n".join(
            f"玩家：{m['user']}\n{npc['name']}：{m['npc']}" for m in npc["memory"][-10:]
        ) or "（暂无）"
        return (
            f"你是 {npc['name']}，AI 小镇里的{npc['role']}。\n"
            f"性格：{npc['personality']}\n"
            "用中文回答，保持角色设定，每次回答不超过 3 句话，口语化自然。\n"
            f"最近的对话记忆：\n{mem_lines}"
        )

    # ── 离线规则回退：带记忆引用的角色化回复 ─────────────────────
    def _fallback(self, npc, user_message):
        text = user_message.strip()
        mem = npc["memory"]

        # 记忆回问：玩家问"刚才/记得"时引用上一轮
        if re.search(r"刚才|记得|上次|之前|聊了什么", text):
            if mem:
                last = mem[-1]
                return f"当然记得！你刚才对我说「{last['user'][:20]}」，我回了你「{last['npc'][:24]}」。我这记性可好了。"
            return "我们还没聊过什么呢，这是你跟我说的第一句话。"

        if re.search(r"你好|您好|hi|hello|嗨", text, re.I):
            return npc["greeting"]

        # 角色专属话题
        if npc["role"] == "茶馆掌柜" and re.search(r"烧饼|茶|吃|喝|点心|买", text):
            return "刚出炉的芝麻烧饼还烫手呢！配一壶茉莉花茶，两个铜板管够。坐下慢慢喝？"
        if npc["role"] == "巡捕" and re.search(r"洋楼|城堡|安全|进|门|参观|夜", text):
            return "那栋红砖洋楼是镇口的老建筑，从前是租界的货栈。白天随便看，天黑后这条街归我巡。有事找我，巡捕房就在街口。"
        if npc["role"] == "老板娘" and re.search(r"包子|豆浆|油条|吃|早点|饿", text):
            return "热豆浆刚磨好，油条炸得金黄！两个铜板一套，坐下吃，管饱。年轻人饿着肚子可走不动远路。"
        if npc["role"] == "账房先生" and re.search(r"账|钱|价|生意|买卖|多少", text):
            return "账上的事问我准没错。这镇上谁家买地、谁家盖房，进项出项我都记着呢。想打听哪一笔？"

        if re.search(r"你是谁|名字|做什么", text):
            return f"我是{npc['name']}，{npc['role']}。{npc['personality']}"

        return (
            f"（{npc['role']}的视角）嗯，「{text[:16]}」这件事嘛……"
            "我觉得挺有意思的。要不你问问我擅长的话题？"
        )


# 全局单例 + 镇民注册
manager = NPCManager()
manager.register_npc(
    "面包师老王", "茶馆掌柜",
    "热情开朗，三句话不离烧饼和茶水，喜欢聊小镇生活的烟火气",
    "哎呀，来客人了！我是老王，这镇上最香的烧饼就是我茶馆炉子里烤的。想听小镇的故事，还是先来一壶茶？",
)
manager.register_npc(
    "守卫铁山", "巡捕",
    "严肃认真，警惕性高，话不多但句句在点上，关心小镇治安",
    "站住……哦，是镇民啊。我是铁山，这条街归我巡。三十年了，闭着眼都知道哪儿块砖松了。有事直说。",
)
manager.register_npc(
    "王婶", "老板娘",
    "嗓门亮堂，心直口快，把街坊当自家人，最见不得年轻人饿肚子",
    "哟，客人来啦？我是王婶，我家小铺的豆浆油条是这镇上最实在的。赶路呢？先垫两个热包子再走！",
)
manager.register_npc(
    "钱先生", "账房先生",
    "精明谨慎，一口一个账上不能错，说话爱打比方、爱算细账，消息灵通",
    "幸会幸会。鄙人姓钱，在镇上替各家商号管账。这镇子一砖一瓦进多少出多少，我账本上都记得清清楚楚。",
)
