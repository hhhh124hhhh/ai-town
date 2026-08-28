using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 黄昏灯光一键搭建（幂等可重跑）：
    /// ①主方向光压橙+低角度夕阳（保留现有灯，只改参数）
    /// ②路灯灯头暖黄点光（从 _Props/路灯_* 自动生成，灯头高度≈4m）
    /// ③篝火闪烁光（AnimateLight 脚本驱动）
    /// ④天空盒 Exposure 压暗+雾色调暖（黄昏青灰→暖灰橙）
    /// ⑤HighQualityVolumeProfile 补 ColorAdjustments（色温+15 偏暖、饱和+8）并提 Bloom
    /// 场景改动由本菜单保存（manage_scene save 兜底），材质/Profile 改动 SetDirty+SaveAssets。
    /// </summary>
    public static class AiTownDuskLighting
    {
        private const string ReportTag = "[DuskLighting]";
        private const string VolumeProfilePath = "Assets/Settings/VolumeProfiles/HighQualityVolumeProfile.asset";

        // 夕阳主光：低角度暖橙
        private static readonly Vector3 SunEuler = new Vector3(12f, 215f, 0f);
        private static readonly Color SunColor = new Color(1.00f, 0.72f, 0.48f);
        private const float SunIntensity = 1.35f;

        // 路灯点光：暖黄钠灯
        private static readonly Color LampColor = new Color(1.00f, 0.80f, 0.52f);
        private const float LampIntensity = 2.6f;
        private const float LampRange = 9f;

        // 黄昏雾：暖灰橙（替代白天青灰）
        private static readonly Color DuskFog = new Color(0.83f, 0.70f, 0.58f);

        [MenuItem("Tools/AI Town/Apply Dusk Lighting")]
        public static void Apply()
        {
            int n = 0;
            n += ApplySun();
            n += ApplyLampLights();
            n += ApplyBonfireLight();
            ApplySkyAndFog();
            ApplyVolumeProfile();

            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (scene.isDirty) UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            Debug.Log($"{ReportTag} 黄昏灯光已应用：{n} 盏灯 + 天空/雾 + Volume（场景已保存）");
        }

        private static int ApplySun()
        {
            var go = GameObject.Find("Directional Light");
            if (go == null) return 0;
            var light = go.GetComponent<Light>();
            if (light == null) return 0;
            go.transform.eulerAngles = SunEuler;
            light.color = SunColor;
            light.intensity = SunIntensity;
            light.shadows = LightShadows.Soft; // 阴影距离走 Quality 设置，这里只保证软影
            return 1;
        }

        private static int ApplyLampLights()
        {
            var props = GameObject.Find("_Props");
            if (props == null) return 0;
            int count = 0;
            foreach (Transform child in props.transform)
            {
                if (!child.name.StartsWith("路灯_")) continue;
                string lightName = "LampLight_" + child.name;
                var existing = child.Find(lightName);
                if (existing != null) { Object.DestroyImmediate(existing.gameObject); }

                var go = new GameObject(lightName);
                go.transform.SetParent(child, false);
                go.transform.localPosition = new Vector3(0f, 4.0f, 0f); // 灯头高度
                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = LampColor;
                light.intensity = LampIntensity;
                light.range = LampRange;
                light.shadows = LightShadows.None; // 9 盏点光不开影，保帧率
                count++;
            }
            return count;
        }

        private static int ApplyBonfireLight()
        {
            var props = GameObject.Find("_Props");
            if (props == null) return 0;
            Transform bonfire = null;
            foreach (Transform child in props.transform)
                if (child.name.StartsWith("篝火_")) { bonfire = child; break; }
            if (bonfire == null) return 0;

            string lightName = "BonfireLight";
            var existing = bonfire.Find(lightName);
            if (existing != null) Object.DestroyImmediate(existing.gameObject);

            var go = new GameObject(lightName);
            go.transform.SetParent(bonfire, false);
            go.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1.00f, 0.62f, 0.30f);
            light.intensity = 3.2f;
            light.range = 10f;
            light.shadows = LightShadows.None;
            go.AddComponent<AnimateLight>(); // 篝火闪烁
            return 1;
        }

        private static void ApplySkyAndFog()
        {
            // 天空盒压暗降曝，云层出黄昏金边
            if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Exposure"))
                RenderSettings.skybox.SetFloat("_Exposure", 0.72f);
            EditorUtility.SetDirty(RenderSettings.skybox);

            RenderSettings.fogColor = DuskFog;
        }

        private static void ApplyVolumeProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            if (profile == null)
            {
                Debug.LogWarning($"{ReportTag} 缺少 {VolumeProfilePath}，跳过 Volume 调整");
                return;
            }

            // Bloom 提到 0.45/阈值 0.85：路灯招牌出光晕，白天画面几乎不受影响
            foreach (var c in profile.components)
            {
                if (c.name == "Bloom")
                {
                    var bloom = (Bloom)c;
                    bloom.intensity.Override(0.45f);
                    bloom.threshold.Override(0.85f);
                    EditorUtility.SetDirty(c);
                }
                else if (c.name == "Tonemapping")
                {
                    var tm = (Tonemapping)c;
                    tm.mode.Override(TonemappingMode.ACES);
                    EditorUtility.SetDirty(c);
                }
            }

            // ColorAdjustments：模板 LowQuality 有、HighQuality 没有——补一份（黄昏色温+饱和）
            ColorAdjustments ca = null;
            foreach (var c in profile.components)
                if (c is ColorAdjustments found) { ca = found; break; }
            if (ca == null)
            {
                ca = profile.Add<ColorAdjustments>();
                ca.name = "ColorAdjustments";
            }
            ca.postExposure.Override(0.1f);
            ca.colorFilter.Override(new Color(1.02f, 0.94f, 0.86f));
            ca.saturation.Override(8f);
            ca.contrast.Override(6f);
            EditorUtility.SetDirty(ca);
            EditorUtility.SetDirty(profile);
        }
    }
}
