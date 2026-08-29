# T06 · Day3-Unity:NPCController + DialogSystem

> 状态:已完成(2026-08-27,守卫铁山+面包师老王,E 键对话,Digit 快捷提问,LLM 角色化回复全链路铁证) · 优先级:P0 · 里程碑:M3

## 目标
场景 2 个 NPC,按 E 对话,大模型回复,短期记忆。

## 步骤
1. NPC 占位:Capsule+头顶名牌(TextMeshPro,中文需 SDF 字体——参照全局记忆 TMP 中文化三步)。
2. `NPCController.cs`:定点 Idle+名牌+玩家靠近提示"按 E 对话"。
3. `DialogSystem.cs`/`DialogPanel.cs`:按 E 开对话框,输入+发送,调 /api/npc/chat,显示回复;头顶气泡最近一句。

## 验收标准
- [ ] 面包师(面包店门口)/守卫(城堡门口)各一,头顶名字
- [ ] 靠近提示、按 E 对话、回复正常、记住上下文
