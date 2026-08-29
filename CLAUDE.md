# AGENTS.md · ai-town(Luanti Builder → Unity/团结引擎迁移)协作约定

> `CLAUDE.md` 指向本文件。**真相源在文件里,不在对话里。**
> 本文件是**运行时合同**:精要规则 + 约束 + 指针。完整详规在 `flow/规范/`。

## 目录地图(哪个文件夹放什么)
> 2026-08-29:`flow/`、`Docs/` 与本文件已从工作区根迁入 `ai-town` 仓库受版本管理(工作区根只留指针)。
- 仓库根 `flow/` — 唯一控制层(项目"怎么跑"):`charter.md` 目标 / `plan.md` 计划=契约(任务卡 T01-T10) / `进展.md` 进展日志(顶部=交接棒) / `decisions.md` 决策日志 / `踩坑记录.md` 问题与经验库 / `tasks/` 任务卡 / `规范/` 方法论详规
- 仓库根 `Docs/` — 集中内容层(项目"做出什么":验收标准/物品清单/UI kit 预览/审计)
- 仓库本体 — Unity/团结引擎工程(交付物):Assets/Scripts/Core(Player/API/NPC/UI)、Scenes、StreamingAssets/Buildings(JSON 建筑)、Materials、server(Python AI 后端)、Assets/Scripts/Core/Editor
- `D:\luanti-builder\`(项目外,只读参照) — 原项目源码(历史参考,Python 已迁入本仓 server/)
- **判据**:协调 / 推进总体项目的 → `flow/`;需要统一发现的知识和方案 → `Docs/`;代码 → 本仓对应目录

## 项目专属约束(ai-town)
- **编辑器操作通道**:Codely Bridge UNITY-TCP 结构化命令(`.codely/unity_tcp_cmd.ps1`,execute_menu_item/read_console/manage_screenshot);**禁用 execute_csharp_script(工具损坏)**;编辑器占用时禁 batchmode(进程级检测)。
- **多会话并行铁律(编辑器=单写者资源)**:本项目常有多 agent 窗口共用一个团结编辑器(同 TCP 桥端口),三层分工——纯文件层(server/文档/JSON)可随便并行;C# 代码层可并行但编译要错峰;**场景+编辑器层只允许一个会话动**。规则:
  - 动场景前看 `flow/进展.md` 顶部,确认前一棒已收尾(manage_scene save 落盘);大改场景前让并行会话停手。
  - 改 C# 后触发编译会吃掉对方正在跑的菜单;跑菜单的一方见 console 冒 "Compiling Scripts" 等 ~20s 重跑。菜单回包 success ≠ 执行,**判据=查该菜单专属日志(如 [PropPlacer v4])**。
  - **read_console 先 `{"action":"clear"}` 清空再跑命令再读**——不清会读到最旧的缓存条目,导致"改了没生效"误判(2026-08-29 实证)。
  - Play 模式互斥:一方在 Play,另一方编辑器菜单全静默失败(console 有 InvalidOperationException play mode);跑菜单前先查 manage_editor get_state。
  - 共享 JSON(manifest/layout.json)改前重读最新、改完即跑验证菜单生效;撞 "file changed" 重读再改,勿盲重试。
  - 每次跑 Step 1 Place Props 会整棵重建 `_Props`(连带清掉灯泡/BonfireLight 子节点),收尾必须重跑 Apply Dusk Lighting;改完场景必 `manage_scene save` + 磁盘字面量复核(勿信菜单自带的"已保存")。
  - 场景 YAML 不可合并(TuanjieYAMLMerge 实测废)→ 场景只能单线推进,每块完成即 git commit 检查点。
- **烘焙材质必须落盘**为 Assets 材质资产,运行时内存材质存场景会丢引用。
- **团结引擎坑**:activeInputHandler=1 时旧 Input 全失效(用新 Input System + 双路径兜底);TMP 中文需 SDF 字体资产;输入/移动类需求参照全局 CODELY 记忆的团结引擎判例。
- 建筑设计为"Play 时动态生成",烘焙菜单用于把 JSON 建筑固化进场景——别把编辑态无建筑误判为 bug。
- Python 端 API:localhost:8765;调用不发 orgId 头。

## 开工前必读
1. `flow/charter.md` — 4 天迁移冲刺目标/范围/约束
2. `flow/plan.md` — 任务卡 T01-T10(契约,未确认不要偏离)
3. `flow/进展.md` **顶部一条** — 上一棒交接棒
4. 你被分配的任务卡:`flow/tasks/T0*.md`
5. 迁移原始契约:`Docs/LuantiBuilder迁移开发文档.md`

## 收工前必做
1. 在 `flow/进展.md` **最上面追加一条进展**(做了什么/为什么/产出路径/下一步),**并把这条同时贴在回复里**。
2. 决策追加 `flow/decisions.md`;问题/踩坑追加 `flow/踩坑记录.md`。
3. 文档自检:动了结构/方向/约定,主动提议更新本文件。

## 核心约束(铁律)
- 审稿模型 ≠ 产出模型(交付前换另一个模型评审)。
- 产出落文件,不留在对话里。
- 先 plan 后 act,计划即契约;要改先改 `flow/plan.md`。
- 一会话一焦点。
- 产物本身是真相唯一来源;文档只补"看产物看不出的为什么"。
- 从根本上解,不打补丁;进展条只带「指针+增量」。

## 详规索引
- 工作流程:`flow/规范/工作流程.md` · 文档维护:`flow/规范/文档维护SOP.md` · hook 机制:`flow/规范/hook机制.md`

---
## 项目知识(durable,随项目积累 ↓)
- Unity 与 Luanti 坐标系一致(Y 上,1 格=1 米),建筑 JSON 可直接迁移无需换算(迁移文档 2.4 结论)。
- 城堡/小木屋测试 JSON:`Assets/StreamingAssets/Buildings/`(castle 24 方块 y=0 落地正确)。
- 本项目 2026-08-27 19:14 由 urp-sample 模板重建,Quality=PC High(4 个自定义渲染器),activeInputHandler=1。
