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
    [Tooltip("普通飞行速度 m/s（按 0.25 世界比例调低，防眩晕）")]
    public float FlySpeed = 6.0f;
    [Tooltip("冲刺飞行速度 m/s")]
    public float FlySprintSpeed = 15.0f;
    [Tooltip("视角旋转灵敏度（与 FirstPersonController.RotationSpeed 同义）")]
    public float RotationSpeed = 1.0f;
    [Tooltip("视角平滑系数，越大跟手越小越稳（0=关闭平滑）")]
    public float RotationSmoothing = 12.0f;
    [Tooltip("移动加速度渐变，越大越快提速（0=瞬移启停）")]
    public float MoveAcceleration = 6.0f;

    private FirstPersonController _fpc;
    private CharacterController _controller;
    private StarterAssetsInputs _input;
    private Transform _cameraTarget;
    private bool _flying;
    private float _pitch;
    private float _yawVelocity;   // 平滑后的实际移动速度
    private Vector3 _moveVelocity;

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

        // 打字/对话期间不响应移动（对话锁移动与 FirstPersonController 的处理保持一致）
        if (UiTextFocus.IsTyping || DialogSystem.Instance != null)
        {
            _moveVelocity = Vector3.zero;
            return;
        }

        float speed = _input.sprint ? FlySprintSpeed : FlySpeed;
        Vector3 direction = transform.right * _input.move.x + transform.forward * _input.move.y;
        if (direction.sqrMagnitude > 1f) direction.Normalize();

        direction += Vector3.up * GetVerticalAxis();
        if (direction.sqrMagnitude > 1f) direction.Normalize();

        // 速度渐变：SmoothDamp 让启停有加速度感，避免瞬移启停带来的眩晕
        Vector3 target = direction * speed;
        float smooth = MoveAcceleration > 0f ? MoveAcceleration : 1000f;
        _moveVelocity = Vector3.Lerp(
            _moveVelocity,
            target,
            1f - Mathf.Exp(-smooth * Time.deltaTime));

        // 位移走 CharacterController（2026-08-29 穿模定案）：飞行也保留碰撞体，
        // 墙/建筑/地面挡得住——旧写法 transform.position += 是零碰撞裸移，
        // 全场景可穿，穿进建筑内部=相机陷进几何体（用户观感"穿模/飞不高"）。
        // CC.Move 不施加重力，飞行手感不变；被挡时 CC 自动沿墙滑动。
        Vector3 delta = _moveVelocity * Time.deltaTime;
        if (_controller != null && _controller.enabled)
        {
            _controller.Move(delta);
        }
        else
        {
            transform.position += delta; // 兜底：控制器缺失时退回裸移
        }
    }

    private void CameraRotation()
    {
        if (_input == null || _cameraTarget == null) return;

        // 与 FirstPersonController 同号：正 pitch = 低头（符号反了会让飞行时上下视角反转）
        if (_input.look.sqrMagnitude >= 0.01f)
        {
            _pitch += _input.look.y * RotationSpeed;
            _pitch = Mathf.Clamp(_pitch, -89f, 89f);
            transform.Rotate(Vector3.up * (_input.look.x * RotationSpeed));
        }

        // 平滑俯仰：持续向目标角收敛（鼠标停住也走完剩余行程，不再中途冻结）
        float currentPitch = _cameraTarget.transform.localEulerAngles.x;
        if (currentPitch > 180f) currentPitch -= 360f;
        float smoothedPitch = RotationSmoothing > 0f
            ? Mathf.Lerp(currentPitch, _pitch, 1f - Mathf.Exp(-RotationSmoothing * Time.unscaledDeltaTime))
            : _pitch;

        _cameraTarget.transform.localRotation = Quaternion.Euler(smoothedPitch, 0f, 0f);
    }

    /// <summary>开启/关闭飞行并同步相关组件状态。</summary>
    public void SetFlying(bool on)
    {
        if (on == _flying) return;
        _flying = on;

        if (on)
        {
            if (_fpc != null) _fpc.enabled = false;
            // CharacterController 保持启用（2026-08-29 穿模修复）：飞行移动走 CC.Move
            // 保留碰撞，只有行走逻辑（FPC）需要让位。关掉 CC=全场景裸移必穿模。
            if (_cameraTarget != null)
            {
                _pitch = _cameraTarget.transform.localEulerAngles.x;
                if (_pitch > 180f) _pitch -= 360f;
                _pitch = Mathf.Clamp(_pitch, -89f, 89f);
            }
            Debug.Log("[FlyMode] 飞行模式开启");
        }
        else
        {
            if (_fpc != null) _fpc.enabled = true;
            if (_controller != null) _controller.enabled = true;
            _moveVelocity = Vector3.zero; // 清零速度缓存，下次开启从静止起步
            Debug.Log("[FlyMode] 飞行模式关闭");
        }
    }

    private static float GetVerticalAxis()
    {
        if (UiTextFocus.IsTyping) return 0f; // 打字时 Space/Ctrl 属于文本输入
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
        if (UiTextFocus.IsTyping || DialogSystem.Instance != null) return false; // 打字/对话中不切飞行
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
