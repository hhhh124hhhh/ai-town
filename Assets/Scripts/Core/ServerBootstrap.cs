using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// AI 后端自动拉起：游戏启动（BeforeSceneLoad）探活 127.0.0.1:8765，没有服务就静默启动
/// server/ai_town_server.py（隐藏窗口、幂等——后端已在运行则直接复用）。启动失败只打日志，
/// 游戏按既有离线回退运行，绝不阻塞进场景。
/// 编辑器与打包版通用：server 目录按 dataPath 相对位置探测（打包需把 server 目录放到
/// exe 旁，或随 StreamingAssets/server 下发）；python 依次试 server/python（便携版）、PATH。
/// 后端进程不随游戏退出而关闭（孤儿进程），下次启动端口命中即复用。
/// </summary>
public static class ServerBootstrap
{
    private const int Port = 8765;
    private const string Marker = "[ServerBootstrap]";
    private const string ScriptName = "ai_town_server.py";
    private const float BootTimeoutSeconds = 15f; // 拉起进程后等端口开的最长时间

    public enum BackendState { Starting, Ready, Unavailable }

    /// <summary>后端当前状态（后台线程写、主线程读，枚举赋值原子）。</summary>
    public static BackendState State { get; private set; } = BackendState.Starting;

    public static bool IsReady => State == BackendState.Ready;

    private static int _kicked; // 0=未触发 1=已触发，防止多处调用重复拉起

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void OnRuntimeInit()
    {
        EnsureStarted();
    }

    /// <summary>幂等触发：首次调用起一个后台线程做「探活 → 拉起 → 等就绪」。</summary>
    public static void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _kicked, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ => Run());
    }

    /// <summary>
    /// 协程：等后端就绪（开场白等启动期请求用）。就绪或超时/不可用即返回，
    /// 调用方按结果走在线/离线分支。等待期 UI 层正好用「正在构思…」文案吸收。
    /// </summary>
    public static IEnumerator WaitReady(float timeoutSeconds = 6f)
    {
        EnsureStarted();
        float waited = 0f;
        while (!IsReady && waited < timeoutSeconds)
        {
            // 已判定拉不起来（找不到 python/server）就不傻等，留 1.5s 给探活线程收尾
            if (State == BackendState.Unavailable && waited > 1.5f) yield break;
            yield return null;
            waited += Time.unscaledDeltaTime;
        }
    }

    private static void Run()
    {
        try
        {
            if (IsPortOpen())
            {
                State = BackendState.Ready;
                Debug.Log($"{Marker} AI 后端已在运行（127.0.0.1:{Port}），复用现有进程");
                return;
            }

            string serverDir = FindServerDir();
            if (serverDir == null)
            {
                State = BackendState.Unavailable;
                Debug.LogWarning($"{Marker} 未找到 {ScriptName}（候选：dataPath 相对 ../server、StreamingAssets/server），AI 功能走离线回退");
                return;
            }

            if (!TryStartServer(serverDir, out string python))
            {
                State = BackendState.Unavailable;
                Debug.LogWarning($"{Marker} 未找到可用的 python（试过 server/python、PATH python/python3/py），AI 功能走离线回退");
                return;
            }

            Debug.Log($"{Marker} 已拉起 AI 后端：{python} {ScriptName}（{serverDir}），等待端口 {Port} …");
            float waited = 0f;
            while (waited < BootTimeoutSeconds)
            {
                Thread.Sleep(400);
                waited += 0.4f;
                if (IsPortOpen())
                {
                    State = BackendState.Ready;
                    Debug.Log($"{Marker} AI 后端就绪（耗时 {waited:0.0}s）：http://127.0.0.1:{Port}");
                    return;
                }
            }

            State = BackendState.Unavailable;
            Debug.LogWarning($"{Marker} 等待 {BootTimeoutSeconds:0}s 端口仍未开，判定启动失败（可手动跑 server/start_server.bat 看报错）");
        }
        catch (Exception e)
        {
            State = BackendState.Unavailable;
            Debug.LogWarning($"{Marker} 异常：{e.Message}");
        }
    }

    /// <summary>静默启动：CreateNoWindow 不弹黑框；不重定向输出即无管道缓冲死锁。</summary>
    private static bool TryStartServer(string serverDir, out string pythonUsed)
    {
        foreach (string python in PythonCandidates(serverDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = ScriptName,
                    WorkingDirectory = serverDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                pythonUsed = python;
                return true;
            }
            catch
            {
                // 该候选不存在/不可用，试下一个
            }
        }
        pythonUsed = null;
        return false;
    }

    private static string[] PythonCandidates(string serverDir)
    {
        return new[]
        {
            // 打包发行：把便携 python 解到 server/python/ 即随游戏自带
            Path.Combine(serverDir, "python", "python.exe"),
            Path.Combine(serverDir, ".venv", "Scripts", "python.exe"),
            "python",  // PATH（开发机常规路径）
            "python3",
            "py",      // Windows 官方启动器兜底
        };
    }

    /// <summary>server 目录探测：编辑器=Assets 同级 server/（2026-08-29 server 并入
    /// 仓库内）+ 旧布局（Assets 上两级，兼容旧检出版本）；打包=exe 旁或 StreamingAssets 内。
    /// 打包版 dataPath=xxx_Data → "Data/StreamingAssets/server"（2026-08-30 实测修正：
    /// 旧 ".." 候选解出的是 exe 旁 StreamingAssets，多退一层，永不命中）。</summary>
    private static string FindServerDir()
    {
        string dataPath = UnityEngine.Application.dataPath;
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(dataPath, "server")),                          // 编辑器：Assets/server
            Path.GetFullPath(Path.Combine(dataPath, "..", "server")),                    // 编辑器：项目根/server
            Path.GetFullPath(Path.Combine(dataPath, "..", "..", "server")),              // 旧：工作区根/server
            Path.GetFullPath(Path.Combine(dataPath, "StreamingAssets", "server")),       // 打包：Data/StreamingAssets/server
            Path.GetFullPath(Path.Combine(dataPath, "..", "StreamingAssets", "server")), // 旧候选保留（exe 旁变体）
            Path.GetFullPath(Path.Combine(dataPath, "..", "..", "..", "server")),        // 旧：兼容更深层级
        };
        foreach (string dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, ScriptName))) return dir;
        }
        return null;
    }

    /// <summary>TCP 探活（端口有监听即视为后端在跑，本项目独占 8765）。</summary>
    private static bool IsPortOpen()
    {
        try
        {
            using (var client = new TcpClient())
            {
                IAsyncResult async = client.BeginConnect("127.0.0.1", Port, null, null);
                if (!async.AsyncWaitHandle.WaitOne(400)) return false;
                client.EndConnect(async);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }
}
