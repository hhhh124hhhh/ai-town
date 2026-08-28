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
                _panelVisible = !_panelVisible;
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

    private void DrawHud()
    {
        const float Pad = 20f; // 与 UiTheme.Hud 的 padding 一致
        var st = UiTheme.Text(13);
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

        var rect = new Rect(UiTheme.VW - w - 16f, 16f, w, h);
        GUILayout.BeginArea(rect, UiTheme.Hud);
        UiTheme.Wash(rect, 0.8f);
        GUILayout.Label(line1, st);
        if (line2 != null) GUILayout.Label(line2, st);
        GUILayout.EndArea();
    }

    private void DrawPanel()
    {
        float w = 420f;
        float h = Mathf.Min(470f, UiTheme.VH - 130f);
        var rect = new Rect(UiTheme.VW - w - 12f, _hudBottom + 12f, w, h); // 动态挂 HUD 下方

        GUILayout.BeginArea(rect, UiTheme.Panel);
        UiTheme.Wash(rect);
        GUILayout.Label("<b>委托大厅</b>  <color=#5A5042>[C 关闭]</color>", UiTheme.Title);
        if (_state != null)
        {
            GUILayout.Label($"★{_state.level} {_state.levelName}　繁荣 {_state.prosperity}　大洋 {_state.gold}　完成 {_state.completed} 单", UiTheme.Text(13));
            if (_state.npcs != null && _state.npcs.Length > 0)
            {
                var aff = new System.Text.StringBuilder();
                foreach (var n in _state.npcs) aff.Append($"{n.name}({n.affinityLabel}) ");
                GUILayout.Label($"<color=#5A5042>{aff}</color>", UiTheme.Text(12));
            }
        }
        GUILayout.Space(6);

        var active = _state?.active;
        if (active != null)
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(h - 220f));
            GUILayout.Label($"<b>「{active.title}」</b>　委托人：{active.npc}　难度 {new string('●', Math.Max(1, active.difficulty))}", UiTheme.Text(14));
            GUILayout.Label(active.desc, UiTheme.Text(14));
            GUILayout.Space(4);
            GUILayout.Label("<b>验收要求</b>", UiTheme.Text(13));
            GUILayout.Label($"· 建筑类型：<color=#9E2B25>{active.typeLabel}</color>（Tab 面板输入「建一座{active.typeLabel}」或点图纸）", UiTheme.Text(13));
            GUILayout.Label($"· 占地 ≥ {active.minSize:0} 米　· 方块 ≥ {active.minBlocks} 个", UiTheme.Text(13));
            GUILayout.Label($"· 建在 <color=#1E7A1E>绿圈</color>内（{active.npc} 附近 {active.zoneRadius:0} 米）", UiTheme.Text(13));
            GUILayout.Label($"酬劳：{active.rewardGold} 大洋" + (string.IsNullOrEmpty(active.unlock) ? "" : $" + 解锁图纸 <color=#8A5A00>{active.unlock}</color>"), UiTheme.Text(13));
            GUILayout.EndScrollView();

            GUI.enabled = !_busy && _builds.Count > 0;
            if (GUILayout.Button($"提交验收（接单后已建 {_builds.Count} 栋）", UiTheme.BtnPrimary))
            {
                StartCoroutine(SubmitCo());
            }
            GUI.enabled = !_busy;
            if (GUILayout.Button("放弃委托", UiTheme.Btn))
            {
                StartCoroutine(AbandonCo());
            }
            GUI.enabled = true;
        }
        else if (!_busy)
        {
            GUILayout.Label("当前没有委托。找谁接活？（走到 NPC 附近可 [E] 闲聊打听）", UiTheme.Text(13));
            GUILayout.Space(4);
            if (_npcs.Count == 0)
            {
                GUILayout.Label("<color=red>场景里没有 NPC（NPCController）</color>", UiTheme.Text(13));
            }
            foreach (var npc in _npcs)
            {
                if (GUILayout.Button($"向 {npc.npcName}（{npc.roleName}）请求委托", UiTheme.Btn))
                {
                    StartCoroutine(NewCo(npc));
                }
            }
        }

        if (_busy)
        {
            GUILayout.Label("<i>……正在与 NPC 交谈</i>", new GUIStyle(GUI.skin.label) { normal = { textColor = UiTheme.InkSoft } });
        }
        if (!string.IsNullOrEmpty(_resultBox))
        {
            GUILayout.Space(4);
            GUILayout.Box(_resultBox, new GUIStyle(UiTheme.Card) { wordWrap = true, richText = true, fontSize = 13 }, GUILayout.Height(92));
        }
        if (!string.IsNullOrEmpty(_status))
        {
            GUILayout.Label(_status, UiTheme.Hint);
        }
        GUILayout.EndArea();
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
        }
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
            fontSize = 28,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(1f, 1f, 1f, alpha) },
        };
        GUI.Label(new Rect(cx - 300, UiTheme.VH * 0.335f + 110, 600, 40), $"交 付 成 功", titleStyle);

        var rewardStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 19,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(1f, 0.85f, 0.5f, alpha) },
        };
        string unlockTxt = string.IsNullOrEmpty(_flashUnlock) ? "" : $"　·　解锁图纸 {_flashUnlock}";
        GUI.Label(new Rect(cx - 400, UiTheme.VH * 0.335f + 150, 800, 34),
                  $"＋{_flashGold} 大洋　＋{_flashProsperity} 繁荣{unlockTxt}", rewardStyle);

        // 繁荣度升级庆祝（叠加在下方）
        if (_levelUpTo > 0 && _state != null)
        {
            var lvlStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
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
