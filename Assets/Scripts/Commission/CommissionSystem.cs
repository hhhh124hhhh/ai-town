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
        StartCoroutine(PollStateCo()); // 10s 静默轮询：服务器重启后 UI 不再精神分裂
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
                _panelOpenedAtLeastOnce = true; // 箭头引导时序（见 HasZoneGuide 注释）：知情后才重引导
            }
        }
#endif
        // 等级跟踪（升级后闪烁结束即归位；HUD 常显最新等级）
        if (_state != null && _levelShown != _state.level && Time.unscaledTime >= _flashUntil)
        {
            _levelShown = _state.level;
            _levelUpTo = 0;
        }

        // 到圈提示（2026-08-29 用户"没一个是能放的"——玩家把氛围烛光当落点标记，
        // 不知道"能放"的入口是 Tab 生成）：首次进绿圈明确告知放置流程，30s 冷却防刷
        if (_state?.active != null && _zoneGuideCenter.HasValue
            && Time.unscaledTime >= _zoneHintCooldownUntil
            && !CinematicIntro.IsCinematic)
        {
            var player = GameObject.Find("Player");
            if (player != null)
            {
                var pp = new Vector2(player.transform.position.x, player.transform.position.z);
                if (Vector2.Distance(pp, _zoneGuideCenter.Value) <= 3f)
                {
                    ShowTopHint("已到绿圈——按 [Tab] 说出建筑，落点选在圈内，建完按 [C] 验收", 7f);
                    _zoneHintCooldownUntil = Time.unscaledTime + 30f;
                }
            }
        }
    }

    private float _zoneHintCooldownUntil;

    // ── 状态轮询（2026-08-29 "UI 有单服务器无单"判例）────────────────────
    // _state 是本地缓存，python 重启=服务器清零但客户端永远显示旧快照（HUD"进行中"、
    // NPC 说有单、箭头/绿圈却消失——三方精神分裂）。10s 静默轮询以服务器为准对齐。
    private float _nextPollAt;
    private bool _hadActive; // 上一帧是否本地认为有单（检测"单消失"边沿提示）

    private IEnumerator PollStateCo()
    {
        while (true)
        {
            yield return new WaitForSecondsRealtime(10f);
            if (ApiClient.Instance == null || _busy) continue;
            string json = null;
            yield return ApiClient.Instance.GetCommissionState(
                j => json = j, _ => { });
            if (json == null) continue; // 轮询失败保持现状（离线态不折腾）
            var resp = JsonUtility.FromJson<StateResponse>(json);
            if (resp == null || !resp.ok) continue;

            bool hadActive = _state?.active != null;
            _state = resp.state;
            _fetched = true;
            _offline = false;

            // 边沿检测：本地有单→服务器没了（服务器重启/被清），提示并撤引导
            if (hadActive && _state.active == null)
            {
                DestroyZoneRing();
                _builds.Clear();
                ShowTopHint("委托记录已失效（服务重启）——按 [C] 重新接单", 8f);
            }
            // 单还在但 zone 漂了/首次见到单：对齐绿圈
            else if (_state.active != null && _zoneRing == null)
            {
                ResolveZonePlacement(_state.active);
                CreateZoneRing(_state.active);
            }
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

        // 单一真源（同 BuildingPanel）：可见性每帧从协调器派生，对话开/关等外部状态
        // 变化立即生效，不留按键时刻的过期拷贝（"面板乱跳"判例 2026-08-29）
        _panelVisible = UiPanelLayout.CommissionVisible;

        UiTheme.BeginScale();
        DrawFlash();

        if (!_offline && _fetched) DrawHud();
        else if (_offline) DrawOfflineHint(); // 审计口诀第四条：异常要带"该怎么做"

        if (_panelVisible)
        {
            DrawPanel();
        }
        else
        {
            DrawSubmitHint(); // 面板关着时引导提交（打开时按钮可见，无需引导条）
        }
        UiTheme.EndScale();
    }

    private float _submitHintUntil;
    private string _submitHintText = "";

    /// <summary>服务离线提示（demo 现场保命条）：只有负反馈会让人困惑，必须带行动方案。
    /// 委托/HUD 依赖本地 Python 服务；建造（Tab）离线可用，此条只指路不影响主演示。</summary>
    private static void DrawOfflineHint()
    {
        string txt = "委托服务未连接——双击 server/start_server.bat 后重开 C 面板";
        var st = UiTheme.Hint;
        var measure = new GUIStyle(st) { wordWrap = false };
        var s = measure.CalcSize(new GUIContent(txt));
        float w = s.x + 32f;
        float h = s.y + 14f;
        var rect = new Rect((UiTheme.VW - w) / 2f, 16f, w, h); // 顶部中央（离线时 HUD 缺位）
        GUILayout.BeginArea(rect);
        UiTheme.PaperCard(rect, 0.9f);
        GUILayout.Space(5f);
        GUILayout.Label(txt, st);
        GUILayout.EndArea();
    }

    /// <summary>
    /// 顶部中央引导条统一入口（素纸卡，10s 级）：落成引导/开局存量委托提醒共用，
    /// C 面板打开时让位（面板内按钮可见，无需引导）。面板自动隐藏类流程的提示必须挂这里。
    /// </summary>
    public void ShowTopHint(string text, float seconds)
    {
        _submitHintText = text;
        _submitHintUntil = Time.unscaledTime + seconds;
    }

    /// <summary>
    /// 生成落位后由 BuildingPanel 调用：有进行中委托时顶部中央引导「按 C 提交验收」。
    /// 生成后面板已自动隐藏（按键驱动定则），完成提示必须挂在常驻 HUD 层——
    /// 否则玩家落成后屏幕上零引导，不知道验收入口在哪（2026-08-29"没人验收"判例）。
    /// </summary>
    public void NotifyPlacedForCommission(string buildingName)
    {
        if (_state?.active == null) return; // 没接单不引导（自由建造模式）
        ShowTopHint($"「{_state.active.title}」已落成——按 [C] 提交验收", 9f);
    }

    /// <summary>顶部中央引导条渲染。</summary>
    private void DrawSubmitHint()
    {
        if (Time.unscaledTime >= _submitHintUntil || string.IsNullOrEmpty(_submitHintText)) return;
        var st = UiTheme.Text(UiTheme.SizeEmph);
        var measure = new GUIStyle(st) { wordWrap = false };
        var s = measure.CalcSize(new GUIContent(_submitHintText));
        float w = s.x + 48f;
        float h = s.y + 20f;
        // 顶部中央（左右上角被 HUD/键位卡占位，底部中央是功能坞位；顶部中空闲且视线起点）
        var rect = new Rect((UiTheme.VW - w) / 2f, 16f, w, h);
        GUILayout.BeginArea(rect);
        UiTheme.PaperCard(rect, 0.94f);
        GUILayout.Space(8f);
        GUILayout.Label(_submitHintText, st);
        GUILayout.EndArea();
    }

    private float _hudBottom = 100f; // HUD 实际底边（自适应后），委托面板挂其下方

    // 繁荣度等级阈值（镜像服务端 PROSPERITY_LEVELS，进度条数据源）
    private static readonly (int threshold, string name)[] ProsperityLevels =
    {
        (0, "荒地聚落"), (100, "边陲小村"), (250, "热闹小镇"), (450, "繁荣市镇"), (700, "传奇之城"),
    };

    private void DrawHud()
    {
        const float Pad = 16f;
        var st = UiTheme.Text(UiTheme.SizeBody);
        var active = _state.active;

        // ── 数值变化检测（审计口诀第三条：数字不能静跳）──
        TrackValueChange(ref _goldAnim, _state.gold);
        TrackValueChange(ref _prosperityAnim, _state.prosperity);

        string line1 = $"<b>★{_state.level} {_state.levelName}</b>　繁荣 {_state.prosperity}　大洋 {_state.gold}　完成 {_state.completed} 单";
        string line2 = BuildCommissionLine(active);

        // 按内容自适应：宽度=最长行+对称 padding；高度=上下 padding+行高
        var measure = new GUIStyle(st) { wordWrap = false };
        var s1 = measure.CalcSize(new GUIContent(line1));
        var s2 = line2 != null ? measure.CalcSize(new GUIContent(line2)) : Vector2.zero;
        float w = Mathf.Max(240f, Mathf.Max(s1.x, s2.x)) + Pad * 2f;
        float h = Pad * 2f + s1.y + (line2 != null ? s2.y + 4f : 0f);
        _hudBottom = 16f + h;

        // HUD 均衡布局：左上=游戏状态，右上=键位卡，底部中央=功能坞，委托弹窗居中。
        // 小卡禁用 9-slice Hud 样式（padding 84/每边把内容区吃成负数，文字挤出卡外——
        // 2026-08-29 截图审计"左右两边文字看不到"根因），改素纸卡 PaperCard。
        var rect = new Rect(16f, 16f, w, h);
        GUILayout.BeginArea(rect);
        UiTheme.PaperCard(rect, 0.92f);
        GUILayout.Space(6f);
        DrawAnimatedStatusLine(line1, st, measure);
        if (line2 != null) GUILayout.Label(line2, st);
        GUILayout.EndArea();

        DrawKeyHints(); // 右上：系统/键位卡（占小地图位）
    }

    // ── 数值变化动画（设计系统：每个"变化"都有 0.1s 级反馈）──────────────
    private struct ValueAnim { public int last; public float until; public bool up; }
    private ValueAnim _goldAnim;
    private ValueAnim _prosperityAnim;
    private const float ValueFlashDur = 0.9f;

    private void TrackValueChange(ref ValueAnim anim, int current)
    {
        if (anim.last == 0) { anim.last = current; return; } // 首帧建基线不闪
        if (current != anim.last)
        {
            anim.up = current > anim.last;
            anim.until = Time.unscaledTime + ValueFlashDur;
            anim.last = current;
        }
    }

    /// <summary>状态行渲染：大洋/繁荣近 0.9s 内变化时该词弹跳放大+涨朱红/跌淡墨闪一拍。
    /// 富文本 size 逐帧重算（IMGUI 无独立数值动画，弹跳用 <size> 插值实现）。</summary>
    private void DrawAnimatedStatusLine(string line1, GUIStyle st, GUIStyle measure)
    {
        float t = -1f; // -1=无动画
        bool up = false;
        float gLeft = _goldAnim.until - Time.unscaledTime;
        float pLeft = _prosperityAnim.until - Time.unscaledTime;
        if (gLeft > 0f) { t = 1f - gLeft / ValueFlashDur; up = _goldAnim.up; }
        else if (pLeft > 0f) { t = 1f - pLeft / ValueFlashDur; up = _prosperityAnim.up; }

        if (t < 0f)
        {
            GUILayout.Label(line1, st);
            return;
        }
        // 弹跳包络：前 0.2s 冲到峰值 1.35x，之后回弹到 1（easeOutBack 近似）
        float pop = t < 0.25f ? Mathf.Lerp(1f, 1.35f, t / 0.25f) : Mathf.Lerp(1.35f, 1f, (t - 0.25f) / 0.75f);
        int sizeNow = Mathf.RoundToInt(UiTheme.SizeBody * pop);
        // 变化词染闪色：涨=朱红、跌=淡墨（0.9s 后自然回正文墨色）
        string flash = up ? "#9E2B25" : "#5A5042";
        string animated = line1
            .Replace($"大洋 {_goldAnim.last}", $"<size={sizeNow}><color={flash}>大洋 {_goldAnim.last}</color></size>")
            .Replace($"繁荣 {_prosperityAnim.last}", $"<size={sizeNow}><color={flash}>繁荣 {_prosperityAnim.last}</color></size>");
        GUILayout.Label(animated, st);
    }

    /// <summary>HUD 委托行：进行中=委托名+绿圈实时方位距离（审计口诀第四条：
    /// 告诉玩家"去哪+还有多远"，消掉"绿圈找不到"迷失）；无委托=null 不占行。</summary>
    private string BuildCommissionLine(CommissionInfo active)
    {
        if (active == null) return null;
        string baseLine = $"<color=#9E2B25><b>委托：{(string.IsNullOrEmpty(active.npc) ? "" : active.npc + " · ")}{(string.IsNullOrEmpty(active.title) ? "进行中" : active.title)}</b></color>";
        // 绿圈导航：zoneCenter 由 OnBuildPlaced 记录；玩家未放建筑前用委托发单点（NPC 位置）
        if (_zoneGuideCenter.HasValue)
        {
            var player = GameObject.Find("Player");
            if (player != null)
            {
                Vector3 delta = _zoneGuideCenter.Value - new Vector2(player.transform.position.x, player.transform.position.z);
                float dist = delta.magnitude;
                if (dist < 2.5f)
                {
                    return $"{baseLine}　<color=#1E7A1E>◉ 已在绿圈内</color>";
                }
                string dir = CompassDir(delta.x, delta.y);
                return $"{baseLine}　<color=#1E7A1E>◉ 绿圈</color>·{dir} {dist:0} 米";
            }
        }
        return $"{baseLine}　建完按 [C] 提交验收";
    }

    /// <summary>平面向量→罗盘八方位（北=-Z，与场景惯例一致）。</summary>
    private static string CompassDir(float dx, float dz)
    {
        // atan2(x, -z)：北 0° 东 90°
        float ang = Mathf.Atan2(dx, -dz) * Mathf.Rad2Deg;
        if (ang < 0f) ang += 360f;
        string[] dirs = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };
        return dirs[Mathf.RoundToInt(ang / 45f) % 8];
    }

    /// <summary>右上角键位提示卡（均衡法则的系统位；本游戏无小地图，键位提示承担该角色）。</summary>
    private static void DrawKeyHints()
    {
        var st = UiTheme.Hint;
        var measure = new GUIStyle(st) { wordWrap = false };
        string txt = "[Tab] 建造　[C] 委托　[E] 对话　[X] 回出生点";
        var s = measure.CalcSize(new GUIContent(txt));
        float w = s.x + 32f;
        float h = s.y + 12f;
        // 同 DrawHud：素纸卡替代 9-slice（padding 吃空内容=文字不可见判例）
        var rect = new Rect(UiTheme.VW - w - UiTheme.RightMargin, 16f, w, h);
        GUILayout.BeginArea(rect);
        UiTheme.PaperCard(rect, 0.85f);
        GUILayout.Space(6f);
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
        // v2 panel_main 9-slice border 78 + padding 96（UiTheme.Panel 自带）。
        // 2026-08-29 截图审计：420 宽内容列仅 228px（420-192），标题/状态行全部挤爆换行、
        // 内容溢出面板底——加宽到 600（内容列 408）+ 高度给足。
        // 委托大厅=弹窗型功能面板，按弹窗对称规则居中显示（HUD 均衡法则）。
        float w = 600f;
        float h = Mathf.Min(780f, UiTheme.VH - 60f);
        var rect = new Rect((UiTheme.VW - w) / 2f, (UiTheme.VH - h) / 2f, w, h); // 屏幕居中
        UiTheme.DrawShadow(rect);

        GUILayout.BeginArea(rect, UiTheme.Panel);
        UiTheme.Wash(rect);

        // ── 头部行：标题 + 自绘关闭 ×（楷体缺 × 字形方块判例；快捷键提示不塞标题）+ 印章 ──
        GUILayout.BeginHorizontal();
        GUILayout.Label("委托大厅", UiTheme.Title, GUILayout.Height(48f));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("", UiTheme.BtnIcon, GUILayout.Width(34f), GUILayout.Height(34f)))
        {
            UiPanelLayout.Close(UiPanelLayout.Panel.Commission);
            _panelVisible = UiPanelLayout.CommissionVisible;
        }
        var closeRect = GUILayoutUtility.GetLastRect();
        bool closeHover = closeRect.Contains(Event.current.mousePosition);
        UiTheme.DrawX(closeRect, closeHover ? UiTheme.Vermilion : UiTheme.InkSoft, 2.5f);
        GUILayout.Space(10f);
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
                GUILayout.Height(Mathf.Clamp(h - 560f, 60f, 140f)));
            KeyValueRow("建筑类型", $"骑楼式 {active.typeLabel} —— Tab 面板输入「建一座{active.typeLabel}」或点图纸");
            KeyValueRow("规模", $"占地 ≥ {active.minSize:0} 米　·　方块 ≥ {active.minBlocks} 个");
            KeyValueRow("落点", $"建在 <color=#1E7A1E>绿圈</color>内（{active.npc} 附近 {active.zoneRadius:0} 米）");
            GUILayout.EndScrollView();

            // ── 酬劳行（结尾甜枣：票据纸底框+大金数字；CardFlat——Card 88 padding 会吃空小组件）──
            GUILayout.Space(16f);
            GUILayout.BeginHorizontal(UiTheme.CardFlat);
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
                    AudioManager.Play("SFX_Click"); // 审计口诀第二条：按钮 0.1s 内必须有反馈
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
                AudioManager.Play("SFX_Click"); // 主操作同享点击反馈（提交结果另有锣声）
                StartCoroutine(SubmitCo());
            }
            GUI.enabled = !_busy;
            if (GUILayout.Button("放弃委托", UiTheme.Btn, GUILayout.Height(44f)))
            {
                AudioManager.Play("SFX_Click");
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
            // Card 88 padding 在 84 高盒里把文字吃光（同小组件禁大 padding 判例）→ CardFlat
            GUILayout.Box(_resultBox, new GUIStyle(UiTheme.CardFlat) { wordWrap = true, richText = true, fontSize = UiTheme.SizeBody }, GUILayout.Height(84f));
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
                if (_state.active != null)
                {
                    ResolveZonePlacement(_state.active); // 旧布局下的 zone 坐标在新场景可能被占
                    CreateZoneRing(_state.active);
                    // 箭头引导三层时序（用户定则）：恢复单只轻引导——提示条+HUD 方位行；
                    // 玩家按过 C（知情）箭头才出现，60s 没按自动出（兜底防卡死）。
                    // 注意 _commissionIssuedAt 在 Start 前为 0=兜底立即满足，这里显式重置为现在。
                    _commissionIssuedAt = Time.unscaledTime;
                    _panelOpenedAtLeastOnce = false;
                    ShowTopHint("手上有未完成委托——按 [C] 看详情，跟着绿圈方位走", 10f);
                }
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
        _commissionIssuedAt = Time.unscaledTime; // 箭头 60s 兜底起点
        ResolveZonePlacement(resp.commission); // 绿圈空地解析（占用了自动挪到附近空位）
        CreateZoneRing(resp.commission);
        npc.ShowBubble($"委托：{resp.commission.title}（[C] 查看详情）", 8f);
        _status = $"<color=green>已接下「{resp.commission.title}」，在绿圈内用 Tab 面板建造，建完按 [C] 提交验收</color>";
    }

    // ── 绿圈空地解析（2026-08-29 用户改布局后"绿圈被建筑占了"）────────────
    // 原则：场景布局用户已调好不动，动的是绿圈——运行时以 NPC 为圆心环形采样，
    // 找第一个"整块圆盘不与任何建筑脚印相交"的点。服务端 submit 判分用的是客户端
    // 上报的落点圆心（_lastPlacedPos），绿圈本地挪位不影响判分闭环。

    /// <summary>原地修正 commission 的 zone 坐标到附近空地（不占用则原样返回）。</summary>
    private void ResolveZonePlacement(CommissionInfo c)
    {
        if (c == null || c.zoneRadius <= 0f) return;
        var origin = new Vector2(c.zoneX, c.zoneZ);
        if (!ZoneBlocked(origin, c.zoneRadius)) return; // 现位空闲

        // 8 方向 × 1~3 倍半径环形采样，最近空位优先
        for (int ring = 1; ring <= 3; ring++)
        {
            for (int d = 0; d < 8; d++)
            {
                float ang = (d * 45f + 22.5f) * Mathf.Deg2Rad; // +22.5° 避开正轴先撞建筑
                var cand = origin + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * (c.zoneRadius * ring);
                // 镇界内（与 PlayerBounds/放置系统同界）
                if (cand.x < -17f || cand.x > 21f || cand.y < -21f || cand.y > 25f) continue;
                if (!ZoneBlocked(cand, c.zoneRadius))
                {
                    c.zoneX = cand.x;
                    c.zoneZ = cand.y;
                    return;
                }
            }
        }
        // 三圈全占：缩小半径到 60% 再试一轮（比无圈好）
        if (c.zoneRadius > 4f)
        {
            c.zoneRadius *= 0.6f;
            ResolveZonePlacement(c);
        }
    }

    /// <summary>圆盘是否与 _Buildings 任一建筑脚印相交（脚印内收 6%，同放置系统规则）。</summary>
    private static bool ZoneBlocked(Vector2 center, float radius)
    {
        var buildings = GameObject.Find("_Buildings");
        if (buildings == null) return false;
        float r2 = radius * radius;
        foreach (Transform child in buildings.transform)
        {
            if (!child.gameObject.activeInHierarchy) continue;
            var rs = child.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) continue;
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            float inX = b.size.x * 0.06f, inZ = b.size.z * 0.06f;
            var min = new Vector2(b.min.x + inX, b.min.z + inZ);
            var max = new Vector2(b.max.x - inX, b.max.z - inZ);
            // 圆心到 AABB 最近点距离 < 半径 = 相交
            var closest = new Vector2(Mathf.Clamp(center.x, min.x, max.x), Mathf.Clamp(center.y, min.y, max.y));
            if ((closest - center).sqrMagnitude < r2) return true;
        }
        return false;
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
                $"【{npc.npcName}】那就拜托你了——「{a.title}」：{a.desc} 建在绿圈内（{a.zoneRadius:0} 米），建完按 [C] 提交验收就行。");
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

    /// <summary>验收区绿圈（LineRenderer 圈线 + 半透明地面圆盘填充——2026-08-29 用户
    /// "绿色区域不明显"：0.2m 细线在暖色黄昏场景不可辨且只有线没有面，没有"区域"体感。
    /// 圈线加粗 0.35m，圆盘淡绿填充让验收范围整块可见）。</summary>
    private void CreateZoneRing(CommissionInfo c)
    {
        if (c == null) return;
        _lastCommissionNpc = c.npc;
        DestroyZoneRing();
        // 脏数据守卫：zone 字段反序列化失败会是 (0,0,0)，在原点画一个无意义圈误导玩家
        if (c.zoneRadius <= 0f) return;

        var go = new GameObject("CommissionZoneRing");
        _zoneGuideCenter = new Vector2(c.zoneX, c.zoneZ); // 导航圆心=发单点（落位后跟随建筑）
        _zoneRing = go.AddComponent<LineRenderer>();
        BuildRingAndDisk(go, _zoneRing, new Vector2(c.zoneX, c.zoneZ), c.zoneRadius);
    }

    /// <summary>构建圈线（65 点 loop 宽 0.35）+ 地面圆盘（64 段三角扇淡绿 0.16 alpha）。</summary>
    private static void BuildRingAndDisk(GameObject root, LineRenderer lr, Vector2 center, float radius)
    {
        lr.loop = true;
        lr.useWorldSpace = true;
        lr.widthMultiplier = 0.35f;
        lr.positionCount = 65;
        lr.material = RuntimeFxMat.Make(new Color(0.25f, 1f, 0.45f, 0.95f)); // URP shader（Deferred 判例）
        WriteRingPoints(lr, center, radius);

        // 地面圆盘：验收区域整块淡绿（y=0.05 垫在路网 0.035 之上防闪面）
        var diskGo = new GameObject("ZoneDisk");
        diskGo.transform.SetParent(root.transform, false);
        var mf = diskGo.AddComponent<MeshFilter>();
        mf.sharedMesh = BuildDiskMesh(radius);
        var mr = diskGo.AddComponent<MeshRenderer>();
        mr.material = RuntimeFxMat.Make(new Color(0.25f, 1f, 0.45f, 0.16f));
        diskGo.transform.position = new Vector3(center.x, 0.05f, center.y);
    }

    private static void WriteRingPoints(LineRenderer lr, Vector2 center, float radius)
    {
        for (int i = 0; i <= 64; i++)
        {
            float a = i / 64f * Mathf.PI * 2f;
            lr.SetPosition(i, new Vector3(center.x + Mathf.Cos(a) * radius, 0.25f, center.y + Mathf.Sin(a) * radius));
        }
    }

    /// <summary>水平圆盘网格（64 段三角扇，绕序逆时针=法线朝上）。</summary>
    private static Mesh BuildDiskMesh(float radius)
    {
        const int seg = 64;
        var verts = new Vector3[seg + 2];
        var tris = new int[seg * 3];
        verts[0] = Vector3.zero; // 圆心
        for (int i = 0; i <= seg; i++)
        {
            float a = i / (float)seg * Mathf.PI * 2f;
            verts[i + 1] = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
        }
        for (int i = 0; i < seg; i++)
        {
            tris[i * 3] = 0;
            tris[i * 3 + 1] = i + 2;
            tris[i * 3 + 2] = i + 1;
        }
        var mesh = new Mesh { name = "ZoneDisk" };
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        return mesh;
    }

    // ── 放置系统对接（BuildingPlacement 调用）──────────────────────────
    private Vector3? _lastPlacedPos;   // 最近一次建筑落点（XZ 上报服务端）
    private Vector2? _zoneGuideCenter; // 绿圈导航圆心（HUD 方位/世界箭头用；落位跟随，验收/清委托清空）

    // ── 箭头引导时序（2026-08-29 用户定则三层）：恢复单只轻引导（提示条+HUD 方位）；
    // 按 C 知情后箭头出现（重引导）；60s 没点过 C 自动出（兜底防卡死）。
    // 每单重置：接新单重新走"轻→重"时序，兜底时间从接单起算。
    private bool _panelOpenedAtLeastOnce;
    private float _commissionIssuedAt;
    private const float ArrowFallbackSeconds = 60f;

    /// <summary>绿圈导航圆心是否有效（CommissionArrow 世界箭头数据源）。
    /// 含时序门控：箭头须玩家"按过 C"或 60s 兜底超时才出现。</summary>
    public bool HasZoneGuide =>
        _zoneGuideCenter.HasValue
        && (_panelOpenedAtLeastOnce || Time.unscaledTime - _commissionIssuedAt >= ArrowFallbackSeconds);

    /// <summary>绿圈导航圆心（XZ）。HasZoneGuide 为 true 时有效。</summary>
    public Vector2 ZoneGuideCenter => _zoneGuideCenter ?? Vector2.zero;

    /// <summary>建筑放置确认后调用：绿圈圆心跟随建筑落位。</summary>
    public void OnBuildPlaced(Vector3 pos)
    {
        _lastPlacedPos = pos;
        _zoneGuideCenter = new Vector2(pos.x, pos.z);
        if (_state?.active == null || _zoneRing == null) return;

        // 圈线 65 点 + 地面圆盘一起搬到落位（半径不变）
        var center = new Vector2(pos.x, pos.z);
        WriteRingPoints(_zoneRing, center, _state.active.zoneRadius);
        if (_zoneRing.transform.childCount > 0)
        {
            _zoneRing.transform.GetChild(0).position = new Vector3(pos.x, 0.05f, pos.z);
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
        _zoneGuideCenter = null; // 圈没了导航也停（验收通过/清委托）
    }
}
