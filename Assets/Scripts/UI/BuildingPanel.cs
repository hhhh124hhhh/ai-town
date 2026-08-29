using System.Collections;
using UnityEngine;

/// <summary>
/// 建筑生成面板（IMGUI 纯代码，无需场景预制）：
/// 输入描述或选模板 → 调 Python API → 在玩家前方生成建筑。
/// Tab 键显隐面板。
/// </summary>
public class BuildingPanel : MonoBehaviour
{
    // 分类图纸册（2026-08-29 用户反馈：27 个按钮平铺认知负荷过高）：
    // 4 类分段页签，每类 ≤8 个=网格 2 行封顶，面板高度可控不再溢出
    private static readonly (string tab, (string id, string zh)[] items)[] Tabs =
    {
        ("屋舍", new[] { ("castle", "洋楼"), ("house", "房屋"), ("qilou", "骑楼"), ("village", "村落"), ("wall", "围墙") }),
        ("公所", new[] { ("temple", "庙宇"), ("gulou", "鼓楼"), ("xitai", "戏台"), ("paifang", "牌坊"), ("bridge", "桥"), ("tower", "高塔"), ("pagoda", "宝塔") }),
        ("园景", new[] { ("garden", "花园"), ("fountain", "喷泉"), ("windmill", "风车"), ("gazebo", "凉亭"), ("lighthouse", "灯塔"), ("statue", "雕像"), ("tree", "树") }),
        ("奇趣", new[] { ("pyramid", "金字塔"), ("sphere", "球体"), ("spiral", "螺旋"), ("mushroom", "蘑菇"), ("heart", "心形"), ("skyscraper", "高楼"), ("spaceship", "飞船"), ("shanghai", "上海") }),
    };

    private string _input = "";
    private int _activeTab;
    private string _status = "";
    private bool _busy;
    private float _busySince;        // _busy 置真时刻（看门狗用）
    private bool _visible = true;

    private const string InputControlName = "bp_input";

    private Transform _player;

    private void Start()
    {
        var player = GameObject.Find("Player");
        if (player != null) _player = player.transform;

        // 委托系统懒创建（HUD + C 面板），场景无需手动接线
        CommissionSystem.EnsureExists();
        // 开场演出懒创建（接管相机与输入，结束后自毁）
        CinematicIntro.EnsureExists();
    }

    private void Update()
    {
        // _busy 看门狗：Play 中途脚本重载会掐死协程，_busy 卡 true 后回车永久静默失效
        if (_busy && Time.unscaledTime - _busySince > 75f)
        {
            _busy = false;
            _status = "<color=red>生成超时已重置，再按一次回车</color>";
        }
        if (CinematicIntro.IsCinematic || CinematicIntro.InputCooldown) return; // 演出期间不响应
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        // 打字期间的 Enter/Tab 静默（2026-08-29"输入触发游戏键"终修）：
        // ①文本框聚焦时 Tab 不切面板（IME 候选/打字习惯的 Tab 都不该关面板；点框外/Esc
        //   失焦后 Tab 恢复逃生口功能）②Enter 上屏那一帧（文本刚变化 0.4s 内）不触发生成——
        //   半句拼音不再被当生成指令发出去；打完停顿后按 Enter 才是真提交
        bool fieldFocused = FieldFocused;
        if (kb != null && kb.tabKey.wasPressedThisFrame
            && !fieldFocused && !UiTextFocus.UguiFieldFocused)
        {
            if (DialogSystem.Instance != null) { DialogSystem.Instance.CloseByUser(); }
            else
            {
                UiPanelLayout.Request(UiPanelLayout.Panel.Building);
                _visible = UiPanelLayout.BuildingVisible;
            }
            if (!_visible) UiTextFocus.Clear();
        }
        // 文本变化计时（IME 上屏启发式：上屏瞬间 _input 变化）
        if (_input != _lastInput)
        {
            _lastInput = _input;
            _lastInputChangeAt = Time.realtimeSinceStartup;
        }
        if (BuildingPlacement.Active) return; // 放置期间其余输入归放置模式（回车不再触发生成）
        // 回车=生成（便携手感 + 远程测试钩子：桥 manage_input 可触发）
        // 门控：文本框聚焦且文本刚变化（<0.4s）→ 视为 IME 上屏确认键，吞掉
        // 对话框打开时让位给 DialogSystem，避免一次回车同时触发两处
        if (kb != null && kb.enterKey.wasPressedThisFrame && !_busy && _visible
            && (!fieldFocused || Time.realtimeSinceStartup - _lastInputChangeAt > 0.4f)
            && DialogSystem.Instance == null)
        {
            StartCoroutine(GenerateCo(_input, null));
        }
#endif
    }

