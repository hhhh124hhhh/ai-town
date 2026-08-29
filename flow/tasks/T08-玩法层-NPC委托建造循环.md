# T08 · 黑客松玩法层：NPC 委托建造循环

> 状态:代码完成待 Play 验收(2026-08-28,Python 逻辑测试 32/32+HTTP 冒烟+Unity 编译验证已过,差用户 Play 走一遍) · 优先级:P0 · 里程碑:M5

## 目标
给"能力演示"装上游戏循环:NPC 发委托 → 玩家建造 → 规则验收 + LLM 点评 → 金币/繁荣度/好感度/解锁模板。三套已有系统(AI 生成/NPC 记忆/建造面板)通过"委托"这个接缝咬合成玩法。

## 循环设计
```
[C] 委托面板 → 向 NPC 请求委托(LLM 角色话术发单,地面画绿圈验收区)
→ [Tab] 建造(现有面板,模板 6 个初始锁定)
→ [提交验收] 服务端规则判分(类型/占地/方块数/距离,多建筑取最优)
→ 通过:LLM 点评+金币+繁荣+好感+解锁模板;NPC 记忆写入(后续对话能"记得这单")
→ 未过:逐条 ✓/✗ 反馈,可调整重交
```

## 实现(已落地)
- Python `server/commission_ai.py`:委托序列(老王 5 单/铁山 5 单)、规则判分、繁荣度 5 级、好感 3 档、模板解锁(委托奖 windmill/lighthouse/temple,繁荣里程碑 skyscraper/spaceship/shanghai)、难度递进(半径 18→8m)、LLM 风味文本+离线回退话术。
- 服务路由:`/api/commission/state|new|submit|abandon`(ai_town_server.py)。
- Unity `Assets/Scripts/Commission/CommissionSystem.cs`:右上 HUD(等级/繁荣/金币/委托)、C 键面板、接单/验收/放弃、绿圈 LineRenderer(Sprites/Default 无资源依赖)、BuildingPanel.Start() 懒创建无需场景接线、离线 fail-open(模板全解锁不影响原演示)。
- `ApiClient.cs`:GetCommissionState/RequestCommission/SubmitCommission/AbandonCommission(JSON 透传,超时 65s 容 LLM)。
- `BuildingPanel.cs`:模板按钮锁定态(🔒)、每次生成登记 RegisterBuild(验收上报,服务端取最优)。

## 验收标准
- [x] Python 逻辑:server/test_commission.py 32/32(类型解析/发单/判分/奖励/放弃)
- [x] HTTP 冒烟:state→new→submit 错/对→记忆写入,LLM 通道实测活
- [x] Unity 编译:bridge add_component CommissionSystem 成功(真编译判据)
- [ ] 用户 Play:C 开面板→老王发单→绿圈→Tab 建"大房屋"→提交→S/A 评级+奖励+解锁;建错类型被 ✗ 打回;之后 E 闲聊 NPC 能提到这单

## 已知边界
- 模板锁只锁模板按钮,自然语言仍可生成任意类型(锁=便捷奖励,非硬门,Day2 核心能力不受限)。
- json_gen.py 存量 bug:village 分支 `MATERIAL_HEX["sandstone"]` 键不存在会 KeyError(非本任务引入,待修)。
