using UnityEngine;

/// <summary>
/// 形状工厂：按 JSON 的 shape 字段生成基础几何体并上色。
/// box/cyl/sphere 用内置 Primitive；cone/pyramid/dome 用程序化 Mesh；
/// arch/stairs 用 Primitive 组合。未知形状用 Cube 兜底。
/// </summary>
public static class ShapeFactory
{
    private static Material _sharedMat;

    public static GameObject Create(string shape, Vector3 pos, Vector3 size, Color color)
    {
        return Create(shape, pos, size, GetMaterial(color));
    }

    /// <summary>hex 入口（推荐）：走 MaterialLibrary 分类贴图材质（石/木/砖/砂+玻璃/发光）。</summary>
    public static GameObject Create(string shape, Vector3 pos, Vector3 size, string colorHex)
    {
        return Create(shape, pos, size, MaterialLibrary.GetOrCreate(colorHex));
    }

    /// <summary>
    /// 材质重载：编辑器烘焙时传入持久化材质资产，
    /// 保证场景保存后引用不丢（运行时动态创建的内存材质无法被场景序列化）。
    /// </summary>
    public static GameObject Create(string shape, Vector3 pos, Vector3 size, Material sharedMat)
    {
        GameObject obj;
        // Unity 内置 Cylinder 网格本身高 2 个单位（y∈[-1,1]），其余原始体高/径均为 1。
        // 建筑契约是 pos=方块中心、size=实际尺寸（米）：圆柱纵向缩放减半，否则块会
        // 向下多埋一半高度，放置系统的包围盒贴地会把整栋建筑抬到半空。
        bool unityCylinder = false;
        switch (shape)
        {
            case "box" or "solid":
                obj = CreatePrimitive(PrimitiveType.Cube);
                break;
            case "cyl" or "cylinder":
                obj = CreatePrimitive(PrimitiveType.Cylinder);
                unityCylinder = true;
                break;
            case "sphere":
                obj = CreatePrimitive(PrimitiveType.Sphere);
                break;
            case "cone":
                obj = CreateCone();
                break;
            case "pyramid":
                obj = CreatePyramid();
                break;
            case "dome":
                obj = CreateDome();
                break;
            case "arch":
                obj = CreateArch(sharedMat);
                break;
            case "stairs":
                obj = CreateStairs(sharedMat);
                break;
            default:
                obj = CreatePrimitive(PrimitiveType.Cube);
                break;
        }

        if (obj != null)
        {
            obj.transform.position = pos;
            obj.transform.localScale = unityCylinder
                ? new Vector3(size.x, size.y * 0.5f, size.z)
                : size;
            obj.name = $"{shape}_{pos.x:0}_{pos.y:0}_{pos.z:0}";

            Renderer renderer = obj.GetComponentInChildren<Renderer>();
            if (renderer != null) renderer.sharedMaterial = sharedMat;
        }
        return obj;
    }

    private static GameObject CreatePrimitive(PrimitiveType type)
    {
        GameObject obj = GameObject.CreatePrimitive(type);
        return obj;
    }

    /// <summary>圆锥（底面半径 0.5、高 1 且轴心在几何中心 y∈[-0.5,0.5]，
    /// 与内置 Primitive 同规格，靠 lossScale 缩放；中心语义 pos=块中心）。</summary>
    private static GameObject CreateCone()
    {
        return BuildMeshObject(MeshBuilder.Cone(segments: 16, radius: 0.5f, height: 1f, cap: true));
    }

    /// <summary>四棱锥（金字塔）。</summary>
    private static GameObject CreatePyramid()
    {
        return BuildMeshObject(MeshBuilder.Pyramid(baseHalf: 0.5f, height: 1f));
    }

    /// <summary>半球穹顶（半径 0.5，底面贴块盒底 y=-0.5、顶 y=0——中心语义下穹顶天然只占块盒下半）。</summary>
    private static GameObject CreateDome()
    {
        return BuildMeshObject(MeshBuilder.Dome(segments: 16, rings: 8, radius: 0.5f));
    }

    /// <summary>拱门：左右立柱 + 顶部横梁三块 box 组合。</summary>
    private static GameObject CreateArch(Material sharedMat)
    {
        var root = new GameObject("arch");
        float w = 1f, h = 1f, t = 0.25f;

        var left = GameObject.CreatePrimitive(PrimitiveType.Cube);
        left.transform.SetParent(root.transform, false);
        left.transform.localPosition = new Vector3(-(w / 2 - t / 2), h / 2, 0);
        left.transform.localScale = new Vector3(t, h, w);
        left.name = "post_l";

        var right = GameObject.CreatePrimitive(PrimitiveType.Cube);
        right.transform.SetParent(root.transform, false);
        right.transform.localPosition = new Vector3(w / 2 - t / 2, h / 2, 0);
        right.transform.localScale = new Vector3(t, h, w);
        right.name = "post_r";

        var top = GameObject.CreatePrimitive(PrimitiveType.Cube);
        top.transform.SetParent(root.transform, false);
        top.transform.localPosition = new Vector3(0, h - t / 2, 0);
        top.transform.localScale = new Vector3(w, t, w);
        top.name = "beam";

        return root;
    }

