using System.Collections;
using UnityEngine;

/// <summary>
/// NPC 控制器：占位胶囊外形（运行时生成）、头顶名牌与聊天气泡、
/// 玩家靠近（interactRange）显示"按 E 对话"，E 键打开 DialogSystem。
/// </summary>
public class NPCController : MonoBehaviour
{
    [Header("角色设定")]
    public string npcName = "镇民";
    public string roleName = "镇民";
    public Color bodyColor = new Color(0.9f, 0.7f, 0.3f);
    [Tooltip("true=运行时生成占位胶囊；false=复用子物体里的现成模型（如 AI 生成的 FBX）")]
    public bool usePlaceholderBody = true;

    [Header("交互")]
    public float interactRange = 3f;

    [Header("朝向")]
    [Tooltip("转身 smoothTime（秒），越大转得越从容")]
    public float turnSmoothTime = 0.35f;
    [Tooltip("转身最大角速度（度/秒），防止大幅转身起手过猛")]
    public float maxTurnSpeed = 270f;
    [Tooltip("目标偏角超过该值才重新转身（度），避免贴身炮塔式连续追踪")]
    public float reAimDegrees = 25f;
    [Tooltip("模型正面不是 +Z 时用它修正（度）")]
    public float modelYawOffset = 0f;

    private Transform _player;
    private float _yaw;
    private float _targetYaw;
    private float _yawVelocity;
    private bool _yawInit;
    private Transform _head;
    private Renderer _modelRenderer;
    private bool _playerNearby;
    private string _bubbleText = "";
    private float _bubbleUntil;

    /// <summary>当前是否与玩家对话中（DialogSystem 打开着）。</summary>
    public bool InConversation { get; set; }

    private void Awake()
    {
        _player = GameObject.Find("Player")?.transform;
        if (usePlaceholderBody) BuildBody();
        else BindExistingModel();
    }

    /// <summary>现成模型模式：不生成占位外形，取最高处网格顶端做名牌锚点，并挂待机微动。</summary>
    private void BindExistingModel()
    {
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            if (_modelRenderer == null || r.bounds.max.y > _modelRenderer.bounds.max.y)
                _modelRenderer = r;
        }
        if (_modelRenderer != null && _modelRenderer.GetComponent<NpcIdleMotion>() == null)
            _modelRenderer.gameObject.AddComponent<NpcIdleMotion>();
        StartCoroutine(BindHeadAnchor());
    }

    private IEnumerator BindHeadAnchor()
    {
        yield return null; // 等一帧，Renderer.bounds 才反映真实世界包围盒
        if (_modelRenderer == null) yield break;

        var head = new GameObject("Head");
        head.transform.SetParent(transform, false);
        head.transform.position = new Vector3(
            transform.position.x, _modelRenderer.bounds.max.y, transform.position.z);
        _head = head.transform;
    }

    /// <summary>占位外形：胶囊身体 + 球头。编辑态场景里只需挂本组件的空物体。</summary>
    private void BuildBody()
    {
        if (transform.Find("Body") != null) return;

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(transform, false);
        body.transform.localScale = new Vector3(0.7f, 0.85f, 0.7f);
        body.transform.localPosition = new Vector3(0f, 0.85f, 0f);

        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "Head";
        head.transform.SetParent(transform, false);
        head.transform.localScale = Vector3.one * 0.45f;
        head.transform.localPosition = new Vector3(0f, 1.75f, 0f);
        _head = head.transform;

        // 占位材质：URP Lit 共享实例按颜色复用
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            r.sharedMaterial = NpcMaterials.Get(bodyColor);
        }
    }

    private void Update()
    {
        if (_player == null)
        {
            _player = GameObject.Find("Player")?.transform;
            if (_player == null) return;
        }

        float dist = Vector3.Distance(transform.position, _player.position);
        _playerNearby = dist <= interactRange;

        // 朝向：靠近/对话时才"重新锁定"目标朝向，转身用 SmoothDampAngle 自然加减速；
        // 离开范围不打断，把当前这一转身收完再停，避免中途急停
        if (!_yawInit)
        {
            _yaw = transform.eulerAngles.y;
            _targetYaw = _yaw;
            _yawInit = true;
        }

        if ((_playerNearby || InConversation) && !CinematicIntro.IsCinematic)
        {
            Vector3 toPlayer = _player.position - transform.position;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude > 0.0001f)
            {
                float desired = Mathf.Atan2(toPlayer.x, toPlayer.z) * Mathf.Rad2Deg + modelYawOffset;
                if (Mathf.Abs(Mathf.DeltaAngle(_yaw, desired)) > reAimDegrees)
                    _targetYaw = desired; // 偏角足够大才重瞄，玩家小幅挪动不触发转身
            }
        }

        float newYaw = Mathf.SmoothDampAngle(_yaw, _targetYaw, ref _yawVelocity, turnSmoothTime, maxTurnSpeed);
        if (!Mathf.Approximately(newYaw, _yaw))
        {
            transform.rotation = Quaternion.Euler(0f, newYaw, 0f);
            _yaw = newYaw;
        }

        if (_playerNearby && !_playerNearbyPrev && DialogSystem.Instance == null
            && !CinematicIntro.IsCinematic) // 开场演出期间不冒问候气泡
        {
            // 首次进入范围时头顶冒一句问候气泡
            ShowBubble($"{npcName}（{roleName}）");
        }
        _playerNearbyPrev = _playerNearby;

