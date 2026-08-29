using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using StarterAssets;

/// <summary>
/// 玩家活动范围（软边界）：每帧把 Player 的 XZ 钳在护城河内堤围出的镇区矩形内（Y 不限，飞行可任意高度），
/// 南面石桥走单独走廊（可走完整座桥，不能下桥到界外）。顶到边界且仍在向外的移动输入时，
/// 弹一次性提示"镇外荒地，尚在开发"（IMGUI，带冷却防刷屏）。
/// 场景无关，运行时自建（RuntimeInitializeOnLoadMethod），无需场景接线；开场演出期间不介入。
/// 边界依据：AiTownRiverSetup 内堤 x=-18/22、z=-22/26，各内收 0.5m；桥面 x±1.5、z -32..-20。
/// </summary>
public class PlayerBounds : MonoBehaviour
{
    /// <summary>镇区主范围（XZ 世界坐标）：内堤线各内收 0.5m。</summary>
    private static readonly Rect MainArea = new Rect(-17.5f, -21.5f, 39f, 47f);

    /// <summary>南面石桥走廊：桥面收 0.1m 防跨出桥栏，南端收 0.5m 防下桥出界。</summary>
    private static readonly Rect BridgeCorridor = new Rect(-1.4f, -31.5f, 2.8f, 11.5f);

    private const float ToastCooldown = 5f;
    private const float ToastDuration = 2.2f;
    private const string ToastText = "镇外荒地，尚在开发";

    private Transform _player;
    private StarterAssetsInputs _input;
    private float _nextToastAt;
    private float _toastUntil;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        new GameObject("_PlayerBounds").AddComponent<PlayerBounds>();
    }

    private void Update()
    {
        if (_player == null)
        {
            var p = GameObject.Find("Player");
            if (p == null) return; // Player 尚未生成，下一帧重试
            _player = p.transform;
            _input = p.GetComponent<StarterAssetsInputs>();
        }

        if (CinematicIntro.IsCinematic || CinematicIntro.InputCooldown) return;

        Vector3 pos = _player.position;
        bool onBridge = BridgeCorridor.Contains(new Vector2(pos.x, pos.z));
        var area = onBridge ? BridgeCorridor : MainArea;
        float cx = Mathf.Clamp(pos.x, area.xMin, area.xMax);
        float cz = Mathf.Clamp(pos.z, area.yMin, area.yMax);
        if (cx == pos.x && cz == pos.z) return;

        // 是否仍在向外推（有移动输入）：站着不动不弹提示
        bool pushing = _input != null && _input.move.sqrMagnitude > 0.01f;
        _player.position = new Vector3(cx, pos.y, cz);
        if (pushing && Time.unscaledTime >= _nextToastAt)
        {
            _nextToastAt = Time.unscaledTime + ToastCooldown;
            _toastUntil = Time.unscaledTime + ToastDuration;
        }
    }

    private void OnGUI()
    {
        if (Time.unscaledTime >= _toastUntil) return;
        UiTheme.BeginScale();
        float alpha = Mathf.Clamp01((_toastUntil - Time.unscaledTime) / 0.5f);
        var style = UiTheme.Text(UiTheme.SizeEmph);
        var size = style.CalcSize(new GUIContent(ToastText));
        var rect = new Rect((UiTheme.VW - size.x) * 0.5f, UiTheme.VH * 0.16f, size.x + 48f, size.y + 20f);
        // 设计系统收编（2026-08-29）：Card 的 88 padding 按大面板设计，会把这个 ~260px
        // 小 Toast 内容区吃空（小组件禁大面板 9-slice 判例）——改 PaperCard 素纸卡
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUILayout.BeginArea(rect);
        UiTheme.PaperCard(rect, alpha * 0.95f);
        GUILayout.Space(8f);
        GUILayout.Label(ToastText, style);
        GUILayout.EndArea();
        GUI.color = prev;
        UiTheme.EndScale();
    }
}
