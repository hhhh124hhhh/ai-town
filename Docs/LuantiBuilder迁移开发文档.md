# Luanti Builder → Unity 迁移开发文档

好的，我来整理一份详细的开发和转换文档，并写入飞书文档。先读取飞书文档的操作规范。

沙箱暂时有问题，我先用工具搜索查找飞书文档创建工具。

环境暂时有问题，我换个路径试试读取 skill。

当前本地环境暂时无法访问，我先把完整的开发和转换文档整理出来，你可以先看内容，环境恢复后我再写入飞书文档。

---

# Luanti Builder → Unity 迁移开发文档

## 一、项目概述

### 1\.1 原项目介绍

**Luanti Builder** 是一个自然语言生成 3D 沙盒建筑的跨平台工具，由个人开发者 cpufreestyle \(MichaelQiu\) 开发，核心包含：

- **建筑生成引擎**：自然语言/关键词 → 3D 建筑，支持 23 种模板 \+ 30\+ 大模型 AI 生成

- **AI 小镇系统**：带记忆系统、反思机制、自然语言对话的自主 NPC

- **Builder Web GUI**：Python 编写的零依赖 Web 工具，含 3D 预览、对话式迭代、语音输入

- **游戏 Mod**：饥饿系统、乐高方块、自定义生物等

### 1\.2 迁移目标

在 **4 天**内将项目核心能力迁移到 Unity（团结引擎），实现：

- ✅ 白模 3D 场景（坐标大小参照原项目）

- ✅ 建筑生成 AI 对接（输入描述 → 生成建筑）

- ✅ AI NPC 对话（1\-2 个角色，带记忆）

- ✅ 第一人称漫游 \+ 飞行模式

- ✅ 可演示的完整场景

### 1\.3 核心迁移策略

**AI 大脑留在 Python，Unity 只做渲染和交互层。**

```Plaintext
┌─────────────────┐     HTTP/JSON     ┌─────────────────┐
│  Python Builder │ ◄───────────────► │   Unity 端      │
│                 │                     │                 │
│ • 大模型调用    │                     │ • 3D 场景渲染   │
│ • 建筑生成算法  │                     │ • 第一人称漫游   │
│ • NPC 记忆系统  │                     │ • NPC 模型/动画  │
│ • 对话提示词    │                     │ • 对话 UI        │
│ • 反思机制      │                     │ • 输入/交互      │
└─────────────────┘                     └─────────────────┘
```

---

## 二、原项目架构分析

### 2\.1 目录结构

```Plaintext
luanti-builder/
├── luanti_builder_web.py    # 入口，启动 Web 服务
├── lb_pkg/                   # 核心包（8个模块）
│   ├── paths.py              # 跨平台路径检测
│   ├── nlp.py                # 关键词解析（中英文）
│   ├── lua_gen.py            # 规则式 Lua 生成器（23种模板）
│   ├── llm.py                # 大模型调用与解析（AI模式）
│   ├── preview.py            # 3D 预览方块列表生成
│   ├── worlds.py             # Mod 安装、世界管理、游戏启动
│   ├── webui.py              # 前端 HTML/CSS/JS
│   └── server.py             # HTTP 服务器
├── my_first_mod/             # 游戏 Mod（饥饿/生物/乐高/上海）
│   ├── init.lua (73KB)
│   └── textures/
├── nl_builder/               # AI 建筑 Mod（由 GUI 生成）
│   └── init.lua (136KB)
├── ai_town/                  # AI 小镇 Mod
│   ├── init.lua (42KB)
│   ├── models/
│   └── textures/
├── lego_style/               # 乐高纹理包（173张）
│   └── textures/
└── minetest.conf             # 游戏配置
```

### 2\.2 核心模块职责

