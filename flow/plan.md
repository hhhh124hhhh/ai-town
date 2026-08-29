# 计划 (plan) —— 契约

> 经确认后执行。要偏离,**先改这里**再动手。
> 来源:`ai-town/Docs/LuantiBuilder迁移开发文档.md`(4 天计划),按 2026-08-27 实际进度校准。

## 里程碑
- [x] M1 · Day1 白模场景 + JSON 链路(2026-08-27 完成:天空盒/地面修复+建筑烘焙)
- [x] M2 · Day2 对接 Builder AI + 建筑系统完善(2026-08-27 完成:中文描述→红城堡落地,23 模板,形状补全)
- [x] M3 · Day3 AI 小镇 NPC 植入(2026-08-27 完成:2 NPC+LLM 记忆对话全链路)
- [ ] M4 · Day4 整合 + 演示场景 + 打包(进行中 2026-08-29:材质民国换皮/道具/氛围/UI v2/光照 T09/特效 T10/委托引导闭环已全部完成并推送;剩余=T07 打包彩排 + A2 招牌写字)
- [x] M5 · 黑客松玩法层:NPC 委托建造循环(2026-08-29 完成:T08 主体 + 引导三层时序 + 状态落盘 + 轮询对齐 + 打字门控,用户多轮 Play 验收修正,见 T08)

## 任务拆解
| 任务 | 负责角色 / 工具 | 输入 | 产出(落哪个文件) | 验收标准 |
|---|---|---|---|---|
| T01 Day1 收尾:Play 验收 + 渲染修复 | 用户点菜单 / Codely | 已写好的 AiTownSceneSetup/AiTownBakeBuildings | 菜单执行结果截图 | 蓝天+草地地面+城堡落地,编辑态可见;WASD/F 飞行正常 |
| T02 Day2-Python:json_gen + API | Codely | `D:\luanti-builder\lb_pkg\lua_gen.py`、`server.py` | `lb_pkg/json_gen.py`、server.py 新路由 | curl 调 `/api/generate_json` 返回正确 JSON |
| T03 Day2-Unity:ApiClient + BuildingPanel | Codely | T02 接口 | `Assets/Scripts/API/ApiClient.cs`、`Assets/Scripts/UI/BuildingPanel.cs` | Unity 输入"金色宝塔"→生成建筑;模板下拉秒出 |
| T04 Day2-Unity:补形状 cone/dome/arch/stairs/spiral | Codely | 迁移文档 2.3 节 20 种形状 | `ShapeFactory.cs` 扩展 | 各形状能生成,建筑不缺件 |
| T05 Day3-Python:npc_ai + 接口 | Codely | `D:\luanti-builder\ai_town\init.lua` 记忆设计 | `lb_pkg/npc_ai.py`、`/api/npc/chat`、`/api/npc/memory` | 对话有记忆,curl 验证 |
| T06 Day3-Unity:NPCController + DialogSystem | Codely | T05 接口 | `Assets/Scripts/NPC/*`、NPC 预制体 | 2 个 NPC,按 E 对话,记住上下文 |
| T07 Day4:演示场景收尾 + 打包 | Codely + 用户 | 全部前序(光照→T09,特效→T10) | 演示彩排、`D:\Builds\LuantiBuilder\` exe | 迁移文档第八节演示验收全过 |
| T09 演出层:光照与 URP 后效(自 T07 拆出) | Codely | 民国氛围已就位 | Global Volume(Bloom/SSAO/调色)+黄昏定妆+夜灯(+可选 N 键昼夜) | Game 视图辉光/AO 可辨,FPS≥30,无粉色材质 |
| T10 演出层:特效与粒子 | Codely | T08/CinematicIntro/篝火道具 | `Assets/Effects/`+EffectsCatalog,三 Must:建造落尘/交付庆典/篝火 | 三触发点 Game 视图可见时机正确,FPS≥30 |
| T08 玩法层:NPC 委托建造循环(黑客松赛道) | Codely(已完成待验收) | T02/T03/T05/T06 | `server/commission_ai.py`、`Assets/Scripts/Commission/CommissionSystem.cs` 等 | Play:C→接单→绿圈建造→验收→奖励/解锁;NPC 记得这单 |

## 实时进展 / 交接棒
→ 见 `flow/进展.md` 顶部(每棒收工在那追加一条:做了什么 / 为什么 / 产出路径 / 下一步)。
(plan.md 只管"计划=契约";"现在到哪了"在进展日志,不在这儿覆盖。)
