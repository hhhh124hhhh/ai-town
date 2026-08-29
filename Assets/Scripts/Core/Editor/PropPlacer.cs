using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 道具批量摆放（两步式）：读取 .codely/props_manifest.json（仓库根目录，不进 Assets）。
    /// 厘米单位 FBX（如 Rodin 导出）在 Instantiate 当帧甚至下一帧读到的 renderer.bounds
    /// 仍是单位修正前的陈旧值（大 100 倍），因此拆成两个菜单由外部控制时序：
    ///   ① Tools → AI Town → Step 1 Place Props     只实例化（scale=1，不测量）
    ///   ② Tools → AI Town → Step 2 Finish Props    等若干秒后执行：测量→归一化缩放→贴地对位→补碰撞体→存场景
    /// Step 2 可重复执行；重摆整个场景时先跑 Step 1 再跑 Step 2。
    /// </summary>
    public static class PropPlacer
    {
        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private static readonly string ManifestPath = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "..", ".codely", "props_manifest.json"));

        [Serializable]
        private class InstanceDef { public float x; public float z; public float rot; }

        [Serializable]
        private class PropDef { public string name; public string prefab; public float height; public float scale; public float y; public float rotX; public string material; public InstanceDef[] instances; }

        [Serializable]
        private class Manifest { public PropDef[] props; }

        private class PendingProp { public GameObject go; public string label; public float height; public float targetX; public float targetZ; }

        private static readonly List<PendingProp> s_pending = new List<PendingProp>();

        [MenuItem("Tools/AI Town/Step 1 Place Props")]
        public static void PlacePropsStep1()
        {
            AssetDatabase.Refresh(); // 外部刚放入的 prefab/FBX 先导入
            var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);

            var old = GameObject.Find("_Props");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var root = new GameObject("_Props");

            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
            if (manifest?.props == null || manifest.props.Length == 0)
                throw new InvalidOperationException("清单为空: " + ManifestPath);

            s_pending.Clear();
            foreach (var prop in manifest.props)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prop.prefab);
                if (prefab == null)
                {
                    Debug.LogError($"[PropPlacer v3] 找不到 prefab: {prop.prefab}");
                    continue;
                }
                for (int i = 0; i < prop.instances.Length; i++)
                {
                    var inst = prop.instances[i];
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
                    go.name = $"{prop.name}_{i + 1}";
                    // rotX 供 Z-up FBX（如 Rodin 树）竖立使用，默认 0 保持原行为
                    go.transform.rotation = Quaternion.Euler(prop.rotX, inst.rot, 0f);
                    if (prop.scale > 0)
                    {
                        // 硬编码缩放模式：厘米单位 FBX 的 renderer.bounds 含未修正的辅助包围盒，
                        // 运行时测量不可靠，缩放由实测反推写死在清单里；轴心视为底面中心。
                        go.transform.localScale = Vector3.one * prop.scale;
                        go.transform.position = new Vector3(inst.x, prop.y, inst.z);
                        if (!string.IsNullOrEmpty(prop.material))
                        {
                            // Rodin FBX 内嵌材质在 URP 下渲染为白色，指派清单里的纯色材质
                            var mr = go.GetComponent<MeshRenderer>();
                            var mat = AssetDatabase.LoadAssetAtPath<Material>(prop.material);
                            if (mr != null && mat != null) mr.sharedMaterial = mat;
                            else Debug.LogError($"[PropPlacer v4] {go.name} 材质指派失败: {prop.material}");
                        }
                        EnsureBoxCollider(go, ColliderShrinkXZ(prop.name));
                        Debug.Log($"[PropPlacer v4] {go.name} scale={prop.scale} (硬编码) pos=({inst.x},{prop.y},{inst.z})");
                    }
                    else
                    {
                        s_pending.Add(new PendingProp
                        {
                            go = go,
                            label = go.name,
                            height = prop.height,
                            targetX = inst.x,
                            targetZ = inst.z
                        });
                    }
                }
            }
            if (s_pending.Count == 0)
            {
                EditorSceneManager.SaveOpenScenes();
                Debug.Log("[PropPlacer v4] Step1 完成：全部道具已按硬编码缩放摆放，场景已保存");
            }
            else
            {
                Debug.Log($"[PropPlacer v3-style] Step1 完成：实例化 {s_pending.Count} 个待测量道具，稍后执行 Step 2");
            }
        }

        [MenuItem("Tools/AI Town/Step 2 Finish Props")]
        public static void PlacePropsStep2()
        {
            if (s_pending.Count == 0)
            {
                Debug.LogWarning("[PropPlacer v3] 没有待处理的道具，请先执行 Step 1");
                return;
            }

            int placed = 0;
            foreach (var p in s_pending)
            {
                if (p.go == null) continue;
                var b = RenderBounds(p.go);
                if (b.size.y <= 1e-5f)
                {
                    Debug.LogError($"[PropPlacer v3] {p.label} 包围盒异常({b.size.y:F5})，跳过");
                    continue;
                }
                float scale = p.height / b.size.y;
                p.go.transform.localScale = Vector3.one * scale;
                Debug.Log($"[PropPlacer v3] {p.label} 原始高={b.size.y:F4} 缩放={scale:F2}");

                b = RenderBounds(p.go); // 缩放后重算再对位
                var target = new Vector3(p.targetX, 0f, p.targetZ);
                p.go.transform.position += new Vector3(
                    target.x - b.center.x,
                    -b.min.y,
                    target.z - b.center.z);
                EnsureBoxCollider(p.go, ColliderShrinkXZ(p.label));
                placed++;
            }

            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[PropPlacer v3] Step2 完成：摆放 {placed} 个道具，场景已保存");
            s_pending.Clear();
        }

        /// <summary>
        /// 幂等补碰撞体：遍历 _Props 现有实例，缺 Collider 的按类型收窄 footprint 补 BoxCollider。
        /// 供存量场景一次性补齐（Step 1 重摆会自动带上，此菜单也可随时重跑）。
        /// </summary>
        [MenuItem("Tools/AI Town/Add Prop Colliders")]
        public static void AddPropColliders()
        {
            var root = GameObject.Find("_Props");
            if (root == null) { Debug.LogWarning("[PropPlacer v4] 场景无 _Props 节点"); return; }
            int added = 0, kept = 0;
            foreach (Transform t in root.transform)
            {
                if (t.GetComponentInChildren<Collider>() != null) { kept++; continue; }
                EnsureBoxCollider(t.gameObject, ColliderShrinkXZ(t.name));
                added++;
            }
            Debug.Log($"[PropPlacer v4] 补碰撞体完成：新增 {added} 件，已有 {kept} 件（幂等可重跑）");
        }

        /// <summary>按道具类型收窄碰撞盒水平 footprint：树只挡树干区（可从树冠下穿行），旗帜类杆细，椅车近实心。</summary>
        private static float ColliderShrinkXZ(string propName)
        {
            int i = propName.IndexOf('_');
            string type = i > 0 ? propName.Substring(0, i) : propName;
            switch (type)
            {
                case "树": return 0.35f;
                case "旗帜": case "酒旗": case "灯笼柱": return 0.4f;
                case "长椅": case "马车": return 0.85f;
                default: return 0.7f;
            }
        }

        private static Bounds RenderBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        /// <summary>
        /// 幂等补 BoxCollider：世界包围盒由各 MeshFilter 的 mesh.bounds 手工变换累加
        /// （renderer.bounds 对厘米 FBX 有陈旧值坑，mesh.bounds 是序列化数据恒可靠），
        /// 盒体挂在 identity 旋转的子物体上——rotX 270 竖立的 Z-up FBX 与任意 Y 旋转
        /// 都能得到世界轴对齐的碰撞盒。shrinkXZ 收窄水平 footprint。
        /// </summary>
        private static void EnsureBoxCollider(GameObject go, float shrinkXZ)
        {
            if (go.GetComponentInChildren<Collider>() != null) return;
            var bounds = MeshWorldBounds(go);
            if (bounds.size.x <= 1e-4f || bounds.size.y <= 1e-4f || bounds.size.z <= 1e-4f)
            {
                Debug.LogWarning($"[PropPlacer v4] {go.name} 网格包围盒异常({bounds.size:F4})，跳过碰撞体");
                return;
            }

            var holder = new GameObject("_Collider");
            holder.transform.SetParent(go.transform, false);
            holder.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);
            var col = holder.AddComponent<BoxCollider>();
            var ls = holder.transform.lossyScale; // holder localScale=1 → 世界缩放继承父链
            col.size = new Vector3(
                Mathf.Max(0.01f, bounds.size.x * shrinkXZ / Mathf.Max(1e-6f, ls.x)),
                Mathf.Max(0.01f, bounds.size.y / Mathf.Max(1e-6f, ls.y)),
                Mathf.Max(0.01f, bounds.size.z * shrinkXZ / Mathf.Max(1e-6f, ls.z)));
        }

        /// <summary>全部后代网格的世界 AABB：TransformPoint 换心 + 旋转半尺寸绝对值求和，绕开 renderer.bounds。</summary>
        private static Bounds MeshWorldBounds(GameObject go)
        {
            var b = new Bounds(go.transform.position, Vector3.zero);
            bool has = false;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                var t = mf.transform;
                var mb = mf.sharedMesh.bounds;
                Vector3 c = t.TransformPoint(mb.center);
                Quaternion r = t.rotation;
                Vector3 ex = r * new Vector3(mb.extents.x, 0f, 0f);
                Vector3 ey = r * new Vector3(0f, mb.extents.y, 0f);
                Vector3 ez = r * new Vector3(0f, 0f, mb.extents.z);
                var wb = new Bounds(c, 2f * new Vector3(
                    Mathf.Abs(ex.x) + Mathf.Abs(ey.x) + Mathf.Abs(ez.x),
                    Mathf.Abs(ex.y) + Mathf.Abs(ey.y) + Mathf.Abs(ez.y),
                    Mathf.Abs(ex.z) + Mathf.Abs(ey.z) + Mathf.Abs(ez.z)));
                if (!has) { b = wb; has = true; } else b.Encapsulate(wb);
            }
            return b;
        }
    }
}
