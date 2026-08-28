using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 民国氛围一键改造：全景天空盒（远山薄雾）+ 雾效大气透视 + 草地降饱和
    /// + 青石板主路/广场 + 树冠自然绿 + 暖色阳光。
    /// 依赖生成资产：Assets/Textures/Skybox/minguo_skybox.png（缺省回退现有天空盒）、
    /// Assets/Resources/Textures/Ground/minguo_stone.png（缺省跳过铺路）。
    /// </summary>
    public static class AiTownMinguoAtmosphere
    {
        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private const string SkyboxTexPath = "Assets/Textures/Skybox/minguo_skybox.png";
        private const string SkyboxMatPath = "Assets/Materials/MinguoSkybox_Mat.mat";
        private const string StoneTexPath = "Assets/Resources/Textures/Ground/minguo_stone.png";
        private const string DirtTexPath = "Assets/Resources/Textures/Ground/dirt_road.png";
        private const string StoneMatPath = "Assets/Materials/StoneRoad_Mat.mat";
        private const string RoadMatDir = "Assets/Materials/Roads";
        private const string DirtMatPath = "Assets/Materials/DirtRoad_Mat.mat";
        private const string GroundMatPath = "Assets/Materials/Ground_Mat.mat";
        private const string LeafMatPath = "Assets/Materials/Generated/Mat_TreeLeaf_Minguo.mat";
        private const string TrunkMatPath = "Assets/Materials/Generated/Mat_TreeTrunk_Minguo.mat";
        /// <summary>新增路网统一抬高到 0.03，盖过广场(0.015)/主路(0.02)，引路(RoadBuilder)再抬一档。</summary>
        private const float RoadY = 0.03f;

        [MenuItem("Tools/AI Town/Minguo Atmosphere")]
        public static void ApplyFromMenu()
        {
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            ApplyAll();
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[MinguoAtmosphere] 民国氛围已应用（天空盒/雾/草地/石板路/树色/暖光）");
        }

        [MenuItem("Tools/AI Town/Build Road Network")]
        public static void BuildRoadNetworkFromMenu()
        {
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            int count = BuildRoadNetwork();
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[RoadNetwork] 初始路网已生成并保存（{count} 段，_Roads 下 Road_*）");
        }

        /// <summary>
        /// 初始路网：以广场为中心的十字骨架+东支线（规划图 .codely/road_plan.html）。
        /// 洋楼门前Apron 8×2.2 / 西巷三段(L形,通小木屋) / 骑楼街(廊柱前东西向)+东支南北连路
        /// / 东端货栈空场 6×6 / 南出镇土路。幂等：同名重建。
        /// 每段独立材质资产（tiling 按尺寸等比，4m 一 repeat，与现有主路一致）。
        /// </summary>
        private static int BuildRoadNetwork()
        {
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(StoneTexPath);
            if (tex == null)
            {
                Debug.LogWarning("[RoadNetwork] 缺少 minguo_stone.png，跳过铺路");
                return 0;
            }
            if (!AssetDatabase.IsValidFolder(RoadMatDir)) AssetDatabase.CreateFolder("Assets/Materials", "Roads");
            Material dirt = EnsureDirtMat();

            var root = GameObject.Find("_Roads");
            if (root == null) root = new GameObject("_Roads");

            // (名称, 中心(x,z), 宽, 长, 材质)
            var segments = new (string name, float x, float z, float w, float l, Material mat)[]
            {
                ("Road_GateApron",   0f,     1.1f,  8f,   2.2f, EnsureStoneMat($"{RoadMatDir}/Stone_GateApron.mat", tex)),
                ("Road_West_A",      -11f,   1.8f,  2f,   8f,   EnsureStoneMat($"{RoadMatDir}/Stone_West_A.mat", tex)),
                ("Road_West_B",      -8.4f,  -1.1f, 3.2f, 2.2f, EnsureStoneMat($"{RoadMatDir}/Stone_West_B.mat", tex)),
                ("Road_West_C",      -12.85f, 4.6f, 5.3f, 2f,   EnsureStoneMat($"{RoadMatDir}/Stone_West_C.mat", tex)),
                ("Road_Qilou_EW",    14f,    -13f,  24f,  3f,   EnsureStoneMat($"{RoadMatDir}/Stone_Qilou_EW.mat", tex)),
                ("Road_Qilou_NS",    8.5f,   -6f,   3f,   11f,  EnsureStoneMat($"{RoadMatDir}/Stone_Qilou_NS.mat", tex)),
                ("Road_East_Square", 22.5f,  -8.5f, 6f,   6f,   EnsureStoneMat($"{RoadMatDir}/Stone_East_Square.mat", tex)),
                ("Road_South",       0f,     -16f,  3f,   8f,   dirt),
            };

            foreach (var seg in segments)
            {
                // Plane 原生 10×10：scale=(宽/10,1,长/10)；tiling 取整数格（≈4m/repeat），
                // 非整数格会在路边缘截断纹理，是"拉伸/密度不对"观感的根因
                LayPlane(root.transform, seg.name, new Vector3(seg.x, RoadY, seg.z),
                    new Vector3(seg.w / 10f, 1f, seg.l / 10f), seg.mat,
                    new Vector2(Mathf.Max(1, Mathf.RoundToInt(seg.w / 4f)),
                                Mathf.Max(1, Mathf.RoundToInt(seg.l / 4f))));
            }
            return segments.Length;
        }

        /// <summary>出镇土路材质：夯土无缝贴图（缺图回退纯色），极低 smoothness 模拟干土。</summary>
        private static Material EnsureDirtMat()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(DirtMatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, DirtMatPath);
            }
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(DirtTexPath);
            if (tex != null && mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            else if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.63f, 0.55f, 0.42f));
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.05f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>作用于当前打开的场景（供烘焙流程复用）。</summary>
        public static void ApplyAll()
        {
            ApplyPanoramicSkybox();
            ApplyFog();
            TintGrass();
            LayStoneRoad();
            WarmSunlight();
            FixTreeColors();
        }

        /// <summary>全景天空盒：有生成图则建 Skybox/Panoramic 材质替换 Procedural。</summary>
        private static void ApplyPanoramicSkybox()
        {
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SkyboxTexPath);
            if (tex == null)
            {
                Debug.LogWarning("[MinguoAtmosphere] 缺少 minguo_skybox.png，保留现有天空盒");
                return;
            }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyboxMatPath);
            if (mat == null)
            {
                var shader = Shader.Find("Skybox/Panoramic");
                if (shader == null)
                {
                    Debug.LogWarning("[MinguoAtmosphere] 找不到 Skybox/Panoramic 着色器");
                    return;
                }
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, SkyboxMatPath);
            }
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            if (mat.HasProperty("_Exposure")) mat.SetFloat("_Exposure", 1.0f);
            RenderSettings.skybox = mat;
        }

        /// <summary>线性雾：浅青灰对齐天边色，收紧雾距让中景就开始衰减——年代感的核心一笔。</summary>
        private static void ApplyFog()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 18f;
            RenderSettings.fogEndDistance = 75f;
            RenderSettings.fogColor = new Color(0.74f, 0.76f, 0.78f);
        }

        /// <summary>草地 tint：低饱和贴图基础上再压暖灰（贴图相乘），贴图本身已是干草色。</summary>
        private static void TintGrass()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(GroundMatPath);
            if (mat == null) return;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.92f, 0.90f, 0.82f));
            EditorUtility.SetDirty(mat);
        }

        /// <summary>青石板主路（出生点→城门）+ 中心广场，叠在草地上方 2cm。路/广场独立材质保证各自平铺密度。</summary>
        private static void LayStoneRoad()
        {
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(StoneTexPath);
            if (tex == null)
            {
                Debug.LogWarning("[MinguoAtmosphere] 缺少 minguo_stone.png，跳过铺路");
                return;
            }
            Material roadMat = EnsureStoneMat(StoneMatPath, tex);
            Material plazaMat = EnsureStoneMat(StoneMatPath.Replace(".mat", "_Plaza.mat"), tex);

            var root = GameObject.Find("_Roads");
            if (root == null) root = new GameObject("_Roads");

            // 主路：4m 宽，出生点(z=-12)到城门前(z=-2)；Plane 原生 10×10，scale.x=0.4
            LayPlane(root.transform, "Road_Main", new Vector3(0f, 0.02f, -7f),
                new Vector3(0.4f, 1f, 1f), roadMat, new Vector2(1f, 2.5f));
            // 中心广场：14×12，覆盖井/篝火/椅/旗环抱的区域
            LayPlane(root.transform, "Plaza_Main", new Vector3(0f, 0.015f, -6f),
                new Vector3(1.4f, 1.2f, 1f), plazaMat, new Vector2(3.5f, 3f));
        }

        private static Material EnsureStoneMat(string path, Texture2D tex)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void LayPlane(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat, Vector2 tiling)
        {
            Transform old = parent.Find(name);
            if (old != null) Object.DestroyImmediate(old.gameObject);

            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = name;
            plane.transform.SetParent(parent, false);
            plane.transform.position = pos;
            plane.transform.localScale = scale;
            // 路面叠在草地上，无需碰撞体（射线落到 Ground 即可）
            var collider = plane.GetComponent<MeshCollider>();
            if (collider != null) Object.DestroyImmediate(collider);

            var r = plane.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            if (mat.HasProperty("_BaseMap")) mat.SetTextureScale("_BaseMap", tiling);
        }

        /// <summary>阳光偏暖（民国晨光感），强度微降。</summary>
        private static void WarmSunlight()
        {
            var lightGo = GameObject.Find("Directional Light");
            if (lightGo == null) return;
            var light = lightGo.GetComponent<Light>();
            if (light == null) return;
            light.color = new Color(1f, 0.94f, 0.84f);
            light.intensity = 1.8f;
        }

        /// <summary>
        /// 树冠青蓝→自然绿、树干粉→棕：按内嵌材质自身色相判定叶/干槽位
        /// （蓝通道占优=叶，红通道占优=干），逐槽替换为民国树色材质。
        /// </summary>
        private static void FixTreeColors()
        {
            Material leaf = EnsureColorMat(LeafMatPath, new Color(0.36f, 0.50f, 0.22f));   // 自然黄绿
            Material trunk = EnsureColorMat(TrunkMatPath, new Color(0.42f, 0.29f, 0.16f)); // 棕

            var props = GameObject.Find("_Props");
            if (props == null) return;

            int fixedTrees = 0;
            foreach (Transform child in props.transform)
            {
                if (!child.name.StartsWith("树")) continue;
                var r = child.GetComponentInChildren<Renderer>();
                if (r == null) continue;

                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i];
                    if (m == null) continue;
                    Color c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor")
                        : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                    if (c.b > c.r + 0.05f) { mats[i] = leaf; changed = true; }   // 青蓝→叶
                    else if (c.r > c.b + 0.05f) { mats[i] = trunk; changed = true; } // 粉红→干
                }
                if (changed) { r.sharedMaterials = mats; fixedTrees++; }
            }
            Debug.Log($"[MinguoAtmosphere] 树色已换（{fixedTrees} 棵）");
        }

        private static Material EnsureColorMat(string path, Color color)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
                var dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                {
                    // Generated 目录由烘焙流程保证存在；这里兜底建一层
                    Debug.LogWarning($"[MinguoAtmosphere] 材质目录不存在: {dir}，请先跑一次烘焙");
                }
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.3f);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
