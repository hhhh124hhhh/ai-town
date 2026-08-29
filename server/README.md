# ai-town server

民国小镇 AI 后端，Unity `ApiClient.cs`（默认 `http://127.0.0.1:8765`）的完整实现。

## 启动

```bash
./start_server.sh
# 或
python3 ai_town_server.py
```

纯 Python 3 标准库，**零依赖**。

## 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 健康检查 |
| POST | `/api/generate_json` | `{"description"\|"template"}` → `{"building":{name,blocks[]}}`，27 个模板 |
| POST | `/api/npc/chat` | `{"name","message"}` → `{"reply"}`，4 位民国 NPC |
| GET | `/api/intro/line` | `{"line"}` 开场白 |
| GET | `/api/npc/memory?name=X` | 最近对话（调试用） |
| GET | `/api/commission/state` | 金钱/繁荣度/等级/解锁/好感 |
| POST | `/api/commission/new` | 发单（绿圈跟随 npcPos） |
| POST | `/api/commission/submit` | 验收判分（绿圈距离/方块数/占地/类型） |
| POST | `/api/commission/abandon` | 放弃委托 |

## 可选 LLM

设置环境变量后，NPC 对话与开场白走 OpenAI 兼容大模型（失败自动回退内置规则回复）：

```bash
export TOWN_LLM_BASE_URL="https://api.example.com/v1"
export TOWN_LLM_API_KEY="sk-..."
export TOWN_LLM_MODEL="gpt-4o-mini"
```

## 其他环境变量

`TOWN_HOST`（默认 127.0.0.1）、`TOWN_PORT`（默认 8765）。

玩法状态持久化在同目录 `state.json`（可随时删除重置）。
