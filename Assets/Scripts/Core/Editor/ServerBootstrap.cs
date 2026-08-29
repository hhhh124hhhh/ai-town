using System.Diagnostics;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AiTown.EditorTools
{
    /// <summary>
    /// ServerBootstrap——进 Play 前自动拉起 python 后端（server/ai_town_server.py）。
    /// 交接棒约定的"python 服务自动拉起"：InitializeOnLoad 注册 PlayModeStateChanged，
    /// ExitingEditMode 时探测 127.0.0.1:8765，未监听则以独立进程启动服务器
    /// （CreateNoWindow 静默窗口，退出 Unity 不杀子进程，服务器生命周期独立）。
    /// 已有服务器（含手工启动的）则跳过，不重复拉起。
    /// </summary>
    [InitializeOnLoad]
    public static class ServerBootstrap
    {
        private const int Port = 8765;
        private const string Menu = "Tools/AI Town/自动拉起后端服务";
        private const string PrefKey = "AiTown.ServerBootstrap.Enabled";

        private static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        static ServerBootstrap()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        [MenuItem(Menu)]
        private static void Toggle()
        {
            Enabled = !Enabled;
            Debug.Log($"[ServerBootstrap] 自动拉起后端：{(Enabled ? "开" : "关")}（下次进 Play 生效）");
        }

        [MenuItem(Menu, true)]
        private static bool ToggleValidate()
        {
            UnityEditor.Menu.SetChecked(Menu, Enabled);
            return true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode) return;
            if (!Enabled) return;
            if (PortOpen()) return; // 已在运行（手工/上次拉起的），不重复

            string serverDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.dataPath) ?? "", "server");
            string script = System.IO.Path.Combine(serverDir, "ai_town_server.py");
            if (!System.IO.File.Exists(script))
            {
                Debug.LogWarning($"[ServerBootstrap] 未找到 {script}，跳过自动拉起");
                return;
            }

            string python = FindPython();
            if (python == null)
            {
                Debug.LogError("[ServerBootstrap] 未找到 python3/python，请安装或手工运行 server/start_server.command");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{script}\"",
                WorkingDirectory = serverDir,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            Process.Start(psi);

            // 给进程一点绑定端口的时间；起不来不阻塞进 Play（游戏内另有错误提示）
            for (int i = 0; i < 20 && !PortOpen(); i++) System.Threading.Thread.Sleep(100);
            Debug.Log(PortOpen()
                ? $"[ServerBootstrap] 后端已自动拉起（{python}, :{Port}）"
                : $"[ServerBootstrap] 后端启动超时，游戏内会提示；可手工运行 server/start_server.command");
        }

        private static bool PortOpen()
        {
            try
            {
                using var tcp = new TcpClient();
                var ar = tcp.BeginConnect("127.0.0.1", Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(200)) return false;
                tcp.EndConnect(ar);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>macOS 优先 python3（系统自带），Windows 用 py 启动器/python。</summary>
        private static string FindPython()
        {
            foreach (var name in Application.platform == RuntimePlatform.OSXEditor
                         ? new[] { "python3", "python" }
                         : new[] { "python", "python3", "py" })
            {
                foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
                {
                    try
                    {
                        var p = System.IO.Path.Combine(dir.Trim(), name);
                        if (System.IO.File.Exists(p)) return p;
                    }
                    catch { /* PATH 里有坏项，忽略 */ }
                }
            }
            return null;
        }
    }
}
