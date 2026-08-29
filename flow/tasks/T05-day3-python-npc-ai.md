# T05 · Day3-Python:npc_ai.py + NPC 接口

> 状态:已完成(2026-08-27,server/npc_ai.py,NPCManager 记忆 50 条上限,DEEPSEEK_API_KEY 真 LLM 通道,三接口全通) · 优先级:P0 · 里程碑:M3

## 目标
NPC 记忆对话后端。参照 `D:\luanti-builder\ai_town\init.lua` 的记忆系统设计。

## 步骤
1. `lb_pkg/npc_ai.py`:NPCManager(register/chat/get_memory),系统提示词拼角色+性格+最近 10 条记忆,记忆上限 50;预注册 面包师(热情)与 守卫(严肃)。
2. server.py 加 `POST /api/npc/chat` 与 `GET /api/npc/memory?name=`。
3. curl 验证两轮对话记忆保持。

## 验收标准
- [ ] "你是谁?"→角色化回复;"我们刚才聊了什么"→能复述
