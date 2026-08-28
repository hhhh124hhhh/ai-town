using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif
using StarterAssets;

/// <summary>
/// 对话系统：成熟游戏聊天条布局——历史滚动区在上，快捷问题三个按钮并排一行，
/// 底部输入行与发送并排。输入框用 uGUI InputField（Overlay Canvas）：
/// IMGUI TextField 走不了系统输入法（中文打不进），uGUI 才支持 IME 自由输入。
/// 由 NPCController.E 键打开；Esc 关闭。对话期间锁定玩家移动（关闭时还原）。
/// 全局唯一，同一时间只与一个 NPC 对话。
/// </summary>
public class DialogSystem : MonoBehaviour
{
    private static DialogSystem _instance;
    public static DialogSystem Instance => _instance;

    public NPCController Target { get; private set; }

    private readonly List<string> _history = new();
    private Vector2 _scroll;
    private bool _waitingReply;

    // ── uGUI 输入层 ──
    private GameObject _uiRoot;      // Overlay Canvas 根
    private InputField _field;       // 输入框
    private RectTransform _row;      // 输入行（输入框+发送，每帧对齐 IMGUI 预留位）
    private bool _focusNextUgui;
    private bool _resubmitFocus;

    /// <summary>输入框当前文本（uGUI 为唯一真源）。</summary>
    private string InputText => _field != null ? _field.text : "";
    private bool InputFocused => _field != null && _field.isFocused;

    // 对话期间锁玩家移动（记住先前状态，关闭时还原——飞行中开对话也能正确还原）
    private FirstPersonController _fpc;
    private bool _fpcWasEnabled;

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
        if (_uiRoot != null) Destroy(_uiRoot);
    }

    /// <summary>与指定 NPC 开始对话（静态入口，自动创建系统实例）。</summary>
    public static DialogSystem OpenConversation(NPCController npc)
    {
        DialogSystem sys = Instance;
        if (sys == null)
        {
            var go = new GameObject("_DialogSystem");
            sys = go.AddComponent<DialogSystem>();
        }
        sys.Begin(npc);
        return sys;
    }

    private void Begin(NPCController npc)
    {
        AudioManager.Play("SFX_Bell");
        Target = npc;
        Target.InConversation = true;
        _history.Clear();
        _history.Add($"【{npc.npcName}】{GetGreeting(npc)}");
        npc.ShowBubble(GetGreeting(npc));

        EnsureUgui();
        _focusNextUgui = true;

        // 锁玩家移动：对话中 WASD 不再驱动角色（成熟游戏惯例）
        var player = GameObject.Find("Player");
        if (player != null)
        {
            _fpc = player.GetComponent<FirstPersonController>();
            _fpcWasEnabled = _fpc != null && _fpc.enabled;
            if (_fpc != null) _fpc.enabled = false;
        }

        Debug.Log($"[Dialog] 与 {npc.npcName}（{npc.roleName}）开始对话");
    }

    public void Close()
    {
        if (Target != null) Target.InConversation = false;
        Target = null;
        if (_fpc != null) _fpc.enabled = _fpcWasEnabled;
        _fpc = null;
        if (_uiRoot != null) Destroy(_uiRoot);
        Destroy(gameObject);
    }

    private void Update()
    {
        // uGUI 焦点事务（延迟一帧，避开 onSubmit 重入）
        if (_resubmitFocus)
        {
            _resubmitFocus = false;
            if (_field != null) _field.ActivateInputField();
        }
        else if (_focusNextUgui)
        {
            _focusNextUgui = false;
            if (_field != null) _field.ActivateInputField();
        }

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.escapeKey.wasPressedThisFrame && Target != null)
        {
            Close();
            return;
        }

        // 数字键快捷提问（输入框为空且未聚焦时生效，避免想打数字被抢走）
        if (Target != null && !_waitingReply && string.IsNullOrEmpty(InputText) && !InputFocused)
        {
            string quick = null;
            if (kb.digit1Key.wasPressedThisFrame) quick = "你是谁？";
            else if (kb.digit2Key.wasPressedThisFrame) quick = "那座老洋楼有什么来历？";
            else if (kb.digit3Key.wasPressedThisFrame) quick = "我刚才问了什么？";
            if (quick != null)
            {
                StartCoroutine(SendCo(quick));
            }
        }
