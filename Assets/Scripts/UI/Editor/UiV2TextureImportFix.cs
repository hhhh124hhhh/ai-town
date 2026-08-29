using UnityEditor;
using UnityEngine;

/// <summary>
/// v2 UI 贴图导入参数批量优化（2026-08-29 用户实测"毛边没融入环境"后建立）：
/// AI 手绘贴图边缘有半透明羽边（QC semi 1~5%），默认 bilinear+无 mipmap 在
/// 9-slice 拉伸/缩放时毛边发虚刺眼。改 trilinear+mipmap 后边缘柔化自然融入。
/// 菜单：Tools → AI Town → Fix UI v2 Texture Import（幂等可重跑）。
/// </summary>
public static class UiV2TextureImportFix
{
    [MenuItem("Tools/AI Town/Fix UI v2 Texture Import")]
    public static void Run()
    {
        const string folder = "Assets/Resources/UI/v2";
        var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
        int changed = 0;
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) continue;

            bool dirty = false;
            // 过滤模式：bilinear（无 mip）→ trilinear（mip 间插值，缩放柔边）
            if (importer.filterMode != FilterMode.Trilinear)
            {
                importer.filterMode = FilterMode.Trilinear;
                dirty = true;
            }
            // mipmap：关闭 → 开启（缩小采样时用低分辨率级，毛边不闪）
            if (!importer.mipmapEnabled)
            {
                importer.mipmapEnabled = true;
                dirty = true;
            }
            // 边缘扩展：9-slice 边框拉伸时 clamp 而非 repeat（防边缘采样串色）
            if (importer.wrapMode != TextureWrapMode.Clamp)
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                dirty = true;
            }
            // 压缩质量：UI 贴图用高质量（默认压缩会放大羽边色带）
            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                dirty = true;
            }
            if (dirty)
            {
                importer.SaveAndReimport();
                changed++;
                Debug.Log($"[UI v2 Import] fixed {path}");
            }
        }
        Debug.Log($"[UI v2 Import] done, {changed} textures reimported");
    }
}
