using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 护城河一键搭建：环形河床 + 半透明水面（UV 滚动波纹）+ 内外石堤 + 南面石桥 + 连接石径 + 水花。
    /// 全部叠在地面之上（河床 0.02 / 水面 0.10 / 堤顶 0.22），不改动 Ground 网格，幂等可重跑。
    /// 水块统一 8×8m 对齐网格，保证共享材质 tiling 下纹理周期无缝衔接。
    /// 可选依赖：Assets/Resources/Textures/Water/water_albedo.png + water_normal.png（缺省纯色回退）、
    /// Splash(Water).prefab（缺省跳过水花）。
    /// </summary>
    public static class AiTownRiverSetup
    {
        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private const string AlbedoTexPath = "Assets/Resources/Textures/Water/water_albedo.png";
        private const string NormalTexPath = "Assets/Resources/Textures/Water/water_normal.png";
        private const string WaterMatPath = "Assets/Materials/Water_Mat.mat";
        private const string WaterbedMatPath = "Assets/Materials/Waterbed_Mat.mat";
        private const string DikeMatPath = "Assets/Materials/StoneDike_Mat.mat";
        private const string RoadMatPath = "Assets/Materials/StoneRoad_Mat.mat";
        private const string SplashPrefabPath = "Assets/TJGeneratorLibEffects/Prefabs/Combat/Explosions (Text)/Splash(Water).prefab";

        // 河带参数：水面块 8×8（Plane 原生 10×10 → scale 0.8）
        private const float BlockScale = 0.8f;
        private const float BedY = 0.02f;
        private const float WaterY = 0.10f;
        private static readonly float[] LongBandX = { -22f, -14f, -6f, 2f, 10f, 18f, 26f }; // 南北长带 7 块（x: -26..30）
        private static readonly float[] ShortBandZ = { -18f, -10f, -2f, 6f, 14f, 22f };     // 东西短带 6 块（z: -22..26）
        private const float ZNorth = 30f;
        private const float ZSouth = -26f;
        private const float XWest = -22f;
        private const float XEast = 26f;

        [MenuItem("Tools/AI Town/Add River")]
        public static void ApplyFromMenu()
        {
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            BuildRiver();
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[AiTownRiverSetup] 护城河已搭建（河床/水面/石堤/石桥/石径/水花）");
        }

        /// <summary>作用于当前打开的场景（供其他流程复用）。</summary>
        public static void BuildRiver()
        {
            EnsureNormalImportSettings();
            Material water = EnsureWaterMat();
            Material bed = EnsureSimpleMat(WaterbedMatPath, new Color(0.13f, 0.19f, 0.19f), 0.05f);
            Material dike = EnsureSimpleMat(DikeMatPath, new Color(0.49f, 0.52f, 0.50f), 0.15f);

            var root = GameObject.Find("_River");
            if (root != null) Object.DestroyImmediate(root);
            root = new GameObject("_River");
            var bedRoot = new GameObject("Riverbed");
            bedRoot.transform.SetParent(root.transform, false);
            var waterRoot = new GameObject("Water");
            waterRoot.transform.SetParent(root.transform, false);
            var dikeRoot = new GameObject("Dikes");
            dikeRoot.transform.SetParent(root.transform, false);

            // 河床：深色面盖住草地（去碰撞，射线仍落到 Ground）
            // 水面：半透明 + WaterScroll 滚动
            foreach (float x in LongBandX)
            {
                CreateQuad(bedRoot.transform, $"Bed_N_{x}", new Vector3(x, BedY, ZNorth), bed);
                CreateQuad(bedRoot.transform, $"Bed_S_{x}", new Vector3(x, BedY, ZSouth), bed);
                CreateWaterBlock(waterRoot.transform, $"Water_N_{x}", new Vector3(x, WaterY, ZNorth), water);
                CreateWaterBlock(waterRoot.transform, $"Water_S_{x}", new Vector3(x, WaterY, ZSouth), water);
            }
            foreach (float z in ShortBandZ)
            {
                CreateQuad(bedRoot.transform, $"Bed_W_{z}", new Vector3(XWest, BedY, z), bed);
                CreateQuad(bedRoot.transform, $"Bed_E_{z}", new Vector3(XEast, BedY, z), bed);
                CreateWaterBlock(waterRoot.transform, $"Water_W_{z}", new Vector3(XWest, WaterY, z), water);
                CreateWaterBlock(waterRoot.transform, $"Water_E_{z}", new Vector3(XEast, WaterY, z), water);
            }

            // 石堤：环带内外各一圈（8 条），挡住水面边缘
            CreateBox(dikeRoot.transform, "Dike_N_in", new Vector3(2f, 0.11f, 26f), new Vector3(56f, 0.22f, 0.5f), dike);
            CreateBox(dikeRoot.transform, "Dike_N_out", new Vector3(2f, 0.11f, 34f), new Vector3(56f, 0.22f, 0.5f), dike);
            CreateBox(dikeRoot.transform, "Dike_S_in", new Vector3(2f, 0.11f, -22f), new Vector3(56f, 0.22f, 0.5f), dike);
            CreateBox(dikeRoot.transform, "Dike_S_out", new Vector3(2f, 0.11f, -30f), new Vector3(56f, 0.22f, 0.5f), dike);
            CreateBox(dikeRoot.transform, "Dike_W_in", new Vector3(-18f, 0.11f, 2f), new Vector3(0.5f, 0.22f, 48f), dike);
            CreateBox(dikeRoot.transform, "Dike_W_out", new Vector3(-26f, 0.11f, 2f), new Vector3(0.5f, 0.22f, 48f), dike);
            CreateBox(dikeRoot.transform, "Dike_E_in", new Vector3(22f, 0.11f, 2f), new Vector3(0.5f, 0.22f, 48f), dike);
            CreateBox(dikeRoot.transform, "Dike_E_out", new Vector3(30f, 0.11f, 2f), new Vector3(0.5f, 0.22f, 48f), dike);

            BuildBridge(root.transform, dike);
            BuildPath(root.transform);
            PlaceSplash(root.transform);

            Debug.Log("[AiTownRiverSetup] 河道节点构建完成：水面 26 块 / 石堤 8 条 / 石桥 1 座");
        }

        /// <summary>石桥：跨南带正对出生点动线（x=0, z -32..-20），桥面顶与堤顶齐平无台阶。</summary>
        private static void BuildBridge(Transform parent, Material dike)
        {
            var bridge = new GameObject("Bridge");
            bridge.transform.SetParent(parent, false);
            CreateBox(bridge.transform, "Deck", new Vector3(0f, 0.16f, -26f), new Vector3(3f, 0.12f, 12f), dike);
            CreateBox(bridge.transform, "Rail_L", new Vector3(-1.44f, 0.395f, -26f), new Vector3(0.12f, 0.35f, 12f), dike);
            CreateBox(bridge.transform, "Rail_R", new Vector3(1.44f, 0.395f, -26f), new Vector3(0.12f, 0.35f, 12f), dike);
            foreach (float x in new[] { -1.44f, 1.44f })
            {
                foreach (float z in new[] { -20.3f, -31.7f })
                {
                    CreateBox(bridge.transform, "Post", new Vector3(x, 0.475f, z), new Vector3(0.3f, 0.55f, 0.3f), dike);
                }
            }
        }

        /// <summary>石径：桥南端接主路北端（z -20..-12 的 8m 草地缺口），复用青石板材质。</summary>
        private static void BuildPath(Transform parent)
        {
            var roadMat = AssetDatabase.LoadAssetAtPath<Material>(RoadMatPath);
            if (roadMat == null)
            {
                Debug.LogWarning("[AiTownRiverSetup] 缺少 StoneRoad_Mat，跳过石径");
                return;
            }
            var path = new GameObject("Path_To_Road");
            path.transform.SetParent(parent, false);
            CreateQuad(path.transform, "Path", new Vector3(0f, 0.02f, -16f), roadMat);
        }

        /// <summary>水花：特效 prefab 存在则放桥边水面一处。</summary>
        private static void PlaceSplash(Transform parent)
        {
            var splash = AssetDatabase.LoadAssetAtPath<GameObject>(SplashPrefabPath);
            if (splash == null)
            {
                Debug.LogWarning($"[AiTownRiverSetup] 缺少水花特效 {SplashPrefabPath}，跳过");
                return;
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(splash);
            go.name = "Splash_Bridge";
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(1.6f, WaterY, -24.5f);
        }

        private static void CreateWaterBlock(Transform parent, string name, Vector3 pos, Material mat)
        {
            var go = CreateQuad(parent, name, pos, mat);
            go.AddComponent<WaterScroll>();
        }

        private static GameObject CreateQuad(Transform parent, string name, Vector3 pos, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = new Vector3(BlockScale, 1f, BlockScale);
            var collider = go.GetComponent<MeshCollider>();
            if (collider != null) Object.DestroyImmediate(collider);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        private static void CreateBox(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
        }

        /// <summary>法线贴图导入类型修正（生成 png 默认按 Default 导入，法线必须 NormalMap 类型）。</summary>
        private static void EnsureNormalImportSettings()
        {
            var imp = AssetDatabase.LoadAssetAtPath<TextureImporter>(NormalTexPath);
            if (imp != null && imp.textureType != TextureImporterType.NormalMap)
            {
                imp.textureType = TextureImporterType.NormalMap;
                imp.SaveAndReimport();
            }
        }

        private static Material EnsureWaterMat()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, WaterMatPath);
            }

            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(AlbedoTexPath);
            if (albedo != null)
            {
                mat.SetTexture("_BaseMap", albedo);
                mat.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.78f));
                mat.SetTextureScale("_BaseMap", new Vector2(2f, 2f)); // 8m 块 ×2 = 纹理 4m 一周期
            }
            else
            {
                mat.SetColor("_BaseColor", new Color(0.22f, 0.36f, 0.38f, 0.82f));
                Debug.LogWarning("[AiTownRiverSetup] 缺少 water_albedo.png，水面用纯色回退（生成贴图后重跑本菜单）");
            }

            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(NormalTexPath);
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetTextureScale("_BumpMap", new Vector2(2f, 2f));
            }

            // URP Lit 透明面设置（alpha 混合，不写深度，队列 Transparent）
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)RenderQueue.Transparent;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.72f);

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material EnsureSimpleMat(string path, Color color, float smoothness)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
