using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem; // 项目启用新 Input System，U 键撑伞切换走 Keyboard.current
using StarterAssets;
using UnityEngine.Rendering.Universal; // URP Overlay 相机栈：手臂专用相机，防穿模不挡场景

/// <summary>
/// 第一人称手臂 + 油纸伞（挂在 Player 上，相机子层运行时自建）。
/// 组成：程序化右臂（前臂布袖 + 袖口 + 握伞手）+ 收拢/撑开双态油纸伞 +
/// ArmCamera（URP Overlay，只渲染 FirstPersonArm 层——手臂永不穿墙、不被世界遮挡）。
/// 行走正弦摆动（速度参照物）；U 键撑伞/收伞；PlayBuild/PlayGrab 供建造流程触发
/// 打击感动作；CinematicIntro 演出期整体屏蔽（显示层与输入锁成对）。
/// 类名保持 HeldItemUmbrella：场景序列化引用兼容，已挂 Player，无需重接。
/// </summary>
public class HeldItemUmbrella : MonoBehaviour
{
    [Header("Layout 收拢态（相机局部坐标）")]
    public Vector3 HoldOffset = new Vector3(0.42f, -0.46f, 0.72f);
    public Vector3 HoldRotation = new Vector3(-16f, 12f, 9f);

    [Header("Layout 撑开态（U 键切换，伞面倒向右上不挡准星）")]
    public Vector3 OpenOffset = new Vector3(0.40f, -0.44f, 0.66f);
    public Vector3 OpenRotation = new Vector3(-38f, 16f, 18f);

    [Header("ArmCamera 双相机防穿模")]
    [Tooltip("手臂相机 FOV，比主相机略大避免手部变形")]
    public float ArmFov = 80f;
    [Tooltip("近裁剪面压到 0.01，贴近镜头的手不穿墙")]
    public float ArmNearClip = 0.01f;

    [Header("Swing 摆动")]
    [Tooltip("走路摆动幅度（米）")]
    public float SwingAmplitude = 0.045f;
    [Tooltip("走路摆动角幅度（度）")]
    public float SwingTilt = 4.5f;
    [Tooltip("待机呼吸幅度（米）")]
    public float IdleAmplitude = 0.006f;

    [Header("Colors")]
    public Color CanopyColor = new Color(0.16f, 0.24f, 0.45f);   // 靛蓝伞面
    public Color BambooColor = new Color(0.62f, 0.50f, 0.33f);   // 竹黄伞骨
    public Color SkinColor = new Color(0.85f, 0.66f, 0.52f);     // 手部肤色
    public Color SleeveColor = new Color(0.19f, 0.23f, 0.31f);   // 靛青粗布袖
    public Color TrimColor = new Color(0.42f, 0.37f, 0.28f);     // 袖口布包边

    public static HeldItemUmbrella Instance { get; private set; }
    public bool IsUmbrellaOpen { get; private set; }

    private Transform _held;
    private GameObject _furledGroup;
    private GameObject _openGroup;
    private CharacterController _controller;
    private StarterAssetsInputs _input;
    private Vector3 _basePos;
    private Quaternion _baseRot;
    private float _phase;
    private bool _punching;
    private bool _switching;
    private bool _layerWarned;
    private Camera _armCam;
    private int _armLayer = -1;
    private Material _canopyMat;
    private Material _bambooMat;
    private Material _skinMat;
    private Material _sleeveMat;
    private Material _trimMat;

    private void Awake()
    {
        Instance = this;
        _controller = GetComponent<CharacterController>();
        _input = GetComponent<StarterAssetsInputs>();

        var cameraRoot = transform.Find("PlayerCameraRoot");
        if (cameraRoot == null)
        {
            Debug.LogError("[HeldItemUmbrella] 找不到 PlayerCameraRoot");
            enabled = false;
            return;
        }

        _armLayer = LayerMask.NameToLayer("FirstPersonArm");

        _held = BuildArmAndUmbrella().transform;
        _held.SetParent(cameraRoot, false);
        if (_armLayer >= 0)
        {
            SetLayerRecursive(_held, _armLayer);
        }
        _held.localPosition = HoldOffset;
        _held.localRotation = Quaternion.Euler(HoldRotation);
        _basePos = _held.localPosition;
        _baseRot = _held.localRotation;

        EnsureArmCamera();
    }

