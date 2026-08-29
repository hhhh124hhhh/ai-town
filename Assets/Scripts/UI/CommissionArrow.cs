using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 委托绿圈引导箭头（2026-08-29 用户"绿圈有引导箭头吗"——HUD 文字方位要脑内换算，
/// 世界内箭头才是"识别优于回忆"）：墨色纸箭头悬浮玩家前方 3m/高 2m，始终水平指向
/// 绿圈圆心，随玩家移动/转向实时刷新；进圈 2.5m 内自动隐藏（到达即撤，不遮视线）。
/// 纯程序化 Mesh（三角箭头+尾杆，淡墨 0.85），无素材依赖；委托重建/清空时随
/// CommissionSystem 的 _zoneGuideCenter 生死。场景无关自建。
/// </summary>
public class CommissionArrow : MonoBehaviour
{
    private static CommissionArrow _instance;
    private Transform _player;
    private MeshFilter _mf;
    private MeshRenderer _mr;
    private Vector2? _target;
    private Camera _cam;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("_CommissionArrow");
        _instance = go.AddComponent<CommissionArrow>();
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = BuildArrowMesh();
        var mr = go.AddComponent<MeshRenderer>();
        var shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            mr.material = new Material(shader);
            mr.material.color = new Color(0.16f, 0.15f, 0.13f, 0.85f); // 淡墨（纸墨系统）
        }
        _instance._mf = mf;
        _instance._mr = mr;
        go.SetActive(false); // 无目标时隐形，不占渲染
    }

    /// <summary>三角箭头+尾杆组合网格（+Z 为指向，水平面内）。</summary>
    private static Mesh BuildArrowMesh()
    {
        // 顶点（XZ 平面，+Z 前）：箭头三角（0,1,2）+ 尾杆矩形（3,4,5,6）
        var verts = new Vector3[]
        {
            new(0f, 0f, 0.9f),  new(-0.42f, 0f, -0.1f), new(0.42f, 0f, -0.1f), // 头
            new(-0.16f, 0f, -0.1f), new(-0.16f, 0f, -0.9f),                     // 杆左
            new(0.16f, 0f, -0.1f),  new(0.16f, 0f, -0.9f),                      // 杆右
        };
        var tris = new int[]
        {
            0, 1, 2,        // 箭头（逆时针朝上）
            3, 4, 5,  5, 4, 6, // 尾杆两三角
        };
        var mesh = new Mesh { name = "CommissionArrow" };
        mesh.vertices = verts;
        mesh.triangles = tris;
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
            if (p == null) return;
            _player = p.transform;
        }
        if (_cam == null) _cam = Camera.main;

        bool show = _target.HasValue && _player != null
                    && !CinematicIntro.IsCinematic && !CinematicIntro.InputCooldown
                    && DialogSystem.Instance == null && !BuildingPlacement.Active;
        if (!show)
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
            return;
        }

        // 进圈即撤（到达后箭头挡视线反而碍事；HUD 的"已在绿圈内"接管）
        var pp = new Vector2(_player.position.x, _player.position.z);
        if (Vector2.Distance(pp, _target.Value) < 2.5f)
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
            return;
        }

        if (!gameObject.activeSelf) gameObject.SetActive(true);

        // 悬浮玩家前方 3m、高 2m，水平指向目标；轻微上下浮动（呼吸感，识别度高于静止）
        Vector3 dir3 = new Vector3(_target.Value.x - pp.x, 0f, _target.Value.y - pp.y);
        if (dir3.sqrMagnitude < 0.01f) return;
        dir3.Normalize();
        float bob = Mathf.Sin(Time.unscaledTime * 2.2f) * 0.08f;
        transform.position = new Vector3(
            _player.position.x + dir3.x * 3f, 2f + bob, _player.position.z + dir3.z * 3f);
        transform.rotation = Quaternion.LookRotation(dir3, Vector3.up);

        // 朝向相机的面可见性兜底：单面 Mesh 背对相机时会隐形，用双面材质更稳——
        // 这里直接按相机在箭头哪一侧翻转法线不可行（Sprites/Default 已双面），跳过
    }
}
