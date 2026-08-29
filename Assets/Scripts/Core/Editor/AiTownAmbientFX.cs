using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 常驻氛围特效一键布置（幂等，可重复执行整根重建）：
/// 篝火火焰（火堆顶）/ 小木屋烟囱青烟（屋顶）/ 广场与主路烛光萤点（黄昏氛围）。
/// 位置不写死高度——按目标道具与建筑的实际包围盒现场测量（厘米 FBX 判例：
/// 运行时测 bounds 不可靠，编辑器摆放阶段测是可靠的）。
/// 同时对特效库材质做 URP 兼容体检：Built-in 粒子 shader 在 URP 下渲成粉色，
/// 自动转 URP/Particles/Unlit 并保留贴图/色调/混合模式（Additive 系 → 加色混合）。
/// </summary>
public static class AiTownAmbientFX
{
    private const string FxRootName = "_AmbientFX";

    private const string FirePrefab = "ToonFireTorchIntenseRed";
    private const string SmokePrefab = "SmokeBurstDark";
    private const string CandlePrefab = "CandleLight2";

    // 运行时一次性特效走的同一批 Resources 副本，材质体检一并覆盖
    private static readonly string[] FxPrefabPaths =
    {
        "Assets/Resources/Effects/ToonFireTorchIntenseRed.prefab",
        "Assets/Resources/Effects/ConfettiBlastRed.prefab",
        "Assets/Resources/Effects/DustDirtyPoof.prefab",
        "Assets/Resources/Effects/SmokeBurstDark.prefab",
        "Assets/Resources/Effects/CandleLight2.prefab",
    };

    [MenuItem("Tools/AI Town/Place Ambient FX")]
    public static void Place()
    {
        var root = GameObject.Find(FxRootName);
        if (root != null) Object.DestroyImmediate(root);
        root = new GameObject(FxRootName);

        int placed = 0;
        placed += PlaceBonfireFire(root.transform);
        placed += PlaceChimneySmoke(root.transform);
        placed += PlaceCandleMotes(root.transform);

        AuditAndFixMaterials(root.transform);
        EditorSceneManager.SaveOpenScenes(); // 项目流程：落盘仍以 bridge manage_scene save 复核为准
        Debug.Log($"[AmbientFX] 常驻特效布置完成：{placed} 处（_AmbientFX，幂等可重跑）");
    }

    // ── 篝火火焰 ────────────────────────────────────────────────────────
    private static int PlaceBonfireFire(Transform root)
    {
        var bonfire = FindByNameContains(GameObject.Find("_Props")?.transform, "篝火");
        if (bonfire == null)
        {
            Debug.LogWarning("[AmbientFX] 没找到篝火道具（_Props 下名字含「篝火」）");
            return 0;
        }

        var fire = InstantiatePrefab(FirePrefab, root);
        if (fire == null) return 0;

        var logs = BoundsOf(bonfire);
        // 火焰高度 ≈ 火堆高度再冒 0.5m 内（任务卡：火焰 ≤ logs 上方 0.5m）
        float target = Mathf.Clamp(logs.size.y + 0.5f, 0.8f, 1.4f);
        var fireBounds = BoundsOf(fire.transform);
        float s = fireBounds.size.y > 0.01f ? target / fireBounds.size.y : 1f;
        fire.transform.localScale = Vector3.one * s;
        fire.transform.position = new Vector3(logs.center.x, logs.max.y, logs.center.z);
        // 底边对齐：不管 prefab 轴心在底还是中心，按实测包围盒把底面坐到火堆顶
        fire.transform.position += Vector3.up * (logs.max.y - BoundsOf(fire.transform).min.y);
        fire.name = "FX_Bonfire_Fire";
        Debug.Log($"[AmbientFX] 篝火火焰 @ {fire.transform.position} scale={s:0.00}");
        return 1;
    }

    // ── 烟囱青烟 ────────────────────────────────────────────────────────
    private static int PlaceChimneySmoke(Transform root)
    {
        var hut = FindByNameContains(GameObject.Find("_Buildings")?.transform, "小木屋");
        if (hut == null)
        {
            Debug.LogWarning("[AmbientFX] 没找到小木屋（_Buildings 下名字含「小木屋」）");
            return 0;
        }

        var smoke = InstantiatePrefab(SmokePrefab, root);
        if (smoke == null) return 0;

        // Burst 型转常驻：loop=true 周期性冒一蓬，起始延迟随机化避免机械感
        foreach (var ps in smoke.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            main.loop = true;
            main.startDelay = Random.Range(0f, Mathf.Max(0.5f, main.duration));
        }

        var roof = BoundsOf(hut);
        var smokeBounds = BoundsOf(smoke.transform);
        float s = smokeBounds.size.y > 0.01f ? Mathf.Clamp(2.2f / smokeBounds.size.y, 0.5f, 2f) : 1f;
        smoke.transform.localScale = Vector3.one * s;
        smoke.transform.position = new Vector3(roof.center.x, roof.max.y, roof.center.z);
        smoke.transform.position += Vector3.up * (roof.max.y - BoundsOf(smoke.transform).min.y);
        smoke.name = "FX_Hut_Smoke";
        Debug.Log($"[AmbientFX] 烟囱青烟 @ {smoke.transform.position} scale={s:0.00}");
        return 1;
    }

    // ── 烛光萤点（广场 + 主路，黄昏氛围点缀）────────────────────────────
    private static readonly Vector3[] MoteSpots =
    {
        new Vector3(-2.6f, 0.55f, -9.2f),
        new Vector3(1.6f, 0.75f, -6.2f),
        new Vector3(0.6f, 0.45f, -10.6f),
        new Vector3(8.5f, 0.60f, -9.0f), // NS 街上空
    };