|模块|文件|职责|迁移策略|
|---|---|---|---|
|自然语言处理|`nlp.py`|关键词解析，提取建筑类型/颜色/尺寸|保留在 Python|
|大模型接口|`llm.py`|30\+ 大模型调用，JSON 解析，对话上下文|保留在 Python|
|Lua 代码生成|`lua_gen.py`|23 种建筑模板，20 种形状命令|保留算法，改输出 JSON|
|世界管理|`worlds.py`|Mod 安装、游戏启动|不需要（Unity 端自管理）|
|路径检测|`paths.py`|跨平台路径|不需要|

### 2\.3 建筑形状命令系统

原项目定义了 20 种基础形状，所有建筑都由这些形状组合而成：

|形状|说明|Unity 对应|
|---|---|---|
|box / solid|立方体|Cube|
|cyl|圆柱体|Cylinder|
|cone|圆锥体|Cylinder 缩放 \+ 自定义 Mesh|
|sphere|球体|Sphere|
|dome|穹顶|Sphere 裁上半|
|ring|环形|自定义 Mesh|
|pyramid|金字塔|自定义 Mesh|
|arch|拱门|自定义 Mesh|
|stairs|楼梯|多个 Cube 错位|
|spiral|螺旋楼梯|多个 Cube 旋转排列|
|line / hline / vline|线|拉伸 Cube|
|floor / wall|地板/墙|拉伸 Cube|
|cross|十字|两个 Cube 交叉|
|taper|锥形渐变|自定义 Mesh|
|fence|栅栏|Cube 组合|
|cornice|檐口|Cube 组合|

### 2\.4 坐标系统对照

|项|Luanti/Minetest|Unity|是否一致|
|---|---|---|---|
|坐标轴|Y 轴向上|Y 轴向上|✅ 一致|
|单位|1 方块 = 1 米|1 Cube = 1 米|✅ 一致|
|建筑起点|y=1（地面 y=0）|自定义地面高度|✅ 可对应|
|玩家朝向|get\_look\_dir\(\)|transform\.forward|✅ 概念一致|

**结论：坐标和大小可直接迁移，无需换算。**

---

## 三、4 天开发计划

### Day 1：白模场景 \+ JSON 链路

**目标**：Unity 里能根据 JSON 生成建筑，能走进去看

#### 上午：项目搭建

1. 新建 Unity URP 项目（推荐 2022\.3 LTS）

    - 项目路径：`D:\UnityProjects\LuantiBuilder`（无中文无空格）

    - 模板：3D \(URP\)

2. 导入 Starter Assets \- First Person（Asset Store 免费）

    - 注意：如果用新 Input System，需在 Player Settings 切换

3. Package Manager 确认 TextMeshPro 已安装

4. 场景设置：

    - 地面：Plane，y=0，缩放 100x1x100

    - 方向光：旋转 50, \-30, 0

    - 玩家出生点：\(0, 2, \-10\)，面向建筑

#### 下午：建筑生成系统

5. 写 `ShapeFactory.cs`——形状工厂

    - 输入：形状类型 \+ 位置 \+ 尺寸 \+ 颜色

    - 输出：GameObject

    - 先支持 box / cyl / sphere 三种，其他后续补

6. 写 `JsonLoader.cs`——JSON 读取器

    - 读取建筑 JSON 文件

    - 遍历 blocks 数组，调用 ShapeFactory 生成

    - 所有方块挂在一个根 GameObject 下，方便整体移动/删除

7. 写 `BuildingManager.cs`——建筑管理器

    - 管理场景中所有建筑

    - 提供 Generate\(path\) / Clear\(\) / Reload\(\) 方法

8. 测试：用一个手写的 JSON（城堡）验证能加载出来

9. 飞行模式：写 `FlyMode.cs`，按 F 切换，开启后禁用重力、提升移动速度

#### 验收标准

- 双击运行 → 场景里有一座城堡白模

- WASD 移动，鼠标转视角

- 能走进建筑内部

- 按 F 切换飞行，能飞起来鸟瞰

---

### Day 2：对接 Builder AI \+ 建筑系统完善

**目标**：Unity 里点按钮，调用大模型生成建筑

#### 上午：Python 端 JSON 输出接口

