using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 建筑烘焙：把 StreamingAssets/Buildings/ 下的 JSON 建筑固化进 Main.unity，
    /// 编辑态 Scene 视图即可看到、选中、摆放建筑（Day4 演示场景的基础）。
    /// 烘焙后自动关闭 BuildingManager 的运行时 autoLoad，避免 Play 时重复生成。
    /// 材质按颜色落盘为 Assets/Materials/Generated/ 下的资产，保证场景保存后引用不丢。
    /// </summary>
    public static class AiTownBakeBuildings
    {
        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private const string BuildingsRootName = "_Buildings";
        private const string GeneratedMatsDir = "Assets/Materials/Generated";

        [MenuItem("Tools/AI Town/Bake Buildings To Scene")]
        public static void BakeAll()
        {
            AssetDatabase.Refresh(); // 确保外部改动（贴图/JSON）已导入再开场景
            var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            BakeCurrentScene();
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// batchmode 单次入口：天空盒 + 地面色 + 烘焙建筑一次完成，输出成功标记。
        /// 用法：Tuanjie.exe -batchmode -quit -executeMethod AiTown.EditorTools.AiTownBakeBuildings.RunFixAndBake
        /// </summary>
        public static void RunFixAndBake()
        {
            try
            {
                AssetDatabase.Refresh();
                var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
                AiTownSceneSetup.ApplySkyboxPublic();
                BakeCurrentScene();
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                Debug.Log("AITOWN_FIX_AND_BAKE_OK");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        /// <summary>烘焙到当前已打开的场景（不负责开关场景与保存）。</summary>
        private static void BakeCurrentScene()
        {

            var root = GameObject.Find(BuildingsRootName);
            if (root == null)
            {
                Debug.LogError($"[Bake] 场景中找不到 {BuildingsRootName} 根节点，请先运行 Tools → AI Town → Setup Main Scene");
                return;
            }

            string folder = Path.Combine(Application.streamingAssetsPath, JsonLoader.BuildingsFolder);
            if (!Directory.Exists(folder))
            {
                Debug.LogError($"[Bake] 目录不存在: {folder}");
                return;
            }

            string[] files = Directory.GetFiles(folder, "*.json");
            if (files.Length == 0)
            {
                Debug.LogWarning($"[Bake] {folder} 下没有 JSON 文件，无事可烘焙");
                return;
            }

            EnsureFolders();

            int total = 0;
            foreach (string file in files)
            {
                BuildingData data = JsonLoader.LoadFromJson(File.ReadAllText(file));
                if (data == null || data.blocks == null || data.blocks.Length == 0)
                {
                    Debug.LogWarning($"[Bake] 跳过无效文件: {file}");
                    continue;
                }

                string buildingName = string.IsNullOrEmpty(data.name)
                    ? Path.GetFileNameWithoutExtension(file)
                    : data.name;

                // 同名旧烘焙先清掉，支持修改 JSON 后反复重烘
                Transform old = root.transform.Find(buildingName);
                if (old != null)
                {
                    Object.DestroyImmediate(old.gameObject);
                }

                var buildingRoot = new GameObject(buildingName).transform;
                buildingRoot.SetParent(root.transform, false);

                foreach (BlockData block in data.blocks)
                {
                    ShapeFactory.TryParseColor(block.color, out Color color);
                    Material mat = GetOrCreateMaterialAsset(block.color, color);
                    GameObject obj = ShapeFactory.Create(
                        block.shape,
                        ToVector(block.pos),
                        ToVector(block.size),
                        mat);
                    obj.transform.SetParent(buildingRoot, false);
                    total++;
                }

                Debug.Log($"[Bake] 已烘焙「{buildingName}」{data.blocks.Length} 个方块");
            }

            // 建筑已固化进场景，关闭运行时自动加载避免 Play 时重复生成
            BuildingManager manager = root.GetComponent<BuildingManager>();
            if (manager != null)
            {
                manager.autoLoadBuildingNames = null;
            }

            Debug.Log($"[Bake] 完成：共 {total} 个方块已生成，autoLoad 已关闭");
        }

        /// <summary>同一颜色复用同一个材质资产，避免材质文件数与方块数同阶膨胀。</summary>
        private static Material GetOrCreateMaterialAsset(string hex, Color color)
        {
            string key = string.IsNullOrEmpty(hex) ? "FFFFFF" : hex.TrimStart('#').ToUpperInvariant();
            string path = $"{GeneratedMatsDir}/Block_{key}.mat";

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                AssetDatabase.CreateAsset(mat, path);
            }
            return mat;
        }

        private static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            {
                AssetDatabase.CreateFolder("Assets", "Materials");
            }
            if (!AssetDatabase.IsValidFolder(GeneratedMatsDir))
            {
                AssetDatabase.CreateFolder("Assets/Materials", "Generated");
            }
        }

        private static Vector3 ToVector(float[] v)
        {
            return new Vector3(v[0], v[1], v[2]);
        }
    }
}
