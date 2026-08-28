using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using StarterAssets;

/// <summary>
/// 脱困保险：按 X 立即回到出生点地面。关闭飞行模式（若在飞）、瞬移并重置 CharacterController。
/// 场景无关，运行时自建（RuntimeInitializeOnLoadMethod），无需场景接线；
/// 开场演出与对话输入期间不响应，避免抢键。
/// </summary>
public class PlayerGroundRescue : MonoBehaviour
{
    /// <summary>出生点：AiTownSceneSetup 放 Player 的位置，略微抬高自然落地。</summary>
    private static readonly Vector3 SpawnPoint = new Vector3(0f, 2.5f, -10f);

    private Transform _player;
    private FlyMode _fly;
    private CharacterController _controller;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("_PlayerGroundRescue");
        go.AddComponent<PlayerGroundRescue>();
    }

    private void Update()
    {
        if (_player == null)
        {
            var p = GameObject.Find("Player");
            if (p == null) return; // Player 尚未生成，下一帧重试
            _player = p.transform;
            _fly = p.GetComponent<FlyMode>();
            _controller = p.GetComponent<CharacterController>();
        }

        if (CinematicIntro.IsCinematic || CinematicIntro.InputCooldown) return;
        if (DialogSystem.Instance != null || UiTextFocus.IsTyping) return; // 对话/打字期间不抢 X

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null || !kb.xKey.wasPressedThisFrame) return;

        if (_fly != null) _fly.SetFlying(false); // 飞行中先落回行走状态

        if (_controller != null && _controller.enabled)
        {
            // CharacterController 启用时直接改 transform 会被物理钳回，先关再瞬移再开
            _controller.enabled = false;
            _player.position = SpawnPoint;
            _controller.enabled = true;
        }
        else
        {
            _player.position = SpawnPoint;
        }
        Debug.Log("[Rescue] 已回到出生点地面（X 键）");
#endif
    }
}
