using System.Collections;
using UnityEngine;

/// <summary>
/// 建筑生成面板（IMGUI 纯代码，无需场景预制）：
/// 输入描述或选模板 → 调 Python API → 在玩家前方生成建筑。
/// Tab 键显隐面板。
/// </summary>
public class BuildingPanel : MonoBehaviour
{
    // (模板id, 中文按钮名)：id 走 API，中文上按钮（民国世界观统一）
    private static readonly (string id, string zh)[] Templates =
    {
        ("castle", "洋楼"), ("house", "房屋"), ("tower", "高塔"), ("pagoda", "宝塔"),
        ("qilou", "骑楼"), ("paifang", "牌坊"), ("xitai", "戏台"), ("gulou", "鼓楼"),
        ("temple", "庙宇"), ("bridge", "桥"), ("fountain", "喷泉"), ("wall", "围墙"),
        ("garden", "花园"), ("windmill", "风车"), ("gazebo", "凉亭"), ("lighthouse", "灯塔"),
        ("village", "村落"), ("statue", "雕像"), ("tree", "树"), ("pyramid", "金字塔"),
        ("sphere", "球体"), ("spiral", "螺旋"), ("mushroom", "蘑菇"), ("heart", "心形"),
        ("skyscraper", "高楼"), ("spaceship", "飞船"), ("shanghai", "上海"),
    };

    private string _input = "建一座青砖老洋楼";
    private int _templateIndex;
    private string _status = "";
    private bool _busy;
    private float _busySince;        // _busy 置真时刻（看门狗用）
    private bool _visible = true;
    private Vector2 _scroll;

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
        // Tab 永远可切换面板（含放置模式——打字焦点残留/放置中都算唯一逃生口；隐藏时一并释放焦点）
        if (kb != null && kb.tabKey.wasPressedThisFrame)
        {
            _visible = !_visible;
            if (!_visible) UiTextFocus.Clear();
        }
        if (BuildingPlacement.Active) return; // 放置期间其余输入归放置模式（回车不再触发生成）
        // 回车=生成（便携手感 + 远程测试钩子：桥 manage_input 可触发）
        // 对话框打开时让位给 DialogSystem，避免一次回车同时触发两处
        if (kb != null && kb.enterKey.wasPressedThisFrame && !_busy && _visible
            && DialogSystem.Instance == null)
        {
            StartCoroutine(GenerateCo(_input, null));
        }
#endif
    }

    private Rect _inputFieldRect; // 输入框在面板内区域（点击框外释放键盘焦点用）
    private bool _inputFocused;   // 上一帧键盘焦点是否在本输入框

    private void OnGUI()
    {
        if (!_visible || CinematicIntro.IsCinematic)
        {
            // 面板不可见时不得持有键盘焦点,否则 WASD 一直打进隐藏输入框
            if (GUIUtility.keyboardControl != 0) UiTextFocus.Clear();
            return;
        }

        UiTheme.BeginScale();
        var areaRect = new Rect(16, 16, 440, 420); // 440 宽 = 4×80 按钮 + padding 72 + 间距
        GUILayout.BeginArea(areaRect, UiTheme.Panel);
        UiTheme.Wash(areaRect);
        GUILayout.Label("<b>AI 建筑生成</b>  <color=#5A5042>(Tab 隐藏)</color>", UiTheme.Title);

        _input = GUILayout.TextField(_input, UiTheme.Field);
        _inputFieldRect = GUILayoutUtility.GetLastRect();
        _inputFocused = GUIUtility.keyboardControl != 0;

        // 点击输入框以外（场景/其他按钮）即释放键盘焦点——移动键回到角色,防误输入
        var ev = Event.current;
        if (ev != null && ev.type == EventType.MouseDown && _inputFocused && !_inputFieldRect.Contains(ev.mousePosition))
            UiTextFocus.Clear();

        GUI.enabled = !_busy;
        if (GUILayout.Button("生成（自然语言）", UiTheme.BtnPrimary))
        {
            StartCoroutine(GenerateCo(_input, null));
        }

        GUILayout.Space(4);
        GUILayout.Label("图纸快速生成：", UiTheme.Hint);
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(110));
        int columns = 4;
        int rows = Mathf.CeilToInt(Templates.Length / (float)columns);
        int selected = -1;
        for (int r = 0; r < rows; r++)
        {
            GUILayout.BeginHorizontal();
            for (int c = 0; c < columns; c++)
            {
                int idx = r * columns + c;
                if (idx >= Templates.Length) break;
                bool unlocked = CommissionSystem.IsTemplateUnlocked(Templates[idx].id);
                GUI.enabled = !_busy && unlocked;
                if (GUILayout.Button(unlocked ? Templates[idx].zh : "🔒" + Templates[idx].zh, UiTheme.Btn, GUILayout.Width(80)))
                {
                    selected = idx;
                    AudioManager.Play("SFX_Click");
                }
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
        GUI.enabled = !_busy;
        if (selected >= 0)
        {
            StartCoroutine(GenerateCo(null, Templates[selected].id));
        }

        if (GUILayout.Button("清除全部建筑", UiTheme.Btn))
        {
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
            GUILayout.Label(_status, UiTheme.Body);
        }
        GUILayout.EndArea();
        UiTheme.EndScale();
    }

    private IEnumerator GenerateCo(string description, string template)
    {
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
                    }
                    RoadBuilder.ConnectBuilding(building);
                }
                else
                {
                    // 幽灵已跟随准星：明确告知进入放置，别让"生成中…"挂着误导没生成
                    _status = $"<color=green>已生成「{result.name}」——准星选落点，左键放置 / 右键取消</color>";
                }
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
