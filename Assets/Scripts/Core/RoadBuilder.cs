using System.Collections;
using UnityEngine;

/// <summary>
/// 自动接路：新建筑落地后，从最近路面（_Roads 下 Road_/Plaza_ 段）边缘
/// 向建筑门口铺一条 2m 宽石板引路（L 形两段，逐段 0.5s 生长动画）。
/// 材质复用最近路面段的材质实例（不改动共享资产的 tiling）；路网缺失时回退纯色土路。
/// 引路挂在 _Roads/AutoPaths 下：同名建筑重建先清旧路，「清除全部建筑」时一并清理。
/// 由 BuildingPanel 生成建筑成功后调用。
/// </summary>
public class RoadBuilder : MonoBehaviour
{
    private const float PathY = 0.035f;      // 高于静态路网 0.03，避免共面闪烁
    private const float PathWidth = 2f;
    private const float GrowSeconds = 0.5f;
    private const float MaxSeekDistance = 40f;
    private const float MinLegLength = 0.2f; // 短于此的引路段直接省略

    private static RoadBuilder _runner;

    /// <summary>生成建筑成功后调用；内部懒创建运行器执行生长动画。</summary>
    public static void ConnectBuilding(GameObject building)
    {
        if (building == null) return;
        if (_runner == null) _runner = new GameObject("_RoadBuilder").AddComponent<RoadBuilder>();
        _runner.StartCoroutine(_runner.ConnectCo(building));
    }

    /// <summary>清除全部自动引路（配合「清除全部建筑」）。</summary>
    public static void ClearAll()
    {
        Transform auto = FindAutoRoot(false);
        if (auto == null) return;
        for (int i = auto.childCount - 1; i >= 0; i--)
        {
            Destroy(auto.GetChild(i).gameObject);
        }
    }

    private void OnDestroy()
    {
        if (_runner == this) _runner = null;
    }

    private IEnumerator ConnectCo(GameObject building)
    {
        Transform roads = GameObject.Find("_Roads")?.transform;
        if (roads == null) yield break;
        Transform auto = FindAutoRoot(true);

        // 同名建筑重建/移位：先清旧引路
        string prefix = "Path_" + building.name;
        for (int i = auto.childCount - 1; i >= 0; i--)
        {
            if (auto.GetChild(i).name.StartsWith(prefix))
            {
                Destroy(auto.GetChild(i).gameObject);
            }
        }

        if (!TryGetBoundsXZ(building, out Vector2 bMin, out Vector2 bMax)) yield break;

        // 最近路面段：XZ 矩形间距取最小（Plane 原生 10×10，尺寸 = localScale × 10）
        Transform best = null;
        float bestDist = float.MaxValue;
        Vector2 rMin = Vector2.zero, rMax = Vector2.zero;
        Material srcMat = null;
        foreach (Transform seg in roads)
        {
            if (seg == auto) continue;
            if (!seg.name.StartsWith("Road_") && !seg.name.StartsWith("Plaza_")) continue;
            float hx = Mathf.Abs(seg.localScale.x) * 5f;
            float hz = Mathf.Abs(seg.localScale.z) * 5f;
            Vector2 min = new Vector2(seg.position.x - hx, seg.position.z - hz);
            Vector2 max = new Vector2(seg.position.x + hx, seg.position.z + hz);
            float dx = Mathf.Max(Mathf.Max(min.x - bMax.x, bMin.x - max.x), 0f);
            float dz = Mathf.Max(Mathf.Max(min.y - bMax.y, bMin.y - max.y), 0f);
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d < bestDist)
            {
                bestDist = d;
                best = seg;
                rMin = min;
                rMax = max;
                var r = seg.GetComponent<Renderer>();
                srcMat = r != null ? r.sharedMaterial : null;
            }
        }
        if (best == null || bestDist > MaxSeekDistance || bestDist < 0.3f) yield break;

