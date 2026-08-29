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

    /// <summary>往对话窗追加一条系统/NPC 行（供委托系统回写接单结果）。</summary>
    public void AddSystemLine(string line)
    {
        _history.Add(line);
        _scroll.y = float.MaxValue;
    }

    private readonly List<string> _history = new();
    private Vector2 _scroll;
    private bool _waitingReply;

    // ── uGUI 输入层 ──
    private GameObject _uiRoot;      // Overlay Canvas 根
    private InputField _field;       // 输入框
    private RectTransform _row;      // 输入行（输入框+发送，每帧对齐 IMGUI 预留位）
    private bool _focusNextUgui;
    private bool _resubmitFocus;
    private static Font _cjkFont;    // OS 字体资产缓存（每次对话重建 UI，字体不随物体销毁，必须复用防泄漏）

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
        RefreshQuickAsks(); // 首帧 OnGUI 就有快捷项（Update 还没轮到）

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
            RefreshQuickAsks();
            string quick = null;
            if (kb.digit1Key.wasPressedThisFrame && _quickAsks.Count > 0) quick = _quickAsks[0].question;
            else if (kb.digit2Key.wasPressedThisFrame && _quickAsks.Count > 1) quick = _quickAsks[1].question;
            else if (kb.digit3Key.wasPressedThisFrame && _quickAsks.Count > 2) quick = _quickAsks[2].question;
            if (quick != null)
            {
                StartCoroutine(SendCo(quick));
            }
        }
