using UnityEngine;

/// <summary>
/// NPC 待机微动：呼吸起伏 + 缓慢摇摆，消除静态蜡像感。
/// 挂在模型子物体上，保留其基础 localPos/localRot/localScale，每帧仅叠加微小偏移。
/// </summary>
public class NpcIdleMotion : MonoBehaviour
{
    [Header("呼吸（纵向起伏）")]
    public float bobAmplitude = 0.008f;
    public float bobPeriod = 3.2f;

    [Header("摇摆（缓慢转身 + 侧倾）")]
    public float swayAmplitude = 1.6f;
    public float tiltAmplitude = 0.5f;
    public float swayPeriod = 7.5f;

    private Vector3 _basePos;
    private Quaternion _baseRot;
    private float _phase;

    private void Awake()
    {
        _basePos = transform.localPosition;
        _baseRot = transform.localRotation;
        _phase = Random.value * Mathf.PI * 2f; // 各 NPC 相位错开，避免同步呼吸
    }

    private void Update()
    {
        float t = Time.time;
        float bob = Mathf.Sin((t / bobPeriod + _phase) * Mathf.PI * 2f) * bobAmplitude;
        float sway = Mathf.Sin((t / swayPeriod + _phase) * Mathf.PI * 2f) * swayAmplitude;
        float tilt = Mathf.Sin((t / swayPeriod * 0.7f + _phase * 1.3f) * Mathf.PI * 2f) * tiltAmplitude;

        transform.localPosition = _basePos + Vector3.up * bob;
        // 模型带 X=270 直立修正：其局部 Z 轴 = 世界向上，故 sway 走 Euler.z、tilt 走 Euler.y
        transform.localRotation = _baseRot * Quaternion.Euler(0f, tilt, sway);
    }
}