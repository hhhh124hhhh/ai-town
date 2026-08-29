using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem; // PlayerInput / Mouse（项目已启用新 Input System）

/// <summary>
/// 开场演出「第一句话」：
/// 黑屏 → LLM 现场生成开场白打字机浮现 → 黑幕拉开高空环绕、
/// 小镇建筑逐块生长（复用烘焙方块逐个点亮）→ 大标题淡入 →
/// 相机俯冲到玩家视角 → 交还控制权 + 操作提示。任意键跳过。
/// 由 BuildingPanel.Start() 懒创建；演出期间禁用玩家控制组件。
/// </summary>
public class CinematicIntro : MonoBehaviour
{
    private static CinematicIntro _instance;
    public static CinematicIntro Instance => _instance;

    /// <summary>开场演出进行中（黑屏→按任意键开始），各 UI 面板据此隐藏自身、输入据此不响应。</summary>
    public static bool IsCinematic
    {
        get
        {
            return _instance != null
                && _instance._phase >= Phase.Black
                && _instance._phase <= Phase.AwaitStart;
        }
    }

    // “按任意键开始”的那一帧，同一按键的 wasPressedThisFrame 对所有脚本可见：
    // Handoff 同帧把 IsCinematic 翻 false 后，Tab/E/C 会立刻漏进游戏输入
    // （用 Tab 开始 → 建筑面板当场被隐藏）。开始后短暂冷却屏蔽输入。
    private static float _inputCooldownUntil = -1f;
    /// <summary>开场刚结束的输入冷却期（开始键不应触发任何面板/交互）。</summary>
    public static bool InputCooldown => Time.unscaledTime < _inputCooldownUntil;

    public static void EnsureExists()
    {
        if (_instance == null)
        {
            var go = new GameObject("_CinematicIntro");
            go.AddComponent<CinematicIntro>();
        }
    }

    // ── 演出参数 ──
    private const float TypeCharSeconds = 0.035f;
    private const float HoldAfterType = 1.4f;
    private const float TitleAt = 3.8f;
    private const float OrbitAt = 10f;      // 环绕时长：135° 弧前慢后快攒冲势，生长同期收尾
    private const float SkimSeconds = 1.2f; // 低空掠过主街（起手配锣声）
    private const float DiveSeconds = 0.9f; // 掠过锚点 → 玩家相机交棒
    private const string FallbackLine = "听说，来了一位——说句话就能让砖瓦自己长成楼的营造师。";
    private const string Title = "AI 小镇";
    private const string Subtitle = "一言既出，砖瓦成楼";

    // ── 演出状态（OnGUI 读取）──
    private enum Phase { Black, Type, Scene, Skim, Dive, AwaitStart, Toast, Done }
    private Phase _phase = Phase.Black;
    private string _line = "";
    private int _typedChars;
    private float _phaseT;               // 当前相位已耗时
    private float _blackAlpha = 1f;      // 黑幕
    private float _titleAlpha;           // 标题
    private string _toast = "";
    private bool _toastShown;
    private bool _titleFadeOut;
    private int _dustMilestone;          // 生长里程碑落尘计数（每 25% 一档，共 4 档）

    // ── 场景引用 ──
    private Camera _introCam;
    private Camera _playerCam;
    private Behaviour[] _disabledBehaviours;
    private readonly List<Transform> _blocks = new();
    private Transform _player;
    private Vector3 _skimFromPos;        // 掠过起点（环绕终点）
    private Vector3 _diveFromPos;
    private Quaternion _diveFromRot;
    private Vector3 _playerCamPos;
    private Quaternion _playerCamRot;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    private void Start()
    {
        _ = AudioManager.I; // 懒创建并开始 BGM 循环
        _player = GameObject.Find("Player")?.transform;
        CollectBlocks();
        SetupCameraAndControls();
        StartCoroutine(IntroCo());
    }

    /// <summary>收集烘焙方块（按高度排：从下往上生长），先全部隐藏。</summary>
    private void CollectBlocks()
    {
        var root = GameObject.Find("_Buildings");
        if (root != null)
        {
            foreach (Transform building in root.transform)
            {
                foreach (Transform block in building)
                {
                    _blocks.Add(block);
                }
            }
        }
        _blocks.Sort((a, b) => a.position.y.CompareTo(b.position.y));
        foreach (var b in _blocks) b.gameObject.SetActive(false);
    }

