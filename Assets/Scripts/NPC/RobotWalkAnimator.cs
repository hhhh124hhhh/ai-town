using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 机械机器人行走程序动画：机身轻微起伏 + 左右腿交替前后摆动。
/// 替代骨骼动画（walking_robot 是 52 个独立 part，无骨架），
/// 走时由 NpcWander 驱动 SetWalking(true/false)。
/// </summary>
[RequireComponent(typeof(NpcWander))]
public class RobotWalkAnimator : MonoBehaviour
{
    [Header("腿摆动参数")]
    public float legSwingAmplitude = 0.5f;      // 腿前后摆动幅度（度）
    public float legSwingPeriod = 0.5f;         // 一步周期（秒）
    [Header("机身起伏")]
    public float bobAmplitude = 0.03f;          // 机身上下微动
    public float bobPeriod = 0.5f;

    private Transform[] _legs;
    private Transform[] _bodyParts;
    private NpcWander _wander;
    private bool _walking;
    private float _phase;
    private readonly Dictionary<Transform, Vector3> _basePositions = new();
    private readonly Dictionary<Transform, Quaternion> _baseRotations = new();
    private bool _bound;

    private void Awake()
    {
        _wander = GetComponent<NpcWander>();
    }

    /// <summary>延迟绑定：等所有 part 的 localPosition 稳定（游戏运行时 first frame 再绑）。</summary>
    private void EnsureBound()
    {
        if (_bound) return;
        var parts = GetComponentsInChildren<MeshRenderer>(true);
        if (parts.Length == 0) return;

        // 底部 12 个 part 当腿，其余当机身
        var ordered = parts.OrderBy(p => p.bounds.min.y).ToList();
        _legs = ordered.Take(12).Select(p => p.transform).ToArray();
        _bodyParts = ordered.Skip(12).Select(p => p.transform).ToArray();

        foreach (var t in _legs.Concat(_bodyParts))
        {
            _basePositions[t] = t.localPosition;
            _baseRotations[t] = t.localRotation;
        }
        _bound = true;
    }

    private void Update()
    {
        if (CinematicIntro.IsCinematic) return;
        EnsureBound();
        if (!_bound) return;

        _walking = _wander != null && IsWanderWalking();

        if (_walking)
        {
            _phase += Time.deltaTime;
            float swingT = _phase / legSwingPeriod * Mathf.PI * 2f;

            // 左/右腿反相摆动：偶数左腿前摆、奇数右腿后摆
            for (int i = 0; i < _legs.Length; i++)
            {
                float phase = (i % 2 == 0) ? swingT : swingT + Mathf.PI;
                float swing = Mathf.Sin(phase) * legSwingAmplitude;
                var t = _legs[i];
                t.localRotation = _baseRotations[t] * Quaternion.Euler(swing, 0f, 0f);
            }

            // 机身起伏
            float bob = Mathf.Sin(_phase / bobPeriod * Mathf.PI * 2f) * bobAmplitude;
            foreach (var t in _bodyParts)
                t.localPosition = _basePositions[t] + Vector3.up * bob;
        }
        else
        {
            // 停：归位（缓收）
            foreach (var t in _legs)
            {
                t.localRotation = Quaternion.Slerp(t.localRotation, _baseRotations[t], 0.2f);
                t.localPosition = Vector3.Lerp(t.localPosition, _basePositions[t], 0.2f);
            }
            foreach (var t in _bodyParts)
                t.localPosition = Vector3.Lerp(t.localPosition, _basePositions[t], 0.2f);
            _phase = 0f;
        }
    }

    private bool IsWanderWalking()
    {
        if (_wander == null) return false;
        var f = typeof(NpcWander).GetField("_walking",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return f != null && (bool)f.GetValue(_wander);
    }
}
