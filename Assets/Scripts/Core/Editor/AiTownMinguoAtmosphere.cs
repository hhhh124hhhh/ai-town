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
        private const string StoneMatPath = "Assets/Materials/StoneRoad_Mat.mat";
        private const string GroundMatPath = "Assets/Materials/Ground_Mat.mat";
        private const string LeafMatPath = "Assets/Materials/Generated/Mat_TreeLeaf_Minguo.mat";
        private const string TrunkMatPath = "Assets/Materials/Generated/Mat_TreeTrunk_Minguo.mat";

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
            RenderSettings.fogStartDistance = 25f;
            RenderSettings.fogEndDistance = 110f;
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
