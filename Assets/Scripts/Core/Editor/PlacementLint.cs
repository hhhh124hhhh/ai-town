using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 摆放校验（placement lint，UE Map Check 的单人版）：
    /// 数据真源 = StreamingAssets/layout.json（roads/districts/lint 参数），JsonUtility 反序列化。
    /// 新增路面/引路只要登记进 layout.json 即被校验覆盖（不再改 C#）。
    /// 规则：
    ///   R1 禁区：roads AABB(+roadMargin)、建筑包围盒(+buildingPad)、出生点安全半径
    ///   R2 间距：道具中心距 >= propSpacing
    ///   R3 豁免：districts.allow 声明该区合法道具类型族（furniture/lamp/flag/crate…），
    ///      道具名前缀命中即在该区合法（广场家具/市集箱笼/桥头栅栏等设计意图）
    /// </summary>
    public static class PlacementLint
    {
        private const string ReportTag = "[PlacementLint]";

        // ── layout.json 数据模型（字段名与 JSON 对齐）──
        [Serializable] private class RoadDef { public string name; public string district; public string root; public float x, z, w, l, y; public string style; }
        [Serializable] private class DistrictDef { public string name; public float[] rect; public string[] allow; }
        [Serializable] private class LintCfg { public float roadMargin; public float spawnRadius; public float propSpacing; public float buildingPad; }
        [Serializable] private class Layout { public RoadDef[] roads; public DistrictDef[] districts; public LintCfg lint; }

        private static Layout _layout;
        /// <summary>每次 Run 重新读盘（文件小，且保证改 JSON 立即生效——缓存会让调试期改动失效）。</summary>
        private static Layout L => _layout = JsonUtility.FromJson<Layout>(
            File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "layout.json")));

        private class Violation { public Transform Obj; public string Reason; public Vector2 Suggest; }
        private class Rect2 { public string Name; public Vector2 Min, Max;
            public bool Contains(Vector2 p) => p.x >= Min.x && p.x <= Max.x && p.y >= Min.y && p.y <= Max.y; }

        [MenuItem("Tools/AI Town/Validate Placement")]
        public static void Validate()
        {
            Debug.Log(Run(autofix: false));
        }

        [MenuItem("Tools/AI Town/Validate And Fix Placement")]
        public static void ValidateAndFix()
        {
            Debug.Log(Run(autofix: true));
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (scene.isDirty) UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
        }

        private static string Run(bool autofix)
        {
            var cfg = L.lint;
            var violations = new List<Violation>();
            var roads = CollectRoadRects(cfg.roadMargin);
            var buildings = CollectBuildingRects(cfg.buildingPad);
            var props = CollectProps();
            var spawnGo = GameObject.Find("_SpawnPoint");
            Vector2 spawn = spawnGo != null
                ? new Vector2(spawnGo.transform.position.x, spawnGo.transform.position.z)
                : new Vector2(0f, -10f);

            // ── R1 禁区 ──
            foreach (var p in props)
            {
                Vector2 xz = new Vector2(p.position.x, p.position.z);

                foreach (var r in roads)
                {
                    if (r.Contains(xz) && !AllowedInRoad(r.Name, p.name))
                    {
                        violations.Add(new Violation { Obj = p, Reason = $"压路「{r.Name}」", Suggest = PushOut(xz, r.Min, r.Max) });
                        break;
                    }
                }

                foreach (var b in buildings)
                {
                    if (b.Contains(xz))
                    {
                        violations.Add(new Violation { Obj = p, Reason = "压建筑", Suggest = PushOut(xz, b.Min, b.Max) });
                        break;
                    }
                }

                if (Vector2.Distance(xz, spawn) < cfg.spawnRadius && violations.FindAll(v => v.Obj == p).Count == 0)
                {
                    Vector2 dir = (xz - spawn).normalized;
                    if (dir.sqrMagnitude < 0.001f) dir = new Vector2(0f, -1f);
                    violations.Add(new Violation { Obj = p, Reason = "落在出生点安全区", Suggest = spawn + dir * (cfg.spawnRadius + 0.5f) });
                }
            }

            // ── R2 间距 ──
            for (int i = 0; i < props.Count; i++)
            for (int j = i + 1; j < props.Count; j++)
            {
                // 旗灯搭配/同前缀成组豁免（设计意图：酒旗挂灯下、市集堆箱）
                if (Prefix(props[i].name) == Prefix(props[j].name)) continue;
                bool pairFlagLamp = (Prefix(props[i].name) == "旗帜" && Prefix(props[j].name) == "路灯")
                                 || (Prefix(props[i].name) == "路灯" && Prefix(props[j].name) == "旗帜");
                if (pairFlagLamp) continue;
                Vector2 a = new Vector2(props[i].position.x, props[i].position.z);
                Vector2 b = new Vector2(props[j].position.x, props[j].position.z);
                if (Vector2.Distance(a, b) < cfg.propSpacing)
                {
                    Vector2 dir = (b - a).normalized;
                    if (dir.sqrMagnitude < 0.001f) dir = new Vector2(1f, 0f);
                    violations.Add(new Violation { Obj = props[j], Reason = $"与「{props[i].name}」间距 < {cfg.propSpacing}m", Suggest = a + dir * (cfg.propSpacing + 0.2f) });
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{ReportTag} 校验完成：{props.Count} 个道具，{violations.Count} 处违规{(autofix ? "（已自动修正）" : "")}");
            var touched = new HashSet<Transform>();
            foreach (var v in violations)
            {
                if (autofix && touched.Add(v.Obj))
                {
                    Undo.RecordObject(v.Obj, "PlacementLint Fix");
                    Vector3 pos = v.Obj.position;
                    v.Obj.position = new Vector3(v.Suggest.x, pos.y, v.Suggest.y);
                    sb.AppendLine($"  ✓ 已修正 {v.Obj.name}：{v.Reason} → ({v.Suggest.x:0.0}, {v.Suggest.y:0.0})");
                }
                else if (!autofix)
                    sb.AppendLine($"  ✗ {v.Obj.name}：{v.Reason}（建议 → {v.Suggest.x:0.0}, {v.Suggest.y:0.0}）");
            }
            if (violations.Count == 0) sb.AppendLine("  全部通过 ✓");
            return sb.ToString();
        }

        /// <summary>道具名 → 类型前缀（树_1 → 树）。</summary>
        private static string Prefix(string name)
        {
            int i = name.IndexOf('_');
            return i > 0 ? name.Substring(0, i) : name;
        }

        /// <summary>
        /// <summary>
        /// 道具是否在路段豁免：路段带 district 字段 → 查该 district 的 allow，
        /// 道具类型前缀命中类型族别名（furniture/lamp/flag/crate/well/fire/cart/fence/tree）即合法。
        /// </summary>
        private static bool AllowedInRoad(string roadName, string propName)
        {
            string type = Prefix(propName);
            RoadDef road = null;
            foreach (var r in L.roads) if (r.name == roadName) { road = r; break; }
            if (road == null || string.IsNullOrEmpty(road.district)) return false;

            DistrictDef dist = null;
            foreach (var d in L.districts) if (d.name == road.district && d.allow != null) { dist = d; break; }
            if (dist == null) return false;

            foreach (var allow in dist.allow)
            {
                switch (allow)
                {
                    case "furniture":
                        if (type == "水井" || type == "篝火" || type == "长椅" || type == "马车" || type == "旗帜" || type == "木箱") return true;
                        break;
                    case "lamp": if (type == "路灯") return true; break;
                    case "flag": if (type == "旗帜" || type == "酒旗") return true; break;
                    case "lantern": if (type == "灯笼柱") return true; break;
                    case "crate": if (type == "木箱") return true; break;
                    case "well": if (type == "水井") return true; break;
                    case "fire": if (type == "篝火") return true; break;
                    case "cart": if (type == "马车") return true; break;
                    case "fence": if (type == "栅栏") return true; break;
                    case "tree": if (type == "树") return true; break;
                }
            }
            return false;
        }

        private static Vector2 PushOut(Vector2 p, Vector2 min, Vector2 max)
        {
            Vector2 c = (min + max) * 0.5f;
            Vector2 dir = p - c;
            if (dir.sqrMagnitude < 0.001f) dir = new Vector2(0f, -1f);
            dir.Normalize();
            Vector2 q = p;
            for (float t = 0.5f; t < 20f; t += 0.5f)
            {
                q = p + dir * t;
                if (q.x < min.x || q.x > max.x || q.y < min.y || q.y > max.y) break;
            }
            return q;
        }

        private static List<Rect2> CollectRoadRects(float margin)
        {
            var list = new List<Rect2>();
            foreach (var r in L.roads)
            {
                float hx = r.w * 0.5f + margin;
                float hz = r.l * 0.5f + margin;
                list.Add(new Rect2 { Name = r.name,
                    Min = new Vector2(r.x - hx, r.z - hz),
                    Max = new Vector2(r.x + hx, r.z + hz) });
            }
            return list;
        }

        private static List<Rect2> CollectBuildingRects(float pad)
        {
            var list = new List<Rect2>();
            var root = GameObject.Find("_Buildings");
            if (root == null) return list;
            foreach (Transform b in root.transform)
            {
                var rs = b.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) continue;
                var bounds = rs[0].bounds;
                foreach (var r in rs) bounds.Encapsulate(r.bounds);
                list.Add(new Rect2 { Name = b.name,
                    Min = new Vector2(bounds.min.x - pad, bounds.min.z - pad),
                    Max = new Vector2(bounds.max.x + pad, bounds.max.z + pad) });
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
    }
}
