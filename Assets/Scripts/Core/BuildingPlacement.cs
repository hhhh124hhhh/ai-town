using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using StarterAssets;

/// <summary>
/// 建筑放置模式：BuildingPanel 生成建筑后进入，半透明幽灵预览随准星所指地面点移动，
/// 左键确认 / 右键或 Esc 取消 / R 旋转 90° / 滚轮 15° 微调。
/// 与已有建筑、道具或玩家重叠时幽灵变红禁止确认；
/// 有进行中委托且落点在绿圈外时提示但不禁止（提交时服务端判距离）。
/// 期间接管指针锁定（MouseLookGate 挂起，视角直通），退出后自动还原。
/// </summary>
public class BuildingPlacement : MonoBehaviour
{
    private const float RayMaxDistance = 80f;
    private const float OverlapTolerance = 0.3f;   // 重叠判定的宽容量（米）
    private const float ConfirmIgnoreTime = 0.15f; // 进入后短暂忽略确认，防面板鼠标残余点击

    /// <summary>小镇可放置范围（XZ 矩形）：按路网实测范围外扩一点，
    /// 北门(z≈+2.5)到南土路(z≈-20)、西街(x≈-15.5)到骑楼东街(x≈+26)。</summary>
    private static readonly Rect TownBounds = new Rect(-18f, -22f, 44f, 27f);

    private static readonly Color ValidColor = new Color(0.3f, 1f, 0.5f, 0.45f);
    private static readonly Color InvalidColor = new Color(1f, 0.25f, 0.2f, 0.45f);

    private static BuildingPlacement _instance;

    /// <summary>是否处于放置模式（BuildingPanel/MouseLookGate 等据此挂起自身逻辑）。</summary>
    public static bool Active => _instance != null && _instance._ghost != null;

    /// <summary>
    /// 进入放置模式。building 为已生成但尚待摆放的真实建筑（会被隐藏），
    /// 确认后以最终姿态激活并通过 onConfirmed 回调；取消则连同真实建筑一起销毁。
    /// 返回 false 表示无法进入（缺相机等），调用方应回退到旧摆放逻辑。
    /// </summary>
    public static bool Begin(GameObject building, Action<GameObject> onConfirmed)
    {
        var cam = Camera.main;
        if (building == null || cam == null) return false;

        if (_instance == null)
        {
            var go = new GameObject("_BuildingPlacement");
            _instance = go.AddComponent<BuildingPlacement>();
        }
        _instance.StartPlacement(building, cam, onConfirmed);
        return true;
    }

    private GameObject _real;
    private GameObject _ghost;
    private GameObject _ghostRoot;
    private LineRenderer _footRing;
    private Action<GameObject> _onConfirmed;

    private Transform _player;
    private StarterAssetsInputs _input;
    private Material _ghostMat;

    private Bounds _localBounds;     // 建筑在自身根节点本地空间内的包围盒
    private Vector3 _offsetLocal;    // 本地包围盒中心相对根节点的偏移
    private float _yaw;
    private float _enterTime;
    private bool _valid;
    private string _invalidReason = "";
    private string _warn = "";
    private float _scrollAccum;

    private static Material GhostMaterial
    {
        get
        {
            if (_instance._ghostMat == null)
            {
                // Sprites/Default：本项目已验证可用于自发光式纯色绘制（委托绿圈在用）
                _instance._ghostMat = new Material(Shader.Find("Sprites/Default"))
                {
                    hideFlags = HideFlags.DontSave,
                };
            }
            return _instance._ghostMat;
        }
    }