    private void Update()
    {
        // ArmCamera 懒建：首帧 Camera.main 可能未就绪
        if (_armCam == null && _armLayer >= 0)
        {
            EnsureArmCamera();
        }

        // 演出期（黑屏→按任意键开始）屏蔽显示层；输入锁与显示屏蔽成对
        bool show = !CinematicIntro.IsCinematic;
        if (_held.gameObject.activeSelf != show) _held.gameObject.SetActive(show);
        if (_armCam != null && _armCam.enabled != show) _armCam.enabled = show;
        if (!show) return;

        // U 键撑伞/收伞（演出刚结束的输入冷却期不响应；打字中不抢键）
        if (!_switching && !_punching && !CinematicIntro.InputCooldown
            && !UiTextFocus.IsTyping
            && Keyboard.current != null && Keyboard.current.uKey.wasPressedThisFrame)
        {
            ToggleUmbrella();
        }

        if (_punching || _switching) return; // 动作协程接管姿态期间暂停摆动

        float speed;
        if (_controller != null && _controller.enabled)
        {
            speed = new Vector2(_controller.velocity.x, _controller.velocity.z).magnitude;
        }
        else
        {
            // 飞行模式：CharacterController 已禁用，用输入近似速度做摆动参照
            speed = _input != null && _input.move.sqrMagnitude > 0.01f ? 5f : 0f;
        }

        if (speed > 0.3f)
        {
            // 步频随速度：约 speed/步幅(0.75m) 步每秒 × π 得摆动角频率
            _phase += (speed / 0.75f) * Mathf.PI * Time.deltaTime;
            float s = Mathf.Sin(_phase);
            float c = Mathf.Cos(_phase * 2f); // 上下是步频两倍（左右脚各一下）

            _held.localPosition = _basePos
                + new Vector3(s * SwingAmplitude * 0.6f, c * SwingAmplitude * 0.4f, 0f);
            _held.localRotation = _baseRot * Quaternion.Euler(
                s * SwingTilt * 0.4f, 0f, s * SwingTilt);
        }
        else
        {
            // 待机呼吸：慢正弦上下微浮
            _phase += 1.2f * Time.deltaTime;
            float breathe = Mathf.Sin(_phase) * IdleAmplitude;
            _held.localPosition = _basePos + new Vector3(0f, breathe, 0f);
            _held.localRotation = _baseRot;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_canopyMat != null) Destroy(_canopyMat);
        if (_bambooMat != null) Destroy(_bambooMat);
        if (_skinMat != null) Destroy(_skinMat);
        if (_sleeveMat != null) Destroy(_sleeveMat);
        if (_trimMat != null) Destroy(_trimMat);
    }

    // ── 对外动作接口（建造流程 / 后续交互调用）──

    /// <summary>建造挥动：短促前刺，配合 SFX_Build 落楼反馈。</summary>
    public void PlayBuild()
    {
        if (CinematicIntro.IsCinematic || _punching || _switching) return;
        StartCoroutine(PunchCo(
            new Vector3(-0.07f, 0.05f, -0.16f), new Vector3(-14f, -6f, -4f), 0.09f, 0.26f));
    }

    /// <summary>抓取/生成起手：手部下沉前探的预备动作。</summary>
    public void PlayGrab()
    {
        if (CinematicIntro.IsCinematic || _punching || _switching) return;
        StartCoroutine(PunchCo(
            new Vector3(-0.03f, -0.06f, -0.10f), new Vector3(10f, 0f, -6f), 0.12f, 0.30f));
    }

    /// <summary>撑伞/收伞切换（U 键）。撑开态伞面倒向右上，不挡准星。</summary>
    public void ToggleUmbrella()
    {
        SetUmbrellaOpen(!IsUmbrellaOpen);
    }

    public void SetUmbrellaOpen(bool open)
    {
        if (_switching || open == IsUmbrellaOpen || _furledGroup == null) return;
        IsUmbrellaOpen = open;
        StartCoroutine(SwitchUmbrellaCo(open));
    }

