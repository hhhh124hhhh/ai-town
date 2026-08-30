using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// CLI 构建入口：Tuanjie.exe -batchmode -executeMethod AiTownBuild.BuildWindows64
/// 产物输出到项目根 Builds/AITown/；构建前把 server 目录与便携 python 同步进
/// StreamingAssets（源=项目根 server/，单次真源，避免两处手工同步漂移）。
/// </summary>
public static class AiTownBuild
{
    private const string OutputDir = "Builds/AITown";
    private const string ExeName = "AITown.exe";

    [MenuItem("Tools/AI Town/Build Windows64 (Batch)")]
    public static void BuildFromMenu() => BuildWindows64();

    public static void BuildWindows64()
    {
        SyncServerToStreamingAssets();

        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();
        if (scenes.Length == 0)
        {
            Debug.LogError("[AiTownBuild] Build Settings 没有启用场景");
            if (Application.isBatchMode) EditorApplication.Exit(2);
            return;
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine(OutputDir, ExeName),
            target = BuildTarget.StandaloneWindows64,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"[AiTownBuild] 构建成功：{Path.GetFullPath(options.locationPathName)}" +
                      $"（{report.summary.totalSize / 1024 / 1024} MB，{report.summary.totalErrors} 错误）");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError($"[AiTownBuild] 构建失败：{report.summary.result}，{report.summary.totalErrors} 错误");
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    /// <summary>项目根 server/ → Assets/StreamingAssets/server/（含便携 python/）。
    /// 排除运行时产物（state.json / 日志 / __pycache__），构建时确保进度从零开始。</summary>
    private static void SyncServerToStreamingAssets()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string src = Path.Combine(projectRoot, "server");
        string dst = Path.Combine(Application.dataPath, "StreamingAssets", "server");
        if (!Directory.Exists(src))
        {
            Debug.LogWarning($"[AiTownBuild] 未找到 {src}，跳过 server 同步（打包版走离线回退）");
            return;
        }

        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            string rel = Path.GetRelativePath(src, file);
            // 运行时产物不入包；venv/缓存目录跳过（便携 python 在 python/ 子目录，保留）
            if (name == "state.json" || name.EndsWith(".log") || rel.Contains("__pycache__")
                || rel.Contains(".venv")) continue;
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
        }
        Debug.Log($"[AiTownBuild] server 同步完成 → StreamingAssets/server（{Directory.GetFiles(dst, "*", SearchOption.AllDirectories).Length} 文件）");
    }
}
