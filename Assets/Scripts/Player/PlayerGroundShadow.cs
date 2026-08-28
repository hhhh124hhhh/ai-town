using UnityEngine;

/// <summary>
/// 玩家脚下光斑阴影（blob shadow）：程序化径向渐变贴图 + 透明 quad，
/// 第一人称/飞行时提供"我在哪"的位置参照物，治理悬空与移动眩晕。
/// 每帧从玩家位置向下 Raycast 贴地（飞行时影子留在地面 = 强位置参照）。
/// 挂 Player 根上，Awake 自动创建，无需场景接线。
/// </summary>
public class PlayerGroundShadow : MonoBehaviour
{
    [Header("Shadow")]
    [Tooltip("阴影直径（米）")]
    public float Diameter = 1.4f;
    [Tooltip("中心不透明度")]
    public float CenterAlpha = 0.4f;
    [Tooltip("贴地抬升，避免与路面/地面 z-fighting")]
    public float SurfaceOffset = 0.04f;
    [Tooltip("向下 Raycast 最大距离")]
    public float MaxDistance = 60f;

    private Transform _quad;
    private Material _mat;
    private const float PivotHeight = 1.0f; // 胶囊 pivot 离脚底的距离，Raycast 起点补回

    private void Awake()
    {
        _quad = GameObject.CreatePrimitive(PrimitiveType.Quad).transform;
        _quad.name = "GroundShadow";
        // 不参与物理：去掉碰撞体，避免挡 Raycast 挡到自己和 BasicRigidBodyPush 推动
        Destroy(_quad.GetComponent<Collider>());
        _quad.SetParent(transform, false);

        _mat = new Material(Shader.Find("Sprites/Default"));
        _mat.mainTexture = BuildRadialTexture();
        var mr = _quad.GetComponent<MeshRenderer>();
        mr.sharedMaterial = _mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        _quad.localScale = new Vector3(Diameter, Diameter, 1f);
        _quad.localRotation = Quaternion.Euler(90f, 0f, 0f); // 面朝上
    }

    private void LateUpdate()
    {
        // 从胶囊中段往下找地面（玩家 pivot 在脚底附近，仍多给余量）
        Vector3 origin = transform.position + Vector3.up * PivotHeight;
        float dist = MaxDistance + PivotHeight;
        if (Physics.Raycast(origin, Vector3.down, out var hit, dist,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            _quad.position = hit.point + Vector3.up * SurfaceOffset;
            _quad.gameObject.SetActive(true);
        }
        else
        {
            _quad.gameObject.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (_mat != null) Destroy(_mat);
    }

    /// <summary>径向渐变 alpha 贴图：中心不透明、边缘全透明，模拟软阴影。</summary>
    private static Texture2D BuildRadialTexture()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.name = "BlobShadow";
        float half = size * 0.5f;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - half + 0.5f) * (x - half + 0.5f)
                                   + (y - half + 0.5f) * (y - half + 0.5f)) / half;
                // 半径 1 之外全透明，之内平滑衰减
                float a = Mathf.Clamp01(1f - d);
                a *= a; // 平方衰减更像软影
                pixels[y * size + x] = new Color(0f, 0f, 0f, a);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(false, true);
        return tex;
    }
}