1. 在 `lb_pkg/` 下新建 `json_gen.py`

    - 复用 `lua_gen.py` 的建筑生成函数

    - 输出格式从 Lua 代码改成 JSON

```Python
# 输出格式示例
{
    "name": "红色城堡",
    "blocks": [
        {"shape": "box", "pos": [0,1,0], "size": [10,5,10], "color": "#8B4513"},
        {"shape": "cylinder", "pos": [-5,0,-5], "size": [2,8,2], "color": "#8B4513"}
    ]
}
```

2. 在 `server.py` 加 API 接口：

    - `POST /api/generate_json`——输入描述，返回建筑 JSON

    - 复用 `llm.py` 的大模型调用逻辑

    - 关键词模式走 `json_gen.py`，AI 模式走 `llm.py` \+ JSON 转换

3. 测试：用 curl 或 Postman 调用接口，验证返回正确 JSON

#### 下午：Unity 端 API 对接 \+ 形状补全

4. 写 `ApiClient.cs`——API 客户端

    - `UnityWebRequest` POST 到 Python 端

    - 输入建筑描述，接收 JSON，调用 BuildingManager 生成

    - 加加载动画（"生成中\.\.\."）

5. 写 UI 面板 `BuildingPanel.cs`

    - 输入框：建筑描述

    - 生成按钮：调用 ApiClient

    - 模板下拉：23 种建筑模板快速选择

    - 清除按钮：清空场景建筑

6. 补全形状：cone / dome / arch / stairs / spiral

    - 用 Primitive 组合或简单 Mesh 实现

    - 优先保证建筑能生成，细节可以后续优化

7. 热重载：可选，监听 JSON 文件变化自动刷新

#### 验收标准

- Unity 里输入"建一个金色宝塔"→ 点生成 → 等几秒 → 场景里出现宝塔

- 模板下拉选"城堡"→ 瞬间生成

- 生成过程有加载提示

---

### Day 3：AI 小镇 NPC 植入

**目标**：场景里有 1\-2 个 NPC，能对话，有记忆

#### 上午：Python 端 NPC AI 模块

1. 新建 `npc_ai.py`——NPC AI 模块

    - 参考 `ai_town/init.lua` 的记忆系统和对话提示词

    - 数据结构：

    ```Python
    class NPC:
        name: str
        role: str  # 面包师/守卫/学者
        personality: str
        memory: List[str]  # 对话记忆
        location: Tuple[float, float, float]
    ```

    - 对话方法：`chat(npc_name, user_message) -> str`

        - 拼接系统提示词（角色\+性格\+记忆）

        - 调用大模型 API

        - 将对话存入记忆

        - 返回回复

    - 记忆方法：`get_memory(npc_name) -> List[str]`

2. 在 `server.py` 加 API 接口：

    - `POST /api/npc/chat`——发消息，返回 NPC 回复

    - `GET /api/npc/memory?name=xxx`——获取 NPC 记忆

3. 测试：调用对话接口，验证 NPC 能回复并记住上下文

#### 下午：Unity 端 NPC 系统

4. NPC 模型：

    - 优先用 Asset Store 免费人形角色

    - 找不到就用 Capsule \+ 头顶名牌占位

5. 写 `NPCController.cs`——NPC 控制器

    - 定点站立 \+ Idle 动画

    - 头顶显示名字

    - 玩家靠近时显示"按 E 对话"提示

6. 写 `DialogSystem.cs`——对话系统

    - 按 E 打开对话框

    - 输入框 \+ 发送按钮

    - 调用 `/api/npc/chat` 接口

    - 显示 NPC 回复（打字机效果可选）

    - 聊天气泡：NPC 头顶显示最近一句话

7. 放置 2 个 NPC：

    - 面包师：站在面包店门口，角色设定"热情的面包师"

    - 守卫：站在城堡门口，角色设定"严肃的守卫"

8. 测试：走近面包师按 E → 输入"你是谁？"→ NPC 回复 → 再问"我们刚才聊了什么"→ NPC 能记住

#### 验收标准

