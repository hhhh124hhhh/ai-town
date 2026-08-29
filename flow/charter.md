# 项目宪章 (charter)

> 立项填。所有 Agent 开工要读的第一份。

- **项目名**：ai-town（Luanti Builder → Unity/团结引擎 迁移）
- **目标**(做成什么样算成功,一句话)：4 天内在团结引擎里复刻 luanti-builder 核心能力——白模建筑生成 + AI 建筑对接 + AI NPC 记忆对话 + 第一人称漫游/飞行，可完整演示。
- **范围**:
  - 做:白模场景与 JSON 建筑链路、Python 端(/api/generate_json、/api/npc/chat)API、Unity 端 ApiClient/UI 面板、1-2 个带记忆 NPC、演示场景整合、Windows 打包。
  - 不做:体素化渲染优化、方块放置/破坏、NPC 反思机制、多人联机、移动端(均为"4 天后"方向,见迁移文档第九节)。
- **约束**(时间 / 资源 / 必须遵守的):
  - 参考文档:`ai-town/Docs/LuantiBuilder迁移开发文档.md`(4 天计划为契约)。
  - 原项目源码只读参照:`D:\luanti-builder`(Python 大脑留在原仓库,Unity 只做渲染交互)。
  - 团结引擎 2022.3.62t12;编辑器占用项目时禁 batchmode,改菜单/文件操作路径。
  - 通信约定:Python 端 `http://localhost:8765`。
- **成功标准**(尽量可衡量):
  - Day1-4 各自验收标准全过(见 plan.md 任务卡);最终:启动→生成建筑→漫游→NPC 对话→飞行 全流程顺畅,打包 exe 可独立运行。
- **角色**:拍板 = 用户 / 主控 = Codely(Claude) / 评审 = Codex 或换模型 / Python 端 = 任意 Agent 按任务卡执行。
