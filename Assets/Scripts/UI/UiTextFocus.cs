using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 打字门控：文本输入框持有键盘焦点期间（建筑面板的 IMGUI TextField / 对话的 uGUI InputField），
/// 移动与交互按键一律静默，避免打字时角色乱动、误触发 E/X/U/C/F 等按键。
/// 消费方：FirstPersonInputs（PlayerInput 回调源头清零）、FlyMode 直读键盘、各按键轮询脚本。
/// </summary>
public static class UiTextFocus
{
    /// <summary>当前是否处于文本输入状态（任一输入框聚焦中）。</summary>
    public static bool IsTyping
    {
        get
        {
            if (GUIUtility.keyboardControl != 0) return true; // IMGUI 输入框
            var es = EventSystem.current;
            return es != null && es.currentSelectedGameObject != null
                && es.currentSelectedGameObject.GetComponent<InputField>() != null; // uGUI 输入框
        }
    }

    /// <summary>强制交还键盘焦点：进入放置模式等接管输入的时机调用，防残留焦点把门控卡死。</summary>
    public static void Clear()
    {
        GUIUtility.keyboardControl = 0;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
    }
}