- 场景里有 2 个 NPC，头顶有名字

- 走近显示"按 E 对话"

- 对话能收到大模型回复

- NPC 能记住上下文（短期记忆）

---

### Day 4：整合 \+ 演示场景 \+ 打磨

**目标**：一个完整可演示的场景

#### 上午：演示场景搭建

1. 生成演示建筑：

    - 城堡（用 Builder 生成，放在场景中心）

    - 面包店（小房子，放在城堡旁边）

    - 宝塔（放在远处）

    - 道路（用 box 拼一条主路）

2. 放置 NPC：

    - 面包师在面包店门口

    - 守卫在城堡门口

3. 玩家出生点：放在主路入口，面向城堡

4. 场景导航：

    - 烘焙 NavMesh（NPC 如果需要移动的话，4天内可以不做自主移动）

    - 加几个方向牌（可选）

#### 下午：打磨 \+ 打包

5. 材质纹理：

    - 导入 173 张乐高纹理（时间够的话）

    - 时间不够就先用纯色，重点建筑贴纹理

6. 光照氛围：

    - URP Volume：加 Bloom（发光积木）、SSAO

    - 昼夜切换：按 N 切换白天/夜晚（可选）

7. 建造动画：

    - 协程分帧生成，方块从下往上逐层出现（可选）

8. UI 优化：

    - 主界面：建筑生成面板 \+ 提示文字

    - 隐藏/显示 UI 按钮（截图模式）

9. 修 bug、调比例、测试完整流程

10. 打 Windows 包：

    - File → Build Settings → Windows

    - 输出到 `D:\Builds\LuantiBuilder\`

    - 测试打包后的 exe 能正常运行

#### 验收标准

- 从头演示完整流程：

    1. 启动 exe → 看到演示小镇

    2. 输入"建一个红色城堡"→ 生成建筑

    3. 走进去参观

    4. 找面包师对话

    5. 按 F 飞起来看全景

- 打包后的 exe 能独立运行（不依赖 Unity Editor）

---

## 四、Python 端改造详解

### 4\.1 新增 json\_gen\.py

**位置**：`lb_pkg/json_gen.py`

**职责**：将 `lua_gen.py` 的建筑生成逻辑输出为 JSON 格式。

```Python
"""建筑生成 JSON 输出模块。复用 lua_gen 的生成逻辑，输出 JSON 方块列表。"""
import json
from . import lua_gen

def building_to_json(name, blocks):
    """将方块列表转为 JSON 格式"""
    return {
        "name": name,
        "blocks": [
            {
                "shape": b["shape"],
                "pos": b["pos"],
                "size": b["size"],
                "color": b.get("color", "#FFFFFF")
            }
            for b in blocks
        ]
    }

def generate_castle(color="#8B4513", size=1):
    """生成城堡，返回 JSON"""
    blocks = lua_gen.generate_castle(color=color, size=size)
    return building_to_json("城堡", blocks)

# ... 其他 22 种建筑模板同理
```

### 4\.2 新增 npc\_ai\.py

**位置**：`lb_pkg/npc_ai.py`

**职责**：NPC AI 模块，参考 ai\_town/init\.lua 实现记忆和对话。

```Python
"""NPC AI 模块：记忆系统 + 大模型对话。参考 ai_town/init.lua 设计。"""
from .llm import call_llm

class NPCManager:
    def __init__(self):
        self.npcs = {}  # name -> NPC data
    
    def register_npc(self, name, role, personality):
        self.npcs[name] = {
            "name": name,
            "role": role,
            "personality": personality,
            "memory": []  # 对话记忆列表
        }
    
    def chat(self, name, user_message):
        """与 NPC 对话，返回回复"""
        npc = self.npcs.get(name)
        if not npc:
            return f"（找不到 {name}）"
        
        # 构建系统提示词
        system_prompt = f"""你是 {name}，一个{npc['role']}。
性格：{npc['personality']}
用中文回答，保持角色设定，回答简洁自然。

最近的对话记忆：
{chr(10).join(npc['memory'][-10:]) if npc['memory'] else '（暂无记忆）'}
"""
        
        # 调用大模型
        reply = call_llm(system_prompt, user_message)
        
        # 存入记忆
        npc['memory'].append(f"玩家：{user_message}")
        npc['memory'].append(f"{name}：{reply}")
        
        # 记忆上限：保留最近 50 条
        if len(npc['memory']) > 50:
            npc['memory'] = npc['memory'][-50:]
        
        return reply
    
    def get_memory(self, name):
        npc = self.npcs.get(name)
        return npc['memory'] if npc else []

