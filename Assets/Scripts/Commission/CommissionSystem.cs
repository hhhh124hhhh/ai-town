using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 委托建造系统：右上 HUD（繁荣度/金币/进行中委托）+ C 键委托面板。
/// 循环：向 NPC 请求委托（地面出现绿圈验收区）→ Tab 面板建造 → 提交验收
/// → 服务端规则判分（类型/占地/方块数/距离）+ LLM 角色化点评 → 金币/繁荣/好感/解锁模板。
/// 由 BuildingPanel.Start() 懒创建，无需场景接线；服务离线时模板全解锁（fail-open，不影响原演示）。
/// </summary>
public class CommissionSystem : MonoBehaviour
{
    private static CommissionSystem _instance;
    public static CommissionSystem Instance => _instance;

    // ── 服务端 JSON 镜像（字段名与 server/commission_ai.py 对齐）──────────
    [Serializable]
    public class CommissionInfo
    {
        public string id;
        public string npc;
        public string title;
        public string desc;
        public string type;
        public string typeLabel;
        public int minBlocks;
        public float minSize;
        public float zoneX;
        public float zoneZ;
        public float zoneRadius;
        public int rewardGold;
        public string unlock;
        public int difficulty;
    }

    [Serializable]
    public class NpcAffinity
    {
        public string name;
        public string role;
        public int affinity;
        public string affinityLabel;
    }

    [Serializable]
    public class CommissionState
    {
        public int gold;
        public int prosperity;
        public int level;
        public string levelName;
        public int completed;
        public string[] unlocked;
        public string[] lockedDefault;
        public NpcAffinity[] npcs;
        public CommissionInfo active;
    }

    [Serializable]
    private class StateResponse
    {
        public bool ok;
        public string error;
        public CommissionState state;
    }

    [Serializable]
    private class NewResponse
    {
        public bool ok;
        public string error;
        public CommissionInfo commission;
        public CommissionState state;
    }

    [Serializable]
    private class SubmitResponse
    {
        public bool ok;
        public string error;
        public bool pass;
        public string grade;
        public string comment;
        public string[] reasons;
        public string buildName;
        public int rewardGold;
        public int rewardProsperity;
        public string unlocked;
        public CommissionState state;
    }

    [Serializable]
    private class BuildEntry
    {
        public string name;
        public string description;
        public string template;
        public int blockCount;
        public float[] pos;
        public float[] extents;
    }

    [Serializable]
    private class BuildsRequest
    {
        public BuildEntry[] builds;
        public float[] zoneCenter;   // 最近一次放置落点 XZ（服务端绿圈判分跟随）
    }

    private class BuildRecord
    {
        public string Name;
        public string Description;
        public string Template;
        public int BlockCount;
        public Transform Root;
    }

    private readonly List<BuildRecord> _builds = new();
    private readonly List<NPCController> _npcs = new();
    private CommissionState _state;
    private bool _fetched;          // 已从服务端拉到过状态
    private bool _offline;          // 服务不可达：HUD 隐藏、模板全解锁
    private bool _panelVisible;
    private bool _busy;
    private string _status = "";
    private string _resultBox = "";
    private Vector2 _scroll;
    private LineRenderer _zoneRing;

    /// <summary>当前进行中的委托（无则 null）。对话快捷项据此动态生成。</summary>
    public CommissionInfo ActiveCommission => _state?.active;

    /// <summary>懒创建（BuildingPanel.Start 调用），场景无需手动接线。</summary>
    public static void EnsureExists()
    {
        if (_instance == null)
        {
            var go = new GameObject("_CommissionSystem");
            go.AddComponent<CommissionSystem>();
        }
    }

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
        RefreshNpcCache();
        StartCoroutine(RefreshStateCo());
    }

    private void RefreshNpcCache()
    {
        _npcs.Clear();
        foreach (var npc in FindObjectsOfType<NPCController>()) _npcs.Add(npc);
    }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (!CinematicIntro.IsCinematic && !CinematicIntro.InputCooldown)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.cKey.wasPressedThisFrame && DialogSystem.Instance == null
                && !BuildingPlacement.Active && !UiTextFocus.IsTyping)
            {
                if (_panelVisible) RefreshNpcCache(); // 面板里可能新增了 NPC
                // v2 互斥：C 切换走 UiPanelLayout（开委托自动关建筑）
                UiPanelLayout.Request(UiPanelLayout.Panel.Commission);
                _panelVisible = UiPanelLayout.CommissionVisible;
            }
        }
