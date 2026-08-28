using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 摆放校验（placement lint，UE Map Check 的单人版）：
    /// 规则来源业界惯例（Wildlands exclusion areas / UE5 PCG pruning）——
    ///   R1 禁区：路网段 AABB(膨胀0.5m)、建筑包围盒、出生点4m安全半径、委托绿圈
    ///   R2 间距：道具间 footprint 中心距 >= 1.2m
    /// 违规输出 Console 报告；Fix 按钮把违规道具沿"最近合法方向"推到禁区外。
    /// </summary>
    public static class PlacementLint
    {
        private const float RoadMargin = 0.5f;      // 路缘外扩=人行余量
        private const float SpawnRadius = 4f;       // 出生点安全半径
        private const float PropSpacing = 1.2f;     // 道具最小中心距
        private const float BuildingPad = 0.3f;     // 建筑footprint外扩
        private const string ReportTag = "[PlacementLint]";

        private class Violation
        {
            public Transform Obj;
            public string Reason;
            public Vector3 Suggest;   // 建议落点（世界 XZ）
        }

        [MenuItem("Tools/AI Town/Validate Placement")]
        public static void Validate()
        {
            var report = Run(autofix: false);
            Debug.Log(report);
        }

        [MenuItem("Tools/AI Town/Validate And Fix Placement")]
        public static void ValidateAndFix()
        {
            var report = Run(autofix: true);
            Debug.Log(report);
            EditorSceneManager_SaveSceneIfDirty();
        }

        private static void EditorSceneManager_SaveSceneIfDirty()
        {
            if (UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty)
            {
                UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
            }
        }

        /// <summary>执行校验，返回报告文本。autofix=true 时把可修复违规物体推到建议点。</summary>
        private static string Run(bool autofix)
        {
            var violations = new List<Violation>();
            var roads = CollectRoadRects();
            var buildings = CollectBuildingRects();
            var props = CollectProps();
            var spawn = GameObject.Find("_SpawnPoint");
            Vector2 spawnXZ = spawn != null
                ? new Vector2(spawn.transform.position.x, spawn.transform.position.z)
                : new Vector2(0f, -10f);

            // ── R1 禁区 ──
            foreach (var p in props)
            {
                Vector2 xz = new Vector2(p.position.x, p.position.z);

                // 广场家具例外：井/火/椅/车/旗/箱 允许在 Plaza_Main 上（广场本来的功能就是摆这些）
                bool isPlazaFurniture = p.name.StartsWith("水井") || p.name.StartsWith("篝火")
                    || p.name.StartsWith("长椅") || p.name.StartsWith("马车")
                    || p.name.StartsWith("旗帜") || p.name.StartsWith("木箱");
                // 路灯例外：允许在路缘 1m 带内（沿路照明是路灯的本职）
                bool isStreetLamp = p.name.StartsWith("路灯");

                foreach (var r in roads)
                {
                    // 市集/广场区（Plaza/East_Square）上的家具与路灯合法（灯本来沿街沿广场）
                    bool isMarketArea = r.Name.StartsWith("Plaza") || r.Name.StartsWith("Road_East_Square");
                    if (r.Contains(xz) && !(isMarketArea && (isPlazaFurniture || isStreetLamp))
                        && !(isStreetLamp && NearEdge(xz, r, 1.6f)))
                    {
                        violations.Add(new Violation
                        {
                            Obj = p,
                            Reason = $"压路「{r.Name}」",
                            Suggest = PushOut(xz, r.Min, r.Max),
                        });
                        break;
                    }
                }

                foreach (var b in buildings)
                {
                    if (b.Contains(xz))
                    {
                        violations.Add(new Violation
                        {
                            Obj = p,
                            Reason = "压建筑",
                            Suggest = PushOut(xz, b.Min, b.Max),
                        });
                        break;
                    }
                }

                if (Vector2.Distance(xz, spawnXZ) < SpawnRadius && violations.FindAll(v => v.Obj == p).Count == 0)
                {
                    Vector2 dir = (xz - spawnXZ).normalized;
                    if (dir.sqrMagnitude < 0.001f) dir = new Vector2(0f, -1f);
                    violations.Add(new Violation
                    {
                        Obj = p,
                        Reason = "落在出生点安全区",
                        Suggest = spawnXZ + dir * (SpawnRadius + 0.5f),
                    });
                }
            }

            // ── R2 间距（O(n²)，道具量级 <100 可接受）──
            for (int i = 0; i < props.Count; i++)
            {
                for (int j = i + 1; j < props.Count; j++)
                {
                    // 木箱堆叠/旗灯搭配是设计意图，不做间距检查
                    if (props[j].name.StartsWith("木箱") && props[i].name.StartsWith("木箱")) continue;
                    if ((props[i].name.StartsWith("旗帜") && props[j].name.StartsWith("路灯"))
                        || (props[i].name.StartsWith("路灯") && props[j].name.StartsWith("旗帜"))) continue;

                    Vector2 a = new Vector2(props[i].position.x, props[i].position.z);
                    Vector2 b = new Vector2(props[j].position.x, props[j].position.z);
                    if (Vector2.Distance(a, b) < PropSpacing)
                    {
                        Vector2 dir = (b - a).normalized;
                        if (dir.sqrMagnitude < 0.001f) dir = new Vector2(1f, 0f);
                        violations.Add(new Violation
                        {
                            Obj = props[j],
                            Reason = $"与「{props[i].name}」间距 < {PropSpacing}m",
                            Suggest = a + dir * (PropSpacing + 0.2f),
                        });
                    }
                }
            }

            // ── 修复 + 报告 ──
            var sb = new StringBuilder();
            sb.AppendLine($"{ReportTag} 校验完成：{props.Count} 个道具，{violations.Count} 处违规{(autofix ? "（已自动修正）" : "")}");
            var fixedObjs = new HashSet<Transform>();
            foreach (var v in violations)
            {
                if (autofix && !fixedObjs.Contains(v.Obj))
                {
                    Undo.RecordObject(v.Obj, "PlacementLint Fix");
                    Vector3 pos = v.Obj.position;
                    v.Obj.position = new Vector3(v.Suggest.x, pos.y, v.Suggest.y);
                    fixedObjs.Add(v.Obj);
                    sb.AppendLine($"  ✓ 已修正 {v.Obj.name}：{v.Reason} → ({v.Suggest.x:0.0}, {v.Suggest.y:0.0})");
                }
                else if (!autofix)
                {
                    sb.AppendLine($"  ✗ {v.Obj.name}：{v.Reason}（建议 → {v.Suggest.x:0.0}, {v.Suggest.y:0.0}）");
                }
            }
            if (violations.Count == 0) sb.AppendLine("  全部通过 ✓");
            return sb.ToString();
        }

        // ── 数据采集 ─────────────────────────────────────────────
        private class Rect2 { public string Name; public Vector2 Min, Max; public bool Contains(Vector2 p) => p.x >= Min.x && p.x <= Max.x && p.y >= Min.y && p.y <= Max.y; }

        private static List<Rect2> CollectRoadRects()
        {
            var list = new List<Rect2>();
            // 路网不只看 _Roads：河流系统的桥头引路（_River/Path_To_Road/Path）也是路，
            // 名字不带 Road_ 前缀导致首版漏检（树站引路上的根因）
            var roots = new List<Transform>();
            var roads = GameObject.Find("_Roads");
            if (roads != null) roots.Add(roads.transform);
            var pathToRoad = GameObject.Find("_River/Path_To_Road");
            if (pathToRoad != null) roots.Add(pathToRoad.transform);

            foreach (var rootT in roots)
            {
                foreach (Transform seg in rootT)
                {
                    // Plane 原生 10×10，world 尺寸 = localScale×10
                    float hx = Mathf.Abs(seg.localScale.x) * 5f + RoadMargin;
                    float hz = Mathf.Abs(seg.localScale.z) * 5f + RoadMargin;
                    list.Add(new Rect2
                    {
                        Name = seg.name,
                        Min = new Vector2(seg.position.x - hx, seg.position.z - hz),
                        Max = new Vector2(seg.position.x + hx, seg.position.z + hz),
                    });
                }
            }
            return list;
        }

        private static List<Rect2> CollectBuildingRects()
        {
            var list = new List<Rect2>();
            var root = GameObject.Find("_Buildings");
            if (root == null) return list;
            foreach (Transform b in root.transform)
            {
                var renderers = b.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) continue;
                var bounds = renderers[0].bounds;
                foreach (var r in renderers) bounds.Encapsulate(r.bounds);
                list.Add(new Rect2
                {
                    Name = b.name,
                    Min = new Vector2(bounds.min.x - BuildingPad, bounds.min.z - BuildingPad),
                    Max = new Vector2(bounds.max.x + BuildingPad, bounds.max.z + BuildingPad),
                });
            }
            return list;
        }

        private static List<Transform> CollectProps()
        {
            var list = new List<Transform>();
            var root = GameObject.Find("_Props");
            if (root == null) return list;
            foreach (Transform t in root.transform) list.Add(t);
            return list;
        }

        /// <summary>点是否在矩形边缘带内（用于路灯贴路合法判定）。</summary>
        private static bool NearEdge(Vector2 p, Rect2 r, float band)
        {
            bool nearX = (p.x > r.Min.x && p.x < r.Min.x + band) || (p.x < r.Max.x && p.x > r.Max.x - band);
            bool nearZ = (p.y > r.Min.y && p.y < r.Min.y + band) || (p.y < r.Max.y && p.y > r.Max.y - band);
            return nearX || nearZ;
        }

        /// <summary>把点推到矩形外：沿"到矩形中心的反向"推到边界外 0.5m。</summary>
        private static Vector2 PushOut(Vector2 p, Vector2 min, Vector2 max)
        {
            Vector2 center = (min + max) * 0.5f;
            Vector2 dir = p - center;
            if (dir.sqrMagnitude < 0.001f) dir = new Vector2(0f, -1f);
            dir.Normalize();

            // 沿 dir 找到刚好离开矩形的 t（粗糙但够用：步进到出界）
            Vector2 q = p;
            for (float t = 0.5f; t < 20f; t += 0.5f)
            {
                q = p + dir * t;
                if (q.x < min.x || q.x > max.x || q.y < min.y || q.y > max.y) break;
            }
            return q;
        }
    }
}
