# T04 · Day2-Unity:补全形状 cone/dome/arch/stairs/spiral

> 状态:已完成(2026-08-27,ShapeFactory 补 cone/pyramid/dome 程序化 Mesh+MeshCollider,arch/stairs Primitive 组合) · 优先级:P1 · 里程碑:M2

## 目标
ShapeFactory 支持迁移文档 2.3 节的关键形状,建筑不缺件。

## 范围
- cone(圆柱缩放+自定义 Mesh)、dome(Sphere 裁半)、arch、stairs(多 Cube 错位)、spiral(多 Cube 旋转)
- 其余(line/floor/wall/cross/taper/fence/cornice 等)用 Cube 组合兜底即可

## 验收标准
- [ ] 各形状单独生成测试通过(可用 StreamingAssets 测试 JSON)
- [ ] 模板建筑(塔/拱门类)生成不缺件