    /// <summary>双态切换：旋到目标姿态，中点换组显隐（0.32 秒一气呵成）。</summary>
    private IEnumerator SwitchUmbrellaCo(bool open)
    {
        _switching = true;
        Vector3 fromPos = _held.localPosition;
        Quaternion fromRot = _held.localRotation;
        Vector3 toPos = open ? OpenOffset : HoldOffset;
        Quaternion toRot = Quaternion.Euler(open ? OpenRotation : HoldRotation);
        const float duration = 0.32f;
        float t = 0f;
        bool swapped = false;
        while (t < duration)
        {
            t += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration));
            _held.localPosition = Vector3.Lerp(fromPos, toPos, k);
            _held.localRotation = Quaternion.Slerp(fromRot, toRot, k);
            if (!swapped && k > 0.45f)
            {
                _furledGroup.SetActive(!open);
                _openGroup.SetActive(open);
                swapped = true;
            }
            yield return null;
        }
        _held.localPosition = toPos;
        _held.localRotation = toRot;
        _furledGroup.SetActive(!open);
        _openGroup.SetActive(open);
        _basePos = toPos;
        _baseRot = toRot;
        _switching = false;
    }

    /// <summary>动作协程：抬手 upTime、回落 downTime，SmoothStep 出打击感。</summary>
    private IEnumerator PunchCo(Vector3 punchOffset, Vector3 punchEuler, float upTime, float downTime)
    {
        _punching = true;
        Vector3 fromPos = _held.localPosition;
        Quaternion fromRot = _held.localRotation;
        Vector3 toPos = fromPos + punchOffset;
        Quaternion toRot = fromRot * Quaternion.Euler(punchEuler);

        float t = 0f;
        while (t < upTime)
        {
            t += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / upTime));
            _held.localPosition = Vector3.Lerp(fromPos, toPos, k);
            _held.localRotation = Quaternion.Slerp(fromRot, toRot, k);
            yield return null;
        }
        t = 0f;
        while (t < downTime)
        {
            t += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / downTime));
            _held.localPosition = Vector3.Lerp(toPos, fromPos, k);
            _held.localRotation = Quaternion.Slerp(toRot, fromRot, k);
            yield return null;
        }
        _held.localPosition = fromPos;
        _held.localRotation = fromRot;
        _punching = false;
    }

    // ── ArmCamera：URP Overlay 相机栈 ──

    /// <summary>
    /// 主相机下建 ArmCamera（Overlay）：只渲染 FirstPersonArm 层，
    /// 主相机剔除该层——手臂永远画在场景之上，穿墙也不可见穿模。
    /// 层未创建（未跑 Setup 菜单）时回退主相机直渲，仅丢防穿模能力。
    /// </summary>
    private void EnsureArmCamera()
    {
        var main = Camera.main;
        if (main == null) return;

        if (_armLayer < 0)
        {
            if (!_layerWarned)
            {
                _layerWarned = true;
                Debug.LogWarning("[HeldItemUmbrella] 未找到 FirstPersonArm 层——先运行 Tools/AI Town/Setup First Person Arm；手臂暂由主相机直渲（可能穿墙）");
            }
            return;
        }

        var go = new GameObject("ArmCamera");
        go.transform.SetParent(main.transform, false);
        _armCam = go.AddComponent<Camera>();
        _armCam.fieldOfView = ArmFov;
        _armCam.nearClipPlane = ArmNearClip;
        _armCam.farClipPlane = 10f;
        _armCam.cullingMask = 1 << _armLayer;
        _armCam.clearFlags = CameraClearFlags.Depth;
        _armCam.enabled = !CinematicIntro.IsCinematic;

        // URP Overlay + 挂入主相机渲染栈（模板 Cockpit 双相机同款管线）
        var armData = _armCam.GetUniversalAdditionalCameraData();
        armData.renderType = CameraRenderType.Overlay;
        var mainData = main.GetUniversalAdditionalCameraData();
        mainData.cameraStack.Add(_armCam);
        main.cullingMask &= ~(1 << _armLayer);
    }

    private static void SetLayerRecursive(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
        {
            SetLayerRecursive(root.GetChild(i), layer);
        }
    }

    // ── 程序化建模 ──

    /// <summary>
    /// 右臂 + 双态伞：竹杆（共用）+ 收拢组（伞布束/骨尖/伞头帽）+
    /// 撑开组（伞面/伞骨/伞顶，默认隐藏）+ 握伞手/袖口/前臂布袖。
    /// 全程无 Collider 无 Rigidbody（防穿模铁律）。
    /// </summary>
    private GameObject BuildArmAndUmbrella()
    {
        _canopyMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _canopyMat.SetColor("_BaseColor", CanopyColor);
        _canopyMat.SetFloat("_Smoothness", 0.35f);

        _bambooMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _bambooMat.SetColor("_BaseColor", BambooColor);
        _bambooMat.SetFloat("_Smoothness", 0.2f);

        _skinMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _skinMat.SetColor("_BaseColor", SkinColor);
        _skinMat.SetFloat("_Smoothness", 0.25f);

        _sleeveMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _sleeveMat.SetColor("_BaseColor", SleeveColor);
        _sleeveMat.SetFloat("_Smoothness", 0.06f); // 粗布哑光

        _trimMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _trimMat.SetColor("_BaseColor", TrimColor);
        _trimMat.SetFloat("_Smoothness", 0.1f);

        var root = new GameObject("HeldArm");

        // 竹杆（双态共用）：直径 1.6cm，全长 0.78m
        var shaft = CreatePrimitive(PrimitiveType.Cylinder, "Shaft", _bambooMat, root.transform);
        shaft.transform.localScale = new Vector3(0.016f, 0.39f, 0.016f); // Cylinder 高 2 → 半高 0.39

        // ── 收拢态组（伞布收拢束 + 骨尖 + 伞头帽）──
        _furledGroup = new GameObject("Furled");
        _furledGroup.transform.SetParent(root.transform, false);

        // 收拢伞布束：三环锥台（底 3.2cm → 中 2.6cm → 顶 0.9cm），高 0.30m
        var sheath = new GameObject("Sheath");
        sheath.transform.SetParent(_furledGroup.transform, false);
        sheath.transform.localPosition = new Vector3(0f, 0.20f, 0f);
        sheath.AddComponent<MeshFilter>().sharedMesh = BuildConeMesh(
            new[] { (r: 0.032f, y: 0f), (r: 0.026f, y: 0.18f), (r: 0.009f, y: 0.30f) }, false);
        sheath.AddComponent<MeshRenderer>().sharedMaterial = _canopyMat;

        // 骨尖：6 根细竹条从束顶微散开（收拢伞的标志性形态）
        for (int i = 0; i < 6; i++)
        {
            float a = i / 6f * Mathf.PI * 2f;
            var rib = CreatePrimitive(PrimitiveType.Cube, $"RibTip{i}", _bambooMat, _furledGroup.transform);
            rib.transform.localPosition = new Vector3(
                Mathf.Cos(a) * 0.014f, 0.335f, Mathf.Sin(a) * 0.014f);
            rib.transform.localRotation = Quaternion.Euler(
                Mathf.Sin(a) * 18f, 0f, -Mathf.Cos(a) * 18f);
            rib.transform.localScale = new Vector3(0.0035f, 0.05f, 0.0035f);
        }

        // 伞头小帽
        var cap = CreatePrimitive(PrimitiveType.Sphere, "Cap", _bambooMat, _furledGroup.transform);
        cap.transform.localScale = new Vector3(0.024f, 0.034f, 0.024f);
        cap.transform.localPosition = new Vector3(0f, 0.40f, 0f);

        // ── 撑开态组（默认隐藏）──
        _openGroup = new GameObject("Open");
        _openGroup.transform.SetParent(root.transform, false);
        _openGroup.SetActive(false);

        // 伞面：穹顶四环（顶 0.44 → 檐口 0.245），内外双面避免仰视穿帮
        var canopy = new GameObject("Canopy");
        canopy.transform.SetParent(_openGroup.transform, false);
        canopy.AddComponent<MeshFilter>().sharedMesh = BuildConeMesh(
            new[] { (r: 0.020f, y: 0.44f), (r: 0.150f, y: 0.38f), (r: 0.260f, y: 0.30f), (r: 0.290f, y: 0.245f) }, true);
        canopy.AddComponent<MeshRenderer>().sharedMaterial = _canopyMat;

        // 撑开伞骨：8 根细竹条沿伞面坡度张开
        var apex = new Vector3(0f, 0.45f, 0f);
        for (int i = 0; i < 8; i++)
        {
            float a = i / 8f * Mathf.PI * 2f;
            var rim = new Vector3(Mathf.Cos(a) * 0.285f, 0.245f, Mathf.Sin(a) * 0.285f);
            var dir = rim - apex;
            var rib = CreatePrimitive(PrimitiveType.Cube, $"Rib{i}", _bambooMat, _openGroup.transform);
            rib.transform.localPosition = (apex + rim) * 0.5f;
            rib.transform.localRotation = Quaternion.FromToRotation(Vector3.up, dir.normalized);
            rib.transform.localScale = new Vector3(0.004f, dir.magnitude, 0.004f);
        }

        // 伞顶
        var finial = CreatePrimitive(PrimitiveType.Sphere, "Finial", _bambooMat, _openGroup.transform);
        finial.transform.localScale = new Vector3(0.020f, 0.030f, 0.020f);
        finial.transform.localPosition = new Vector3(0f, 0.465f, 0f);

        // ── 右臂：握伞手 + 袖口 + 前臂布袖（伸向画面右下角外）──
        var hand = CreatePrimitive(PrimitiveType.Sphere, "Hand", _skinMat, root.transform);
        hand.transform.localScale = new Vector3(0.055f, 0.065f, 0.06f);
        hand.transform.localPosition = new Vector3(0f, 0.02f, 0f);

        var wrist = new Vector3(0f, -0.16f, 0f);
        var elbow = new Vector3(0.17f, -0.34f, -0.30f);
        var forearmDir = (elbow - wrist).normalized;

        // 袖口：粗布包边小段
        var cuff = CreatePrimitive(PrimitiveType.Cylinder, "Cuff", _trimMat, root.transform);
        cuff.transform.localPosition = wrist + forearmDir * 0.035f;
        cuff.transform.localRotation = Quaternion.FromToRotation(Vector3.up, forearmDir);
        cuff.transform.localScale = new Vector3(0.062f, 0.035f, 0.062f);

        // 前臂：布袖圆柱，末端伸出视野由近裁剪自然截断
        var forearm = CreatePrimitive(PrimitiveType.Cylinder, "Forearm", _sleeveMat, root.transform);
        forearm.transform.localPosition = wrist + forearmDir * (0.035f + 0.17f);
        forearm.transform.localRotation = Quaternion.FromToRotation(Vector3.up, forearmDir);
        forearm.transform.localScale = new Vector3(0.05f, 0.17f, 0.05f);

        return root;
    }

    /// <summary>创建图元并剥掉 Collider（手持物不参与物理，防穿模铁律）。</summary>
    private static GameObject CreatePrimitive(PrimitiveType type, string name, Material mat, Transform parent)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.GetComponent<Renderer>().sharedMaterial = mat;
        go.transform.SetParent(parent, false);
        return go;
    }

    /// <summary>
    /// 锥台侧面网格：多个水平圆环自下而上连成伞面。
    /// doubleSided 时追加一份翻转三角（撑伞仰视不穿帮）。
    /// </summary>
    private static Mesh BuildConeMesh((float r, float y)[] rings, bool doubleSided)
    {
        var mesh = new Mesh();
        mesh.name = "UmbrellaCone";

        int ringPts = rings.Length > 1 ? 12 + 1 : 12; // 首点重复，UV 接缝
        int segments = ringPts - 1;
        var verts = new Vector3[ringPts * rings.Length];
        var uvs = new Vector2[ringPts * rings.Length];
        for (int ri = 0; ri < rings.Length; ri++)
        {
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                int idx = ri * ringPts + i;
                verts[idx] = new Vector3(Mathf.Cos(a) * rings[ri].r, rings[ri].y, Mathf.Sin(a) * rings[ri].r);
                uvs[idx] = new Vector2(i / (float)segments, ri / (float)(rings.Length - 1));
            }
        }

        var tris = new List<int>(segments * 4 * 3 * (rings.Length - 1));
        for (int ri = 0; ri < rings.Length - 1; ri++)
        {
            for (int i = 0; i < segments; i++)
            {
                int a = ri * ringPts + i;
                int b = ri * ringPts + i + 1;
                int c = a + ringPts;
                int d = b + ringPts;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
                if (doubleSided)
                {
                    tris.Add(b); tris.Add(c); tris.Add(a);
                    tris.Add(d); tris.Add(c); tris.Add(b);
                }
            }
        }

        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
