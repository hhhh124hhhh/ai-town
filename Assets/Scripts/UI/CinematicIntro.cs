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
    private const float OrbitAt = 7f;       // 环绕时长（2026-08-29 用户"过长/转角太大"：10s 135°
                                            // → 7s 80°；环绕+掠过+俯冲合计 12.1s→9.1s）
    private const float SkimSeconds = 1.2f; // 低空掠过主街（起手配锣声）
    private const float DiveSeconds = 0.9f; // 掠过锚点 → 玩家相机交棒
    private const float LampSeconds = 0.45f; // 灯亮：全黑→暖黄晕漾开（首帧钩子）
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
    private float _introStart;           // 开场白请求起点（算"AI 现写耗时"用）
    private float _introWaitSeconds;     // 开场白实际等待秒数
    private bool _lineFromAI;            // 开场白是否来自 LLM（false=离线回退稿）
    private float _typedDoneTime;        // 打字完成时刻（印章弹出动画起点）
    private float _lampStart;            // 灯亮动画起点（黑→暖晕漾开）
    private float _shakeUntil;           // 盖章屏震窗口结束时刻（OnGUI 域）

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
        _introStart = Time.unscaledTime;
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

        // 开场待机机位=环绕起点姿态：黑幕半透期（构思中/打字机）就能望见黄昏小镇
        float a0 = -60f * Mathf.Deg2Rad;
        _introCam.transform.position = new Vector3(
            Mathf.Sin(a0) * 32f, 26f, -Mathf.Cos(a0) * 32f);
        _introCam.transform.LookAt(new Vector3(0f, 4f, -2f));
    }

    private IEnumerator IntroCo()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // ── 相位 1：灯亮 → 构思（LLM 现写开场白）──
        // 首帧即暖光晕漾开（primacy effect：第 0 秒给一个钩子，不再是死黑）；
        // BGM 从极低淡入（声音层先立氛围，打字加速段推到正常音量）
        _lampStart = Time.unscaledTime;
        AudioManager.FadeInBgm();
        // 灯亮：黑纱后面先炸开一朵暖金光晕（黑幕渐透=光从暗中漾出）
        EffectsCatalog.Play(EffectsCatalog.Glow, StampBurstWorldPos(), 2.4f);

        string line = null;
        if (ApiClient.Instance != null)
        {
            // 后端可能正被 ServerBootstrap 冷启动拉起：等就绪再取开场白
            // （等待期信笺本来就在演"镇志官正在构思…"，等待被演出吸收，不冷场）
            yield return ServerBootstrap.WaitReady();
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
        _introWaitSeconds = Time.unscaledTime - _introStart;
        _lineFromAI = line != null;

        // ── 相位 2：打字机（慢→停→加速，节拍对比制造期待）──
        _phase = Phase.Type;
        _phaseT = 0f;
        while (_typedChars < _line.Length)
        {
            _typedChars = Mathf.Min(_line.Length, _typedChars + 1);
            char c = _line[_typedChars - 1];
            // 前半句庄重慢打；标点停顿换气；后半句加速攒动能
            bool firstHalf = _typedChars <= _line.Length / 2;
            float wait = firstHalf ? TypeCharSeconds * 1.9f
                       : c == '，' || c == '。' || c == '、' || c == '？' || c == '！'
                       ? TypeCharSeconds * 6f
                       : TypeCharSeconds * 0.55f;
            yield return new WaitForSecondsRealtime(wait);
        }
        _typedDoneTime = Time.unscaledTime;
        _shakeUntil = _typedDoneTime + 0.25f; // 盖章屏震窗口
        AudioManager.Play("SFX_Stamp", 0.8f); // 开场白落款盖章（音效缺失时静默跳过）
        // 盖章同时金光迸溅（峰终定律：把记忆点钉在"AI 现场书写"上）
        EffectsCatalog.Play(EffectsCatalog.StampBurst, StampBurstWorldPos(), 1f);
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
                // 环绕：80° 弧前慢后快（pow 缓动攒冲势），高度/半径 smoothstep 螺旋收拢
                // （2026-08-29 收紧：135°→80°，弧短冲势不散，掠过接得住）
                float t = Mathf.Clamp01(_phaseT / orbitEnd);
                float angleEase = Mathf.Pow(t, 1.4f);
                float ease = t * t * (3f - 2f * t);
                float angle = Mathf.Lerp(-40f, 40f, angleEase) * Mathf.Deg2Rad;
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
            if (_phase == Phase.Black || _phase == Phase.Type)
            {
                // 半透小镇：黑幕只压到 55%，开场白信笺叠在黄昏小镇的朦胧剪影上
                _blackAlpha = Mathf.MoveTowards(_blackAlpha, 0.55f, Time.deltaTime * 1.2f);
            }
            else if (_phase == Phase.Scene || _phase == Phase.Skim || _phase == Phase.Dive)
            {
                _blackAlpha = Mathf.MoveTowards(_blackAlpha, 0f, Time.deltaTime * 1.2f);
            }
            var tex = Texture2D.whiteTexture;
            Color prev = GUI.color;
            // 灯亮后黑纱染暖（冷黑→暖褐纱，光晕从纱后漾出）
            Color veil = (_phase == Phase.Black || _phase == Phase.Type)
                ? new Color(0.17f, 0.10f, 0.05f, _blackAlpha)
                : new Color(0f, 0f, 0f, _blackAlpha);
            GUI.color = veil;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tex);
            GUI.color = prev;
        }

        // 开场白信笺（构思中/打字机/拉开初期可见）：宣纸条幅+淡墨字+朱红印章
        if (_phase == Phase.Black || _phase == Phase.Type
            || (_phase == Phase.Scene && _blackAlpha > 0.25f))
        {
            DrawIntroBanner();
        }

        // 大标题（Scene 后期 → AwaitStart 保持 → 开始后淡出）
        if (_phase == Phase.Scene && _phaseT > TitleAt) _titleAlpha = Mathf.Clamp01((_phaseT - TitleAt) / 1.2f);
        if (_titleFadeOut) _titleAlpha = Mathf.MoveTowards(_titleAlpha, 0f, Time.unscaledDeltaTime * 2.5f);
        if (_titleAlpha > 0.01f)
        {
            // 标题区 scrim：上下渐变暗底（电影字幕标准做法），白字不再裸压门楼/灯笼
            DrawTitleScrim(_titleAlpha);

            float cx = Screen.width / 2f;
            // 标题字距呼吸（中式标题加宽 8%）+描边，主字加 1px 墨影增强图底对比
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Min(76, Screen.width / 11),
                alignment = TextAnchor.MiddleCenter,
                font = UiTheme.KaiFont,
                normal = { textColor = new Color(1f, 0.98f, 0.92f, _titleAlpha) },
            };
            var titleShadow = new GUIStyle(titleStyle) { normal = { textColor = new Color(0f, 0f, 0f, _titleAlpha * 0.85f) } };
            string spacedTitle = SpacedText(Title, 0.08f);
            GUI.Label(new Rect(cx - 400 + 3, Screen.height * 0.27f + 3, 800, 100), spacedTitle, titleShadow);
            GUI.Label(new Rect(cx - 400, Screen.height * 0.27f, 800, 100), spacedTitle, titleStyle);

            // 副标题：去透明实色+双点装饰（· 副题 ·）+墨影——图底对比的格式塔修复
            var subStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Min(30, Screen.width / 34),
                alignment = TextAnchor.MiddleCenter,
                font = UiTheme.KaiFont,
                normal = { textColor = new Color(0.96f, 0.90f, 0.74f, _titleAlpha) },
            };
            var subShadow = new GUIStyle(subStyle) { normal = { textColor = new Color(0f, 0f, 0f, _titleAlpha * 0.9f) } };
            string spacedSub = "· " + SpacedText(Subtitle, 0.10f) + " ·";
            GUI.Label(new Rect(cx - 400 + 2, Screen.height * 0.27f + 100 + 2, 800, 44), spacedSub, subShadow);
            GUI.Label(new Rect(cx - 400, Screen.height * 0.27f + 100, 800, 44), spacedSub, subStyle);
        }

        // 开始确认（俯冲定格后）：呼吸闪烁"开始"提示
        if (_phase == Phase.AwaitStart)
        {
            float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 2.2f));
            var startStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Min(32, Screen.width / 28),
                alignment = TextAnchor.MiddleCenter,
                font = UiTheme.KaiFont,
                normal = { textColor = new Color(1f, 0.96f, 0.84f, pulse) },
            };
            var startShadow = new GUIStyle(startStyle) { normal = { textColor = new Color(0f, 0f, 0f, pulse * 0.9f) } };
            string txt = "— 按任意键，开始你的小镇 —";
            // 0.68：避开中轴 NPC 头部（视觉重量最高区），落在天空留白带上
            Rect sr = new Rect(0, Screen.height * 0.68f, Screen.width, 52f);
            GUI.Label(new Rect(sr.x + 2, sr.y + 2, sr.width, sr.height), txt, startShadow);
            GUI.Label(sr, txt, startStyle);
        }

        // 跳过提示
        if (_phase == Phase.Type || _phase == Phase.Scene
            || _phase == Phase.Skim || _phase == Phase.Dive)
        {
            var hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = UiTheme.SizeBody,
                alignment = TextAnchor.UpperRight,
                font = UiTheme.KaiFont,
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
                font = UiTheme.KaiFont,
                normal = { textColor = new Color(1f, 0.95f, 0.8f) },
            };
            GUI.Box(new Rect(Screen.width / 2f - 300, Screen.height - 90, 600, 40), _toast, toastStyle);
        }
    }

    /// <summary>
    /// 开场白信笺：宣纸条幅承载打字机文字（黑幕半透期叠在黄昏小镇上，文字坐素地）。
    /// 等待期显示"镇志官正在构思…"（LLM 生成期不再是死黑屏）；
    /// 打完盖朱红印章 + "AI 现场书写·耗时 x.x 秒"署名——把 LLM 现写这一卖点演给观众。
    /// </summary>
    private void DrawIntroBanner()
    {
        UiTheme.BeginScale();
        float vw = UiTheme.VW;
        float cy = UiTheme.VH * 0.42f;

        // 浮入：灯亮后 0.27s 信笺从下方 26px 滑入到位（世界与 UI 的接缝先给光，再给纸）
        float enter = Mathf.Clamp01((Time.unscaledTime - _lampStart - LampSeconds * 0.6f) / 0.35f);
        if (enter <= 0f) { UiTheme.EndScale(); return; }
        float slide = (1f - enter) * 26f;

        // 盖章屏震：0.25s 确定性抖动（sin/cos 双频叠加，Layout/Repaint 两趟不漂移）
        float shakeX = 0f, shakeY = 0f;
        if (Time.unscaledTime < _shakeUntil)
        {
            float k = Mathf.Clamp01((_shakeUntil - Time.unscaledTime) / 0.25f);
            float amp = 6f * k * k;
            shakeX = Mathf.Sin(Time.unscaledTime * 115f) * amp;
            shakeY = Mathf.Cos(Time.unscaledTime * 97f) * amp * 0.7f;
        }

        // 内容文本：等待期=构思提示（墨点动画）；打字期=已打出的部分
        bool waiting = string.IsNullOrEmpty(_line);
        string main = waiting
            ? "镇志官正在构思" + new string('.', Mathf.FloorToInt(Time.unscaledTime * 2.5f) % 3 + 1)
            : _line.Substring(0, Mathf.Min(_typedChars, _line.Length));

        // 排版美学：纸宽按"整句"预算固定（打字过程中纸不缩放），文字在纸内
        // 双重居中（TextAnchor.MiddleCenter）——打字期文字从纸中心向两侧生长，
        // 不会前期挤左侧、右侧留大空白（光学居中 + 排版稳定性）
        var style = new GUIStyle(UiTheme.Text(UiTheme.SizeDisplay))
        {
            wordWrap = false,
            alignment = TextAnchor.MiddleCenter,
        };
        string fullText = waiting ? "镇志官正在构思..." : _line;
        var fullSize = style.CalcSize(new GUIContent(fullText));
        float w = Mathf.Max(300f, fullSize.x + 96f);   // 左右各 48 padding（≥1.5 字高）
        float h = Mathf.Max(76f, fullSize.y + 40f);
        var rect = new Rect(vw / 2f - w / 2f + shakeX, cy - h / 2f + slide + shakeY, w, h);

        // 宣纸底：直接在 rect 位置画（UiTheme.Wash 内部从 (0,0) 画，只适用于 BeginArea 内——
        // 裸调会把纸画到屏幕左上角，文字留在原地裸压背景，即截图里的"孤儿米色块+低对比文字"）
        var prev = GUI.color;
        GUI.color = new Color(0.94f, 0.89f, 0.80f, 0.97f); // 宣纸近实底
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = new Color(0.22f, 0.19f, 0.15f, 0.55f); // 细墨边
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.y, 2f, rect.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), Texture2D.whiteTexture);
        GUI.color = prev;
        // 文字画满整个纸区，由 style 的 MiddleCenter 完成居中（不再手动偏移 44px）
        GUI.Label(rect, main, style);

        // 打完：印章弹出 + AI 现写署名
        if (!waiting && _typedChars >= _line.Length && _typedDoneTime > 0f)
        {
            DrawSeal(rect);
            string cap = _lineFromAI
                ? $"本句由 AI 现场书写 · 耗时 {_introWaitSeconds:0.0} 秒"
                : "本地备稿 · 服务连上后由 AI 现场书写";
            // 署名行同款小纸条：淡墨字坐素地（与主信笺成一组，亲密性原则）
            var capStyle = new GUIStyle(UiTheme.Text(UiTheme.SizeBody)) { alignment = TextAnchor.MiddleCenter };
            var capSize = capStyle.CalcSize(new GUIContent(cap));
            float capW = Mathf.Max(300f, capSize.x + 48f);
            var capRect = new Rect(vw / 2f - capW / 2f, rect.yMax + 8f, capW, 30f);
            GUI.color = new Color(0.94f, 0.89f, 0.80f, 0.92f);
            GUI.DrawTexture(capRect, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(capRect, cap, capStyle);
        }
        UiTheme.EndScale();
    }

    /// <summary>朱红印章（AI 小镇），-8° 歪斜 + 盖下收势的弹出动画，压在信笺右下角。</summary>
    private void DrawSeal(Rect banner)
    {
        float since = Time.unscaledTime - _typedDoneTime;
        float pop = 1f + 0.45f * Mathf.Clamp01(1f - since / 0.22f);

        float sealSize = 58f;
        var pivot = new Vector2(banner.xMax - sealSize * 0.55f, banner.yMax - sealSize * 0.45f);
        var saved = GUI.matrix;
        GUIUtility.RotateAroundPivot(-8f, pivot);
        var m = GUI.matrix;
        GUI.matrix = Matrix4x4.Translate(pivot)
                     * Matrix4x4.Scale(new Vector3(pop, pop, 1f))
                     * Matrix4x4.Translate(-pivot)
                     * m;

        var prev = GUI.color;
        var sealRect = new Rect(pivot.x - sealSize / 2f, pivot.y - sealSize / 2f, sealSize, sealSize);
        GUI.color = new Color32(0x9E, 0x2B, 0x25, 0xE6); // 朱红
        GUI.DrawTexture(sealRect, Texture2D.whiteTexture);

        var sealStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = UiTheme.SizeEmph,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color32(0xF5, 0xEF, 0xE2, 0xFF) }, // 宣纸白
        };
        GUI.Label(new Rect(sealRect.x, sealRect.y + 4f, sealSize, sealSize / 2f), "AI", sealStyle);
        GUI.Label(new Rect(sealRect.x, sealRect.y + sealSize / 2f - 2f, sealSize, sealSize / 2f), "小镇", sealStyle);

        GUI.matrix = saved;
        GUI.color = prev;
    }

    /// <summary>印章/灯亮特效的世界落点：演出相机视线中心前方 14m（盖在信笺后方的空气里）。</summary>
    private Vector3 StampBurstWorldPos()
    {
        var cam = _introCam != null ? _introCam : Camera.main;
        if (cam == null) return Vector3.zero;
        var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.55f, 0f));
        return ray.GetPoint(14f);
    }

    /// <summary>
    /// 标题区渐变 scrim：上带亮→暗、下带暗→亮各叠 3 条递进色带（模拟垂直渐变），
    /// 电影字幕标准做法——白字永远不裸压复杂背景。alpha 随标题淡入淡出。
    /// </summary>
    private void DrawTitleScrim(float alpha)
    {
        const int bands = 3;
        float bandH = Screen.height * 0.045f;
        var tex = Texture2D.whiteTexture;
        var prev = GUI.color;

        // 上带（标题上方，由上向下渐暗）
        for (int i = 0; i < bands; i++)
        {
            float a = alpha * 0.42f * (1f - i / (float)bands);
            GUI.color = new Color(0f, 0f, 0f, a);
            GUI.DrawTexture(new Rect(0, Screen.height * 0.24f + i * bandH, Screen.width, bandH), tex);
        }
        // 下带（副标题下方，由暗渐透明）
        for (int i = 0; i < bands; i++)
        {
            float a = alpha * 0.42f * (1f - i / (float)bands) * (1f - i * 0.15f);
            GUI.color = new Color(0f, 0f, 0f, a);
            GUI.DrawTexture(new Rect(0, Screen.height * 0.27f + 148f + i * bandH, Screen.width, bandH), tex);
        }
        GUI.color = prev;
    }

    /// <summary>标题字距加宽：每个字符后插空格（比例 0~0.2），中式标题"字距呼吸"。</summary>
    private static string SpacedText(string s, float ratio)
    {
        if (string.IsNullOrEmpty(s) || ratio <= 0f) return s;
        int spaces = Mathf.Max(1, Mathf.RoundToInt(ratio * 10f) / 2);
        var pad = new string(' ', spaces);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            sb.Append(s[i]);
            if (i < s.Length - 1) sb.Append(pad);
        }
        return sb.ToString();
    }
}
