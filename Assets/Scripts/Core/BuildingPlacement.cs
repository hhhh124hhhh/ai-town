using System;
using System.Collections;
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
    private const float GroundY = 0.04f;           // 落点基准地面高度（垫在路网 0.015~0.035 之上防闪面）
    private const float OverlapTolerance = 0.3f;   // 重叠判定的宽容量（米）
    private const float ConfirmIgnoreTime = 0.15f; // 进入后短暂忽略确认，防面板鼠标残余点击
    private const float MinScale = 0.5f;           // 缩放下限（Ctrl+滚轮调节）
    private const float MaxScale = 2.0f;
    private const float ScaleStep = 0.05f;
    // 障碍物脚印内收比例（每侧占其尺寸）：树冠/挑檐等悬空轮廓不该整块禁建。
    // 道具（树是大头）收 30%，建筑只收 6%（留飞檐余量，墙体仍不允许交叉）
    private const float ShrinkProps = 0.30f;
    private const float ShrinkBuildings = 0.06f;

    /// <summary>小镇可放置范围（XZ 矩形）：与 PlayerBounds 的内堤可走范围一致
    /// （护城河内堤 x-18/22、z-22/26 各内收 0.5m），北门以北的镇内空地一并开放；
    /// 东带 x>21.5 是护城河水面，不放。扩镇时与 PlayerBounds 同步改。</summary>
    private static readonly Rect TownBounds = new Rect(-17.5f, -21.5f, 39f, 47f);

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
            // Play 中途脚本重载会洗掉 static：找回场景里残留的放置实例，
            // 清掉孤儿幽灵/隐藏建筑后复用，否则会双实例抢指针
            var existing = FindFirstObjectByType<BuildingPlacement>();
            if (existing != null)
            {
                if (existing._ghostRoot != null) Destroy(existing._ghostRoot);
                if (existing._real != null) Destroy(existing._real);
                _instance = existing;
            }
        }
        if (_instance == null)
        {
            var go = new GameObject("_BuildingPlacement");
            _instance = go.AddComponent<BuildingPlacement>();
        }
        // 回车生成时输入框仍聚焦：先交还键盘焦点，放置期间的 R/Esc/X 等按键不被打字门控卡住
        UiTextFocus.Clear();
        _instance.StartPlacement(building, cam, onConfirmed);
        return true;
    }

    private GameObject _real;
    private GameObject _ghost;
    private GameObject _ghostRoot;
    private LineRenderer _footRing;
    private LineRenderer _aimMarker;                 // 地面落点菱形标记（不随建筑移动，独立指示落点）
    private LineRenderer[] _plumbLines;              // 幽灵悬空时四角到地面的墨色垂线（工程图语言）
    private Action<GameObject> _onConfirmed;

    private Transform _player;
    private StarterAssetsInputs _input;
    private Material _ghostMat;

    private Bounds _localBounds;     // 建筑在自身根节点本地空间内的包围盒
    private Vector3 _offsetLocal;    // 本地包围盒中心相对根节点的偏移
    private float _yaw;
    private float _scale = 1f;       // 当前缩放（每次进入放置重置为 1）
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
        _aimMarker = CreateAimMarker();
        _plumbLines = CreatePlumbLines();
        _yaw = 0f;
        _scale = 1f;

        // 幽灵出生即拉到玩家前方 8m 地面：进放置模式立刻可见（下一帧起准星接管），
        // 否则建筑默认点位可能在身后/远处，用户误以为"没有生成"
        if (_player != null)
        {
            Vector3 fwd = _player.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 spawn = _player.position + fwd * 8f;
            spawn.x = Mathf.Clamp(spawn.x, TownBounds.xMin, TownBounds.xMax);
            spawn.z = Mathf.Clamp(spawn.z, TownBounds.yMin, TownBounds.yMax);
            spawn.y = 0.05f;
            _ghost.transform.position = spawn;
        }

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
            // 滚轮：单帧增量夹到 ±1（一步 15°），兼容不同平台的滚轮刻度；
            // 按住 Ctrl 时改为缩放（一步 5%，0.5~2.0 倍）
            float scroll = mouse.scroll.ReadValue().y;
            if (!Mathf.Approximately(scroll, 0f))
            {
                float step = Mathf.Clamp(scroll, -1f, 1f);
                bool scaling = kb != null
                    && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
                if (scaling)
                {
                    _scale = Mathf.Clamp(_scale + step * ScaleStep, MinScale, MaxScale);
                }
                else
                {
                    _scrollAccum += step;
                    if (Mathf.Abs(_scrollAccum) >= 1f)
                    {
                        _yaw += Mathf.Sign(_scrollAccum) * 15f;
                        _scrollAccum = 0f;
                    }
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
        if (ray.direction.y < -0.001f)
        {
            // 小镇是平地：落点一律取射线与地面 y=0 的交点（贴地）。
            // 旧版用 raycast 命中点 + y 夹取，瞄到树冠/墙面时幽灵会浮到 0.8m
            float t = -ray.origin.y / ray.direction.y;
            if (t <= RayMaxDistance)
            {
                var p = ray.origin + ray.direction * t;
                aim = new Vector3(p.x, GroundY, p.z);
            }
        }
        if (!aim.HasValue)
        {
            _invalidReason = "指向地面以选择落点";
            ApplyPose(null);
            UpdateAimMarker(null);
            return;
        }

        // 小镇边界：XZ 夹取到路网范围，防止把楼放到镇外空地
        var warns = new List<string>();
        float cx = Mathf.Clamp(aim.Value.x, TownBounds.xMin, TownBounds.xMax);
        float cz = Mathf.Clamp(aim.Value.z, TownBounds.yMin, TownBounds.yMax);
        if (cx != aim.Value.x || cz != aim.Value.z) warns.Add("已到小镇边缘");
        aim = new Vector3(cx, aim.Value.y, cz);

        ApplyPose(aim);
        UpdateAimMarker(aim);
        if (!_valid) return;

        Bounds world = WorldBounds();
        _valid = CheckOverlap(world, out _invalidReason);

        if (_valid && CommissionSystem.Instance != null
            && CommissionSystem.Instance.TryGetActiveZone(out var zone, out float radius))
        {
            // 距离按建筑包围盒中心量（与服务端 pos + extents 语义一致取中心附近）
            Vector3 center = _ghost.transform.position
                + Quaternion.Euler(0f, _yaw, 0f) * (_offsetLocal * _scale);
            float dx = center.x - zone.x;
            float dz = center.z - zone.y;
            if (Mathf.Sqrt(dx * dx + dz * dz) > radius)
            {
                warns.Add($"在绿圈外（{radius:0}m 内），验收距离可能不达标");
            }
        }
        _warn = string.Join("；", warns);
    }

    /// <summary>按落点、朝向与当前缩放摆幽灵；aim 为 null 时幽灵保持原地并标为无效。</summary>
    private void ApplyPose(Vector3? aim)
    {
        _valid = false;
        _ghost.transform.localScale = Vector3.one * _scale;
        if (aim.HasValue)
        {
            var rot = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 pos = aim.Value - rot * (_offsetLocal * _scale);
            pos.y = aim.Value.y - _localBounds.min.y * _scale;
            _ghost.transform.SetPositionAndRotation(pos, rot);
            _valid = true;
        }

        Color c = _valid ? ValidColor : InvalidColor;
        GhostMaterial.color = c;
        UpdateFootRing(c);
        UpdatePlumbLines();
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
        if (buildings != null && OverlapsAny(buildings.transform, xz, ShrinkBuildings, out reason)) return false;

        var props = GameObject.Find("_Props");
        if (props != null && OverlapsAny(props.transform, xz, ShrinkProps, out reason)) return false;

        reason = "";
        return true;
    }

    private static bool OverlapsAny(Transform root, Rect xz, float shrink, out string reason)
    {
        foreach (Transform child in root)
        {
            if (!child.gameObject.activeInHierarchy) continue; // 待放置的真实建筑是隐藏态，跳过
            var renderers = child.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) continue;

            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);

            // XZ 脚印内收：树冠、飞檐等悬空部分不按整包封锁落点
            float insetX = bounds.size.x * shrink;
            float insetZ = bounds.size.z * shrink;
            var foot = Rect.MinMaxRect(
                bounds.min.x + insetX, bounds.min.z + insetZ,
                bounds.max.x - insetX, bounds.max.z - insetZ);

            if (foot.xMax > xz.xMin && foot.xMin < xz.xMax
                && foot.yMax > xz.yMin && foot.yMin < xz.yMax)
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
        Vector3 finalPos = _ghost.transform.position;
        Quaternion finalRot = _ghost.transform.rotation;
        _real.transform.SetPositionAndRotation(finalPos, finalRot);
        _real.transform.localScale = Vector3.one * _scale; // 缩放一并落到真实建筑
        _real.SetActive(true);
        var real = _real;
        Cleanup();
        StartCoroutine(ConfirmCo(real)); // 落地动画 + 落地瞬间起尘（给"砸到地面"一个物理反馈）
        _onConfirmed?.Invoke(real);
    }

    /// <summary>确认落地流程：DropIn 动画收尾时在脚印中心起一蓬尘（缩放随建筑脚印）。</summary>
    private IEnumerator ConfirmCo(GameObject building)
    {
        yield return DropIn(building);
        if (building == null) yield break;

        var renderers = building.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) yield break;

        var bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        float scale = Mathf.Clamp(Mathf.Max(bounds.size.x, bounds.size.z) / 4f, 1f, 2f);
        EffectsCatalog.Play(EffectsCatalog.Dust,
            new Vector3(bounds.center.x, 0.05f, bounds.center.z), scale);
    }

    /// <summary>
    /// 落地动画：建筑从半空落下砸到地面（0.3s 加速）。回调/绿圈/接路都按最终位置结算，
    /// 动画只是视觉层偏移；协程收尾强制回精确落点，动画误差不会渗进游戏逻辑。
    /// </summary>
    private IEnumerator DropIn(GameObject building)
    {
        const float dropHeight = 2.2f;
        const float duration = 0.3f;
        Vector3 final = building.transform.position;
        float t = 0f;
        while (t < duration && building != null)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / duration);
            k = k * k; // ease-in：越接近地面越快，落地质感
            building.transform.position = final + Vector3.up * (dropHeight * (1f - k));
            yield return null;
        }
        if (building != null) building.transform.position = final;
    }

    private void Cancel()
    {
        Destroy(_real);
        Cleanup();
    }

    private void Cleanup()
    {
        if (_ghostRoot != null) Destroy(_ghostRoot);
        if (_aimMarker != null) Destroy(_aimMarker.gameObject);
        if (_plumbLines != null)
        {
            foreach (var line in _plumbLines)
            {
                if (line != null) Destroy(line.gameObject);
            }
        }
        _ghost = null;
        _ghostRoot = null;
        _footRing = null;
        _aimMarker = null;
        _plumbLines = null;
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
            corner *= _scale; // 缩放后脚印/重叠判定按实际包围盒
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
        float y = _ghost.transform.position.y + _localBounds.min.y * _scale + 0.06f;
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

    /// <summary>
    /// 地面落点菱形标记（成熟放置系统标配：落点指示独立于建筑本体——
    /// 动森的地面网格高亮、模拟城市的 footprint 同源设计）。贴地 y=0.06，
    /// 颜色跟有效性走；准星指天时隐藏。
    /// </summary>
    private LineRenderer CreateAimMarker()
    {
        var go = new GameObject("_PlacementAimMarker");
        var lr = go.AddComponent<LineRenderer>();
        lr.loop = true;
        lr.useWorldSpace = true;
        lr.widthMultiplier = 0.08f;
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

    private void UpdateAimMarker(Vector3? aim)
    {
        if (_aimMarker == null) return;
        _aimMarker.enabled = aim.HasValue;
        if (!aim.HasValue) return;

        // 有效色随当前判定刷新（先摆姿势后刷新颜色的顺序依赖 UpdateFootRing）
        Color c = _valid ? ValidColor : InvalidColor;
        _aimMarker.startColor = c;
        _aimMarker.endColor = c;

        const float r = 0.7f;
        float y = 0.06f;
        _aimMarker.SetPosition(0, new Vector3(aim.Value.x, y, aim.Value.z - r));
        _aimMarker.SetPosition(1, new Vector3(aim.Value.x + r, y, aim.Value.z));
        _aimMarker.SetPosition(2, new Vector3(aim.Value.x, y, aim.Value.z + r));
        _aimMarker.SetPosition(3, new Vector3(aim.Value.x - r, y, aim.Value.z));
        _aimMarker.SetPosition(4, new Vector3(aim.Value.x, y, aim.Value.z - r));
    }

    /// <summary>
    /// 悬空垂线（4 条）：幽灵底部高于地面时，从脚印四角垂到地面。
    /// 工程制图的墨线语言，一眼看出"落点在哪、悬空多高"——数据异常时也兜底可见。
    /// </summary>
    private LineRenderer[] CreatePlumbLines()
    {
        var lines = new LineRenderer[4];
        var shader = Shader.Find("Sprites/Default");
        for (int i = 0; i < 4; i++)
        {
            var go = new GameObject($"_PlacementPlumb{i}");
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.03f;
            lr.positionCount = 2;
            if (shader != null)
            {
                lr.material = new Material(shader);
                var ink = new Color(0.16f, 0.15f, 0.13f, 0.75f); // 淡墨
                lr.startColor = ink;
                lr.endColor = ink;
            }
            lr.enabled = false;
            lines[i] = lr;
        }
        return lines;
    }

    private void UpdatePlumbLines()
    {
        if (_plumbLines == null || _ghost == null) return;
        float bottomY = _ghost.transform.position.y + _localBounds.min.y * _scale;
        bool show = _valid && bottomY > 0.15f; // 正常贴地（≤路网/广场垫层）不画
        var b = WorldBounds();
        var corners = new Vector3[]
        {
            new Vector3(b.min.x, bottomY, b.min.z),
            new Vector3(b.max.x, bottomY, b.min.z),
            new Vector3(b.max.x, bottomY, b.max.z),
            new Vector3(b.min.x, bottomY, b.max.z),
        };
        for (int i = 0; i < _plumbLines.Length; i++)
        {
            var lr = _plumbLines[i];
            if (lr == null) continue;
            lr.enabled = show;
            if (!show) continue;
            lr.SetPosition(0, corners[i]);
            lr.SetPosition(1, new Vector3(corners[i].x, 0.06f, corners[i].z));
        }
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
        // 设计系统收编（2026-08-29 用户"按键显示看不清"）：Hud 9-slice padding 84×2
        // 把 560 宽提示条内容区吃成 392px，长按键行必然挤压溢出（小组件禁大面板判例
        // 的漏网件）——改 PaperCard 素纸卡（纸白 0.96+细墨框），文字清晰贴系统语言
        var st = UiTheme.Text(UiTheme.SizeBody);
        var measure = new GUIStyle(st) { wordWrap = false };
        string keys = "左键 放置　·　R 旋转 90°　·　滚轮 微调　·　Ctrl+滚轮 缩放　·　右键/Esc 取消　·　X 回出生点";
        var ks = measure.CalcSize(new GUIContent(keys));
        string state = PlacementStateText();
        float w = Mathf.Max(ks.x, 560f) + 48f;
        float h = 96f;
        var rect = new Rect(UiTheme.VW / 2f - w / 2f, UiTheme.VH - h - 14f, w, h);
        GUILayout.BeginArea(rect);
        UiTheme.PaperCard(rect, 0.94f);
        GUILayout.Space(10f);
        GUILayout.Label(keys, st);
        GUILayout.Space(4f);
        GUILayout.Label(state, UiTheme.Text(UiTheme.SizeEmph));
        GUILayout.EndArea();
    }

    /// <summary>放置状态行：无效原因（朱红）/ 警告（深金）/ 可用（绿字）+ 缩放标注。</summary>
    private string PlacementStateText()
    {
        string scaleTag = _scale != 1f ? $"　·　当前 {_scale:0.00}x" : "";
        if (!_valid) return $"<color=#9E2B25>{_invalidReason}</color>{scaleTag}";
        if (!string.IsNullOrEmpty(_warn)) return $"<color=#8A5A00>{_warn}</color>{scaleTag}";
        return $"<color=#1E7A1E>落点可用——左键确认放置{scaleTag}</color>";
    }
}