    /// <summary>楼梯：8 级台阶 box 组合，坡向 +Z。</summary>
    private static GameObject CreateStairs(Material sharedMat)
    {
        var root = new GameObject("stairs");
        const int steps = 8;

        for (int i = 0; i < steps; i++)
        {
            var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
            step.transform.SetParent(root.transform, false);
            step.transform.localPosition = new Vector3(0, (i + 0.5f) / steps, (i + 0.5f) / steps);
            step.transform.localScale = new Vector3(1f, 1f / steps, 1f / steps);
            step.name = $"step_{i}";
        }
        return root;
    }

    private static GameObject BuildMeshObject(Mesh mesh)
    {
        var obj = new GameObject("mesh_shape");
        var mf = obj.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>();
        obj.AddComponent<MeshCollider>().sharedMesh = mesh;
        return obj;
    }

    /// <summary>
    /// 每种颜色共享一个材质实例，避免上千方块时材质爆炸。
    /// </summary>
    private static Material GetMaterial(Color color)
    {
        if (_sharedMat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            _sharedMat = new Material(shader);
        }
        Material mat = new Material(_sharedMat);
        mat.color = color;
        return mat;
    }

    public static bool TryParseColor(string hex, out Color color)
    {
        if (string.IsNullOrEmpty(hex))
        {
            color = Color.white;
            return false;
        }
        return ColorUtility.TryParseHtmlString(hex, out color);
    }
}

/// <summary>程序化 Mesh 构建器：法线朝外，供 ShapeFactory 的 cone/pyramid/dome 使用。</summary>
internal static class MeshBuilder
{
    public static Mesh Cone(int segments, float radius, float height, bool cap)
    {
        var mesh = new Mesh { name = "cone" };
        var verts = new System.Collections.Generic.List<Vector3>();
        var tris = new System.Collections.Generic.List<int>();

        // 轴心居中：底面 -h/2、顶点 +h/2（json 端 _hex_cone 按 pos=块中心 出坐标）
        float half = height * 0.5f;
        verts.Add(new Vector3(0, half, 0)); // 顶点
        for (int i = 0; i <= segments; i++)
        {
            float a = i / (float)segments * Mathf.PI * 2f;
            verts.Add(new Vector3(Mathf.Cos(a) * radius, -half, Mathf.Sin(a) * radius));
        }
        for (int i = 1; i <= segments; i++)
        {
            tris.Add(0); tris.Add(i); tris.Add(i + 1);
        }
        if (cap)
        {
            int center = verts.Count;
            verts.Add(new Vector3(0, -half, 0));
            for (int i = 1; i <= segments; i++)
            {
                tris.Add(center); tris.Add(i + 1); tris.Add(i);
            }
        }

        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    public static Mesh Pyramid(float baseHalf, float height)
    {
        var mesh = new Mesh { name = "pyramid" };
        // 轴心居中：底面 -h/2、塔尖 +h/2
        Vector3 apex = new Vector3(0, height * 0.5f, 0);
        float baseY = -height * 0.5f;
        Vector3 b0 = new Vector3(-baseHalf, baseY, -baseHalf);
        Vector3 b1 = new Vector3(baseHalf, baseY, -baseHalf);
        Vector3 b2 = new Vector3(baseHalf, baseY, baseHalf);
        Vector3 b3 = new Vector3(-baseHalf, baseY, baseHalf);

        mesh.vertices = new[] { b0, b1, b2, b3, apex };
        mesh.triangles = new[]
        {
            0, 1, 4,  // -Z 面
            1, 2, 4,  // +X 面
            2, 3, 4,  // +Z 面
            3, 0, 4,  // -X 面
            1, 0, 3,  // 底面一半
            1, 3, 2,  // 底面另一半
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    public static Mesh Dome(int segments, int rings, float radius)
    {
        var mesh = new Mesh { name = "dome" };
        var verts = new System.Collections.Generic.List<Vector3>();
        var tris = new System.Collections.Generic.List<int>();

        for (int r = 0; r <= rings; r++)
        {
            float phi = r / (float)rings * Mathf.PI / 2f; // 0..90°
            float y = Mathf.Sin(phi) * radius - radius;   // 底面 -radius，顶 +0
            float ringR = Mathf.Cos(phi) * radius;
            for (int s = 0; s <= segments; s++)
            {
                float a = s / (float)segments * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a) * ringR, y, Mathf.Sin(a) * ringR));
            }
        }
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                int i0 = r * (segments + 1) + s;
                int i1 = i0 + 1;
                int i2 = i0 + segments + 1;
                int i3 = i2 + 1;
                tris.Add(i0); tris.Add(i2); tris.Add(i1);
                tris.Add(i1); tris.Add(i2); tris.Add(i3);
            }
        }

        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
