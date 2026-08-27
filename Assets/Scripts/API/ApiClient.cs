using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Python 端 ai_town_server 的 HTTP 客户端。
/// POST /api/generate_json {"description"|"template"} → 建筑构建 JSON。
/// </summary>
public class ApiClient : MonoBehaviour
{
    [Tooltip("Python ai_town_server.py 的地址")]
    public string baseUrl = "http://127.0.0.1:8765";

    [Tooltip("请求超时（秒）")]
    public float timeoutSeconds = 15f;

    public static ApiClient Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    [Serializable]
    private class GenerateRequest
    {
        public string description;
        public string template;
    }

    [Serializable]
    public class GenerateResponse
    {
        public BuildingData building;
        public string error;
    }

    /// <summary>
    /// 自然语言描述生成建筑。onSuccess 收到 BuildingData，onError 收到错误信息。
    /// </summary>
    public IEnumerator GenerateBuilding(string description, Action<BuildingData> onSuccess, Action<string> onError)
    {
        yield return Generate(description, null, onSuccess, onError);
    }

    /// <summary>模板直出模式（秒回，不走 NLP）。</summary>
    public IEnumerator GenerateByTemplate(string template, Action<BuildingData> onSuccess, Action<string> onError)
    {
        yield return Generate(null, template, onSuccess, onError);
    }

    private IEnumerator Generate(string description, string template, Action<BuildingData> onSuccess, Action<string> onError)
    {
        var req = new GenerateRequest { description = description, template = template };
        string json = JsonUtility.ToJson(req);

        using (var request = new UnityWebRequest($"{baseUrl}/api/generate_json", "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = Mathf.CeilToInt(timeoutSeconds);

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"网络错误: {request.error}（确认 ai_town_server.py 已启动）");
                yield break;
            }

            GenerateResponse resp = null;
            try
            {
                resp = JsonUtility.FromJson<GenerateResponse>(request.downloadHandler.text);
            }
            catch (Exception e)
            {
                onError?.Invoke($"响应解析失败: {e.Message}");
                yield break;
            }

            if (resp == null || !string.IsNullOrEmpty(resp.error))
            {
                onError?.Invoke(resp?.error ?? "空响应");
            }
            else if (resp.building == null || resp.building.blocks == null || resp.building.blocks.Length == 0)
            {
                onError?.Invoke("服务返回了空的建筑数据");
            }
            else
            {
                onSuccess?.Invoke(resp.building);
            }
        }
    }
}