# 全局单例
manager = NPCManager()
manager.register_npc("面包师", "面包师", "热情开朗，喜欢聊面包和小镇生活")
manager.register_npc("守卫", "城堡守卫", "严肃认真，警惕性高，关心小镇安全")
```

### 4\.3 server\.py 新增 API 接口

在现有 `server.py` 的 do\_POST 中新增路由：

```Python
# 建筑生成 JSON 接口
elif path == "/api/generate_json":
    data = json.loads(content)
    description = data.get("description", "")
    template = data.get("template", "")  # 可选模板名
    
    if template:
        # 关键词模板模式
        result = json_gen.generate_by_template(template)
    else:
        # AI 模式：调用大模型生成方块命令，转 JSON
        cmds = llm.generate_building_cmds(description)
        result = json_gen.cmds_to_json(description, cmds)
    
    self.send_json(result)

# NPC 对话接口
elif path == "/api/npc/chat":
    data = json.loads(content)
    name = data.get("name", "")
    message = data.get("message", "")
    reply = npc_ai.manager.chat(name, message)
    self.send_json({"reply": reply})

# NPC 记忆接口
elif path == "/api/npc/memory":
    name = self.query.get("name", [""])[0]
    memory = npc_ai.manager.get_memory(name)
    self.send_json({"memory": memory})
```

---

## 五、Unity 端开发详解

### 5\.1 项目结构

```Plaintext
Assets/
├── Scripts/
│   ├── Core/
│   │   ├── BuildingManager.cs    # 建筑管理器
│   │   ├── JsonLoader.cs         # JSON 读取器
│   │   └── ShapeFactory.cs       # 形状工厂
│   ├── API/
│   │   └── ApiClient.cs          # Python API 客户端
│   ├── NPC/
│   │   ├── NPCController.cs      # NPC 控制器
│   │   └── DialogSystem.cs       # 对话系统
│   ├── Player/
│   │   └── FlyMode.cs            # 飞行模式
│   └── UI/
│       ├── BuildingPanel.cs      # 建筑生成面板
│       └── DialogPanel.cs        # 对话面板
├── Prefabs/
│   ├── Buildings/                # 建筑预制体（可选）
│   └── NPC/                      # NPC 预制体
├── Materials/
│   └── Lego/                     # 乐高材质（导入纹理后生成）
├── Textures/
│   └── Lego/                     # 173 张乐高纹理
└── Scenes/
    └── Main.unity                # 主场景
```

### 5\.2 核心脚本说明

#### ShapeFactory\.cs

```C#
public static class ShapeFactory {
    public static GameObject Create(string shape, Vector3 pos, Vector3 size, Color color) {
        PrimitiveType type = shape switch {
            "box" or "solid" => PrimitiveType.Cube,
            "cyl" => PrimitiveType.Cylinder,
            "sphere" => PrimitiveType.Sphere,
            _ => PrimitiveType.Cube  // 未知形状先用 Cube 兜底
        };
        
        GameObject obj = GameObject.CreatePrimitive(type);
        obj.transform.position = pos;
        obj.transform.localScale = size;
        
        Renderer renderer = obj.GetComponent<Renderer>();
        renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        renderer.material.color = color;
        
        return obj;
    }
}
```

#### BuildingManager\.cs

```C#
public class BuildingManager : MonoBehaviour {
    public Transform buildingRoot;  // 所有建筑的父节点
    
