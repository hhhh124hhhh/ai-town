using UnityEngine;

/// <summary>
/// 手持油纸伞：程序化建模（竹杆+伞面+伞骨），挂载到 PlayerCameraRoot 右下前方，
/// 行走时正弦摆动（速度参照物，消第一人称"飘"感），待机呼吸微动。
/// 挂在 Player 根上（与 FlyMode 同级），相机子物体在 Awake 自动创建。
/// </summary>
public class HeldItemUmbrella : MonoBehaviour
{
    [Header("Layout 摆位（相机局部坐标）")]
    public Vector3 HoldOffset = new Vector3(0.28f, -0.32f, 0.45f);
    public Vector3 HoldRotation = new Vector3(-12f, 18f, 8f);

    [Header("Swing 摆动")]
    [Tooltip("走路摆动幅度（米）")]
    public float SwingAmplitude = 0.045f;
    [Tooltip("走路摆动角幅度（度）")]
    public float SwingTilt = 4.5f;
    [Tooltip("待机呼吸幅度（米）")]
    public float IdleAmplitude = 0.006f;

    [Header("Colors")]
    public Color CanopyColor = new Color(0.16f, 0.24f, 0.45f);   // 靛蓝
    public Color BambooColor = new Color(0.62f, 0.50f, 0.33f);   // 竹黄

    private Transform _held;
    private CharacterController _controller;
    private FirstPersonController _fpc;
    private Vector3 _basePos;
    private Quaternion _baseRot;
    private float _phase;
    private Material _canopyMat;
    private Material _bambooMat;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _fpc = GetComponent<FirstPersonController>();

        var cameraRoot = transform.Find("PlayerCameraRoot");
        if (cameraRoot == null)
        {
            Debug.LogError("[HeldItemUmbrella] 找不到 PlayerCameraRoot");
            enabled = false;
            return;
        }

        _held = BuildUmbrella().transform;
        _held.SetParent(cameraRoot, false);
        _held.localPosition = HoldOffset;
        _held.localRotation = Quaternion.Euler(HoldRotation);
        _basePos = _held.localPosition;
        _baseRot = _held.localRotation;
    }

    private void Update()
    {
        float speed = _controller != null && _controller.enabled
            ? new Vector2(_controller.velocity.x, _controller.velocity.z).magnitude
            : 0f;

        if (speed > 0.3f)
        {
            // 步频随速度：约 speed/步幅(0.75m) 步每秒 × π 得摆动角频率
            _phase += (speed / 0.75f) * Mathf.PI * Time.deltaTime;
            float s = Mathf.Sin(_phase);
            float c = Mathf.Cos(_phase * 2f); // 上下是步频两倍（左右脚各一下）

            _held.localPosition = _basePos
                + new Vector3(s * SwingAmplitude * 0.6f, c * SwingAmplitude * 0.4f, 0f);
            _held.localRotation = _baseRot * Quaternion.Euler(
                s * SwingTilt * 0.4f, 0f, s * SwingTilt);
        }
        else
        {
            // 待机呼吸：慢正弦上下微浮
            _phase += 1.2f * Time.deltaTime;
            float breathe = Mathf.Sin(_phase) * IdleAmplitude;
            _held.localPosition = _basePos + new Vector3(0f, breathe, 0f);
            _held.localRotation = _baseRot;
        }
    }

    private void OnDestroy()
    {
        if (_canopyMat != null) Destroy(_canopyMat);
        if (_bambooMat != null) Destroy(_bambooMat);
    }

    /// <summary>程序化油纸伞：竹杆（圆柱）+ 伞面（8 段低 poly 锥面）+ 伞头小帽。</summary>
    private GameObject BuildUmbrella()
    {
        _canopyMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _canopyMat.SetColor("_BaseColor", CanopyColor);
        _canopyMat.SetFloat("_Smoothness", 0.35f);

        _bambooMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _bambooMat.SetColor("_BaseColor", BambooColor);
        _bambooMat.SetFloat("_Smoothness", 0.2f);

        var root = new GameObject("HeldUmbrella");

        // 竹杆：直径 2cm 长 0.9m，手握中段
        var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        shaft.name = "Shaft";
        shaft.transform.SetParent(root.transform, false);
        shaft.transform.localScale = new Vector3(0.022f, 0.45f, 0.022f); // Cylinder 高 2 → 半高 0.45
        shaft.transform.localPosition = new Vector3(0f, -0.1f, 0f);
        shaft.GetComponent<Renderer>().sharedMaterial = _bambooMat;
        Destroy(shaft.GetComponent<Collider>());

        // 伞面：低 poly 锥面（8 边，微弯可用两段锥近似——一段足够）
        var canopy = new GameObject("Canopy");
        canopy.transform.SetParent(root.transform, false);
        canopy.transform.localPosition = new Vector3(0f, 0.36f, 0f);
        var mf = canopy.AddComponent<MeshFilter>();
        var mr = canopy.AddComponent<MeshRenderer>();
        mf.sharedMesh = BuildCanopyMesh(radius: 0.34f, height: 0.16f, segments: 10);
        mr.sharedMaterial = _canopyMat;

        // 伞头小帽
        var cap = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        cap.name = "Cap";
        cap.transform.SetParent(root.transform, false);
        cap.transform.localScale = new Vector3(0.035f, 0.05f, 0.035f);
        cap.transform.localPosition = new Vector3(0f, 0.54f, 0f);
        cap.GetComponent<Renderer>().sharedMaterial = _bambooMat;
        Destroy(cap.GetComponent<Collider>());

        return root;
    }

    /// <summary>伞面网格：顶点圆 + 底缘圆，侧面 2 三角/段；底缘略外扩上翘模拟纸伞弧面。</summary>
    private static Mesh BuildCanopyMesh(float radius, float height, int segments)
    {
        var mesh = new Mesh();
        mesh.name = "UmbrellaCanopy";

        var verts = new Vector3[segments + 2];
        var uvs = new Vector2[segments + 2];
        verts[0] = new Vector3(0f, height, 0f); // 顶尖
        uvs[0] = new Vector2(0.5f, 1f);

        for (int i = 0; i <= segments; i++)
        {
            float a = i / (float)segments * Mathf.PI * 2f;
            float x = Mathf.Cos(a) * radius;
            float z = Mathf.Sin(a) * radius;
            // 底缘微上翘：弧面感
            verts[i + 1] = new Vector3(x, 0.03f, z);
            uvs[i + 1] = new Vector2(i / (float)segments, 0f);
        }

        var tris = new int[segments * 3];
        for (int i = 0; i < segments; i++)
        {
            tris[i * 3] = 0;
            tris[i * 3 + 1] = i + 2;
            tris[i * 3 + 2] = i + 1;
        }

        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
