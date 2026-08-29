# T01 · Day1 收尾:渲染修复生效 + Play 验收

> 状态:已完成(2026-08-27,天空盒/地面修复+建筑烘焙生效,后续 Day2/3 在其上验收通过) · 优先级:P0 · 里程碑:M1

## 目标
让"蓝天 + 草地地面 + 城堡/小木屋落地"在编辑态与 Play 态都正确,完成 Day1 验收。

## 步骤
1. 编辑器中点菜单 `Tools → AI Town → Fix Ground & Skybox`(天空盒 + 草地贴图地面,材质 `Assets/Textures/Ground/grass_albedo.png`)。
2. 点菜单 `Tools → AI Town → Bake Buildings To Scene`(castle+hut 固化进 Main.unity,autoLoad 自动关闭)。
3. Play:WASD 移动、鼠标视角、F 切飞行、Space/Ctrl 升降、走近城堡可进内部。

## 验收标准(迁移文档 Day1)
- [ ] 编辑态 Scene 视图能看到城堡与小木屋(固化后)
- [ ] 蓝色渐变天空 + 草地纹理地面可见,与背景明确区分
- [ ] 城堡落地不悬空
- [ ] WASD/鼠标/跳跃正常
- [ ] F 飞行切换正常,可鸟瞰

## 备注
- 若 Bake 菜单报"找不到 _Buildings":先跑 Setup Main Scene(现有场景已有 _Buildings,正常不会触发)。
- 若仍有异常:截图发主控,参照 flow/踩坑记录.md 第一条的像素诊断法。
