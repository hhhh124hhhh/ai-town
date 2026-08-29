# T07 · Day4:演示场景 + 光照 + 打包

> 状态:进行中(~70%,2026-08-29 校准):材质(民国换皮)/道具/氛围/光照(T09✓)/特效(T10✓)/UI v2/委托引导闭环均已落地并推送;剩余=A2 招牌写字 + Windows 打包(sidecar 待拍板) + 演示彩排 · 优先级:P1 · 里程碑:M4

## 目标
完整可演示:城堡(中心)+面包店(旁)+宝塔(远处)+主路;面包师/守卫就位;Bloom/SSAO;Windows exe。

## 步骤
1. 用烘焙功能布置演示建筑与道路;出生点主路入口面向城堡。
2. 乐高纹理(173 张,D:\luanti-builder\lego_style\textures\)导入重点建筑;时间紧则纯色。
3. URP Volume:Bloom+SSAO;可选昼夜切换(N 键)。
4. 生成动画:协程分帧逐层出现(可选)。
5. Build Windows → `D:\Builds\LuantiBuilder\`,无 Unity 机器可运行。

## 验收标准(迁移文档第八节)
- [ ] 启动→生成建筑→漫游→NPC 对话→飞行 全流程顺畅
- [ ] 1000 方块 ≥30FPS;AI 生成等待 ≤10s;exe 独立运行

## 打包要点(2026-08-29 补)
- **server 已入仓** `ai-town/server/`(7 文件,python 端零外部依赖)。exe 端 ServerBootstrap 探测顺序:exe 旁 `server/` → `server/python/` 便携版 → PATH python。**待拍板**:便携 python 随包(全离线,体积+~30MB)还是要求演示机有 python。
- **DEEPSEEK_API_KEY** 经环境变量继承;无 Key 时 NPC/委托/开场白全部走离线回退(演示可断网保底,但开场白署名会变"本地备稿")。
- 委托状态落盘 `server/state.json`(已 gitignore;重启不丢单,开局不自动恢复 active 是设计行为)。
- **性能基线警告**:场景 113.6 万三角/289 MeshRenderer(UCDC 满分线 5 万/200DC 的 22 倍);NPC 老王/铁山 9.1 万三角×2 用户已拍板豁免。打包前必跑 `Tools→AI Town→Validate Model Health / Validate Scene Luminance` + Play 实测 FPS;掉帧优先查静态合批是否生效(全场景 Static 已勾 376 节点)。
- **建筑重烘焙会丢 Static 标记**,重烘后必须重跑 `Mark Static Geometry`。
- A2 招牌写字(骑楼/洋楼门脸世界空间 TMP"茶/客栈/洋货")未做,可并入打包前。