#endif
    }

    private readonly List<(string label, string question)> _quickAsks = new();

    /// <summary>
    /// 快捷项跟随游戏状态动态生成（非死板固定三问）：
    /// 无委托 → 头条就是"接个委托"（直接驱动接单）；有本 NPC 的委托 → 问要求/诀窍；
    /// 有别人的委托 → 打听消息。数字键 1/2/3 与按钮共用此列表。
    /// </summary>
    private void RefreshQuickAsks()
    {
        if (Target == null) return;
        if (_quickAsksFor == Target && _quickAsks.Count > 0
            && _quickAsksStamp == (CommissionSystem.Instance != null
                ? CommissionSystem.Instance.ActiveCommission?.id : null))
        {
            return; // 同一 NPC 且委托状态没变，不重算
        }
        _quickAsksFor = Target;
        _quickAsksStamp = CommissionSystem.Instance != null
            ? CommissionSystem.Instance.ActiveCommission?.id : null;
        _quickAsks.Clear();

        var active = CommissionSystem.Instance != null
            ? CommissionSystem.Instance.ActiveCommission : null;
        if (active == null)
        {
            _quickAsks.Add(("接个委托", "接个委托"));
            _quickAsks.Add(("你是谁", "你是谁？"));
            _quickAsks.Add(("镇上传闻", "镇上最近有什么传闻？"));
        }
        else if (active.npc == Target.npcName)
        {
            _quickAsks.Add(("委托要求", $"{active.title}的具体要求是什么？"));
            _quickAsks.Add(("怎么建", $"建{active.typeLabel}有什么诀窍？"));
            _quickAsks.Add(("多久时限", "这单来得及吗？"));
        }
        else
        {
            _quickAsks.Add(("镇上消息", "镇上最近有什么事？"));
            _quickAsks.Add(("你是谁", "你是谁？"));
            _quickAsks.Add(("给我活干", "接个委托"));
        }
    }

    private NPCController _quickAsksFor;   // 快捷项缓存所属 NPC
    private string _quickAsksStamp;        // 委托 id 戳（状态变化即重算）

    private void OnGUI()
    {
        if (Target == null) return;

        UiTheme.BeginScale();
        float w = Mathf.Min(UiTheme.VW * 0.62f, 720f);
        // 高度构成（虚拟坐标，v2 panel_tall border140+padding154 由 PanelTall 自带）：
        // 标题(~34)+Space8+历史区 136+快捷按钮(36+Space8)+Space8+输入行 40 ≈ 270 内容
        // + 上下 padding 154×2（9-slice 内缩）→ 面板总高 560 取整；不够时 IMGUI 溢出
        // 不裁剪，输入行会被挤出面板底框掉到屏幕外（历史 bug 根因），宁大勿小。
        float h = 560f;
        var rect = new Rect((UiTheme.VW - w) / 2f, UiTheme.VH - h - 28f, w, h);

        GUILayout.BeginArea(rect, UiTheme.PanelTall);
        UiTheme.Wash(rect);

        // ── 行 1：标题（说话人识别靠粗体名+淡墨角色，不加字号档）──
        GUILayout.Label($"与 <b>{Target.npcName}</b>（{Target.roleName}）对话  <color=#5A5042>[Esc 关闭]</color>", UiTheme.Title);
        GUILayout.Space(8);

        // ── 行 2：历史区（固定高度。不能 ExpandHeight：GUILayout 滚动区不锁高时
        //    最小高度=内容高度，对话一长就把按钮/输入行挤出面板底框（uGUI 输入行
        //    随 reserved 掉出面板）→"输入框不见了"。固定高+滚动+自动到底才是聊天条正解。
        //    滚动条隐藏——默认黑胶囊与宣纸风冲突，滚轮+自动到底已覆盖需求）
        //    说话人前缀色：【名】朱红=NPC、名=墨粗=玩家（识别优于回忆）──
        _scroll = GUILayout.BeginScrollView(_scroll, GUIStyle.none, GUIStyle.none, GUILayout.Height(136));
        foreach (string line in _history)
        {
            GUILayout.Label(line, UiTheme.Text(UiTheme.SizeBody));
            GUILayout.Space(8f);
        }
        if (_waitingReply)
        {
            GUILayout.Label("<i>……正在思考</i>", UiTheme.Hint);
        }
        GUILayout.EndScrollView();

        // ── 行 3：快捷问题三个按钮并排（32 高+组内自然间距）──
        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        foreach (var (label, question) in _quickAsks)
        {
            if (GUILayout.Button(label, UiTheme.Btn, GUILayout.Height(36f)) && !_waitingReply)
            {
                StartCoroutine(SendCo(question));
            }
        }
        GUILayout.EndHorizontal();

        // ── 行 4：uGUI 输入行占位（真输入框是 uGUI Overlay，按此矩形对位）──
        GUILayout.Space(8f);
        var reserved = GUILayoutUtility.GetRect(0f, 40f, GUILayout.ExpandWidth(true));

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

        // 字号/边距/描边全部随全局缩放走：IMGUI 面板文字是放大后的，uGUI 行不缩放会大小失衡
        float s = UiTheme.Scale;
        float sendW = 92f * s;
        float gap = 8f * s;
        float d = Mathf.RoundToInt(2f * s); // 墨线描边厚度（虚拟 2px）
        int fsInput = Mathf.RoundToInt(15 * s);
        int fsHint = Mathf.RoundToInt(14 * s);
        int fsSend = Mathf.RoundToInt(16 * s);

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
        _row.sizeDelta = new Vector2(600f, 36f);
        rowGo.transform.SetParent(_uiRoot.transform, false);

        if (_cjkFont == null)
        {
            _cjkFont = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 32);
        }

        // 输入框：宣纸底 + 四向墨线描边（两个 Outline 对角叠出对称边）
        var inputGo = new GameObject("Input");
        var inputRt = inputGo.AddComponent<RectTransform>();
        inputRt.SetParent(_row, false);
        inputRt.anchorMin = Vector2.zero;
        inputRt.anchorMax = new Vector2(1f, 1f);
        inputRt.offsetMin = new Vector2(0f, 0f);
        inputRt.offsetMax = new Vector2(-(sendW + gap), 0f);

        var img = inputGo.AddComponent<Image>();
        img.color = UiTheme.Paper;
        var outline = inputGo.AddComponent<Outline>();
        outline.effectColor = UiTheme.Ink;
        outline.effectDistance = new Vector2(d, -d);
        var outline2 = inputGo.AddComponent<Outline>();
        outline2.effectColor = UiTheme.Ink;
        outline2.effectDistance = new Vector2(-d, d);

        _field = inputGo.AddComponent<InputField>();

        var textGo = new GameObject("Text");
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.SetParent(inputGo.transform, false);
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(12f * s, 2f);
        textRt.offsetMax = new Vector2(-12f * s, -2f);
        var text = textGo.AddComponent<Text>();
        text.font = _cjkFont;
        text.fontSize = fsInput;
        text.color = UiTheme.Ink;
        text.alignment = TextAnchor.MiddleLeft;
        text.supportRichText = false;

        var phGo = new GameObject("Placeholder");
        var phRt = phGo.AddComponent<RectTransform>();
        phRt.SetParent(inputGo.transform, false);
        phRt.anchorMin = Vector2.zero;
        phRt.anchorMax = Vector2.one;
        phRt.offsetMin = new Vector2(12f * s, 2f);
        phRt.offsetMax = new Vector2(-12f * s, -2f);
        var ph = phGo.AddComponent<Text>();
        ph.font = _cjkFont;
        ph.fontSize = fsHint;
        ph.fontStyle = FontStyle.Italic;
        ph.color = UiTheme.InkSoft;
        ph.alignment = TextAnchor.MiddleLeft;
        ph.text = "想问什么，直言无妨…（回车发送）";

        _field.textComponent = text;
        _field.placeholder = ph;
        _field.targetGraphic = img;
        _field.onSubmit.AddListener(_ => SendFromField());

        // 发送按钮：朱红底纸字 + 墨线描边，与 BtnPrimary 同语言
        var sendGo = new GameObject("Send");
        var sendRt = sendGo.AddComponent<RectTransform>();
        sendRt.SetParent(_row, false);
        sendRt.anchorMin = new Vector2(1f, 0f);
        sendRt.anchorMax = new Vector2(1f, 1f);
        sendRt.offsetMin = new Vector2(-sendW, 0f);
        sendRt.offsetMax = new Vector2(0f, 0f);

        var sendImg = sendGo.AddComponent<Image>();
        sendImg.color = UiTheme.Vermilion;
        var sendOutline = sendGo.AddComponent<Outline>();
        sendOutline.effectColor = UiTheme.Ink;
        sendOutline.effectDistance = new Vector2(d, -d);
        var sendOutline2 = sendGo.AddComponent<Outline>();
        sendOutline2.effectColor = UiTheme.Ink;
        sendOutline2.effectDistance = new Vector2(-d, d);
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
        sendText.font = _cjkFont;
        sendText.fontSize = fsSend;
        sendText.color = UiTheme.Paper;
        sendText.alignment = TextAnchor.MiddleCenter;
        sendText.text = "发送";

        sendBtn.onClick.AddListener(SendFromField);
    }

    /// <summary>消息是否在讨委托/任务（关键词命中即走接单流程）。</summary>
    private static bool IsCommissionIntent(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return false;
        string[] keys = { "委托", "接活", "任务", "订单", "干活", "有什么活" };
        foreach (var k in keys)
        {
            if (msg.Contains(k)) return true;
        }
        return false;
    }

    private IEnumerator SendCo(string message)
    {
        ApiClient.EnsureExists(); // Play 中途脚本重载会洗掉单例，先懒补建
        if (ApiClient.Instance == null)
        {
            _history.Add("<color=red>[系统] 场景中没有 ApiClient</color>");
            yield break;
        }

        string msg = message.Trim();
        _history.Add($"<color=#2E5F8A>【你】{msg}</color>");
        _scroll.y = float.MaxValue; // 滚到底

        // 对话里提"委托/接活/任务/订单" → 走接单流程（LLM 现场生成委托话术+落绿圈）
        if (CommissionSystem.Instance != null && IsCommissionIntent(msg))
        {
            yield return CommissionSystem.Instance.RequestCommissionFromDialog(Target);
            yield break;
        }

        _waitingReply = true;
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
