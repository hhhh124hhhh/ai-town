# T03 · Day2-Unity:ApiClient + BuildingPanel

> 状态:已完成(2026-08-27,IMGUI 面板+回车生成,中文"红色大城堡"→18 块落地玩家前方,端到端验证) · 优先级:P0 · 里程碑:M2

## 目标
Unity 内输入描述→调 Python API→场景生成建筑,带加载提示。

## 步骤
1. `Assets/Scripts/API/ApiClient.cs`:UnityWebRequest POST localhost:8765/api/generate_json,成功回调 BuildingManager.GenerateFromJson。
2. `Assets/Scripts/UI/BuildingPanel.cs`:描述输入框+生成按钮+模板下拉(23 模板)+清除按钮;"生成中..."状态。
3. 形状补全见 T04 同步进行。

## 验收标准
- [ ] 输入"建一个金色宝塔"→数秒后场景出现宝塔
- [ ] 模板下拉选"城堡"→瞬间生成
- [ ] 生成中有加载提示;清除按钮清空场景建筑