    private void StartPlacement(GameObject building, Camera cam, Action<GameObject> onConfirmed)
    {
        _real = building;
        _real.SetActive(false);
        _onConfirmed = onConfirmed;

        _player = GameObject.Find("Player")?.transform;
        _input = _player != null ? _player.GetComponent<StarterAssetsInputs>() : null;

        // 幽灵 = 真实建筑的克隆，统一换成半透明纯色材质；独立根避免被「清除全部建筑」误删
        _ghostRoot = new GameObject("_PlacementGhost");
        _ghost = Instantiate(_real);
        _ghost.name = "_Ghost_" + _real.name;
        _ghost.transform.SetParent(_ghostRoot.transform, false);
        _ghost.SetActive(true);
        foreach (var r in _ghost.GetComponentsInChildren<Renderer>(true))
        {
            r.sharedMaterial = GhostMaterial;
        }

        _localBounds = ComputeLocalBounds(_ghost.transform);
        _offsetLocal = _localBounds.center;

        // 初始朝向：门所在的 -Z 面朝向玩家（与旧摆放逻辑一致）
        Vector3 toPlayer = _player != null
            ? _player.position - _ghost.transform.position
            : Vector3.back;
        toPlayer.y = 0f;
        _yaw = toPlayer.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(-toPlayer, Vector3.up).eulerAngles.y
            : 0f;

        _footRing = CreateFootRing();
        _enterTime = Time.unscaledTime;

        // 接管指针：MouseLookGate 期间挂起，视角随鼠标直通旋转
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        if (_input != null) _input.cursorLocked = true;
    }

    private void Update()
    {
        if (_ghost == null) return;
        if (_real == null)
        {
            Cancel(); // 真实建筑被外部销毁（如清场），自动退出
            return;
        }
        if (CinematicIntro.IsCinematic || CinematicIntro.InputCooldown
            || DialogSystem.Instance != null)
        {
            return; // 演出/对话期间挂起放置输入
        }

        UpdateRotationInput();
        UpdatePoseAndValidity();
        UpdateConfirmInput();
    }

    private void UpdateRotationInput()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && kb.rKey.wasPressedThisFrame) _yaw += 90f;

        var mouse = Mouse.current;
        if (mouse != null)
        {
            // 滚轮微调：单帧增量夹到 ±1（一步 15°），兼容不同平台的滚轮刻度
            float scroll = mouse.scroll.ReadValue().y;
            if (!Mathf.Approximately(scroll, 0f))
            {
                _scrollAccum += Mathf.Clamp(scroll, -1f, 1f);
                if (Mathf.Abs(_scrollAccum) >= 1f)
                {
                    _yaw += Mathf.Sign(_scrollAccum) * 15f;
                    _scrollAccum = 0f;
                }
            }
        }