    /// <summary>接管相机与输入：禁玩家相机与控制组件，建演出相机。</summary>
    private void SetupCameraAndControls()
    {
        if (_player != null)
        {
            _playerCam = _player.GetComponentInChildren<Camera>(true);
            _disabledBehaviours = new Behaviour[]
            {
                _player.GetComponent("FirstPersonController") as Behaviour,
                _player.GetComponent("StarterAssetsInputs") as Behaviour,
                _player.GetComponent<PlayerInput>(),
                _player.GetComponent<FlyMode>(),
            };
        }
        if (_playerCam != null) _playerCam.enabled = false;
        if (_disabledBehaviours != null)
        {
            foreach (var b in _disabledBehaviours)
            {
                if (b != null) b.enabled = false;
            }
        }

        var camGo = new GameObject("_IntroCamera");
        _introCam = camGo.AddComponent<Camera>();
        _introCam.fieldOfView = 55f;
        _introCam.nearClipPlane = 0.1f;
        _introCam.farClipPlane = 500f;
    }

    private IEnumerator IntroCo()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // ── 相位 1：黑屏取开场白（最多等 3 秒，超时用回退句）──
        string line = null;
        if (ApiClient.Instance != null)
        {
            yield return ApiClient.Instance.GetIntroLine(
                l => line = l, _ => { });
            float waited = 0f;
            while (line == null && waited < 3f)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
            }
        }
        _line = string.IsNullOrEmpty(line) ? FallbackLine : line;

        // ── 相位 2：打字机 ──
        _phase = Phase.Type;
        _phaseT = 0f;
        while (_typedChars < _line.Length)
        {
            _typedChars = Mathf.Min(_line.Length, _typedChars + 1);
            yield return new WaitForSecondsRealtime(TypeCharSeconds);
        }
        yield return new WaitForSecondsRealtime(HoldAfterType);

        // ── 相位 3：黑幕拉开 + 环绕 + 建筑生长 ──
        _phase = Phase.Scene;
        _phaseT = 0f;

        // 记录玩家相机最终位姿（俯冲终点）
        if (_playerCam != null)
        {
            _playerCamPos = _playerCam.transform.position;
            _playerCamRot = _playerCam.transform.rotation;
        }
        else
        {
            _playerCamPos = new Vector3(0f, 1.8f, -5.6f);
            _playerCamRot = Quaternion.identity;
        }

        float orbitEnd = OrbitAt;
        float revealed = 0f;
        float lastReveal = 0f;

        while (_phaseT < orbitEnd + SkimSeconds + DiveSeconds + 0.05f)
        {
            if (_phase == Phase.Scene && _phaseT >= orbitEnd)
            {
                _phase = Phase.Skim;
                _skimFromPos = _introCam.transform.position;
                AudioManager.Play("SFX_Gong"); // 掠过起手一声锣
            }
            else if (_phase == Phase.Skim && _phaseT >= orbitEnd + SkimSeconds)
            {
                _phase = Phase.Dive;
                _diveFromPos = _introCam.transform.position;
                _diveFromRot = _introCam.transform.rotation;
            }

            if (_phase == Phase.Scene)
            {
                // 环绕：135° 弧前慢后快（pow 缓动攒冲势），高度/半径 smoothstep 螺旋收拢
                float t = Mathf.Clamp01(_phaseT / orbitEnd);
                float angleEase = Mathf.Pow(t, 1.4f);
                float ease = t * t * (3f - 2f * t);
                float angle = Mathf.Lerp(-60f, 75f, angleEase) * Mathf.Deg2Rad;
                float height = Mathf.Lerp(26f, 17f, ease);
                float radius = Mathf.Lerp(32f, 24f, ease);
                _introCam.transform.position = new Vector3(
                    Mathf.Sin(angle) * radius, height, -Mathf.Cos(angle) * radius);
                _introCam.transform.LookAt(new Vector3(0f, 4f, -2f));

                // 逐块生长：easeOut 时间轴（开头快结尾慢），最后一块在环绕尾声落成
                float revealEase = 1f - (1f - t) * (1f - t);
                revealed = _blocks.Count * revealEase;
                if (revealed - lastReveal >= 1f)
                {
                    int upto = Mathf.FloorToInt(revealed);
                    for (int i = Mathf.FloorToInt(lastReveal); i < upto && i < _blocks.Count; i++)
                    {
                        _blocks[i].gameObject.SetActive(true);
                    }
                    lastReveal = upto;

                    // 里程碑落尘：生长每过 ~25%，在刚点亮的方块地面起一蓬尘
                    // （跳过 Handoff 不经过这里，天然满足"跳过时不播"）
                    if (_dustMilestone < 4 && upto > 0
                        && upto >= Mathf.CeilToInt(_blocks.Count * (_dustMilestone + 1) / 4f))
                    {
                        var anchor = _blocks[Mathf.Min(upto - 1, _blocks.Count - 1)];
                        if (anchor != null)
                        {
                            EffectsCatalog.Play(EffectsCatalog.Dust,
                                new Vector3(anchor.position.x, 0.05f, anchor.position.z));
                        }
                        _dustMilestone++;
                    }
                }
            }
            else if (_phase == Phase.Skim)
            {
                // 低空掠过：环绕终点 → 广场上空锚点，视线锁玩家头顶（穿镇速度感）
                float t = Mathf.Clamp01((_phaseT - orbitEnd) / SkimSeconds);
                float ease = t * t * (3f - 2f * t);
                var anchor = new Vector3(2f, 7f, -7f); // 广场中心上空（无高物，掠过线过骑楼带顶空）
                _introCam.transform.position = Vector3.Lerp(_skimFromPos, anchor, ease);
                if (_player != null)
                {
                    _introCam.transform.LookAt(_player.position + Vector3.up * 1.2f);
                }
            }
            else
            {
                // 拉起交棒：SmoothStep 到玩家相机位姿
                float t = Mathf.Clamp01((_phaseT - orbitEnd - SkimSeconds) / DiveSeconds);
                float ease = t * t * (3f - 2f * t);
                _introCam.transform.position = Vector3.Lerp(_diveFromPos, _playerCamPos, ease);
                _introCam.transform.rotation = Quaternion.Slerp(_diveFromRot, _playerCamRot, ease);
            }

            _phaseT += Time.unscaledDeltaTime;
            yield return null;
        }

        // ── 相位 4：俯冲完毕定格，等玩家按键开始（"开始游戏"仪式）──
        _phase = Phase.AwaitStart;
    }

    /// <summary>交还控制权（跳过时也走这里：点亮全部方块、还原相机与输入）。</summary>
    private void Handoff()
    {
        foreach (var b in _blocks) if (b != null) b.gameObject.SetActive(true);
        if (_blocks.Count > 0 && _phase >= Phase.Scene && _phase <= Phase.Dive)
        {
            // 环绕/掠过/拉起中直接跳过时相机还在半空，瞬移到玩家视角
            if (_introCam != null)
            {
                _introCam.transform.position = _playerCamPos;
                _introCam.transform.rotation = _playerCamRot;
            }
        }

        StopAllCoroutines();

        if (_introCam != null) Destroy(_introCam.gameObject);
        if (_playerCam != null) _playerCam.enabled = true;
        if (_disabledBehaviours != null)
        {
            foreach (var b in _disabledBehaviours)
            {
                if (b != null) b.enabled = true;
            }
        }
        // 右键视角模式：指针默认自由（MouseLookGate 在按住右键时才锁定）
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        _inputCooldownUntil = Time.unscaledTime + 0.3f;
        _phase = Phase.Toast;
        _toast = "【Tab】说出你的第一个愿望　【C】委托　【F】飞行　【按住右键拖动】环顾小镇";
        _toastShown = true;
        StartCoroutine(ToastCo());
    }

    private IEnumerator ToastCo()
    {
        yield return new WaitForSecondsRealtime(6f);
        _phase = Phase.Done;
        Destroy(gameObject);
    }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (_phase == Phase.AwaitStart)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = Mouse.current;
            bool anyKey = (kb != null && kb.anyKey.isPressed)
                          || (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed));
            if (anyKey)
            {
                _titleFadeOut = true;
                Handoff();
            }
            return;
        }
        if (_phase != Phase.Done && _phase != Phase.Toast)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = Mouse.current;
            bool anyKey = (kb != null && kb.anyKey.isPressed)
                          || (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.scroll.ReadValue().y != 0f));
            if (anyKey)
            {
                // 打字阶段之后才允许跳过（开场白本身很快）
                if (_phase != Phase.Type || _typedChars >= _line.Length)
                {
                    Handoff();
                }
            }
        }
