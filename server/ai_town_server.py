"""ai-town API 服务（本项目自包含版，无 luanti-builder 依赖）。

启动：python ai_town_server.py  或双击 start_server.bat
端口：8765 —— Unity 端 ApiClient.baseUrl 保持 http://127.0.0.1:8765 不变

接口：
  POST /api/generate_json  {"description": "红色大城堡"} 或 {"template": "castle"}
  POST /api/npc/chat       {"name": "面包师老王", "message": "..."}
  GET  /api/commission/state                                            委托进度总览
  POST /api/commission/new     {"npc": "面包师老王", "npcPos": [x,y,z]}   请求委托
  POST /api/commission/submit  {"builds": [{name,description,template,blockCount,pos,extents}]}
  POST /api/commission/abandon                                          放弃当前委托
  GET  /api/intro/line                                                  开场白（LLM 现场生成）
  GET  /api/health
"""
import json
from http.server import HTTPServer, ThreadingHTTPServer, BaseHTTPRequestHandler

from nlp import parse_input
from json_gen import gen_building
from npc_ai import manager as npc_manager, llm_available, call_llm_chat
from commission_ai import manager as commission_manager

PORT = 8765

INTRO_SYSTEM_PROMPT = (
    "你是一个民国小镇游戏的旁白，镇上有青砖老洋楼、骑楼和茶馆。"
    "一位传说中的云游营造师来到镇上——他说一句话就能让砖瓦自己长成楼。"
    "请用民国说书人的腔调写一句开场白迎接他，12到25个字，中文，"
    "口语化带旧时代味道（可用'喽''哩'等语气词），不要引号，不要解释，只输出这一句。"
)
INTRO_FALLBACK = "听说，来了一位——说句话就能让砖瓦自己长成楼的营造师。"


def _intro_line():
    """开场白：LLM 现场生成，失败回退固定句。"""
    if llm_available():
        try:
            line = call_llm_chat(INTRO_SYSTEM_PROMPT, "游戏开始了，请说开场白。")
            line = line.strip().strip('“”"').strip()
            if 4 <= len(line) <= 60:
                return line
        except Exception:
            pass
    return INTRO_FALLBACK


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path == "/api/generate_json":
            try:
                length = int(self.headers.get("Content-Length", 0))
                body = json.loads(self.rfile.read(length) or b"{}")
            except (ValueError, json.JSONDecodeError):
                self._send_json({"error": "请求体不是合法 JSON"})
                return

            description = str(body.get("description", "")).strip()
            template = str(body.get("template", "")).strip()

            if template:
                # 模板直出模式：秒回
                params = {"type": template, "color": body.get("color"), "size": 1,
                          "material": body.get("material"), "features": [], "raw": template}
            elif description:
                params = parse_input(description)
                if not params.get("type"):
                    # 描述里没识别出建筑类型，兜底为 house
                    params["type"] = "house"
            else:
                self._send_json({"error": "请提供 description 或 template 参数"})
                return

            try:
                result = gen_building(params)
            except Exception as e:  # 生成失败返回结构化错误而不是 500
                self._send_json({"error": f"生成失败: {e}"})
                return
            self._send_json({"params": params, "building": result})
        elif self.path == "/api/npc/chat":
            try:
                length = int(self.headers.get("Content-Length", 0))
                body = json.loads(self.rfile.read(length) or b"{}")
            except (ValueError, json.JSONDecodeError):
                self._send_json({"error": "请求体不是合法 JSON"})
                return
            name = str(body.get("name", "")).strip()
            message = str(body.get("message", "")).strip()
            if not name or not message:
                self._send_json({"error": "请提供 name 和 message 参数"})
                return
            reply, used_llm = npc_manager.chat(name, message)
            self._send_json({"name": name, "reply": reply, "used_llm": used_llm})
        elif self.path == "/api/commission/new":
            body = self._read_json()
            if body is None:
                return
            name = str(body.get("npc", "")).strip()
            npc_pos = body.get("npcPos") or [0, 0, 0]
            if not name:
                self._send_json({"ok": False, "error": "请提供 npc 参数"})
                return
            commission, err = commission_manager.new(name, npc_pos)
            if err:
                self._send_json({"ok": False, "error": err})
                return
            self._send_json({"ok": True, "commission": commission,
                             "state": commission_manager.state()})
        elif self.path == "/api/commission/submit":
            body = self._read_json()
            if body is None:
                return
            builds = body.get("builds") or []
            result, err = commission_manager.submit(builds, zone_center=body.get("zoneCenter"))
            if err:
                self._send_json({"ok": False, "error": err})
                return
            result["ok"] = True
            self._send_json(result)
        elif self.path == "/api/commission/abandon":
            result, err = commission_manager.abandon()
            if err:
                self._send_json({"ok": False, "error": err})
                return
            result["ok"] = True
            self._send_json(result)
        else:
            self.send_response(404)
            self.end_headers()

    def do_GET(self):
        from urllib.parse import urlparse, parse_qs
        parsed = urlparse(self.path)
        qs = parse_qs(parsed.query)

        if parsed.path == "/api/health":
            self._send_json({"ok": True, "service": "ai-town", "llm": llm_available()})
        elif parsed.path == "/api/npc/list":
            npcs = [{"name": n["name"], "role": n["role"]} for n in npc_manager.npcs.values()]
            self._send_json({"npcs": npcs})
        elif parsed.path == "/api/npc/memory":
            name = qs.get("name", [""])[0]
            self._send_json({"name": name, "memory": npc_manager.get_memory(name)})
        elif parsed.path == "/api/commission/state":
            self._send_json({"ok": True, "state": commission_manager.state()})
        elif parsed.path == "/api/intro/line":
            self._send_json({"ok": True, "line": _intro_line()})
        else:
            self.send_response(404)
            self.end_headers()

    def _read_json(self):
        """读 POST body 为 JSON dict；非法 JSON 时回错误并返回 None。"""
        try:
            length = int(self.headers.get("Content-Length", 0))
            return json.loads(self.rfile.read(length) or b"{}")
        except (ValueError, json.JSONDecodeError):
            self._send_json({"ok": False, "error": "请求体不是合法 JSON"})
            return None

    def _send_json(self, data):
        raw = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def log_message(self, *args):
        pass


def main():
    # 多线程：LLM 请求动辄 30s+，单线程 HTTPServer 会挂起全部路由（HUD/委托全卡）
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    server.daemon_threads = True
    print(f"[ai-town] API 服务已启动: http://127.0.0.1:{PORT}")
    print(f"[ai-town] POST /api/generate_json  {{\"description\": \"红色城堡\"}} 或 {{\"template\": \"castle\"}}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[ai-town] 已停止")


if __name__ == "__main__":
    main()