    private string _lastInput = "";
    private float _lastInputChangeAt = -10f;

    /// <summary>建筑面板输入框当前是否持有命名焦点（GUI.GetNameOfFocusedControl 精确判
    /// 文本框——按钮点击的 keyboardControl 残留不算，避免按钮焦点陷阱锁死 Tab）。</summary>
    public static bool FieldFocused => GUI.GetNameOfFocusedControl() == InputControlName;

    private Rect _inputFieldRect; // 输入框在面板内区域（点击框外释放键盘焦点用）
    private bool _inputFocused;   // 上一帧键盘焦点是否在本输入框

    private void OnGUI()
    {
        // 单一真源（2026-08-29 用户"面板不能乱跳"定则）：可见性每帧从协调器派生——
        // 任何 C/E/×/生成 引发的 Request/Close/Clear 本帧立即生效，不留过期拷贝
        bool wasVisible = _visible;
        _visible = UiPanelLayout.BuildingVisible;
        
        // 2026-08-29 修复：面板从关闭变为打开时，立即创建绿圈（延迟显示）
        if (!wasVisible && _visible && CommissionSystem.Instance != null 
            && CommissionSystem.Instance.HasActiveCommission)
        {
            CommissionSystem.Instance.EnsureZoneRing();
        }
        
        if (!_visible || CinematicIntro.IsCinematic)
        {
            // 面板不可见时不得持有键盘焦点,否则 WASD 一直打进隐藏输入框
            if (GUIUtility.keyboardControl != 0) UiTextFocus.Clear();
            return;
        }

        UiTheme.BeginScale();
        // 2026-08-29 二次修（截图审计）：600×560——Panel padding 96×2 吃掉 192 后旧 460 宽
        // 内容列仅 268px，模板按钮挤成窄条；600 宽内容列=408。
        // HUD 均衡法则：AI 建造=核心功能→底部中央功能坞位，Tab 从底部长出。
        float w = 600f;
        float h = Mathf.Min(560f, UiTheme.VH - 40f);
        var areaRect = new Rect((UiTheme.VW - w) / 2f, UiTheme.VH - h - 16f, w, h); // 底部中央
        // 面板投影：浅宣纸与暖背景对比不足，先托一层软阴影再画面板
        UiTheme.DrawShadow(areaRect);
        GUILayout.BeginArea(areaRect, UiTheme.Panel);
        UiTheme.Wash(areaRect);

        // ── 头部行：大标题 + 右上角关闭 ×（自绘图形——楷体缺 × 字形会渲染成实心方块
        // （2026-08-29 截图判例），文字方案不可靠；操作提示不塞标题）──
        GUILayout.BeginHorizontal();
        GUILayout.Label("AI 建筑生成", UiTheme.Head, GUILayout.Height(34f));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("", UiTheme.BtnIcon, GUILayout.Width(34f), GUILayout.Height(34f)))
        {
            UiPanelLayout.Close(UiPanelLayout.Panel.Building);
            _visible = UiPanelLayout.BuildingVisible;
            UiTextFocus.Clear();
        }
        var closeRect = GUILayoutUtility.GetLastRect();
        bool closeHover = closeRect.Contains(Event.current.mousePosition);
        UiTheme.DrawX(closeRect, closeHover ? UiTheme.Vermilion : UiTheme.InkSoft, 2.5f);
        GUILayout.EndHorizontal();
        var ruleRect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
        UiTheme.DrawRule(ruleRect, 0.4f);
        GUILayout.Space(10f);

        GUI.enabled = !_busy;
        // ── 输入区（核心操作第一眼）：聚焦朱红描边高亮 ──
        GUI.SetNextControlName(InputControlName);
        _input = GUILayout.TextField(_input, UiTheme.Field, GUILayout.Height(46f));
        _inputFieldRect = GUILayoutUtility.GetLastRect();
        _inputFocused = GUIUtility.keyboardControl != 0;
        bool fieldFocused = GUI.GetNameOfFocusedControl() == InputControlName;
        UiTheme.DrawFrame(_inputFieldRect, fieldFocused ? UiTheme.Vermilion
            : new Color(UiTheme.Ink.r, UiTheme.Ink.g, UiTheme.Ink.b, 0.45f), fieldFocused ? 2.5f : 1.5f);
        // placeholder：空且未聚焦时叠淡墨提示（IMGUI 无原生 placeholder）
        if (string.IsNullOrEmpty(_input) && !fieldFocused)
        {
            var phRect = new Rect(_inputFieldRect.x + 9f, _inputFieldRect.y,
                _inputFieldRect.width - 18f, _inputFieldRect.height);
            GUI.Label(phRect, "说出你的愿望，如：一座青砖老洋楼", UiTheme.Hint);
        }