    public void GenerateFromJson(string json) {
        Clear();
        BuildingData data = JsonUtility.FromJson<BuildingData>(json);
        foreach (BlockData block in data.blocks) {
            Color color = Color.white;
            ColorUtility.TryParseHtmlString(block.color, out color);
            GameObject obj = ShapeFactory.Create(
                block.shape, 
                new Vector3(block.pos[0], block.pos[1], block.pos[2]),
                new Vector3(block.size[0], block.size[1], block.size[2]),
                color
            );
            obj.transform.SetParent(buildingRoot);
        }
    }
    
    public void Clear() {
        foreach (Transform child in buildingRoot) {
            Destroy(child.gameObject);
        }
    }
}

[Serializable]
public class BuildingData {
    public string name;
    public BlockData[] blocks;
}

[Serializable]
public class BlockData {
    public string shape;
    public float[] pos;
    public float[] size;
    public string color;
}
```

#### ApiClient\.cs

```C#
public class ApiClient : MonoBehaviour {
    private string baseUrl = "http://localhost:8765";
    
    public IEnumerator GenerateBuilding(string description, System.Action<string> onSuccess) {
        var data = new { description = description };
        string json = JsonUtility.ToJson(data);
        
        using (UnityWebRequest req = new UnityWebRequest(baseUrl + "/api/generate_json", "POST")) {
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            
            yield return req.SendWebRequest();
            
            if (req.result == UnityWebRequest.Result.Success) {
                onSuccess?.Invoke(req.downloadHandler.text);
            } else {
                Debug.LogError($"生成失败: {req.error}");
            }
        }
    }
    
