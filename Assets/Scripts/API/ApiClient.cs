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

    [Tooltip("请求超时（秒）。LLM 类接口（对话/委托/开场白）固定用 65s，不随此值。")]
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

    [Serializable]
    public class NpcChatRequest
    {
        public string name;
        public string message;
    }

    [Serializable]
    public class NpcChatResponse
    {
        public string name;
        public string reply;
        public string error;
    }

    /// <summary>与 NPC 对话。onReply 收到回复文本，onError 收到错误信息。
    /// LLM 生成可能要 60s+，固定 65s 超时（15s 会假失败）。</summary>
    public IEnumerator ChatWithNPC(string npcName, string message, Action<string> onReply, Action<string> onError)
    {
        var req = new NpcChatRequest { name = npcName, message = message };
        string json = JsonUtility.ToJson(req);

        using (var request = new UnityWebRequest($"{baseUrl}/api/npc/chat", "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 65;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"网络错误: {request.error}");
                yield break;
            }

            NpcChatResponse resp = null;
            try
            {
                resp = JsonUtility.FromJson<NpcChatResponse>(request.downloadHandler.text);
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
            else
            {
                onReply?.Invoke(resp.reply);
            }
        }
    }

    /// <summary>
    /// 自然语言描述生成建筑。onSuccess 收到 BuildingData，onError 收到错误信息。
    /// </summary>
    public IEnumerator GenerateBuilding(string description, Action<BuildingData> onSuccess, Action<string> onError)
    {
        yield return Generate(description, null, onSuccess, onError);
    }

    // ── 委托系统（JSON 原样透传，DTO 由 CommissionSystem 解析）──────────

    /// <summary>开场白（LLM 现场生成）。onLine 收到一句话。</summary>
    public IEnumerator GetIntroLine(Action<string> onLine, Action<string> onError)
    {
        using (var request = new UnityWebRequest($"{baseUrl}/api/intro/line", "GET"))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 65;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"网络错误: {request.error}");
                yield break;
            }
            try
            {
                var resp = JsonUtility.FromJson<IntroLineResponse>(request.downloadHandler.text);
                if (resp != null && !string.IsNullOrEmpty(resp.line)) onLine?.Invoke(resp.line);
                else onError?.Invoke("空响应");
            }
            catch (Exception e)
            {
                onError?.Invoke($"解析失败: {e.Message}");
            }
        }
    }

    [Serializable]
    private class IntroLineResponse
    {
        public string line;
    }

    /// <summary>拉取委托进度总览。</summary>
    public IEnumerator GetCommissionState(Action<string> onJson, Action<string> onError)
    {
        using (var request = new UnityWebRequest($"{baseUrl}/api/commission/state", "GET"))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.CeilToInt(timeoutSeconds);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"网络错误: {request.error}");
                yield break;
            }
            onJson?.Invoke(request.downloadHandler.text);
        }
    }

    /// <summary>向 NPC 请求委托。npcPos 为 NPC 世界坐标（服务端用作验收区圆心）。</summary>
    public IEnumerator RequestCommission(string npcName, Vector3 npcPos, Action<string> onJson, Action<string> onError)
    {
        var req = new CommissionNewRequest { npc = npcName, npcPos = new[] { npcPos.x, npcPos.y, npcPos.z } };
        // 发单含 LLM 话术生成，超时放宽
        yield return PostJson("/api/commission/new", JsonUtility.ToJson(req), 65f, onJson, onError);
    }

    /// <summary>提交验收。buildsJson 由 CommissionSystem 组装。</summary>
    public IEnumerator SubmitCommission(string buildsJson, Action<string> onJson, Action<string> onError)
    {
        yield return PostJson("/api/commission/submit", buildsJson, 65f, onJson, onError);
    }

    /// <summary>放弃当前委托。</summary>
    public IEnumerator AbandonCommission(Action<string> onJson, Action<string> onError)
    {
        yield return PostJson("/api/commission/abandon", "{}", timeoutSeconds, onJson, onError);
    }

    private IEnumerator PostJson(string path, string bodyJson, float timeout, Action<string> onJson, Action<string> onError)
    {
        using (var request = new UnityWebRequest(baseUrl + path, "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(bodyJson);
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = Mathf.CeilToInt(timeout);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"网络错误: {request.error}");
                yield break;
            }
            onJson?.Invoke(request.downloadHandler.text);
        }
    }

    [Serializable]
    private class CommissionNewRequest
    {
        public string npc;
        public float[] npcPos;
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
