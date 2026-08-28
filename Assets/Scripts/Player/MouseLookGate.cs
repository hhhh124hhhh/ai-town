using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using StarterAssets;

/// <summary>
/// 右键视角门控：按住鼠标右键拖动才旋转视角，平时鼠标自由（可点 IMGUI 面板按钮）。
/// 同时接管指针锁定状态：右键按下 → 锁定隐藏；松开 → 释放显示。
/// 时序：PlayerInput 的 OnLook 在帧首写入 _input.look，本组件 Update 里在未按住时清零，
/// FirstPersonController / FlyMode 的 LateUpdate 才消费——清零总能赶在消费之前。
/// </summary>
public class MouseLookGate : MonoBehaviour
{
    private StarterAssetsInputs _input;

    private void Awake()
    {
        _input = GetComponent<StarterAssetsInputs>();
    }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (CinematicIntro.IsCinematic) return; // 演出期间指针由 CinematicIntro 接管
        if (BuildingPlacement.Active) return;   // 放置模式由 BuildingPlacement 接管指针锁定与视角直通

        var mouse = Mouse.current;
        if (mouse == null) return;

        bool held = mouse.rightButton.isPressed;
        if (held)
        {
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
        else if (Cursor.lockState != CursorLockMode.None)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (_input != null)
        {
            // 与 OnApplicationFocus 的重锁行为保持一致：失焦再聚焦时不会锁死自由指针
            _input.cursorLocked = held;
            if (!held) _input.look = Vector2.zero;
        }
#endif
    }
}
