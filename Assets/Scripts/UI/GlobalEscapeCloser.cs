using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 全局 Esc 关闭器（2026-08-29 用户定则：Esc 应该能关闭所有面板）：
/// 一个监听点按优先级分发——放置模式 > 对话 > 建筑/委托面板，
/// 不在三个面板里各自监听（各自为政=互斥协调器被绕过的旧病）。
/// 场景无关，RuntimeInitializeOnLoadMethod 自建，无需接线。
/// 打字/演出/冷却期间不抢（打字中 Esc 属于输入法/文本语义）。
/// </summary>
public static class GlobalEscapeCloser
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("_GlobalEscapeCloser");
        go.AddComponent<EscapeCloserRunner>();
    }
}

public class EscapeCloserRunner : MonoBehaviour
{
#if ENABLE_INPUT_SYSTEM
    private void Update()
    {
        if (CinematicIntro.IsCinematic || CinematicIntro.InputCooldown) return;
        if (UiTextFocus.IsTyping) return; // 打字中 Esc 不归面板（防误关正在输入的内容）
        var kb = Keyboard.current;
        if (kb == null || !kb.escapeKey.wasPressedThisFrame) return;

        // 优先级分发（同帧只关最上层一个，连按逐层关）
        if (BuildingPlacement.Active) return; // 放置模式自带 Esc 取消，不重复处理
        if (DialogSystem.Instance != null)
        {
            DialogSystem.Instance.CloseByUser();
            return;
        }
        if (UiPanelLayout.Current != UiPanelLayout.Panel.None)
        {
            UiPanelLayout.Clear(); // 建筑/委托面板统一收口（可见性每帧派生，当帧生效）
            AudioManager.Play("SFX_Click");
        }
    }
#endif
}