#endif
    }

    private static readonly (string label, string question)[] QuickAsks =
    {
        ("你是谁", "你是谁？"),
        ("老洋楼来历", "那座老洋楼有什么来历？"),
        ("刚才问了啥", "我刚才问了什么？"),
    };

    private void OnGUI()
    {
        if (Target == null) return;

        UiTheme.BeginScale();
        float w = Mathf.Min(UiTheme.VW * 0.62f, 720f);
        float h = 190f;
        var rect = new Rect((UiTheme.VW - w) / 2f, UiTheme.VH - h - 32f, w, h);

        GUILayout.BeginArea(rect, UiTheme.Panel);
        UiTheme.Wash(rect);

        // ── 行 1：标题 ──
        GUILayout.Label($"<b>与 {Target.npcName}（{Target.roleName}）对话</b>  <color=#5A5042>[Esc 关闭]</color>", UiTheme.Title);

        // ── 行 2：历史滚动区 ──
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(58));
        foreach (string line in _history)
        {
            GUILayout.Label(line, UiTheme.Text(14));
        }
        if (_waitingReply)
        {
            GUILayout.Label("<i>……正在思考</i>", new GUIStyle(UiTheme.Text(14)) { normal = { textColor = UiTheme.InkSoft } });
        }
        GUILayout.EndScrollView();

        // ── 行 3：快捷问题三个按钮并排 ──
        GUILayout.BeginHorizontal();
        foreach (var (label, question) in QuickAsks)
        {
            if (GUILayout.Button(label, UiTheme.Btn, GUILayout.Height(30)) && !_waitingReply)
            {
                StartCoroutine(SendCo(question));
            }
        }
        GUILayout.EndHorizontal();

        // ── 行 4：uGUI 输入行占位（真输入框是 uGUI Overlay，按此矩形对位）──
        GUILayout.Space(4);
        var reserved = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));

        GUILayout.EndArea();
        UiTheme.EndScale();

        // 虚拟坐标 → 屏幕像素，uGUI 行每帧对位
        if (_row != null)
        {
            float s = UiTheme.Scale;
            var screen = new Rect(
                (rect.x + reserved.x) * s,
                (rect.y + reserved.y) * s,
                reserved.width * s,
                reserved.height * s);
            _row.position = new Vector2(screen.x, Screen.height - screen.y - screen.height);
            _row.sizeDelta = new Vector2(screen.width, screen.height);
        }
    }

    private void SendFromField()
    {
        string msg = InputText.Trim();
        if (_waitingReply || string.IsNullOrEmpty(msg)) return;
        _field.text = "";
        StartCoroutine(SendCo(msg));
        _resubmitFocus = true; // 下一帧回到输入框继续打字
    }

    // ── uGUI 构建 ─────────────────────────────────────────────────────
    private void EnsureUgui()
    {
        if (_uiRoot != null) return;

        _uiRoot = new GameObject("_DialogUgui");
        var canvas = _uiRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;

        // 纯 Input System 项目：uGUI 事件必须用 InputSystemUIInputModule
        if (EventSystem.current == null)
        {
            var es = new GameObject("_DialogEventSystem");
            es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            es.AddComponent<InputSystemUIInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
        }

        // 输入行容器（pivot 左下，方便按屏幕矩形摆放）
        var rowGo = new GameObject("Row");
        _row = rowGo.AddComponent<RectTransform>();
        _row.pivot = new Vector2(0f, 0f);
        _row.sizeDelta = new Vector2(600f, 34f);
        rowGo.transform.SetParent(_uiRoot.transform, false);

        // 输入框：纸底 + 墨描边
        var inputGo = new GameObject("Input");
        var inputRt = inputGo.AddComponent<RectTransform>();
        inputRt.SetParent(_row, false);
        inputRt.anchorMin = Vector2.zero;
        inputRt.anchorMax = new Vector2(1f, 1f);
        inputRt.offsetMin = new Vector2(0f, 0f);
        inputRt.offsetMax = new Vector2(-100f, 0f);

        var img = inputGo.AddComponent<Image>();
        img.color = UiTheme.Paper;
        var outline = inputGo.AddComponent<Outline>();
        outline.effectColor = UiTheme.Ink;
        outline.effectDistance = new Vector2(2f, -2f);

        _field = inputGo.AddComponent<InputField>();

        var font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 16);

        var textGo = new GameObject("Text");
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.SetParent(inputGo.transform, false);
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(10f, 2f);
        textRt.offsetMax = new Vector2(-10f, -2f);
        var text = textGo.AddComponent<Text>();
        text.font = font;
        text.fontSize = 16;
        text.color = UiTheme.Ink;
        text.alignment = TextAnchor.MiddleLeft;
        text.supportRichText = false;

        var phGo = new GameObject("Placeholder");
        var phRt = phGo.AddComponent<RectTransform>();
        phRt.SetParent(inputGo.transform, false);
        phRt.anchorMin = Vector2.zero;
        phRt.anchorMax = Vector2.one;
        phRt.offsetMin = new Vector2(10f, 2f);
        phRt.offsetMax = new Vector2(-10f, -2f);
        var ph = phGo.AddComponent<Text>();
        ph.font = font;
        ph.fontSize = 14;
        ph.fontStyle = FontStyle.Italic;
        ph.color = UiTheme.InkSoft;
        ph.alignment = TextAnchor.MiddleLeft;
        ph.text = "想问什么直接说…（回车发送）";

        _field.textComponent = text;
        _field.placeholder = ph;
        _field.targetGraphic = img;
        _field.onSubmit.AddListener(_ => SendFromField());

        // 发送按钮：朱红底纸字
        var sendGo = new GameObject("Send");
        var sendRt = sendGo.AddComponent<RectTransform>();
        sendRt.SetParent(_row, false);
        sendRt.anchorMin = new Vector2(1f, 0f);
        sendRt.anchorMax = new Vector2(1f, 1f);
        sendRt.offsetMin = new Vector2(-90f, 0f);
        sendRt.offsetMax = new Vector2(0f, 0f);

        var sendImg = sendGo.AddComponent<Image>();
        sendImg.color = UiTheme.Vermilion;
        var sendBtn = sendGo.AddComponent<Button>();
        sendBtn.targetGraphic = sendImg;

        var sendTextGo = new GameObject("Label");
        var sendTextRt = sendTextGo.AddComponent<RectTransform>();
        sendTextRt.SetParent(sendGo.transform, false);
        sendTextRt.anchorMin = Vector2.zero;
        sendTextRt.anchorMax = Vector2.one;
        sendTextRt.offsetMin = Vector2.zero;
        sendTextRt.offsetMax = Vector2.zero;
        var sendText = sendTextGo.AddComponent<Text>();
        sendText.font = font;
        sendText.fontSize = 16;
        sendText.color = UiTheme.Paper;
        sendText.alignment = TextAnchor.MiddleCenter;
        sendText.text = "发送";

        sendBtn.onClick.AddListener(SendFromField);
    }

    private IEnumerator SendCo(string message)
    {
        if (ApiClient.Instance == null)
        {
            _history.Add("<color=red>[系统] 场景中没有 ApiClient</color>");
            yield break;
        }

        string msg = message.Trim();
        _history.Add($"<color=#2E5F8A>【你】{msg}</color>");
        _waitingReply = true;
        _scroll.y = float.MaxValue; // 滚到底

        string reply = null, error = null;
        yield return ApiClient.Instance.ChatWithNPC(
            Target.npcName, msg,
            r => reply = r,
            e => error = e);

        _waitingReply = false;
        if (reply != null)
        {
            _history.Add($"【{Target.npcName}】{reply}");
            Target.ShowBubble(reply);
            Debug.Log($"[Dialog] {Target.npcName}: {reply}");
        }
        else
        {
            _history.Add($"<color=red>[系统] {error ?? "发送失败"}</color>");
        }
        _scroll.y = float.MaxValue;
    }

    private static string GetGreeting(NPCController npc)
    {
        return npc.roleName switch
        {
            "茶馆掌柜" => "哎呀，来客人了！要听小镇的故事，还是先来一壶热茶？",
            "巡捕" => "站住……哦，是镇民啊。有事直说，我巡逻时间宝贵。",
            "老板娘" => "哟，客人来啦！刚出锅的包子还冒热气呢，先垫两个？",
            "账房先生" => "幸会幸会，鄙人姓钱。想打听镇上的事？我这账本上可都记着呢。",
            _ => "你好呀，找我有事吗？",
        };
    }
}