#endif
    }

    // ── 渲染 ─────────────────────────────────────────────────────────────
    private void OnGUI()
    {
        if (_phase == Phase.Done) return;

        // 黑幕
        if (_blackAlpha > 0.01f)
        {
            if (_phase == Phase.Type)
            {
                _blackAlpha = Mathf.MoveTowards(_blackAlpha, 1f, Time.deltaTime);
            }
            else if (_phase == Phase.Scene || _phase == Phase.Skim || _phase == Phase.Dive)
            {
                _blackAlpha = Mathf.MoveTowards(_blackAlpha, 0f, Time.deltaTime * 1.2f);
            }
            var tex = Texture2D.whiteTexture;
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, _blackAlpha);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tex);
            GUI.color = prev;
        }

        // 开场白（打字机，黑幕期与拉开初期可见）
        if ((_phase == Phase.Type || (_phase == Phase.Scene && _blackAlpha > 0.25f)) && _line.Length > 0)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = new Color(1f, 0.95f, 0.8f) },
            };
            string shown = _line.Substring(0, Mathf.Min(_typedChars, _line.Length));
            Rect r = new Rect(0, Screen.height * 0.42f, Screen.width, 70f);
            // 描边：四向偏移画黑
            var shadow = new GUIStyle(style) { normal = { textColor = Color.black } };
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), shown, shadow);
            GUI.Label(r, shown, style);
        }

        // 大标题（Scene 后期 → AwaitStart 保持 → 开始后淡出）
        if (_phase == Phase.Scene && _phaseT > TitleAt) _titleAlpha = Mathf.Clamp01((_phaseT - TitleAt) / 1.2f);
        if (_titleFadeOut) _titleAlpha = Mathf.MoveTowards(_titleAlpha, 0f, Time.unscaledDeltaTime * 2.5f);
        if (_titleAlpha > 0.01f)
        {
            float cx = Screen.width / 2f;
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Min(72, Screen.width / 12),
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, _titleAlpha) },
            };
            var subStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Min(26, Screen.width / 40),
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.95f, 0.8f, _titleAlpha * 0.9f) },
            };
            GUI.Label(new Rect(cx - 400, Screen.height * 0.30f, 800, 90), Title, titleStyle);
            GUI.Label(new Rect(cx - 400, Screen.height * 0.30f + 84, 800, 40), Subtitle, subStyle);
        }

        // 开始确认（俯冲定格后）：呼吸闪烁"开始"提示
        if (_phase == Phase.AwaitStart)
        {
            float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 2.2f));
            var startStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Min(30, Screen.width / 30),
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.95f, 0.8f, pulse) },
            };
            var startShadow = new GUIStyle(startStyle) { normal = { textColor = new Color(0f, 0f, 0f, pulse) } };
            string txt = "— 按任意键，开始你的小镇 —";
            Rect sr = new Rect(0, Screen.height * 0.58f, Screen.width, 50f);
            GUI.Label(new Rect(sr.x + 2, sr.y + 2, sr.width, sr.height), txt, startShadow);
            GUI.Label(sr, txt, startStyle);
        }

        // 跳过提示
        if (_phase == Phase.Type || _phase == Phase.Scene
            || _phase == Phase.Skim || _phase == Phase.Dive)
        {
            var hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.UpperRight,
                normal = { textColor = new Color(1f, 1f, 1f, 0.55f) },
            };
            GUI.Label(new Rect(Screen.width - 240, Screen.height - 36, 220, 26), "按任意键跳过", hint);
        }

        // 交接 Toast
        if (_phase == Phase.Toast && _toastShown)
        {
            var toastStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 16,
                normal = { textColor = new Color(1f, 0.95f, 0.8f) },
            };
            GUI.Box(new Rect(Screen.width / 2f - 300, Screen.height - 90, 600, 40), _toast, toastStyle);
        }
    }
}
