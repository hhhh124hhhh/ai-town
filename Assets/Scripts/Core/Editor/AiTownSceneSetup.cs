using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 场景搭建：一键生成 Main.unity（地面/光照/玩家三件套/建筑管理器）。
    /// 菜单 Tools → AI Town → Setup Main Scene，或 batchmode -executeMethod AiTownEditorSetup.RunAll。
    /// </summary>
    public static class AiTownSceneSetup
    {
        private const string ScenesDir = "Assets/Scenes";
        private const string MainScenePath = ScenesDir + "/Main.unity";
        private const string GroundMatPath = "Assets/Materials/Ground_Mat.mat";
        private const string SkyboxMatPath = "Assets/Materials/Skybox_Mat.mat";

        private const string PlayerCapsulePrefab = "Assets/SharedAssets/FirstPersonController/Prefabs/PlayerCapsule.prefab";
        private const string PlayerFollowCameraPrefab = "Assets/SharedAssets/FirstPersonController/Prefabs/PlayerFollowCamera.prefab";
        private const string MainCameraPrefab = "Assets/SharedAssets/FirstPersonController/Prefabs/MainCamera.prefab";

        [MenuItem("Tools/AI Town/Setup Main Scene")]
        public static void SetupMainSceneFromMenu()
        {
            SetupMainScene();
        }

        /// <summary>
        /// 增量修复：给已存在的 Main.unity 补天空盒 + 加深地面颜色，不重建场景。
        /// 修复"画面一片浅灰、地面看不见、城堡像悬空"的问题——根因是 urp-sample 模板
        /// 项目下默认天空盒引用渲染为纯灰背景，且浅灰地面与背景同色无法区分。
        /// </summary>
        [MenuItem("Tools/AI Town/Fix Ground & Skybox")]
        public static void FixGroundAndSkybox()
        {
            AssetDatabase.Refresh(); // 外部刚放入的草地贴图先导入，再取材质
            var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            ApplySkybox();
            FixGroundMaterial();
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[AiTownSceneSetup] 已修复天空盒与地面颜色（Fix Ground & Skybox）");
        }

        /// <summary>创建/加载 Procedural 渐变天空盒并应用到当前场景 RenderSettings。</summary>
        private static void ApplySkybox()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyboxMatPath);
            if (mat == null)
            {
                var shader = Shader.Find("Skybox/Procedural");
                if (shader == null)
                {
                    throw new InvalidOperationException("找不到 Skybox/Procedural 着色器");
                }
                mat = new Material(shader);
                if (mat.HasProperty("_AtmosphereThickness")) mat.SetFloat("_AtmosphereThickness", 1.0f);
                if (mat.HasProperty("_SkyTint")) mat.SetColor("_SkyTint", new Color(0.35f, 0.55f, 0.85f));
                if (mat.HasProperty("_Exposure")) mat.SetFloat("_Exposure", 1.05f);
                AssetDatabase.CreateAsset(mat, SkyboxMatPath);
            }
            RenderSettings.skybox = mat;
        }

        /// <summary>地面：应用草地贴图材质（AI 生成的无缝地形纹理），无贴图时退回草绿纯色。</summary>
        private static void FixGroundMaterial()
        {
            const string grassTexPath = "Assets/Textures/Ground/grass_albedo.png";
            Texture2D grass = AssetDatabase.LoadAssetAtPath<Texture2D>(grassTexPath);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(GroundMatPath);
            if (mat == null) return;

            if (grass != null)
            {
                if (mat.HasProperty("_BaseMap"))
                {
                    mat.SetTexture("_BaseMap", grass);
                    mat.SetTextureScale("_BaseMap", new Vector2(50f, 50f)); // 1000m 地面 / 20m 一格
                }
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.1f);
            }
            else
            {
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.38f, 0.55f, 0.28f));
            }
            EditorUtility.SetDirty(mat);
        }

        /// <summary>batchmode/跨类公共入口：天空盒 + 地面色修复（作用于当前打开的场景）。</summary>
        public static void ApplySkyboxPublic()
        {
            ApplySkybox();
            FixGroundMaterial();
        }

        /// <summary>batchmode 单次入口：编译后执行搭建并输出成功标记。</summary>
        public static void RunAll()
        {
            try
            {
                AssetDatabase.Refresh();
                SetupMainScene();
                Debug.Log("AITOWN_SETUP_OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        public static void SetupMainScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateDirectionalLight();
            CreateGround();
            ApplySkybox();
            GameObject player = CreatePlayer();
            CreateBuildingManager(player);

            if (!AssetDatabase.IsValidFolder(ScenesDir))
            {
                AssetDatabase.CreateFolder("Assets", "Scenes");
            }
            EditorSceneManager.SaveScene(scene, MainScenePath);
            AddToBuildSettings(MainScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[AiTownSceneSetup] 已生成 {MainScenePath} 并加入 Build Settings 首位");
        }

        private static void CreateDirectionalLight()
        {
            var go = new GameObject("Directional Light");
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            light.shadows = LightShadows.Soft;
        }

        private static void CreateGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(100f, 1f, 100f); // 1000m × 1000m 演示场地

            var mat = AssetDatabase.LoadAssetAtPath<Material>(GroundMatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = new Color(0.38f, 0.55f, 0.28f); // 草绿，与天空背景明确区分
                AssetDatabase.CreateAsset(mat, GroundMatPath);
            }
            ground.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static GameObject CreatePlayer()
        {
            // Starter Assets 三件套：胶囊玩家 + 跟随 vcam + 带 Brain 的主相机
            var player = InstantiatePrefab(PlayerCapsulePrefab, new Vector3(0f, 2f, -10f), Quaternion.identity);
            player.name = "Player";

            var cameraTarget = player.transform.Find("PlayerCameraRoot");
            if (cameraTarget == null)
            {
                throw new InvalidOperationException("PlayerCapsule 中找不到 PlayerCameraRoot");
            }

            var vcam = InstantiatePrefab(PlayerFollowCameraPrefab, new Vector3(0f, 3.4f, -11.5f), Quaternion.identity);
            vcam.name = "PlayerFollowCamera";
            var virtualCam = vcam.GetComponentInChildren<Cinemachine.CinemachineVirtualCameraBase>();
            if (virtualCam != null)
            {
                virtualCam.Follow = cameraTarget;
            }

            // 初始位姿与 vcam 目标一致（玩家头部后上方），避免编辑态相机落在胶囊内部导致视野全白
            InstantiatePrefab(MainCameraPrefab, new Vector3(0f, 3.375f, -10.5f), Quaternion.identity);

            // 飞行模式挂在玩家上（按 F 切换）
            player.AddComponent<FlyMode>();
            return player;
        }

        private static void CreateBuildingManager(GameObject player)
        {
            var buildings = new GameObject("_Buildings");
            var manager = buildings.AddComponent<BuildingManager>();
            manager.autoLoadBuildingNames = new[] { "castle" }; // Play 即见测试城堡

            // 记录出生点，便于 Day2 之后扩展
            var spawn = new GameObject("_SpawnPoint");
            spawn.transform.position = new Vector3(0f, 2f, -10f);
            spawn.transform.rotation = Quaternion.LookRotation(Vector3.forward);
        }

        private static GameObject InstantiatePrefab(string path, Vector3 position, Quaternion rotation)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                throw new InvalidOperationException($"预制体缺失: {path}");
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = position;
            instance.transform.rotation = rotation;
            return instance;
        }

        private static void AddToBuildSettings(string scenePath)
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == scenePath);
            list.Insert(0, new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}
