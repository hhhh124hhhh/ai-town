using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 特效统一入口：运行时按 key 从 Resources/Effects 实例化一次性特效并自动销毁。
/// 常驻氛围型（篝火/烟囱/烛光萤点）由编辑器菜单 Tools→AI Town→Place Ambient FX 落进
/// 场景（_AmbientFX 节点），不经过这里；两处共用同一批 prefab 资产。
/// </summary>
public static class EffectsCatalog
{
    private const string RootPath = "Effects/";

    /// <summary>一次性特效自毁时长（秒）：confetti 全程 ~2.6s、dust ~1s，3s 收尾都安全。</summary>
    private const float DefaultLifetime = 3f;

    /// <summary>建造落尘（建筑落地 / 开场生长里程碑）。</summary>
    public const string Dust = "dust";

    /// <summary>交付庆典 confetti（委托验收通过）。</summary>
    public const string Celebration = "celebration";

    /// <summary>盖章金光迸溅（开场白落款）。</summary>
    public const string StampBurst = "stampburst";

    /// <summary>灯亮光晕（开场演出首帧，黑纱后漾开的暖金光）。</summary>
    public const string Glow = "glow";

    private static readonly Dictionary<string, GameObject> _cache = new();

    public static GameObject Play(string key, Vector3 pos, float scale = 1f)
    {
        var prefab = Load(key);
        if (prefab == null)
        {
            Debug.LogWarning($"[EffectsCatalog] 找不到特效「{key}」（Resources/{RootPath}）");
            return null;
        }

        var go = Object.Instantiate(prefab, pos, Quaternion.identity);
        go.name = "FX_" + key;
        if (!Mathf.Approximately(scale, 1f)) go.transform.localScale = Vector3.one * scale;

        // 一次性播完：特效库部分预置是循环型，这里统一关循环并起播；
        // 常驻循环型（篝火/烟/烛光）不经过 Play，不受影响
        foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            if (main.loop) main.loop = false;
            ps.Play(true);
        }

        Object.Destroy(go, DefaultLifetime);
        return go;
    }

    private static GameObject Load(string key)
    {
        if (_cache.TryGetValue(key, out var cached) && cached != null) return cached;
        var prefab = Resources.Load<GameObject>(RootPath + KeyToPrefabName(key));
        if (prefab != null) _cache[key] = prefab;
        return prefab;
    }

    private static string KeyToPrefabName(string key)
    {
        switch (key)
        {
            case Dust: return "DustDirtyPoof";
            case Celebration: return "ConfettiBlastRed";
            case StampBurst:
            case Glow: return "StarBurst2D";
            default: return key;
        }
    }
}
