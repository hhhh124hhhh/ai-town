using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 打字门控：文本输入框持有键盘焦点期间（建筑面板的 IMGUI TextField / 对话的 uGUI InputField），
/// 移动与交互按键一律静默，避免打字时角色乱动、误触发 E/X/U/C/F 等按键。
/// 消费方：FirstPersonInputs（PlayerInput 回调源头清零）、FlyMode 直读键盘、各按键轮询脚本。
///
/// 2026-08-29 "打字触发游戏按键"终修（三层）：
/// ①本引擎无 IME 组合 API（onIMECompositionChange/Event.compositionString 均不存在，
///   activeInputHandler=1 纯 Input System）→ 改启发式：**文本刚变化≈IME 刚上屏**，
///   各面板在 Update 里比对输入框内容记 _lastInputChangeAt，Enter/发送在 0.35~0.4s
///   变化窗内一律吞掉——上屏确认键不再误触发生成/发送。
/// ②Tab/Esc 门控按"真文本框聚焦"判（IMGUI 命名控件 / uGUI InputField），
///   不用 IsTyping——它把按钮点击残留的 keyboardControl 也算进去（点了按钮后
///   Tab 关不掉面板/Esc 失灵=按钮焦点陷阱）。
/// ③按钮点击后一律 Clear()：IMGUI 按钮会抢 keyboardControl，不清则 F/E/X/C
///   被 IsTyping 锁死（"按键失灵"的另一张脸）。
/// </summary>
public static class UiTextFocus
{
    /// <summary>当前是否处于文本输入状态（任一输入框聚焦中——保守口径，含按钮焦点残留）。</summary>
    public static bool IsTyping
    {
        get
        {
            if (GUIUtility.keyboardControl != 0) return true; // IMGUI 控件
            return UguiFieldFocused;
        }
    }

    /// <summary>uGUI 输入框聚焦中（对话框；IMGUI 按钮焦点不算——按钮不该锁游戏键）。</summary>
    public static bool UguiFieldFocused
    {
        get
        {
            var es = EventSystem.current;
            return es != null && es.currentSelectedGameObject != null
                && es.currentSelectedGameObject.GetComponent<InputField>() != null;
        }
    }

    /// <summary>强制交还键盘焦点：按钮点击后/进入放置模式等时机调用，防残留焦点锁死游戏键。</summary>
    public static void Clear()
    {
        GUIUtility.keyboardControl = 0;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
    }
}
