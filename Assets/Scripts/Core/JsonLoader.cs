using System;
using System.IO;
using UnityEngine;

/// <summary>
/// JSON 读取器：从 StreamingAssets/Buildings/ 下读取建筑 JSON 并解析。
/// </summary>
public static class JsonLoader
{
    public const string BuildingsFolder = "Buildings";

    public static string GetPath(string buildingName)
    {
        return Path.Combine(Application.streamingAssetsPath, BuildingsFolder, buildingName + ".json");
    }

    public static BuildingData LoadFromFile(string buildingName)
    {
        string path = GetPath(buildingName);
        if (!File.Exists(path))
        {
            Debug.LogError($"[JsonLoader] 找不到建筑文件: {path}");
            return null;
        }
        string json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    public static BuildingData LoadFromJson(string json)
    {
        try
        {
            return JsonUtility.FromJson<BuildingData>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[JsonLoader] JSON 解析失败: {e.Message}");
            return null;
        }
    }
}
