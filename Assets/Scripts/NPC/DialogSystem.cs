using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 对话系统：底部对话面板（历史 + 输入框 + 发送）。
/// 由 NPCController.E 键打开；Esc 关闭。全局唯一，同一时间只与一个 NPC 对话。
/// </summary>
public class DialogSystem : MonoBehaviour
{
    private static DialogSystem _instance;
    public static DialogSystem Instance => _instance;

    public NPCController Target { get; private set; }

    private readonly List<string> _history = new();
    private string _input = "";
    private Vector2 _scroll;
    private bool _waitingReply;
    private bool _focusInputNext;
    private string _controlName = "dialog_input";

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
        Target = npc;
        Target.InConversation = true;
        _history.Clear();
        _history.Add($"【{npc.npcName}】{GetGreeting(npc)}");
        npc.ShowBubble(GetGreeting(npc));
        _focusInputNext = true;
        Debug.Log($"[Dialog] 与 {npc.npcName}（{npc.roleName}）开始对话");
    }

    public void Close()
    {
        if (Target != null) Target.InConversation = false;
        Target = null;
        Destroy(gameObject);
    }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        if (kb.escapeKey.wasPressedThisFrame && Target != null)
        {
            Close();
            return;
        }

        // 数字键快捷提问（输入框为空时生效；Input System 路径，远程/手柄均可触发）
        if (Target != null && !_waitingReply && string.IsNullOrEmpty(_input))
        {
            string quick = null;
            if (kb.digit1Key.wasPressedThisFrame) quick = "你是谁？";
            else if (kb.digit2Key.wasPressedThisFrame) quick = "这座城堡有什么故事？";
            else if (kb.digit3Key.wasPressedThisFrame) quick = "我刚才问了什么？";
            if (quick != null)
            {
                StartCoroutine(SendCo(quick));
            }
        }
#endif
    }

    private void OnGUI()
    {
        if (Target == null) return;

        float w = Mathf.Min(Screen.width * 0.55f, 640f);
        float h = 220f;
        var rect = new Rect((Screen.width - w) / 2f, Screen.height - h - 40f, w, h);

        GUILayout.BeginArea(rect, GUI.skin.box);
        GUILayout.Label($"<b>与 {Target.npcName}（{Target.roleName}）对话</b>   <color=#888>[Esc 关闭]</color>");
        GUILayout.Label("<color=#888><b>[1]</b> 你是谁　<b>[2]</b> 城堡故事　<b>[3]</b> 我刚才问了什么　（或直接输入）</color>", new GUIStyle(GUI.skin.label) { fontSize = 11 });

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(h - 92));
        foreach (string line in _history)
        {
            GUILayout.Label(line, new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true });
        }
        if (_waitingReply)
        {
            GUILayout.Label("<i>……正在思考</i>", new GUIStyle(GUI.skin.label) { normal = { textColor = Color.gray } });
        }
        GUILayout.EndScrollView();

        GUILayout.BeginHorizontal();
        GUI.SetNextControlName(_controlName);
        _input = GUILayout.TextField(_input, GUILayout.Width(w - 96));

        if (_focusInputNext)
        {
            GUI.FocusControl(_controlName);
            _focusInputNext = false;
        }

        if (GUILayout.Button("发送", GUILayout.Width(80)) && !_waitingReply && !string.IsNullOrWhiteSpace(_input))
        {
            StartCoroutine(SendCo(_input));
            _input = "";
            GUI.FocusControl(_controlName);
        }
        GUILayout.EndHorizontal();

        // 输入框聚焦时回车=发送
        if (Event.current.isKey && Event.current.keyCode == KeyCode.Return
            && GUI.GetNameOfFocusedControl() == _controlName
            && !_waitingReply && !string.IsNullOrWhiteSpace(_input))
        {
            StartCoroutine(SendCo(_input));
            _input = "";
        }
        GUILayout.EndArea();
    }

    private IEnumerator SendCo(string message)
    {
        if (ApiClient.Instance == null)
        {
            _history.Add("<color=red>[系统] 场景中没有 ApiClient</color>");
            yield break;
        }

        string msg = message.Trim();
        _history.Add($"<color=#7FB3D5>【你】{msg}</color>");
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
            "面包师" => "哎呀，来客人了！要听面包的故事，还是直接来一条热乎的？",
            "城堡守卫" => "站住……哦，是镇民啊。有事直说，我巡逻时间宝贵。",
            _ => "你好呀，找我有事吗？",
        };
    }
}
