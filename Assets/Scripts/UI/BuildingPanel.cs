using System.Collections;
using UnityEngine;

/// <summary>
/// 建筑生成面板（IMGUI 纯代码，无需场景预制）：
/// 输入描述或选模板 → 调 Python API → 在玩家前方生成建筑。
/// Tab 键显隐面板。
/// </summary>
public class BuildingPanel : MonoBehaviour
{
    private static readonly string[] Templates =
    {
        "castle", "house", "tower", "pagoda", "pyramid", "temple", "bridge",
        "fountain", "lighthouse", "wall", "garden", "windmill", "gazebo",
        "skyscraper", "village", "statue", "sphere", "spiral", "mushroom",
        "heart", "tree", "spaceship", "shanghai",
    };

    private string _input = "建一个红色大城堡";
    private int _templateIndex;
    private string _status = "";
    private bool _busy;
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
        if (CinematicIntro.IsCinematic || CinematicIntro.InputCooldown) return; // 演出期间/开始键那一帧不响应 Tab/回车
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.tabKey.wasPressedThisFrame) _visible = !_visible;
        // 回车=生成（便携手感 + 远程测试钩子：桥 manage_input 可触发）
        // 对话框打开时让位给 DialogSystem，避免一次回车同时触发两处
        if (kb != null && kb.enterKey.wasPressedThisFrame && !_busy && _visible
            && DialogSystem.Instance == null)
        {
            StartCoroutine(GenerateCo(_input, null));
        }
#endif
    }

    private void OnGUI()
    {
        if (!_visible || CinematicIntro.IsCinematic) return;

        GUILayout.BeginArea(new Rect(16, 16, 360, 300), UiTheme.Panel);
        GUILayout.Label("<b>AI 建筑生成</b>  <color=#888>(Tab 隐藏)</color>", UiTheme.Title);

        _input = GUILayout.TextField(_input, UiTheme.Field);

        GUI.enabled = !_busy;
        if (GUILayout.Button("生成（自然语言）", UiTheme.BtnPrimary))
        {
            StartCoroutine(GenerateCo(_input, null));
        }

        GUILayout.Space(4);
        GUILayout.Label("模板快速生成：", UiTheme.Hint);
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(90));
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
                bool unlocked = CommissionSystem.IsTemplateUnlocked(Templates[idx]);
                GUI.enabled = !_busy && unlocked;
                if (GUILayout.Button(unlocked ? Templates[idx] : "🔒" + Templates[idx], UiTheme.Btn, GUILayout.Width(80)))
                {
                    selected = idx;
                }
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
        GUI.enabled = !_busy;
        if (selected >= 0)
        {
            StartCoroutine(GenerateCo(null, Templates[selected]));
        }

        if (GUILayout.Button("清除全部建筑", UiTheme.Btn))
        {
            if (BuildingManager.Instance != null)
            {
                BuildingManager.Instance.Clear();
                _status = "已清除";
            }
        }
        GUI.enabled = true;

        if (!string.IsNullOrEmpty(_status))
        {
            GUILayout.Label(_status, UiTheme.Body);
        }
        GUILayout.EndArea();
    }

    private IEnumerator GenerateCo(string description, string template)
    {
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

        _busy = true;
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
                PlaceInFrontOfPlayer(building.transform);
                _status = $"<color=green>已生成「{result.name}」{result.blocks.Length} 块</color>";

                // 委托系统登记：验收时上报（服务端多建筑取最优匹配）
                if (CommissionSystem.Instance != null)
                {
                    CommissionSystem.Instance.RegisterBuild(
                        result.name, description, template, result.blocks.Length, building.transform);
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
