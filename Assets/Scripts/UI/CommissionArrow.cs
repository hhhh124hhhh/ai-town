using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 运行时特效材质工厂：统一走 URP/Particles/Unlit（本项目 Deferred 渲染器下，
/// 内置 Sprites/Default 属未审计 shader——同族 Particles/Unlit 曾渲染洋红，
/// 效果库当年全部转 URP 才亮，判例 2026-08-28）。URP/Particles/Unlit 支持顶点色
/// （LineRenderer 渐变可用），_BaseColor 为主色。找不到时回退 Sprites/Default。
/// </summary>
public static class RuntimeFxMat
{
    public static Material Make(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader != null)
        {
            var m = new Material(shader);
            m.SetColor("_BaseColor", color);
            return m;
        }
        var fallback = new Material(Shader.Find("Sprites/Default"));
        fallback.color = color;
        return fallback;
    }
}

/// <summary>
/// 委托绿圈引导箭头（2026-08-29 用户"绿圈有引导箭头吗"——HUD 文字方位要脑内换算，
/// 世界内箭头才是"识别优于回忆"）：墨色纸箭头悬浮玩家前方 3m/高 2.2m，始终水平指向
/// 绿圈圆心，随玩家移动/转向实时刷新；进圈 2.5m 内自动隐藏（到达即撤，不遮视线）。
/// 十字翼程序化 Mesh（水平+垂直双面交叉，任意俯仰角至少一面正对视线——单张水平薄片
/// 会被近水平的玩家视线侧对成一条线），淡墨 0.85 无素材依赖。
///
/// 生存铁律：本物体永不 SetActive(false)——SetActive 会禁用 LateUpdate，
/// 从此无人再把它激活（2026-08-29 "箭头从未出现"根因：Bootstrap 时禁用即死锁）。
/// 显隐一律走 _mr.enabled。
/// </summary>
public class CommissionArrow : MonoBehaviour
{
    private static CommissionArrow _instance;
    private Transform _player;
    private MeshRenderer _mr;
    private Vector2? _target;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null) return;
        var go = new GameObject("_CommissionArrow");
        _instance = go.AddComponent<CommissionArrow>();
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = BuildArrowMesh();
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = RuntimeFxMat.Make(new Color(0.16f, 0.15f, 0.13f, 0.85f)); // 淡墨（纸墨系统）
        _instance._mr = mr;
        mr.enabled = false; // 初始隐藏（渲染级，LateUpdate 保持存活）
    }

    /// <summary>十字翼箭头：同一箭形在水平面（XZ）+ 垂直面（XY）各一份、同指 +Z。
    /// 玩家视线接近水平，单张水平薄片会被侧对成一条线，交叉翼保证任意俯仰角可见。</summary>
    private static Mesh BuildArrowMesh()
    {
        var verts = new List<Vector3>(14);
        var tris = new List<int>(18);

        // 箭形轮廓（指向 +Z，横向展幅 0.42）：头三角 + 尾杆矩形，给定平面写入
        void AddArrow(bool vertical)
        {
            Vector3 P(float along, float side) => vertical
                ? new Vector3(0f, side, along)   // XY 竖直面
                : new Vector3(side, 0f, along);  // XZ 水平面
            int b = verts.Count;
            verts.Add(P(0.9f, 0f)); verts.Add(P(-0.1f, -0.42f)); verts.Add(P(-0.1f, 0.42f)); // 头
            verts.Add(P(-0.1f, -0.16f)); verts.Add(P(-0.9f, -0.16f));                        // 杆
            verts.Add(P(-0.1f, 0.16f)); verts.Add(P(-0.9f, 0.16f));
            tris.AddRange(new[] { b, b + 1, b + 2, b + 3, b + 4, b + 5, b + 5, b + 4, b + 6 });
        }
        AddArrow(false);
        AddArrow(true);

        var mesh = new Mesh { name = "CommissionArrow" };
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        return mesh;
    }

    private void LateUpdate()
    {
        var commission = CommissionSystem.Instance;
        _target = commission != null && commission.HasZoneGuide ? commission.ZoneGuideCenter : null;

        if (_player == null)
        {
            var p = GameObject.Find("Player");
            if (p == null) { if (_mr != null) _mr.enabled = false; return; }
            _player = p.transform;
        }

        bool show = _target.HasValue
                    && !CinematicIntro.IsCinematic && !CinematicIntro.InputCooldown
                    && DialogSystem.Instance == null && !BuildingPlacement.Active;
        if (!show)
        {
            if (_mr != null && _mr.enabled) _mr.enabled = false;
            return;
        }

        var pp = new Vector2(_player.position.x, _player.position.z);
        // 进圈即撤（到达后箭头挡视线反而碍事；HUD 的"已在绿圈内"接管）
        if (Vector2.Distance(pp, _target.Value) < 2.5f)
        {
            if (_mr != null && _mr.enabled) _mr.enabled = false;
            return;
        }

        // 悬浮玩家前方 3m、高 2.2m，水平指向目标；轻微上下浮动（呼吸感，识别度高于静止）
        Vector3 dir3 = new Vector3(_target.Value.x - pp.x, 0f, _target.Value.y - pp.y);
        if (dir3.sqrMagnitude < 0.01f) { if (_mr != null) _mr.enabled = false; return; }
        dir3.Normalize();
        float bob = Mathf.Sin(Time.unscaledTime * 2.2f) * 0.08f;
        transform.position = new Vector3(
            _player.position.x + dir3.x * 3f, 2.2f + bob, _player.position.z + dir3.z * 3f);
        transform.rotation = Quaternion.LookRotation(dir3, Vector3.up);
        transform.localScale = Vector3.one * 1.4f;
        if (_mr != null && !_mr.enabled) _mr.enabled = true;
    }
}