#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.eKey.wasPressedThisFrame && _playerNearby
            && !CinematicIntro.IsCinematic && !CinematicIntro.InputCooldown
            && !BuildingPlacement.Active)
        {
            if (DialogSystem.Instance == null)
            {
                DialogSystem.OpenConversation(this);
            }
            else if (DialogSystem.Instance.Target == this)
            {
                DialogSystem.Instance.Close();
            }
        }
#endif
    }

    private bool _playerNearbyPrev;

    /// <summary>头顶气泡显示一句话，持续 seconds 秒。</summary>
    public void ShowBubble(string text, float seconds = 6f)
    {
        AudioManager.Play("SFX_Bubble", 0.5f);
        _bubbleText = text;
        _bubbleUntil = Time.unscaledTime + seconds;
    }

    private void OnGUI()
    {
        if (_head == null || CinematicIntro.IsCinematic) return; // 演出期（含"按任意键开始"定格）不显示名牌/对话提示

        Vector3 world = _head.position + Vector3.up * 0.6f;
        Vector3 screen = Camera.main != null ? Camera.main.WorldToScreenPoint(world) : Vector3.zero;
        if (screen.z <= 0f) return; // 在相机背后

        UiTheme.BeginScale();
        float x = screen.x / UiTheme.Scale;
        float y = UiTheme.VH - screen.y / UiTheme.Scale; // GUI 坐标系 y 向下（缩放坐标系）

        // 名牌（常显，黑描边保证亮背景下可读）
        var labelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 15,
            richText = true,
            normal = { textColor = Color.white },
        };
        var shadowStyle = new GUIStyle(labelStyle) { normal = { textColor = new Color(0f, 0f, 0f, 0.85f) } };
        string label = _playerNearby ? $"{npcName}  <b>[E] 对话</b>" : npcName;
        GUI.Label(new Rect(x - 92 + 1.5f, y - 22 + 1.5f, 180, 24), label, shadowStyle);
        GUI.Label(new Rect(x - 92, y - 22, 180, 24), label, labelStyle);

        // 聊天气泡（限时）
        if (!string.IsNullOrEmpty(_bubbleText) && Time.unscaledTime < _bubbleUntil)
        {
            var bubbleStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = 14,
                wordWrap = true,
                normal = { textColor = new Color(1f, 1f, 0.85f) },
                stretchWidth = false,
            };
            Vector2 size = bubbleStyle.CalcSize(new GUIContent(_bubbleText));
            float w = Mathf.Min(Mathf.Max(size.x + 16f, 120f), 340f);
            float h = Mathf.Max(size.y + 10f, 30f);
            GUI.Box(new Rect(x - w / 2f, y - 22f - h - 6f, w, h), _bubbleText, bubbleStyle);
        }
        else if (Time.unscaledTime >= _bubbleUntil)
        {
            _bubbleText = "";
        }

        UiTheme.EndScale();
    }
}

/// <summary>NPC 占位材质缓存：同色复用，避免材质实例爆炸。</summary>
internal static class NpcMaterials
{
    private static readonly System.Collections.Generic.Dictionary<Color, Material> Cache = new();

    public static Material Get(Color color)
    {
        if (Cache.TryGetValue(color, out var mat)) return mat;
        mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
        Cache[color] = mat;
        return mat;
    }
}