#endif
        // 等级跟踪（升级后闪烁结束即归位；HUD 常显最新等级）
        if (_state != null && _levelShown != _state.level && Time.unscaledTime >= _flashUntil)
        {
            _levelShown = _state.level;
            _levelUpTo = 0;
        }
    }

    /// <summary>BuildingPanel 每次生成建筑后登记，验收时统一上报（服务端取最优匹配）。</summary>
    public void RegisterBuild(string name, string description, string template, int blockCount, Transform root)
    {
        _builds.Add(new BuildRecord
        {
            Name = name,
            Description = description,
            Template = template,
            BlockCount = blockCount,
            Root = root,
        });
        if (_builds.Count > 10) _builds.RemoveAt(0);
    }

    /// <summary>模板是否解锁。无实例/离线/未拉到状态时全解锁（保证原演示不受影响）。</summary>
    public static bool IsTemplateUnlocked(string template)
    {
        var sys = _instance;
        if (sys == null || sys._offline || sys._state?.lockedDefault == null) return true;
        if (Array.IndexOf(sys._state.lockedDefault, template) < 0) return true;
        return sys._state.unlocked != null && Array.IndexOf(sys._state.unlocked, template) >= 0;
    }

    // ── IMGUI ─────────────────────────────────────────────────────────────
    private void OnGUI()
    {
        if (CinematicIntro.IsCinematic) return; // 开场演出期间 HUD/面板不显示

        UiTheme.BeginScale();
        DrawFlash();

        if (!_offline && _fetched) DrawHud();

        if (_panelVisible)
        {
            DrawPanel();
        }
        UiTheme.EndScale();
    }

    private float _hudBottom = 100f; // HUD 实际底边（自适应后），委托面板挂其下方

    // 繁荣度等级阈值（镜像服务端 PROSPERITY_LEVELS，进度条数据源）
    private static readonly (int threshold, string name)[] ProsperityLevels =
    {
        (0, "荒地聚落"), (100, "边陲小村"), (250, "热闹小镇"), (450, "繁荣市镇"), (700, "传奇之城"),
    };

    private void DrawHud()
    {
        const float Pad = 20f;
        var st = UiTheme.Text(UiTheme.SizeBody);
        var active = _state.active;
        string line1 = $"<b>★{_state.level} {_state.levelName}</b>　繁荣 {_state.prosperity}　大洋 {_state.gold}　完成 {_state.completed} 单";
        string line2 = active != null
            ? $"<color=#9E2B25><b>委托：{(string.IsNullOrEmpty(active.npc) ? "" : active.npc + " · ")}{(string.IsNullOrEmpty(active.title) ? "进行中" : active.title)}</b></color>（[C] 面板）"
            : null;

        // 按内容自适应：宽度=最长行+对称 padding；高度=上下 padding+行高+IMGUI 间距余量
        var measure = new GUIStyle(st) { wordWrap = false };
        var s1 = measure.CalcSize(new GUIContent(line1));
        var s2 = line2 != null ? measure.CalcSize(new GUIContent(line2)) : Vector2.zero;
        float w = Mathf.Max(240f, Mathf.Max(s1.x, s2.x)) + Pad * 2f + 10f;
        float h = Pad * 2f + s1.y + (line2 != null ? s2.y + 4f : 0f) + 10f;
        _hudBottom = 16f + h;

        // HUD 均衡布局（2026-08-29 用户定则）：左上=游戏状态（玩家第一眼扫左上），
        // 右上让给系统/键位提示卡，底部中央=功能坞，委托弹窗居中——对称弹窗+均衡 HUD。
        var rect = new Rect(UiTheme.RightMargin, 16f, w, h);
        GUILayout.BeginArea(rect, UiTheme.Hud);
        UiTheme.Wash(rect, 0.95f); // HUD 信息行多、v2 贴图也有纸纹，近实底才素净
        GUILayout.Label(line1, st);
        if (line2 != null) GUILayout.Label(line2, st);
        GUILayout.EndArea();

        DrawKeyHints(); // 右上：系统/键位卡（占小地图位）
    }

    /// <summary>右上角键位提示卡（均衡法则的系统位；本游戏无小地图，键位提示承担该角色）。</summary>
    private static void DrawKeyHints()
    {
        var st = UiTheme.Hint;
        var measure = new GUIStyle(st) { wordWrap = false };
        string txt = "[Tab] 建造　[C] 委托　[E] 对话　[X] 回出生点";
        var s = measure.CalcSize(new GUIContent(txt));
        float w = s.x + 32f;
        float h = s.y + 20f;
        var rect = new Rect(UiTheme.VW - w - UiTheme.RightMargin, 16f, w, h);
        GUILayout.BeginArea(rect, UiTheme.Hud);
        UiTheme.Wash(rect, 0.85f);
        GUILayout.Label(txt, st);
        GUILayout.EndArea();
    }

    /// <summary>区块头：菱形点 + 标题 + 细墨线贯通右侧（报纸栏线语言）。</summary>
    private static void SecHeader(string title)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("◆", UiTheme.Text(UiTheme.SizeHint), GUILayout.Width(14f));
        GUILayout.Label(title, UiTheme.SecHead);
        var rule = GUILayoutUtility.GetRect(8f, 1f, GUILayout.ExpandWidth(true), GUILayout.Height(1f));
        UiTheme.DrawRule(rule, 0.35f);
        GUILayout.EndHorizontal();
    }

    /// <summary>k/v 行式信息（组内 8 间距：k 固定宽淡墨，v 正文）。</summary>
    private static void KeyValueRow(string key, string valueRichText)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(key, UiTheme.Hint, GUILayout.Width(72f));
        GUILayout.Label(valueRichText, UiTheme.Text(UiTheme.SizeBody));
        GUILayout.EndHorizontal();
    }

    private void DrawPanel()
    {
        // v2 panel_main 9-slice border 78 + padding 96（UiTheme.Panel 自带）；420 宽 = 3×96 + 1 字高
        // 委托大厅=弹窗型功能面板，按弹窗对称规则居中显示（2026-08-29 用户 HUD 均衡法则）
        float w = 420f;
        float h = Mathf.Min(500f, UiTheme.VH - 80f);
        var rect = new Rect((UiTheme.VW - w) / 2f, (UiTheme.VH - h) / 2f, w, h); // 屏幕居中

        GUILayout.BeginArea(rect, UiTheme.Panel);
        UiTheme.Wash(rect);

        // ── 头部行：标题 + 印章（盖章=受理隐喻；印章 ≤1/屏）──
        GUILayout.BeginHorizontal();
        GUILayout.Label("委托大厅  <color=#5A5042>[C 关闭]</color>", UiTheme.Title);
        GUILayout.FlexibleSpace();
        var sealRect = GUILayoutUtility.GetRect(48f, 48f, GUILayout.Width(48f), GUILayout.Height(48f));
        UiTheme.DrawSeal(sealRect);
        GUILayout.EndHorizontal();

        if (_state != null)
        {
            // ── 状态条：数字 20 加粗为面板锚点（层级：字重+颜色 > 字号）──
            GUILayout.BeginHorizontal();
            GUILayout.Label($"★{_state.level} {_state.levelName}", UiTheme.Text(UiTheme.SizeBody));
            GUILayout.Space(16f);
            GUILayout.Label($"{_state.prosperity} 繁荣", NumStyle());
            GUILayout.Space(16f);
            GUILayout.Label($"<color=#9E2B25>{_state.gold} 大洋</color>", NumStyle());
            GUILayout.Space(16f);
            GUILayout.Label($"{_state.completed} 单", NumStyle());
            GUILayout.EndHorizontal();

            // ── 目标梯度进度条（游戏心理学：离目标越近动机越强，进度必须可视化）──
            int nextIdx = 0;
            for (int i = 0; i < ProsperityLevels.Length; i++)
            {
                if (_state.prosperity >= ProsperityLevels[i].threshold) nextIdx = i + 1;
            }
            if (nextIdx < ProsperityLevels.Length)
            {
                var next = ProsperityLevels[nextIdx];
                int span = next.threshold - ProsperityLevels[nextIdx - 1].threshold;
                int done = _state.prosperity - ProsperityLevels[nextIdx - 1].threshold;
                GUILayout.Space(8f);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"距 <b>★{nextIdx + 1} {next.name}</b> 还需 {next.threshold - _state.prosperity} 繁荣", UiTheme.Hint);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{_state.prosperity} / {next.threshold}", UiTheme.Hint);
                GUILayout.EndHorizontal();
                var track = GUILayoutUtility.GetRect(8f, 6f, GUILayout.ExpandWidth(true), GUILayout.Height(6f));
                UiTheme.DrawProgress(track, done, Mathf.Max(1, span));
            }

            // 好感行（弱信息：12 号淡墨）
            if (_state.npcs != null && _state.npcs.Length > 0)
            {
                var aff = new System.Text.StringBuilder();
                foreach (var n in _state.npcs) aff.Append($"{n.name}（{n.affinityLabel}）　");
                GUILayout.Space(8f);
                GUILayout.Label(aff.ToString(), UiTheme.Hint);
            }
        }

        GUILayout.Space(16f);

        var active = _state?.active;
        if (active != null)
        {
            // ── 当前委托区 ──
            SecHeader("当前委托");
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{active.title}</b>", UiTheme.SecHead);
            GUILayout.Space(8f);
            GUILayout.Label($"<color=#5A5042>{active.npc}　难度 {new string('●', Math.Max(1, active.difficulty))}</color>", UiTheme.Text(UiTheme.SizeBody));
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
            GUILayout.Label(active.desc, UiTheme.Text(UiTheme.SizeBody));

            // ── 验收要求区（k/v 行式，识别优于回忆：文字明示参数）──
            GUILayout.Space(16f);
            SecHeader("验收要求");
            GUILayout.Space(8f);
            _scroll = GUILayout.BeginScrollView(_scroll, GUIStyle.none, GUIStyle.none,
                GUILayout.Height(Mathf.Min(h - 320f, 150f)));
            KeyValueRow("建筑类型", $"骑楼式 {active.typeLabel} —— Tab 面板输入「建一座{active.typeLabel}」或点图纸");
            KeyValueRow("规模", $"占地 ≥ {active.minSize:0} 米　·　方块 ≥ {active.minBlocks} 个");
            KeyValueRow("落点", $"建在 <color=#1E7A1E>绿圈</color>内（{active.npc} 附近 {active.zoneRadius:0} 米）");
            GUILayout.EndScrollView();

            // ── 酬劳行（结尾甜枣：金底框+大金数字）──
            GUILayout.Space(16f);
            GUILayout.BeginHorizontal(UiTheme.Card);
            GUILayout.Label("酬　劳", UiTheme.Text(UiTheme.SizeBody));
            GUILayout.Space(16f);
            GUILayout.Label($"<size=20><b><color=#8A5A00>{active.rewardGold}</color></b></size>", UiTheme.Rich);
            GUILayout.Label("大洋", UiTheme.Text(UiTheme.SizeBody));
            GUILayout.Space(16f);
            GUILayout.Label(string.IsNullOrEmpty(active.unlock) ? "" : $"＋ 解锁图纸「{active.unlock}」", UiTheme.Text(UiTheme.SizeBody));
            GUILayout.EndHorizontal();
        }
        else if (!_busy)
        {
            SecHeader("当前没有委托");
            GUILayout.Space(8f);
            GUILayout.Label("找谁接活？（走到 NPC 附近可 [E] 闲聊打听）", UiTheme.Text(UiTheme.SizeBody));
            GUILayout.Space(8f);
            if (_npcs.Count == 0)
            {
                GUILayout.Label("<color=red>场景里没有 NPC（NPCController）</color>", UiTheme.Text(UiTheme.SizeBody));
            }
            foreach (var npc in _npcs)
            {
                if (GUILayout.Button($"向 {npc.npcName}（{npc.roleName}）请求委托", UiTheme.Btn))
                {
                    StartCoroutine(NewCo(npc));
                }
            }
        }

        // ── 操作区推底（等效 margin-top:auto）──
        if (active != null)
        {
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUI.enabled = !_busy && _builds.Count > 0;
            if (GUILayout.Button($"提 交 验 收（已建 {_builds.Count} 栋）", UiTheme.BtnPrimary, GUILayout.Height(44f)))
            {
                StartCoroutine(SubmitCo());
            }
            GUI.enabled = !_busy;
            if (GUILayout.Button("放弃委托", UiTheme.Btn, GUILayout.Height(44f)))
            {
                StartCoroutine(AbandonCo());
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        if (_busy)
        {
            GUILayout.Space(8f);
            GUILayout.Label("<i>……正在与 NPC 交谈</i>", UiTheme.Hint);
        }
        if (!string.IsNullOrEmpty(_resultBox))
        {
            GUILayout.Space(8f);
            GUILayout.Box(_resultBox, new GUIStyle(UiTheme.Card) { wordWrap = true, richText = true, fontSize = UiTheme.SizeBody }, GUILayout.Height(84f));
        }
        if (!string.IsNullOrEmpty(_status))
        {
            GUILayout.Space(4f);
            GUILayout.Label(_status, UiTheme.Hint);
        }
        GUILayout.EndArea();
    }

    /// <summary>状态数字样式（20 加粗，无换行）。</summary>
    private static GUIStyle _numStyle;
    private static GUIStyle NumStyle()
    {
        if (_numStyle == null)
        {
            _numStyle = new GUIStyle(UiTheme.Text(UiTheme.SizeNum)) { wordWrap = false, richText = true };
            _numStyle.fontStyle = FontStyle.Bold;
        }
        return _numStyle;
    }

    // ── 网络流程 ───────────────────────────────────────────────────────────
    private IEnumerator RefreshStateCo()
    {
        if (ApiClient.Instance == null) yield break;

        string json = null, error = null;
        yield return ApiClient.Instance.GetCommissionState(j => json = j, e => error = e);

        if (json != null)
        {
            var resp = JsonUtility.FromJson<StateResponse>(json);
            if (resp != null && resp.ok)
            {
                _state = resp.state;
                _fetched = true;
                _offline = false;
                if (_state.active != null) CreateZoneRing(_state.active);
            }
            else _offline = true;
        }
        else _offline = true;
    }

    private IEnumerator NewCo(NPCController npc)
    {
        ApiClient.EnsureExists(); // Play 中途脚本重载会洗掉单例，先懒补建
        if (ApiClient.Instance == null)
        {
            _status = "<color=red>场景中没有 ApiClient</color>";
            yield break;
        }

        _busy = true;
        _status = $"正在听 {npc.npcName} 说……（LLM 生成委托话术）";
        string json = null, error = null;
        yield return ApiClient.Instance.RequestCommission(
            npc.npcName, npc.transform.position,
            j => json = j, e => error = e);
        _busy = false;

        if (json == null)
        {
            _status = $"<color=red>{error ?? "请求失败"}</color>";
            yield break;
        }

        var resp = JsonUtility.FromJson<NewResponse>(json);
        if (resp == null || !resp.ok)
        {
            _status = $"<color=red>{resp?.error ?? error ?? "发单失败"}</color>";
            yield break;
        }

        _state = resp.state;
        _builds.Clear();
        _lastPlacedPos = null; // 新委托未放置前不带上一个委托的落点
        _resultBox = "";
        CreateZoneRing(resp.commission);
        npc.ShowBubble($"委托：{resp.commission.title}（[C] 查看详情）", 8f);
        _status = $"<color=green>已接下「{resp.commission.title}」，在绿圈内用 Tab 面板建造，完成后回来提交验收</color>";
    }

    /// <summary>
    /// 对话中接单入口（DialogSystem 调用）：玩家对 NPC 说"接个委托"即触发。
    /// 已有进行中委托时把提示回给对话窗，不覆盖。
    /// </summary>
    public IEnumerator RequestCommissionFromDialog(NPCController npc)
    {
        if (_state?.active != null)
        {
            DialogSystem.Instance?.AddSystemLine(
                $"【{npc.npcName}】你手上还有一单「{_state.active.title}」没交呢，先干完那单（[C] 看详情）。");
            yield break;
        }

        yield return NewCo(npc);

        // 接单结果同步进对话历史（NewCo 已写 _status / NPC 气泡）
        if (_state?.active != null)
        {
            var a = _state.active;
            DialogSystem.Instance?.AddSystemLine(
                $"【{npc.npcName}】那就拜托你了——「{a.title}」：{a.desc} 建在绿圈内（{a.zoneRadius:0} 米），好了来找我（[C] 提交）。");
        }
        else if (!string.IsNullOrEmpty(_status))
        {
            DialogSystem.Instance?.AddSystemLine($"<color=#9E2B25>[系统] {_status}</color>");
        }
    }

    private IEnumerator SubmitCo()
    {
        if (ApiClient.Instance == null) yield break;

        // 组装建筑清单（清除过的建筑 transform 已销毁，跳过）
        var entries = new List<BuildEntry>();
        foreach (var rec in _builds)
        {
            if (rec.Root == null) continue;
            var renderers = rec.Root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) continue;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            Vector3 pos = rec.Root.position;
            entries.Add(new BuildEntry
            {
                name = rec.Name,
                description = rec.Description,
                template = rec.Template,
                blockCount = rec.BlockCount,
                pos = new[] { pos.x, pos.y, pos.z },
                extents = new[] { bounds.extents.x, bounds.extents.y, bounds.extents.z },
            });
        }
        if (entries.Count == 0)
        {
            _status = "<color=red>接单后的建筑都已被清除，先重新建造</color>";
            yield break;
        }

        _busy = true;
        _status = $"{entries.Count} 栋建筑提交验收，{(_state?.active?.npc ?? "NPC")} 正在检查……";
        string json = null, error = null;
        var req = new BuildsRequest { builds = entries.ToArray() };
        if (_lastPlacedPos.HasValue)
        {
            req.zoneCenter = new[] { _lastPlacedPos.Value.x, _lastPlacedPos.Value.z };
        }
        yield return ApiClient.Instance.SubmitCommission(
            JsonUtility.ToJson(req),
            j => json = j, e => error = e);
        _busy = false;

        if (json == null)
        {
            _status = $"<color=red>{error ?? "提交失败"}</color>";
            yield break;
        }

        var resp = JsonUtility.FromJson<SubmitResponse>(json);
        if (resp == null || !resp.ok)
        {
            _status = $"<color=red>{resp?.error ?? "验收失败"}</color>";
            yield break;
        }

        _state = resp.state;
        string reasons = resp.reasons != null ? string.Join("\n", resp.reasons) : "";
        if (resp.pass)
        {
            int prevLevel = _levelShown;
            _resultBox = $"<color=#1E7A1E><b>验收通过（{resp.grade}）</b></color>\n{resp.comment}\n<color=#8A5A00>+{resp.rewardGold} 大洋　+{resp.rewardProsperity} 繁荣{(string.IsNullOrEmpty(resp.unlocked) ? "" : $"　解锁图纸：{resp.unlocked}")}</color>";
            DestroyZoneRing();
            _builds.Clear();
            _status = "";
            NpcBubble(_lastCommissionNpc, resp.comment);
            ShowFlash(resp.grade, resp.rewardGold, resp.rewardProsperity, resp.unlocked, prevLevel);
        }
        else
        {
            _resultBox = $"<color=red><b>验收未通过</b></color>\n{resp.comment}\n{reasons}";
            _status = "<color=#8A5A00>按委托要求调整后可再次提交</color>";
        }
    }

    private string _lastCommissionNpc = "";

    // ── 验收高光闪现 ─────────────────────────────────────────────────────
    private float _flashUntil;          // Time.unscaledTime 之后隐藏
    private float _flashStart;
    private string _flashGrade;
    private int _flashGold;
    private int _flashProsperity;
    private string _flashUnlock;
    private int _levelUpTo;             // >0 = 本次触发了繁荣度升级庆祝
    private int _levelShown = 1;        // 已展示过的等级（检测升级）

    /// <summary>验收通过闪现 + 繁荣度升级检测。prevLevel 为提交前展示等级。</summary>
    private void ShowFlash(string grade, int gold, int prosperity, string unlock, int prevLevel)
    {
        AudioManager.Play("SFX_Gong");
        // 交付庆典 confetti：与 DrawFlash 弹入同窗起（≤0.2s），位置=最近落位/绿圈中心
        EffectsCatalog.Play(EffectsCatalog.Celebration, CelebratePos());

        _flashGrade = grade;
        _flashGold = gold;
        _flashProsperity = prosperity;
        _flashUnlock = unlock;
        _flashStart = Time.unscaledTime;
        _flashUntil = _flashStart + 2.6f;

        if (_state != null && _state.level > prevLevel)
        {
            _levelUpTo = _state.level;
            _flashUntil = _flashStart + 3.4f; // 升级时多看一会
            StartCoroutine(LevelUpBurstCo()); // 升级追加一爆
        }
    }

    /// <summary>庆典特效落点：优先最近建筑落位，其次绿圈中心，兜底第一个 NPC。</summary>
    private Vector3 CelebratePos()
    {
        if (_lastPlacedPos.HasValue)
        {
            return new Vector3(_lastPlacedPos.Value.x, 0.1f, _lastPlacedPos.Value.z);
        }
        var active = _state?.active;
        if (active != null) return new Vector3(active.zoneX, 0.1f, active.zoneZ);
        return _npcs.Count > 0 ? _npcs[0].transform.position : Vector3.zero;
    }

    private IEnumerator LevelUpBurstCo()
    {
        yield return new WaitForSecondsRealtime(1.0f);
        EffectsCatalog.Play(EffectsCatalog.Celebration, CelebratePos() + Vector3.up * 1.2f);
    }

    private void DrawFlash()
    {
        if (Time.unscaledTime >= _flashUntil || string.IsNullOrEmpty(_flashGrade)) return;

        float t = Time.unscaledTime - _flashStart;
        // 入场 0.25s 弹入，出场前 0.5s 淡出
        float alpha = Mathf.Clamp01(t / 0.25f) * Mathf.Clamp01((_flashUntil - Time.unscaledTime) / 0.5f);
        float scale = 1f + 0.18f * (1f - Mathf.Clamp01(t / 0.25f)); // 入场时略大再缩到位

        // 半透明暗色底带（中带）
        var tex = Texture2D.whiteTexture;
        Color prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.45f * alpha);
        GUI.DrawTexture(new Rect(0, UiTheme.VH * 0.30f, UiTheme.VW, UiTheme.VH * 0.30f), tex);
        GUI.color = prev;

        bool isS = _flashGrade == "S";
        Color gradeColor = isS ? new Color(1f, 0.82f, 0.25f) : new Color(0.5f, 1f, 0.6f);

        float cx = UiTheme.VW / 2f;
        float gradeSize = 110f * scale;

        var gradeStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(gradeSize),
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(gradeColor.r, gradeColor.g, gradeColor.b, alpha) },
        };
        var gs = new GUIStyle(gradeStyle) { normal = { textColor = new Color(0f, 0f, 0f, alpha * 0.8f) } };
        GUI.Label(new Rect(cx - 60 + 3, UiTheme.VH * 0.31f + 3, 120, 130), _flashGrade, gs);
        GUI.Label(new Rect(cx - 60, UiTheme.VH * 0.31f, 120, 130), _flashGrade, gradeStyle);

        var titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = UiTheme.SizeDisplay,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(1f, 1f, 1f, alpha) },
        };
        GUI.Label(new Rect(cx - 300, UiTheme.VH * 0.335f + 110, 600, 40), $"交 付 成 功", titleStyle);

        var rewardStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = UiTheme.SizeNum,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(1f, 0.85f, 0.5f, alpha) },
        };
        string unlockTxt = string.IsNullOrEmpty(_flashUnlock) ? "" : $"　·　解锁图纸 {_flashUnlock}";
        GUI.Label(new Rect(cx - 400, UiTheme.VH * 0.335f + 150, 800, 34),
                  $"＋{_flashGold} 大洋　＋{_flashProsperity} 繁荣{unlockTxt}", rewardStyle);

        // 繁荣度升级庆祝（叠加在下方；演出豁免：峰值时刻，字号取 Display 档）
        if (_levelUpTo > 0 && _state != null)
        {
            var lvlStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = UiTheme.SizeDisplay,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.6f, 0.95f, 1f, alpha) },
            };
            GUI.Label(new Rect(cx - 400, UiTheme.VH * 0.335f + 190, 800, 40),
                      $"★★ 小镇升级：{_state.levelName} ★★", lvlStyle);
        }
    }

    private IEnumerator AbandonCo()
    {
        if (ApiClient.Instance == null) yield break;
        _busy = true;
        string json = null, error = null;
        yield return ApiClient.Instance.AbandonCommission(j => json = j, e => error = e);
        _busy = false;
        if (json == null)
        {
            _status = $"<color=red>{error ?? "操作失败"}</color>";
            yield break;
        }
        var resp = JsonUtility.FromJson<StateResponse>(json);
        if (resp != null && resp.ok) _state = resp.state;
        DestroyZoneRing();
        _builds.Clear();
        _resultBox = "";
        _status = "已放弃委托，可以重新接单";
    }

    // ── 辅助 ──────────────────────────────────────────────────────────────
    private void NpcBubble(string npcName, string text)
    {
        foreach (var npc in _npcs)
        {
            if (npc != null && npc.npcName == npcName)
            {
                npc.ShowBubble(text, 9f);
                return;
            }
        }
    }

    /// <summary>验收区绿圈（LineRenderer，Sprites/Default 半透明，不依赖项目资源）。</summary>
    private void CreateZoneRing(CommissionInfo c)
    {
        if (c == null) return;
        _lastCommissionNpc = c.npc;
        DestroyZoneRing();

        var go = new GameObject("CommissionZoneRing");
        var lr = go.AddComponent<LineRenderer>();
        lr.loop = true;
        lr.useWorldSpace = true;
        lr.widthMultiplier = 0.2f;
        lr.positionCount = 65;
        var shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            lr.material = new Material(shader);
            lr.startColor = new Color(0.3f, 1f, 0.5f, 0.9f);
            lr.endColor = new Color(0.3f, 1f, 0.5f, 0.9f);
        }
        for (int i = 0; i <= 64; i++)
        {
            float a = i / 64f * Mathf.PI * 2f;
            lr.SetPosition(i, new Vector3(c.zoneX + Mathf.Cos(a) * c.zoneRadius, 0.25f, c.zoneZ + Mathf.Sin(a) * c.zoneRadius));
        }
        _zoneRing = lr;
    }

    // ── 放置系统对接（BuildingPlacement 调用）──────────────────────────
    private Vector3? _lastPlacedPos;   // 最近一次建筑落点（XZ 上报服务端）

    /// <summary>建筑放置确认后调用：绿圈圆心跟随建筑落位。</summary>
    public void OnBuildPlaced(Vector3 pos)
    {
        _lastPlacedPos = pos;
        if (_state?.active == null || _zoneRing == null) return;

        // 重写 65 点圆心为落位（半径不变）
        for (int i = 0; i <= 64; i++)
        {
            float a = i / 64f * Mathf.PI * 2f;
            _zoneRing.SetPosition(i, new Vector3(
                pos.x + Mathf.Cos(a) * _state.active.zoneRadius, 0.25f,
                pos.z + Mathf.Sin(a) * _state.active.zoneRadius));
        }
    }

    /// <summary>查询当前委托验收区（圆心 XZ + 半径），供放置系统做绿圈外提示。</summary>
    public bool TryGetActiveZone(out Vector2 zoneXZ, out float radius)
    {
        var active = _state?.active;
        if (active != null)
        {
            zoneXZ = new Vector2(active.zoneX, active.zoneZ);
            radius = active.zoneRadius;
            return true;
        }
        zoneXZ = default;
        radius = 0f;
        return false;
    }

    private void DestroyZoneRing()
    {
        if (_zoneRing != null)
        {
            Destroy(_zoneRing.gameObject);
            _zoneRing = null;
        }
    }
}
