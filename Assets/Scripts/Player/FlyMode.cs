using StarterAssets;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 飞行模式：按 F 切换。开启时禁用地面行走控制器，由本组件接管移动与视角，
/// 复用 StarterAssetsInputs 的 move/look/sprint 输入，Space 升 / LeftCtrl 降。
/// 关闭时还原组件状态，玩家自然落回地面。
/// </summary>
public class FlyMode : MonoBehaviour
{
    [Header("Flight")]
    [Tooltip("普通飞行速度 m/s")]
    public float FlySpeed = 12.0f;
    [Tooltip("冲刺飞行速度 m/s")]
    public float FlySprintSpeed = 30.0f;
    [Tooltip("视角旋转灵敏度（与 FirstPersonController.RotationSpeed 同义）")]
    public float RotationSpeed = 1.0f;

    private FirstPersonController _fpc;
    private CharacterController _controller;
    private StarterAssetsInputs _input;
    private Transform _cameraTarget;
    private bool _flying;
    private float _pitch;

    private void Awake()
    {
        _fpc = GetComponent<FirstPersonController>();
        _controller = GetComponent<CharacterController>();
        _input = GetComponent<StarterAssetsInputs>();
        // Starter Assets 玩家预制体中的相机跟随目标
        _cameraTarget = transform.Find("PlayerCameraRoot");
        if (_cameraTarget == null)
        {
            Debug.LogError("[FlyMode] 找不到 PlayerCameraRoot 子物体，视角控制不可用");
        }
    }

    private void Update()
    {
        if (TogglePressed())
        {
            SetFlying(!_flying);
            return;
        }
        if (_flying)
        {
            Move();
        }
    }

    private void LateUpdate()
    {
        if (_flying)
        {
            CameraRotation();
        }
    }

    private void Move()
    {
        if (_input == null || _cameraTarget == null) return;

        float speed = _input.sprint ? FlySprintSpeed : FlySpeed;
        Vector3 direction = transform.right * _input.move.x + transform.forward * _input.move.y;
        if (direction.sqrMagnitude > 1f) direction.Normalize();

        direction += Vector3.up * GetVerticalAxis();
        transform.position += direction * (speed * Time.deltaTime);
    }

    private void CameraRotation()
    {
        if (_input == null || _cameraTarget == null) return;
        if (_input.look.sqrMagnitude < 0.01f) return;

        // 与 FirstPersonController 一致：鼠标增量不乘 deltaTime
        _pitch += -_input.look.y * RotationSpeed;
        _pitch = Mathf.Clamp(_pitch, -90f, 90f);

        _cameraTarget.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        transform.Rotate(Vector3.up * (_input.look.x * RotationSpeed));
    }

    /// <summary>开启/关闭飞行并同步相关组件状态。</summary>
    public void SetFlying(bool on)
    {
        if (on == _flying) return;
        _flying = on;

        if (on)
        {
            if (_fpc != null) _fpc.enabled = false;
            if (_controller != null) _controller.enabled = false;
            if (_cameraTarget != null)
            {
                _pitch = _cameraTarget.transform.localEulerAngles.x;
                if (_pitch > 180f) _pitch -= 360f;
            }
            Debug.Log("[FlyMode] 飞行模式开启");
        }
        else
        {
            if (_fpc != null) _fpc.enabled = true;
            if (_controller != null) _controller.enabled = true;
            Debug.Log("[FlyMode] 飞行模式关闭");
        }
    }

    private static float GetVerticalAxis()
    {
        float up = 0f;
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.spaceKey.isPressed) up += 1f;
            if (kb.leftCtrlKey.isPressed) up -= 1f;
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKey(KeyCode.Space)) up += 1f;
        if (Input.GetKey(KeyCode.LeftControl)) up -= 1f;
#endif
        return Mathf.Clamp(up, -1f, 1f);
    }

    private static bool TogglePressed()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && kb.fKey.wasPressedThisFrame) return true;
#else
        return false;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.F);
#else
        return false;
#endif
    }
}
