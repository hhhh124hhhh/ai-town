using UnityEngine;

/// <summary>
/// 篝火闪烁：正弦叠加噪声驱动灯光强度/范围抖动，营造火光忽明忽暗。
/// 挂在灯光所在物体上（AiTownDuskLighting 自动添加）。
/// </summary>
public class AnimateLight : MonoBehaviour
{
    private Light _light;
    private float _baseIntensity;
    private float _seed;

    private void Start()
    {
        _light = GetComponent<Light>();
        if (_light == null) enabled = false;
        _baseIntensity = _light.intensity;
        _seed = Random.value * 100f;
    }

    private void Update()
    {
        float t = Time.time * 9f + _seed;
        float flicker = Mathf.Sin(t) * 0.5f + Mathf.Sin(t * 2.7f + 1.3f) * 0.3f + Mathf.Sin(t * 5.1f) * 0.2f;
        _light.intensity = _baseIntensity * (1f + flicker * 0.18f);
    }
}