        // 点击输入框以外（场景/其他按钮）即释放键盘焦点——移动键回到角色,防误输入
        var ev = Event.current;
        if (ev != null && ev.type == EventType.MouseDown && _inputFocused && !_inputFieldRect.Contains(ev.mousePosition))
            UiTextFocus.Clear();

        GUILayout.Space(8f);
        // ── 主操作：最大最高的朱红按钮（视觉权重第一）──
        if (GUILayout.Button("生 成 建 筑", UiTheme.BtnPrimary, GUILayout.Height(54f)))
        {
            UiTextFocus.Clear(); // 按钮抢 keyboardControl，不清则 F/E/X 被 IsTyping 锁死
            AudioManager.Play("SFX_Click");
            StartCoroutine(GenerateCo(_input, null));
        }

        GUILayout.Space(14f);
        // ── 分类页签行（选中=朱红底，未选=纸底墨字）──
        GUILayout.BeginHorizontal();
        for (int t = 0; t < Tabs.Length; t++)
        {
            bool on = t == _activeTab;
            if (GUILayout.Button(Tabs[t].tab, on ? UiTheme.BtnPrimary : UiTheme.Btn, GUILayout.Height(38f),
                GUILayout.ExpandWidth(true)))
            {
                UiTextFocus.Clear(); // 按钮焦点残留清理
                _activeTab = t;
                AudioManager.Play("SFX_Click");
            }
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        // ── 当前分类模板网格（4 列 ≤2 行，锁定灰显）──
        var items = Tabs[_activeTab].items;
        int columns = 4;
        int rows = Mathf.CeilToInt(items.Length / (float)columns);
        int selected = -1;
        for (int r = 0; r < rows; r++)
        {
            GUILayout.BeginHorizontal();
            for (int c = 0; c < columns; c++)
            {
                int idx = r * columns + c;
                if (idx >= items.Length) break;
                bool unlocked = CommissionSystem.IsTemplateUnlocked(items[idx].id);
                GUI.enabled = !_busy && unlocked;
                // 锁定模板灰显：淡墨字+🔒前缀（层级靠颜色区分，不加字号档）
                if (GUILayout.Button(unlocked ? items[idx].zh : "🔒" + items[idx].zh,
                    unlocked ? UiTheme.BtnGrid : UiTheme.BtnLocked, GUILayout.Height(46f),
                    GUILayout.ExpandWidth(true)))
                {
                    UiTextFocus.Clear(); // 按钮焦点残留清理
                    selected = idx;
                    AudioManager.Play("SFX_Click");
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
        }
        GUI.enabled = !_busy;
        if (selected >= 0)
        {
            StartCoroutine(GenerateCo(null, items[selected].id));
        }

        GUILayout.Space(6f);
        if (GUILayout.Button("清除全部建筑", UiTheme.Btn, GUILayout.Height(44f)))
        {
            UiTextFocus.Clear(); // 按钮焦点残留清理
            AudioManager.Play("SFX_Click");
            if (BuildingManager.Instance != null)
            {
                BuildingManager.Instance.Clear();
                _status = "已清除";
            }
            RoadBuilder.ClearAll(); // 自动引路随建筑一并清理
        }
        GUI.enabled = true;

        if (!string.IsNullOrEmpty(_status))
        {
            GUILayout.Space(8f);
            GUILayout.Label(_status, UiTheme.Body);
        }
        GUILayout.EndArea();
        UiTheme.EndScale();
    }

    private IEnumerator GenerateCo(string description, string template)
    {
        // 输入框改 placeholder 空串后（2026-08-29），空描述回车不再发空请求
        if (template == null && string.IsNullOrWhiteSpace(description))
        {
            _status = "<color=#9E2B25>先输入描述，或从下方选一张图纸</color>";
            yield break;
        }
        ApiClient.EnsureExists(); // Play 中途脚本重载会洗掉单例，先懒补建
        if (ApiClient.Instance == null)
        {
            _status = "<color=red>场景中没有 ApiClient</color>";
            yield break;
        }
        if (BuildingManager.Instance == null)
        {
            _status = "<color=red>场景中没有 BuildingManager</color>";
            yield break;
        }
        if (BuildingPlacement.Active)
        {
            _status = "先放置当前建筑（左键放置 / 右键取消）";
            yield break;
        }

        _busy = true;
        _busySince = Time.unscaledTime;
        HeldItemUmbrella.Instance?.PlayGrab();
        _status = $"生成中…（{description ?? template}）";
        BuildingData result = null;
        string error = null;

        yield return ApiClient.Instance.GenerateBuilding(
            description,
            data => result = data,
            msg => error = msg);

        if (result == null && error == null && template != null)
        {
            // description 为空时走模板通道
            yield return ApiClient.Instance.GenerateByTemplate(
                template,
                data => result = data,
                msg => error = msg);
        }

        if (result != null)
        {
            GameObject building = BuildingManager.Instance.GenerateFromJson(result);
            if (building != null)
            {
                // 进入放置模式由玩家自选落点；放置系统不可用时回退玩家前方摆放
                bool entered = BuildingPlacement.Begin(building, placed =>
                {
                    AudioManager.Play("SFX_Build");
                    HeldItemUmbrella.Instance?.PlayBuild();
                    _status = $"<color=green>已生成「{result.name}」{result.blocks.Length} 块</color>";

                    // 委托系统登记：验收时上报（服务端多建筑取最优匹配）
                    if (CommissionSystem.Instance != null)
                    {
                        CommissionSystem.Instance.RegisterBuild(
                            result.name, description, template, result.blocks.Length, placed.transform);
                        CommissionSystem.Instance.OnBuildPlaced(placed.transform.position);
                        CommissionSystem.Instance.NotifyPlacedForCommission(result.name); // 落成→引导按 C 验收
                    }

                    // 自动接路：落点定了才铺，从最近路面长一条引路到门口
                    RoadBuilder.ConnectBuilding(placed);
                });
                if (!entered)
                {
                    PlaceInFrontOfPlayer(building.transform);
                    AudioManager.Play("SFX_Build");
                    HeldItemUmbrella.Instance?.PlayBuild();
                    _status = $"<color=green>已生成「{result.name}」{result.blocks.Length} 块</color>";

                    if (CommissionSystem.Instance != null)
                    {
                        CommissionSystem.Instance.RegisterBuild(
                            result.name, description, template, result.blocks.Length, building.transform);
                        CommissionSystem.Instance.OnBuildPlaced(building.transform.position);
                        CommissionSystem.Instance.NotifyPlacedForCommission(result.name); // 落成→引导按 C 验收
                    }
                    RoadBuilder.ConnectBuilding(building);
                }
                else
                {
                    // 幽灵已跟随准星：明确告知进入放置，别让"生成中…"挂着误导没生成
                    _status = $"<color=green>已生成「{result.name}」——准星选落点，左键放置 / 右键取消</color>";
                }
                // 生成成功面板自动隐藏（用户定则：面板按键驱动，Tab 重开；放置引导在场景提示条）
                UiPanelLayout.Close(UiPanelLayout.Panel.Building);
            }
            else
            {
                _status = "<color=red>生成失败：空数据</color>";
            }
        }
        else
        {
            _status = $"<color=red>{error ?? "生成失败"}</color>";
        }
        _busy = false;
    }

    /// <summary>
    /// 把新建筑放到玩家前方，距离按建筑实际半宽自适应（大建筑离远点），
    /// 并让建筑的 -Z 面（门所在面）朝向玩家。
    /// </summary>
    private void PlaceInFrontOfPlayer(Transform building)
    {
        Transform parent = building.parent;
        if (_player == null || parent == null) return;

        // 建筑包围盒（世界坐标）估算安全距离
        float radius = 6f;
        var renderers = building.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            radius = Mathf.Max(bounds.extents.x, bounds.extents.z);
        }
        float distance = Mathf.Max(radius * 2.5f, 10f);

        Vector3 toPlayer = (_player.position - building.position).normalized;
        toPlayer.y = 0;
        if (toPlayer.sqrMagnitude > 0.001f)
        {
            // 门在建筑 -Z 面：LookRotation(forward=toPlayer) 会让 -Z 背向玩家，
            // 因此取反让 -Z 面朝向玩家
            building.rotation = Quaternion.LookRotation(-toPlayer, Vector3.up);
        }

        Vector3 dir = _player.forward;
        dir.y = 0;
        if (dir.sqrMagnitude < 0.001f) dir = Vector3.forward;
        Vector3 worldTarget = _player.position + dir.normalized * distance;
        building.localPosition = parent.InverseTransformPoint(worldTarget);
    }
}
