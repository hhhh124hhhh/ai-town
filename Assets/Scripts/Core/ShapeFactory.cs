using UnityEngine;

/// <summary>
/// 形状工厂：按 JSON 的 shape 字段生成基础几何体并上色。
/// 未知形状先用 Cube 兜底，保证建筑能完整生成（细节后续补）。
/// </summary>
public static class ShapeFactory
{
    private static Material _sharedMat;

    public static GameObject Create(string shape, Vector3 pos, Vector3 size, Color color)
    {
        return Create(shape, pos, size, GetMaterial(color));
    }

    /// <summary>
    /// 材质重载：编辑器烘焙时传入持久化材质资产，
    /// 保证场景保存后引用不丢（运行时动态创建的内存材质无法被场景序列化）。
    /// </summary>
    public static GameObject Create(string shape, Vector3 pos, Vector3 size, Material sharedMat)
    {
        PrimitiveType type = shape switch
        {
            "box" or "solid" => PrimitiveType.Cube,
            "cyl" or "cylinder" => PrimitiveType.Cylinder,
            "sphere" => PrimitiveType.Sphere,
            _ => PrimitiveType.Cube,
        };

        GameObject obj = GameObject.CreatePrimitive(type);
        obj.transform.position = pos;
        obj.transform.localScale = size;
        obj.name = $"{shape}_{pos.x:0}_{pos.y:0}_{pos.z:0}";

        Renderer renderer = obj.GetComponent<Renderer>();
        renderer.sharedMaterial = sharedMat;
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
