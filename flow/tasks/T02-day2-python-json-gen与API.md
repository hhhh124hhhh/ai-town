# T02 · Day2-Python:json_gen.py + /api/generate_json 接口

> 状态:已完成(2026-08-27,服务迁入本项目 server/,HTTP 8765,23 模板输出连续几何体 JSON) · 优先级:P0 · 里程碑:M2

## 目标
Python 端(localhost:8765)提供建筑 JSON 输出 API,复用 luanti-builder 现有生成逻辑。

## 输入
- 原项目:`D:\luanti-builder\lb_pkg\`(lua_gen.py 规则式 23 模板 / llm.py 大模型 / nlp.py 关键词 / server.py 路由)

## 步骤
1. 新建 `lb_pkg/json_gen.py`:复用 lua_gen 生成函数,输出 `{"name","blocks":[{shape,pos,size,color}]}`(格式见迁移文档 4.1)。
2. `server.py` do_POST 加路由 `POST /api/generate_json`:template 参数走 json_gen 模板;否则 llm 生成命令转 JSON。
3. curl 验证(按迁移文档 Day2 上午验收)。

## 验收标准
- [ ] `curl -X POST localhost:8765/api/generate_json -d '{"template":"城堡"}'` 返回可被 Unity JsonUtility 解析的 JSON
- [ ] AI 模式(自然语言描述)返回合法 JSON

## 注意
- 改的是 `D:\luanti-builder` 仓库(Python 大脑留原仓库,见 decisions.md);动前确认该仓库无未提交冲突。
- 不发 orgId 等额外头(历史坑,见全局记忆)。
