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
    /// 扩展检查（验收标准 docs/3D模型与场景验收标准.md）：
    ///   模型体检：材质 null/magenta/Standard 内嵌/疑似白模 + 单件面数（NPC 拍板豁免）+ 渲染高度
    ///             + 道具件碰撞体/阴影提示 + Static 汇总（只报不修）
    ///   明度采样：主相机渲帧统计阴影/中间调/高光三段占比（Value Hierarchy 参考带）
    ///   Static 标记：_Props/_Buildings/_Roads/_River/Ground 批量勾满（幂等）
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

        // ══ 模型体检（验收标准「关 1」自动化，只报不修——健康问题没有安全的自动修法）══
        // 全场景根遍历（含 inactive；GameObject.Find/GetComponentsInChildren(false) 都会漏 inactive 物体）。
        // 跳过名单 = 非场景美术范畴：玩家/相机是 Starter Assets 模板件（Standard 材质+胶囊网格，
        // 第一人称下玩家不可见），系统件无网格自然跳过，_AmbientFX 粒子按设计排除。

        private const float WhiteBaseThreshold = 0.97f; // _BaseColor 三通道同时 ≥ 此值且无贴图 → 判疑似白模
        private const int MaxPropTris = 30000;          // 单件三角面预算（特写核心资产另立 brief，不走此默认线）
        private const float MaxHeightM = 20f;           // 渲染包围盒高度上限（超出=未缩放巨人/异常）
        private const float MinHeightM = 0.05f;         // 高度下限（塌陷/空壳）

        private static readonly string[] HealthSkipRoots =
            { "PlayerCapsule", "Player", "MainCamera", "PlayerFollowCamera", "_Systems", "_AmbientFX",
              "BGMPlayer", "Directional Light", "_SpawnPoint", "Ground", "_Roads" };

        [MenuItem("Tools/AI Town/Validate Model Health")]
        public static void ValidateModelHealth()
        {
            Debug.Log(RunModelHealth());
        }

        private static string RunModelHealth()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{ReportTag} 模型体检（材质/面数/尺度/碰撞/阴影/Static，全场景扫描）：");
            int items = 0, flagged = 0;
            long totalTris = 0; int totalRenderers = 0;
            int pbTotal = 0, pbNonStatic = 0; // 道具/建筑件 Static 汇总（逐件打印会淹没报告）
            var acceptedNotes = new List<string>(); // 拍板豁免项登记（面数等），报告单列防误读为漏检
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (System.Array.IndexOf(HealthSkipRoots, root.name) >= 0) continue;
                if (root.name == "_Props" || root.name == "_Buildings")
                {
                    // 道具/建筑按清单件拆开体检（一件=一个可搬走的东西），碰撞体纳入检查
                    foreach (Transform item in root.transform)
                    {
                        items++; pbTotal++;
                        if (GameObjectUtility.GetStaticEditorFlags(item.gameObject) == 0)
                            pbNonStatic++;
                        var issues = InspectModel(item, checkCollider: true, allowTrisOver: false, acceptedNotes, ref totalTris, ref totalRenderers);
                        if (issues == null || issues.Count == 0) continue;
                        flagged++;
                        foreach (var issue in issues) sb.AppendLine($"  ✗ {item.name}：{issue}");
                    }
                }
                else
                {
                    // 其余根（NPC/_River…）以根为件整组聚合。三类白名单形制：
                    // 水面/路面/地面是贴地平面（高度<0.05 是形制不是塌陷），玩家走 CharacterController
                    // 地面，路面只是视觉层；光照/出生点是系统件无网格——全在 HealthSkipRoots 豁免。
                    // NPC_ 前缀：面数超预算经用户拍板接受现状（2026-08-29，Tripo 模型不减面），只登记不报错。
                    items++;
                    bool npcAccepted = root.name.StartsWith("NPC_");
                    var issues = InspectModel(root.transform, checkCollider: false, npcAccepted, acceptedNotes, ref totalTris, ref totalRenderers);
                    if (issues == null || issues.Count == 0) continue;
                    flagged++;
                    foreach (var issue in issues) sb.AppendLine($"  ✗ {root.name}：{issue}");
                }
            }
            if (acceptedNotes.Count > 0)
                sb.AppendLine($"  [已接受] {string.Join("；", acceptedNotes)}（用户拍板 2026-08-29，保留现状）");
            sb.AppendLine($"  场景网格总量：约 {totalTris / 10000f:0.0} 万三角 / {totalRenderers} 个 MeshRenderer"
                + "（验收档位：满分 Tris<5 万 · 合格<10 万；renderer 数×材质数为 DrawCalls 上界参考）");
            if (pbNonStatic > 0)
                sb.AppendLine($"  [提示] 未标 Static：{pbNonStatic}/{pbTotal} 件道具/建筑（勾 Static 收静态批处理红利；运行时会动的件勿标）");
            if (items == 0) sb.AppendLine("  场景无可检网格，跳过");
            else if (flagged == 0) sb.AppendLine($"  共 {items} 件，全部通过 ✓");
            else sb.AppendLine($"  共 {items} 件，{flagged} 件异常");
            return sb.ToString();
        }

        /// <summary>单件体检：递归全部后代 renderer（含 inactive）查 null/magenta/Standard 内嵌/无贴图纯白，
        /// 累计三角面（GetIndexCount 零分配）与 renderer 数，封装渲染包围盒查高度。
        /// 道具/建筑件顺带查碰撞体（提示级：玩家可撞物建议有 Collider）。粒子/线条类 Renderer 跳过。
        /// allowTrisOver=true 时面数超预算不报错、改记入 acceptedNotes（用户拍板豁免通道，当前仅 NPC 用）。</summary>
        private static List<string> InspectModel(Transform item, bool checkCollider, bool allowTrisOver, List<string> acceptedNotes, ref long totalTris, ref int totalRenderers)
        {
            var issues = new List<string>();
            var renderers = item.GetComponentsInChildren<Renderer>(true);
            var meshRs = new List<Renderer>();
            foreach (var r in renderers)
                if (r is MeshRenderer || r is SkinnedMeshRenderer) meshRs.Add(r);
            if (meshRs.Count == 0) return renderers.Length > 0 ? issues : null; // 无网格的根=系统件，静默跳过

            int tris = 0;
            Bounds bounds = default;
            bool hasBounds = false;
            foreach (var r in meshRs)
            {
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh != null)
                    for (int s = 0; s < mesh.subMeshCount; s++) tris += (int)(mesh.GetIndexCount(s) / 3);

                if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
                else bounds.Encapsulate(r.bounds);

                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null) { issues.Add("存在空材质槽"); continue; }
                    string shader = mat.shader != null ? mat.shader.name : "";
                    if (shader == "Hidden/InternalErrorShader") { issues.Add($"材质「{mat.name}」shader 报错（magenta）"); continue; }
                    if (shader == "Standard")
                        issues.Add($"材质「{mat.name}」是 Standard 内嵌材质（URP 下渲染异常，需指派 URP .mat）");
                    else if (shader.Contains("Lit") && mat.mainTexture == null && mat.HasProperty("_BaseColor"))
                    {
                        var c = mat.GetColor("_BaseColor");
                        if (c.r >= WhiteBaseThreshold && c.g >= WhiteBaseThreshold && c.b >= WhiteBaseThreshold)
                            issues.Add($"材质「{mat.name}」无贴图且 _BaseColor 近纯白（疑似 FBX 白模跌回）");
                    }
                }
            }
            totalTris += tris;
            totalRenderers += meshRs.Count;
            if (tris > MaxPropTris)
            {
                var note = $"三角面 {tris:N0} 超预算 {MaxPropTris:N0}";
                if (allowTrisOver) acceptedNotes?.Add($"{item.name} {note}");
                else issues.Add(note);
            }
            if (hasBounds)
            {
                float h = bounds.size.y;
                if (h > MaxHeightM) issues.Add($"渲染高度 {h:0.0}m 异常（>{MaxHeightM:0}m，疑似未缩放）");
                else if (h < MinHeightM) issues.Add($"渲染高度 {h:0.00}m 异常（<{MinHeightM}m，疑似塌陷）");
            }
            if (checkCollider && item.GetComponentsInChildren<Collider>(true).Length == 0)
                issues.Add("[提示] 整件无 Collider（玩家可撞/可交互物建议补）");
            if (checkCollider)
            {
                // 阴影：黄昏场景影子是氛围半条命（提示级——个别道具如篝火火焰关投影是合理形制，人工判读）
                bool anyOff = false;
                foreach (var r in meshRs)
                    if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off) { anyOff = true; break; }
                if (anyOff) issues.Add("[提示] 有 renderer 关闭投影（黄昏场景影子是氛围半条命，火焰类除外）");
            }
            return issues;
        }

        // ══ 批量 Static 标记（UCDC 性能档位的低成本抓手：静态几何全勾收静态批处理红利）══
        // 范围：_Props/_Buildings/_Roads/_River/Ground 全部节点（含 inactive），幂等可重跑。
        // 排除：NPC（角色语义，将来要做移动/动画，勾 Static 会被静态合批焊死）、
        //       粒子/系统件/玩家相机（本就不在范围名单内）。
        // 建筑重烘焙会删节点重建丢标记——重烘后重跑本菜单即可补回。
        // 只改编辑器内存态：落盘必须走 manage_scene save（菜单自带保存不可信是本项目定案）。

        private static readonly string[] StaticMarkTargets = { "_Props", "_Buildings", "_Roads", "_River", "Ground" };

        [MenuItem("Tools/AI Town/Mark Static Geometry")]
        public static void MarkStaticGeometry()
        {
            var allFlags = (StaticEditorFlags)~0;
            int marked = 0, already = 0;
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (System.Array.IndexOf(StaticMarkTargets, root.name) < 0) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (GameObjectUtility.GetStaticEditorFlags(t.gameObject) != allFlags)
                    {
                        Undo.RecordObject(t.gameObject, "Mark Static Geometry");
                        GameObjectUtility.SetStaticEditorFlags(t.gameObject, allFlags);
                        marked++;
                    }
                    else already++;
                }
            }
            Debug.Log($"{ReportTag} Static 标记完成：新勾 {marked} 节点，已是全勾 {already} 节点"
                + "（场景未自动保存，落盘走 manage_scene save）");
        }

        // ══ 场景明度采样（验收标准「查 1」，Value Hierarchy 三段参考带，数值供人眼终审）══

        private const int LumSampleW = 256;
        private const int LumSampleH = 144;
        private const float ShadowMax = 0.20f;    // luma < 此值计入阴影段
        private const float HighlightMin = 0.75f; // luma > 此值计入高光段

        [MenuItem("Tools/AI Town/Validate Scene Luminance")]
        public static void ValidateSceneLuminance()
        {
            Debug.Log(RunSceneLuminance());
        }

        private static string RunSceneLuminance()
        {
            var cam = Camera.main;
            if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy)
                return $"{ReportTag} 明度采样失败：场景无可用主相机";

            var rt = new RenderTexture(LumSampleW, LumSampleH, 24);
            var tex = new Texture2D(LumSampleW, LumSampleH, TextureFormat.RGB24, false);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render(); // 强制渲一帧，规避编辑器截图陈旧帧坑
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, LumSampleW, LumSampleH), 0, 0);
                tex.Apply();
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
            }

            var pixels = tex.GetPixels();
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);

            int shadow = 0, mid = 0, high = 0;
            float sum = 0f;
            foreach (var c in pixels)
            {
                float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                sum += l;
                if (l < ShadowMax) shadow++;
                else if (l > HighlightMin) high++;
                else mid++;
            }
            float n = pixels.Length;
            float ps = shadow / n * 100f, pm = mid / n * 100f, ph = high / n * 100f, avg = sum / n;

            var sb = new StringBuilder();
            sb.AppendLine($"{ReportTag} 场景明度三段（主相机 {LumSampleW}×{LumSampleH} 采样）：");
            sb.AppendLine($"  阴影 {ps:0}%（带 15~45%）· 中间调 {pm:0}%（30~65%）· 高光 {ph:0}%（8~40%）· 平均亮度 {avg:0.00}（0.30~0.65）");
            var hints = new List<string>();
            if (ps < 15) hints.Add("阴影不足 → 画面发灰，压暗前景/加遮蔽");
            else if (ps > 45) hints.Add("阴影过重 → 大片死黑，补环境光/雾提亮");
            if (pm < 30 || pm > 65) hints.Add("中间调占比失衡 → 层次扁平，调主光/雾范围");
            if (ph < 8) hints.Add("高光不足 → 背景发闷，查天空盒曝光");
            else if (ph > 40) hints.Add("高光过曝 → 天空/发光块抢戏，降曝光/收 Glow");
            if (avg < 0.30f || avg > 0.65f) hints.Add("整体亮度偏离参考带");
            if (hints.Count == 0) sb.AppendLine("  三段分布均衡 ✓");
            else foreach (var hint in hints) sb.AppendLine($"  ✗ {hint}");
            return sb.ToString();
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