        // 入轨点 = 建筑中心钳到路面矩形；门口 = 中心→入轨方向上包围盒边界外扩 0.25m（压住贴墙草缝）
        Vector2 c = (bMin + bMax) * 0.5f;
        Vector2 entry = new Vector2(Mathf.Clamp(c.x, rMin.x, rMax.x), Mathf.Clamp(c.y, rMin.y, rMax.y));
        Vector2 dirV = entry - c;
        float len = dirV.magnitude;
        float tx = dirV.x > 0f ? (bMax.x - c.x) / dirV.x : dirV.x < 0f ? (bMin.x - c.x) / dirV.x : float.MaxValue;
        float tz = dirV.y > 0f ? (bMax.y - c.y) / dirV.y : dirV.y < 0f ? (bMin.y - c.y) / dirV.y : float.MaxValue;
        float exitDist = Mathf.Min(tx, tz) * len + 0.25f;
        Vector2 exit = c + dirV / len * exitDist;

        // L 形两段：先沿 X 后沿 Z（都从入轨点向门口推进）
        if (Mathf.Abs(exit.x - entry.x) >= MinLegLength)
        {
            yield return GrowLegCo(auto, prefix + "_X",
                new Vector2(entry.x, entry.y), new Vector2(exit.x, entry.y), srcMat);
        }
        if (Mathf.Abs(exit.y - entry.y) >= MinLegLength)
        {
            yield return GrowLegCo(auto, prefix + "_Z",
                new Vector2(exit.x, entry.y), exit, srcMat);
        }
    }

    /// <summary>铺一段引路，从 from 端向 to 方向生长；材质克隆自路面段（独立 tiling）。</summary>
    private IEnumerator GrowLegCo(Transform parent, string legName, Vector2 from, Vector2 to, Material src)
    {
        float length;
        Vector2 dir;
        bool alongX = Mathf.Abs(to.x - from.x) >= Mathf.Abs(to.y - from.y);
        if (alongX)
        {
            length = Mathf.Abs(to.x - from.x);
            dir = new Vector2(Mathf.Sign(to.x - from.x), 0f);
        }
        else
        {
            length = Mathf.Abs(to.y - from.y);
            dir = new Vector2(0f, Mathf.Sign(to.y - from.y));
        }

        var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
        Destroy(plane.GetComponent<MeshCollider>()); // 引路只是装饰，射线落 Ground
        plane.name = legName;
        plane.transform.SetParent(parent, false);

        Material m = src != null
            ? new Material(src)
            : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (src == null && m.HasProperty("_BaseColor"))
        {
            m.SetColor("_BaseColor", new Color(0.63f, 0.55f, 0.42f)); // 无路网时回退土路色
        }
        if (m.HasProperty("_BaseMap"))
        {
            m.SetTextureScale("_BaseMap", alongX
                ? new Vector2(length / 4f, PathWidth / 4f)
                : new Vector2(PathWidth / 4f, length / 4f)); // 4m 一 repeat 与静态路网一致
        }
        plane.GetComponent<Renderer>().sharedMaterial = m;

        for (float t = 0f; t < GrowSeconds; t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / GrowSeconds));
            ApplyLeg(plane.transform, from, dir, Mathf.Max(0.05f, length * k), alongX);
            yield return null;
        }
        ApplyLeg(plane.transform, from, dir, length, alongX);
    }

    private static void ApplyLeg(Transform tr, Vector2 from, Vector2 dir, float curLen, bool alongX)
    {
        Vector2 center = from + dir * (curLen * 0.5f);
        tr.position = new Vector3(center.x, PathY, center.y);
        tr.localScale = alongX
            ? new Vector3(curLen / 10f, 1f, PathWidth / 10f)
            : new Vector3(PathWidth / 10f, 1f, curLen / 10f);
    }

    private static bool TryGetBoundsXZ(GameObject building, out Vector2 min, out Vector2 max)
    {
        var renderers = building.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            min = max = Vector2.zero;
            return false;
        }
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        min = new Vector2(b.min.x, b.min.z);
        max = new Vector2(b.max.x, b.max.z);
        return true;
    }

    private static Transform FindAutoRoot(bool create)
    {
        Transform roads = GameObject.Find("_Roads")?.transform;
        if (roads == null) return null;
        Transform t = roads.Find("AutoPaths");
        if (t == null && create)
        {
            t = new GameObject("AutoPaths").transform;
            t.SetParent(roads, false);
        }
        return t;
    }
}
