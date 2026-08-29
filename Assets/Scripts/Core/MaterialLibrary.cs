using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 建筑材质库：颜色 hex → 分类贴图材质（贴图×颜色 tint）。
/// 贴图为民国风无缝彩贴图（Resources/Textures/Buildings/）：
/// 青砖清水墙/红砖清水墙/木板铺面，_BaseColor 低权重 tint 保留 NLP 颜色语义。
/// 运行时与编辑器烘焙共用；玻璃/发光/树叶走特殊材质分支。
/// </summary>
public static class MaterialLibrary
{
    private enum Cat { Stone, Wood, Brick, Sand, Glass, Glow, Leaves, Flat }

    // json_gen.py 输出的固定 hex → 材质类别
    private static readonly Dictionary<string, Cat> CategoryByHex = new()
    {
        // 石材/金属/黑/白/雪 → 石墙贴图
        { "#95A5A6", Cat.Stone },  // stone / gray
        { "#85929E", Cat.Stone },  // iron
        { "#1C2833", Cat.Stone },  // black
        { "#FDFEFE", Cat.Stone },  // white（白石堡）
        { "#FBFCFC", Cat.Stone },  // snow
        // 木/泥土 → 木纹贴图
        { "#935116", Cat.Wood },   // wood
        { "#6E2C00", Cat.Wood },   // dirt / 树干
        // 砖红 → 红砖清水墙贴图
        { "#C0392B", Cat.Brick },  // red
        { "#943126", Cat.Brick },  // brick
        { "#E74C3C", Cat.Brick },  // 火箭喷口红
        { "#B22222", Cat.Brick },  // firebrick（castle.json 主色）
        { "#9E4B3A", Cat.Brick },  // 民国砖红（骑楼/洋楼模板）
        { "#7A8B8B", Cat.Stone },  // 民国青灰（骑楼柱/清水墙）
        // 沙 → 砂岩贴图
        { "#D5B895", Cat.Sand },   // sand
        // 特殊材质
        { "#D6EAF8", Cat.Glass },  // glass
        { "#F7DC6F", Cat.Glow },   // 发光砖
        { "#1E8449", Cat.Leaves }, // leaves
        { "#27AE60", Cat.Leaves }, // green
    };

    private static readonly Dictionary<string, Material> Cache = new();
    private static readonly Dictionary<string, Texture2D> _texCache = new();

    /// <summary>按 hex 取材质（同 hex 复用），未识别的颜色走"彩砖"贴图 tint。</summary>
    public static Material GetOrCreate(string hex)
    {
        string key = Normalize(hex);
        if (Cache.TryGetValue(key, out Material cached)) return cached;

        Material mat = CreateMaterialInstance(key);
        Cache[key] = mat;
        return mat;
    }

    /// <summary>创建新材质实例（编辑器烘焙路径用：创建后另存为 .mat 资产）。</summary>
    public static Material CreateMaterialInstance(string hex)
    {
        string key = Normalize(hex);
        ShapeFactory.TryParseColor(key, out Color color);

        Cat cat = CategoryByHex.TryGetValue(key, out Cat c)
            ? c
            : ClassifyByColor(color); // 未注册颜色按色相/明度兜底
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        var mat = new Material(shader);

        switch (cat)
        {
            case Cat.Stone:
                SetupTextured(mat, "minguo_greybrick", color, smoothness: 0.25f, tiling: 2);
                break;
            case Cat.Wood:
                SetupTextured(mat, "minguo_wood", color, smoothness: 0.3f, tiling: 2);
                break;
            case Cat.Brick:
                SetupTextured(mat, "minguo_redbrick", color, smoothness: 0.2f, tiling: 2);
                break;
            case Cat.Sand:
                SetupTextured(mat, "sandstone", color, smoothness: 0.35f, tiling: 2);
                break;
            case Cat.Glass:
                mat.SetColor("_BaseColor", new Color(color.r, color.g, color.b, 0.45f));
                SetTransparent(mat);
                mat.SetFloat("_Smoothness", 0.9f);
                break;
            case Cat.Glow:
                // 招牌/匾额：暖黄底色 + 温和自发光（0.55x）。
                // 旧 2.2x 纯白过曝——远处就是一块无字白板，观众读成"UI 没加载"。
                mat.SetColor("_BaseColor", new Color(1f, 0.82f, 0.45f));
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", new Color(1f, 0.72f, 0.35f) * 0.55f);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                break;
            case Cat.Leaves:
            case Cat.Flat:
            default:
                mat.SetColor("_BaseColor", color);
                mat.SetFloat("_Smoothness", 0.3f);
                break;
        }
        return mat;
    }

    private static void SetupTextured(Material mat, string textureName, Color tint, float smoothness, float tiling)
    {
        Texture2D tex = LoadTexture(textureName);
        if (tex != null)
        {
            mat.SetTexture("_BaseMap", tex);
            // 按分类统一平铺：大面上砖块密度稳定，远处不至于退化成纯色
            mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
        }
        // 民国贴图自带色彩：tint 低权重叠加，保留贴图砖色同时区分用户语义色
        mat.SetColor("_BaseColor", Color.Lerp(Color.white, tint, 0.35f));
        mat.SetFloat("_Smoothness", smoothness);
    }

    /// <summary>固定映射表未命中的颜色按 HSV 兜底：灰→石、棕→木、红→砖、金黄→沙。</summary>
    private static Cat ClassifyByColor(Color color)
    {
        Color.RGBToHSV(color, out float h, out float s, out float v);
        if (s < 0.25f) return Cat.Stone;                  // 低饱和（灰/黑/白）→ 石
        if (h >= 0.11f && h < 0.17f) return Cat.Sand;     // 金黄 → 沙
        if (h < 0.03f || h > 0.95f) return Cat.Brick;     // 正红 → 砖
        if (h >= 0.03f && h < 0.11f) return Cat.Wood;     // 棕橙 → 木
        return Cat.Brick;                                 // 其余默认砖
    }

    private static Texture2D LoadTexture(string name)
    {
        if (_texCache.TryGetValue(name, out Texture2D cached)) return cached;

        Texture2D tex = Resources.Load<Texture2D>($"Textures/Buildings/{name}");
        if (tex != null) _texCache[name] = tex;
        else Debug.LogWarning($"[MaterialLibrary] 缺少贴图 Resources/Textures/Buildings/{name}.png，回退纯色");
        return tex;
    }

    private static void SetTransparent(Material mat)
    {
        mat.SetFloat("_Surface", 1f); // Transparent
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        // URP 需开启 _PremultiplyAlpha 关键字路径由 shader 自理，常规透明到此即可
    }

    private static string Normalize(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return "#FFFFFF";
        string h = hex.Trim();
        if (!h.StartsWith("#")) h = "#" + h;
        return h.ToUpperInvariant();
    }
}
