using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 清理 OasisVRSSample_Renderer（Tuanjie Oasis VRS 示例遗留资产）：
    /// 其引用的 4 个 feature 脚本在本工程不存在，每次导入都会触发
    /// "OasisVRSSample_Renderer is missing RendererFeatures" 报错。
    /// 该 renderer 未参与实际渲染（三个管线资产的默认 renderer 均为 index 0），
    /// 因此安全做法是：从管线资产移除引用 + 删除资产本体。
    /// </summary>
    public static class AiTownFixOasisRenderer
    {
        private const string OasisRendererPath = "Assets/Settings/OasisVRSSample_Renderer.asset";
        private static readonly string[] PipelineAssets =
        {
            "Assets/Settings/PC/PC_High.asset",
            "Assets/Settings/PC/PC_Low.asset",
            "Assets/Settings/Mobile/Mobile_High.asset",
        };

        [MenuItem("Tools/AI Town/Fix Oasis Renderer References")]
        public static void Fix()
        {
            int removed = 0;
            foreach (var path in PipelineAssets)
            {
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (pipeline == null)
                {
                    Debug.LogWarning($"[FixOasisRenderer] 管线资产不存在，跳过: {path}");
                    continue;
                }

                var so = new SerializedObject(pipeline);
                var list = so.FindProperty("m_RendererDataList");
                if (list == null || !list.isArray)
                {
                    Debug.LogWarning($"[FixOasisRenderer] 未找到 m_RendererDataList: {path}");
                    continue;
                }

                // 从后往前删，避免默认索引位移影响前面的元素
                for (int i = list.arraySize - 1; i >= 0; i--)
                {
                    var renderer = list.GetArrayElementAtIndex(i).objectReferenceValue;
                    if (renderer == null) continue; // 缺失引用
                    if (!AssetDatabase.GetAssetPath(renderer).Equals(OasisRendererPath)) continue;

                    list.DeleteArrayElementAtIndex(i);
                    removed++;

                    // 被删元素在默认索引之前时，默认索引需要前移一位
                    var defaultIdx = so.FindProperty("m_DefaultRendererIndex");
                    if (defaultIdx != null && defaultIdx.intValue > i) defaultIdx.intValue--;
                    Debug.Log($"[FixOasisRenderer] {path}: 已移除 Oasis renderer 引用（原索引 {i}）");
                }

                so.ApplyModifiedPropertiesWithoutUndo();
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(OasisRendererPath) != null)
            {
                if (AssetDatabase.DeleteAsset(OasisRendererPath))
                    Debug.Log("[FixOasisRenderer] 已删除 Assets/Settings/OasisVRSSample_Renderer.asset");
                else
                    Debug.LogError("[FixOasisRenderer] 删除 Oasis renderer 资产失败");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[FixOasisRenderer] 完成，共移除 {removed} 处引用");
        }
    }
}
