using UnityEngine;

/// <summary>
/// 天空盒云流动：全景天空盒 _Rotation 每帧缓转（默认 0.6°/s，民国灰云缓慢漂移）。
/// 运行时自建（与 PlayerBounds/PlayerGroundRescue 同模式，无需场景接线）。
/// 只改材质内存态，Play 退出自动还原，不落盘。
/// </summary>
public class SkyboxDrift : MonoBehaviour
{
    private const float DegreesPerSecond = 0.6f;

    private Material _skybox;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (GameObject.Find("_SkyboxDrift") == null)
        {
            new GameObject("_SkyboxDrift").AddComponent<SkyboxDrift>();
        }
    }

    private void Start()
    {
        _skybox = RenderSettings.skybox;
        if (_skybox == null || !_skybox.HasProperty("_Rotation"))
        {
            Destroy(gameObject); // 非 Panoramic 天空盒（如 Procedural）没有旋转属性
        }
    }

    private void Update()
    {
        if (_skybox == null) return;
        float rot = _skybox.GetFloat("_Rotation") + DegreesPerSecond * Time.deltaTime;
        if (rot >= 360f) rot -= 360f;
        _skybox.SetFloat("_Rotation", rot);
    }
}