    private static int PlaceCandleMotes(Transform root)
    {
        int n = 0;
        for (int i = 0; i < MoteSpots.Length; i++)
        {
            var mote = InstantiatePrefab(CandlePrefab, root);
            if (mote == null) return n;
            mote.transform.position = MoteSpots[i];
            mote.transform.localScale = Vector3.one * 0.5f;
            mote.name = $"FX_Mote_{i + 1}";
            n++;
        }
        Debug.Log($"[AmbientFX] 烛光萤点 {n} 处");
        return n;
    }

    // ── URP 兼容体检（粉色材质自动转换）────────────────────────────────
    private static void AuditAndFixMaterials(Transform sceneRoot)
    {
        var map = new Dictionary<Material, Material>();
        var guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets/TJGeneratorLibEffects" });
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || IsUrPOk(mat.shader.name)) continue;

            Debug.Log($"[AmbientFX] 非 URP 材质「{mat.name}」shader={mat.shader.name} → 转 URP/Particles/Unlit");
            var convertedPath = path.Replace(".mat", "_URP.mat");
            var converted = AssetDatabase.LoadAssetAtPath<Material>(convertedPath);
            if (converted == null)
            {
                converted = ConvertToUrP(mat);
                AssetDatabase.CreateAsset(converted, convertedPath);
            }
            map[mat] = converted;
        }

        if (map.Count == 0)
        {
            Debug.Log("[AmbientFX] URP 体检：特效库材质全部兼容，无需转换");
            return;
        }

        int fixedCount = ReplaceMaterials(FxPrefabPaths, map);
        fixedCount += ReplaceSceneRenderers(sceneRoot, map);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AmbientFX] URP 体检完成，替换 {fixedCount} 处材质引用");
    }

    private static bool IsUrPOk(string shaderName)
    {
        return shaderName.Contains("Universal Render Pipeline")
               || shaderName.Contains("URP")
               || shaderName == "Sprites/Default";
    }

    private static int ReplaceMaterials(string[] prefabPaths, Dictionary<Material, Material> map)
    {
        int count = 0;
        foreach (var prefabPath in prefabPaths)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) continue;
            foreach (var r in prefab.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (r.sharedMaterial != null && map.TryGetValue(r.sharedMaterial, out var rep))
                {
                    r.sharedMaterial = rep;
                    EditorUtility.SetDirty(r);
                    count++;
                }
                if (r.trailMaterial != null && map.TryGetValue(r.trailMaterial, out var repT))
                {
                    r.trailMaterial = repT;
                    EditorUtility.SetDirty(r);
                    count++;
                }
            }
        }
        return count;
    }

    private static int ReplaceSceneRenderers(Transform sceneRoot, Dictionary<Material, Material> map)
    {
        int count = 0;
        foreach (var r in sceneRoot.GetComponentsInChildren<ParticleSystemRenderer>(true))
        {
            if (r.sharedMaterial != null && map.TryGetValue(r.sharedMaterial, out var rep))
            {
                r.sharedMaterial = rep;
                EditorUtility.SetDirty(r);
                count++;
            }
        }
        return count;
    }

    /// <summary>Built-in 粒子材质 → URP/Particles/Unlit：保留贴图与色调，Additive 系转加色混合。</summary>
    private static Material ConvertToUrP(Material src)
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null)
        {
            Debug.LogError("[AmbientFX] 找不到 Universal Render Pipeline/Particles/Unlit");
            return src;
        }

        var m = new Material(shader) { name = src.name + "_URP" };
        Texture tex = null;
        if (src.HasProperty("_MainTex")) tex = src.GetTexture("_MainTex");
        else if (src.HasProperty("_BaseMap")) tex = src.GetTexture("_BaseMap");
        if (tex != null) m.mainTexture = tex;

        Color tint = Color.white;
        if (src.HasProperty("_TintColor")) tint = src.GetColor("_TintColor");
        else if (src.HasProperty("_Color")) tint = src.GetColor("_Color");
        else if (src.HasProperty("_BaseColor")) tint = src.GetColor("_BaseColor");
        m.SetColor("_BaseColor", tint);

        bool additive = src.shader.name.Contains("Additive");
        m.SetFloat("_Surface", 1f);                    // transparent
        m.SetFloat("_Blend", additive ? 2f : 0f);      // 2=Additive 0=Alpha
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", additive ? (int)BlendMode.One : (int)BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent;
        return m;
    }

    // ── 辅助 ────────────────────────────────────────────────────────────
    private static GameObject InstantiatePrefab(string prefabName, Transform parent)
    {
        var prefab = Resources.Load<GameObject>("Effects/" + prefabName);
        if (prefab == null)
        {
            Debug.LogWarning($"[AmbientFX] Resources/Effects 缺 {prefabName}");
            return null;
        }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        go.name = prefabName;
        return go;
    }

    private static Bounds BoundsOf(Transform t)
    {
        var bounds = new Bounds(t.position, Vector3.zero);
        bool seeded = false;
        foreach (var r in t.GetComponentsInChildren<Renderer>())
        {
            if (seeded) bounds.Encapsulate(r.bounds);
            else
            {
                bounds = r.bounds;
                seeded = true;
            }
        }
        return bounds;
    }

    private static Transform FindByNameContains(Transform parent, string keyword)
    {
        if (parent == null) return null;
        foreach (Transform child in parent)
        {
            if (child.name.Contains(keyword)) return child;
        }
        return null;
    }
}