    public IEnumerator ChatWithNPC(string name, string message, System.Action<string> onReply) {
        var data = new { name = name, message = message };
        string json = JsonUtility.ToJson(data);
        
        using (UnityWebRequest req = new UnityWebRequest(baseUrl + "/api/npc/chat", "POST")) {
            // ... 同上
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success) {
                var result = JsonUtility.FromJson<ChatResult>(req.downloadHandler.text);
                onReply?.Invoke(result.reply);
            }
        }
    }
}
```

### 5\.3 配置说明

**Python 端启动**：

```Bash
cd D:\ai-town-untiy\server
python ai_town_server.py
# 或直接双击 server\start_server.bat
# 服务运行在 http://localhost:8765（自包含 nlp.py/json_gen.py，无 luanti-builder 依赖）
```

**Unity 端 API 地址配置**：

- 在 `ApiClient.cs` 中设置 `baseUrl = "http://localhost:8765"`

- 打包后如果 Python 服务在另一台机器，改成对应 IP

---

## 六、资源迁移清单

### 6\.1 可直接复用的资源

|资源|原路径|迁移到 Unity|工作量|
|---|---|---|---|
|乐高纹理（173张）|`lego_style/textures/`|`Assets/Textures/Lego/`|导入 \+ 设置，1小时|
|NPC 皮肤纹理|`ai_town/textures/`|`Assets/Textures/NPC/`|导入，10分钟|
|建筑坐标数据|`my_first_mod/init.lua`（上海/村庄）|提取为 JSON|写脚本提取，1小时|
|建筑生成算法|`lb_pkg/lua_gen.py`|复用（Python端）|0|
|大模型对接|`lb_pkg/llm.py`|复用（Python端）|0|
|NPC 记忆设计|`ai_town/init.lua`|翻译为 Python|参考翻译，半天|

### 6\.2 需要新建的资源

|资源|说明|获取方式|
|---|---|---|
|NPC 3D 模型|人形角色|Asset Store 免费资源 / Capsule 占位|
|NPC 动画|Idle / Walk|随模型自带 / 用 Starter Assets 动画|
|场景天空盒|天空|URP 自带 / Asset Store|
|音效（可选）|背景音乐/按钮音|免费音效库|

### 6\.3 纹理导入设置

- Texture Type：Default（用于材质）或 Sprite（用于 UI）

- Wrap Mode：Repeat

- Filter Mode：Bilinear（乐高风格可用 Point 保持像素感）

- Max Size：根据原图大小，一般 256 或 512

---

## 七、风险与预案

|风险|概率|影响|预案|
|---|---|---|---|
|NPC 模型找不到合适免费资源|中|演示效果打折扣|用 Capsule \+ 头顶名牌占位，功能优先|
|大模型 API 调用延迟高|高|等待时间长|加"思考中\.\.\."动画；关键词模板模式秒出|
|Day 3 NPC 系统做不完|中|缺少 AI 亮点|砍掉记忆系统，只保留单轮对话，亮点仍在|
|建筑形状补全超预期|中|复杂建筑生成不完整|先只做 box/cyl/sphere，复杂建筑用这三种组合|
|URP 材质兼容问题|低|材质变粉色|用 Edit → Render Pipeline → URP → Upgrade 一键升级|
|Starter Assets 输入系统不兼容|低|控制器没反应|Player Settings 切换 Active Input Handling 为 Both|
|Python 服务和 Unity 通信失败|中|无法生成建筑|先用本地 JSON 文件测试，再排查网络/防火墙|
|打包后 API 地址失效|低|打包版无法生成|做成可配置的，打包前确认 Python 服务地址|

---

## 八、验收标准

### 功能验收

* [ ] 白模场景：至少 3 种建筑（城堡/房子/塔）能正确生成

* [ ] 坐标大小：与原项目 Luanti 中的建筑尺寸一致

* [ ] 第一人称漫游：WASD 移动 \+ 鼠标视角 \+ 跳跃

* [ ] 飞行模式：按 F 切换，飞行时可上升下降

* [ ] AI 建筑生成：输入自然语言描述，调用大模型生成建筑

* [ ] 模板生成：23 种模板可快速选择生成

* [ ] NPC 对话：至少 1 个 NPC 能对话，回复来自大模型

* [ ] NPC 记忆：NPC 能记住最近的对话内容

* [ ] 昼夜切换（可选）：按键切换白天/夜晚

### 性能验收

* [ ] 场景内 1000 个方块时，帧率 ≥ 30 FPS

* [ ] 建筑生成等待时间 ≤ 10 秒（AI 模式）

* [ ] 打包后 exe 启动时间 ≤ 10 秒

### 演示验收

* [ ] 完整演示流程可顺畅走通：启动 → 生成建筑 → 漫游 → NPC 对话 → 飞行

* [ ] 打包后的 exe 能在未安装 Unity 的机器上运行

* [ ] Python 服务和 Unity 客户端可同时运行，通信正常

---

## 九、后续优化方向（4天后）

1. **体素化渲染**：用 Mesh 合并 \+ GPU Instancing 优化性能，支持万级方块

2. **方块交互**：支持放置/破坏方块，接近沙盒体验

3. **更多 NPC**：6 个角色全部实现，加自主行为和日程系统

4. **NPC 反思机制**：实现 Generative Agents 的反思架构，NPC 能总结经验

5. **对话式迭代建造**：多轮对话修改建筑（"把屋顶改成金色"）

6. **语音输入**：集成语音识别，直接说话生成建筑

7. **存档系统**：保存/加载建筑和世界状态

8. **多人联机**：支持多人同时在线建造

9. **移动端适配**：打包 Android/iOS 版本

10. **材质升级**：PBR 材质 \+ 法线贴图，提升画质

---

## 十、参考资料

- 原项目仓库：https://github\.com/cpufreestyle/luanti\-builder

- Luanti 官网：https://www\.luanti\.org/

- Unity 官方文档：https://docs\.unity3d\.com/

- URP 文档：https://docs\.unity3d\.com/Packages/com\.unity\.render\-pipelines\.universal@latest

- Starter Assets：https://assetstore\.unity\.com/packages/essentials/starter\-assets\-first\-person\-character\-controller\-196525

---

以上是完整的开发和转换文档。当前环境暂时无法创建飞书文档，你看内容是否 OK？如果需要调整或补充，告诉我；环境恢复后我立刻写入飞书文档。

