# 决策日志 (decisions)

> 记"过程决策 + 为什么"。**追加,不删改**——下一棒最值钱的上下文。
> (架构 / 产品级的"为什么"按 `flow/规范/文档维护SOP.md` 进 `AGENTS.md`;这里记项目怎么推进的过程决策。)

## 2026-08-27 · 迁移总体策略:AI 大脑留 Python,Unity 只做渲染交互
- 背景:luanti-builder 的建筑生成算法/大模型调用/NPC 记忆均在 Python(lb_pkg),全部翻译成 C# 4 天内不可行。
- 决定:Python 端(localhost:8765)提供 /api/generate_json 与 /api/npc/chat,Unity 通过 UnityWebRequest 只做渲染与交互。
- 否决的方案 & 原因:全部移植 C#——工期不允许且丢失原项目迭代能力。

## 2026-08-27 · 编辑器侧操作路径:菜单优先,batchmode 仅在项目空闲时
- 背景:团结引擎不允许两实例开同一项目;用户常开着编辑器,batchmode 反复撞占用(今日两次)。
- 决定:编辑器开着→写代码+引导用户点菜单(Tools→AI Town→…);编辑器关闭→可用 batchmode 单入口 AiTown.EditorTools.AiTownBakeBuildings.RunFixAndBake。跑 batchmode 前必须进程级检测(Get-CimInstance Win32_Process 过滤 Tuanjie.exe 命令行含 ai-town)。
- 否决的方案 & 原因:Test-Path Temp/UnityLockfile 检测——本机实测可能 False 误判。

## 2026-08-27 · 建筑烘焙材质必须落盘为资产
- 背景:运行时 new Material() 是内存材质,场景序列化后引用丢失(m_Sprite/m_Material 失效老坑)。
- 决定:AiTownBakeBuildings 按颜色把材质写为 Assets/Materials/Generated/Block_*.mat 资产再引用;ShapeFactory 增加 Material 重载。
- 否决的方案 & 原因:共享内存材质(仅运行时生成可用,烘焙场景必丢)。

## 2026-08-27 · 弃用 Codely Bridge execute_csharp_script,回落菜单/文件路径
- 背景:逆向 UNITY-TCP 协议成功(端口见项目根 .com-unity-codely.json,握手+8字节大端长度帧+JSON),但 execute_csharp_script 对任意脚本(含一行 Debug.Log)均报上千条 CS1513/CS1002/CS9176,与输入无关。
- 决定:判定桥脚本执行器损坏,不再尝试;需要编辑器执行的操作一律走菜单或 batchmode。
- 否决的方案 & 原因:继续调 TCP 桥——错误与脚本内容无关,非我方可控;协议层本身通,客户端脚本留档 `.codely/unity_tcp_client.ps1` 备日后修复后用。
