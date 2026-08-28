using UnityEngine;

/// <summary>
/// 水面 UV 滚动：运行时动画纹理 offset 模拟流动波纹。
/// 基色贴图与法线贴图反向滚动，交叉产生波光闪烁。
/// </summary>
public class WaterScroll : MonoBehaviour
{
    [SerializeField] private Vector2 uvSpeed = new Vector2(0.018f, 0.03f);

    private Material mat;
    private static readonly int BaseMapID = Shader.PropertyToID("_BaseMap");
    private static readonly int BumpMapID = Shader.PropertyToID("_BumpMap");

    private void Awake()
    {
        var r = GetComponent<Renderer>();
        if (r != null) mat = r.material; // 实例化材质，避免污染共享资产
    }

    private void Update()
    {
        if (mat == null) return;
        Vector2 off = mat.GetTextureOffset(BaseMapID) + uvSpeed * Time.deltaTime;
        mat.SetTextureOffset(BaseMapID, off);
        if (mat.HasProperty(BumpMapID) && mat.GetTexture(BumpMapID) != null)
        {
            mat.SetTextureOffset(BumpMapID, -off);
        }
    }
}