#endif
    }

    private void UpdatePoseAndValidity()
    {
        var cam = Camera.main;
        if (cam == null) return;

        _warn = "";
        _invalidReason = "";
        _valid = false;

        var ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
        Vector3? aim = null;
        if (Physics.Raycast(ray, out var hit, RayMaxDistance))
        {
            // 落点取命中处，y 夹取在地面附近（防射线打到已有建筑的墙面/楼顶把楼放上天）
            aim = new Vector3(hit.point.x, Mathf.Clamp(hit.point.y, -0.2f, 0.8f), hit.point.z);
        }
        else if (ray.direction.y < -0.05f)
        {
            // 射线打空（指到天空盒假地面）时向 y=0 地面投影兜底，幽灵继续跟准星
            float t = -ray.origin.y / ray.direction.y;
            var p = ray.origin + ray.direction * t;
            aim = new Vector3(p.x, 0f, p.z);
        }
        else
        {
            _invalidReason = "指向地面以选择落点";
            ApplyPose(null);
            return;
        }

        // 小镇边界：XZ 夹取到路网范围，防止把楼放到镇外空地
        var warns = new List<string>();
        float cx = Mathf.Clamp(aim.Value.x, TownBounds.xMin, TownBounds.xMax);
        float cz = Mathf.Clamp(aim.Value.z, TownBounds.yMin, TownBounds.yMax);
        if (cx != aim.Value.x || cz != aim.Value.z) warns.Add("已到小镇边缘");
        aim = new Vector3(cx, aim.Value.y, cz);

        ApplyPose(aim);
        if (!_valid) return;

        Bounds world = WorldBounds();
        _valid = CheckOverlap(world, out _invalidReason);

        if (_valid && CommissionSystem.Instance != null
            && CommissionSystem.Instance.TryGetActiveZone(out var zone, out float radius))
        {
            // 距离按建筑包围盒中心量（与服务端 pos + extents 语义一致取中心附近）
            Vector3 center = _ghost.transform.position
                + Quaternion.Euler(0f, _yaw, 0f) * _offsetLocal;
            float dx = center.x - zone.x;
            float dz = center.z - zone.y;
            if (Mathf.Sqrt(dx * dx + dz * dz) > radius)
            {
                warns.Add($"在绿圈外（{radius:0}m 内），验收距离可能不达标");
            }
        }
        _warn = string.Join("；", warns);
    }

    /// <summary>按落点与当前朝向摆幽灵；aim 为 null 时幽灵保持原地并标为无效。</summary>
    private void ApplyPose(Vector3? aim)
    {
        _valid = false;
        if (aim.HasValue)
        {
            var rot = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 pos = aim.Value - rot * _offsetLocal;
            pos.y = aim.Value.y - _localBounds.min.y;
            _ghost.transform.SetPositionAndRotation(pos, rot);
            _valid = true;
        }

        Color c = _valid ? ValidColor : InvalidColor;
        GhostMaterial.color = c;
        UpdateFootRing(c);
    }

    private bool CheckOverlap(Bounds world, out string reason)
    {
        // 上下都留宽容量，只做水平面（XZ）判定
        var xz = new Rect(world.min.x + OverlapTolerance, world.min.z + OverlapTolerance,
            Mathf.Max(0.01f, world.size.x - OverlapTolerance * 2f),
            Mathf.Max(0.01f, world.size.z - OverlapTolerance * 2f));

        if (_player != null)
        {
            var p = _player.position;
            if (xz.xMin < p.x && p.x < xz.xMax && xz.yMin < p.z && p.z < xz.yMax)
            {
                reason = "落点压住玩家，挪开一点";
                return false;
            }
        }

        var buildings = GameObject.Find("_Buildings");
        if (buildings != null && OverlapsAny(buildings.transform, xz, out reason)) return false;

        var props = GameObject.Find("_Props");
        if (props != null && OverlapsAny(props.transform, xz, out reason)) return false;

        reason = "";
        return true;
    }

    private static bool OverlapsAny(Transform root, Rect xz, out string reason)
    {
        foreach (Transform child in root)
        {
            if (!child.gameObject.activeInHierarchy) continue; // 待放置的真实建筑是隐藏态，跳过
            var renderers = child.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) continue;

            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            if (bounds.max.x > xz.xMin && bounds.min.x < xz.xMax
                && bounds.max.z > xz.yMin && bounds.min.z < xz.yMax)
            {
                reason = $"与「{child.name}」重叠";
                return true;
            }
        }
        reason = "";
        return false;
    }

    private void UpdateConfirmInput()
    {
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (Time.unscaledTime - _enterTime < ConfirmIgnoreTime) return;

        bool confirm = mouse != null && mouse.leftButton.wasPressedThisFrame && _valid;
        bool cancel = (mouse != null && mouse.rightButton.wasPressedThisFrame)
            || (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame);

        if (confirm) Confirm();
        else if (cancel) Cancel();
#endif
    }

    private void Confirm()
    {
        _real.transform.SetPositionAndRotation(
            _ghost.transform.position, _ghost.transform.rotation);
        _real.SetActive(true);
        Cleanup();
        _onConfirmed?.Invoke(_real);
    }

    private void Cancel()
    {
        Destroy(_real);
        Cleanup();
    }

    private void Cleanup()
    {
        if (_ghostRoot != null) Destroy(_ghostRoot);
        _ghost = null;
        _ghostRoot = null;
        _footRing = null;
        _real = null;
        _onConfirmed = null;

        // 还原指针状态，下一帧 MouseLookGate 恢复右键门控
        if (_input != null) _input.cursorLocked = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    // ── 幽灵几何辅助 ──────────────────────────────────────────────────────
    /// <summary>
    /// 自算建筑本地包围盒：未渲染过的对象 renderer.bounds 不可靠，
    /// 用 MeshFilter.sharedMesh.bounds 角点沿层级矩阵变换后封装。
    /// </summary>
    private static Bounds ComputeLocalBounds(Transform root)
    {
        var bounds = new Bounds(Vector3.zero, Vector3.zero);
        bool seeded = false;
        Accumulate(root, Matrix4x4.identity, ref bounds, ref seeded);
        return bounds;
    }

    private static void Accumulate(Transform t, Matrix4x4 parentToLocal, ref Bounds bounds, ref bool seeded)
    {
        var m = parentToLocal * Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);
        var mf = t.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            var mb = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? mb.min.x : mb.max.x,
                    (i & 2) == 0 ? mb.min.y : mb.max.y,
                    (i & 4) == 0 ? mb.min.z : mb.max.z);
                var p = m.MultiplyPoint3x4(corner);
                if (seeded) bounds.Encapsulate(p);
                else
                {
                    bounds = new Bounds(p, Vector3.zero);
                    seeded = true;
                }
            }
        }
        foreach (Transform child in t) Accumulate(child, m, ref bounds, ref seeded);
    }

    private Bounds WorldBounds()
    {
        var rot = Quaternion.Euler(0f, _yaw, 0f);
        var world = new Bounds(Vector3.zero, Vector3.zero);
        bool seeded = false;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? _localBounds.min.x : _localBounds.max.x,
                (i & 2) == 0 ? _localBounds.min.y : _localBounds.max.y,
                (i & 4) == 0 ? _localBounds.min.z : _localBounds.max.z);
            var p = _ghost.transform.position + rot * corner;
            if (seeded) world.Encapsulate(p);
            else
            {
                world = new Bounds(p, Vector3.zero);
                seeded = true;
            }
        }
        return world;
    }

    private LineRenderer CreateFootRing()
    {
        var go = new GameObject("_GhostFootRing");
        go.transform.SetParent(_ghostRoot.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.loop = true;
        lr.useWorldSpace = true;
        lr.widthMultiplier = 0.12f;
        lr.positionCount = 5;
        var shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            lr.material = new Material(shader);
            lr.startColor = ValidColor;
            lr.endColor = ValidColor;
        }
        return lr;
    }

    private void UpdateFootRing(Color color)
    {
        if (_footRing == null || _ghost == null) return;
        _footRing.startColor = color;
        _footRing.endColor = color;

        var b = WorldBounds();
        float y = _ghost.transform.position.y + _localBounds.min.y + 0.06f;
        _footRing.SetPosition(0, new Vector3(b.min.x, y, b.min.z));
        _footRing.SetPosition(1, new Vector3(b.max.x, y, b.min.z));
        _footRing.SetPosition(2, new Vector3(b.max.x, y, b.max.z));
        _footRing.SetPosition(3, new Vector3(b.min.x, y, b.max.z));
        _footRing.SetPosition(4, new Vector3(b.min.x, y, b.min.z));
    }

    // ── 放置模式 HUD（准星 + 底部提示条）─────────────────────────────────
    private void OnGUI()
    {
        if (!Active || CinematicIntro.IsCinematic) return;

        UiTheme.BeginScale();
        DrawCrosshair();
        DrawHintBar();
        UiTheme.EndScale();
    }

    private void DrawCrosshair()
    {
        float cx = UiTheme.VW / 2f;
        float cy = UiTheme.VH / 2f;
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(cx - 4f, cy - 1f, 8f, 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - 1f, cy - 4f, 2f, 8f), Texture2D.whiteTexture);
        GUI.color = new Color(1f, 1f, 1f, 0.9f);
        GUI.DrawTexture(new Rect(cx - 1.5f, cy - 1.5f, 3f, 3f), Texture2D.whiteTexture);
        GUI.color = prev;
    }

    private void DrawHintBar()
    {
        float w = 520f;
        float h = 62f;
        var rect = new Rect(UiTheme.VW / 2f - w / 2f, UiTheme.VH - h - 14f, w, h);
        GUILayout.BeginArea(rect, UiTheme.Hud);
        UiTheme.Wash(rect, 0.8f);
        GUILayout.Label("左键 放置　·　R 旋转 90°　·　滚轮 微调　·　右键/Esc 取消　·　X 回出生点", UiTheme.Text(13));

        if (!_valid)
        {
            GUILayout.Label($"<color=#9E2B25>{_invalidReason}</color>", UiTheme.Text(13));
        }
        else if (!string.IsNullOrEmpty(_warn))
        {
            GUILayout.Label($"<color=#8A5A00>{_warn}</color>", UiTheme.Text(13));
        }
        else
        {
            GUILayout.Label("<color=#5A5042>落点可用</color>", UiTheme.Text(13));
        }
        GUILayout.EndArea();
    }
}
