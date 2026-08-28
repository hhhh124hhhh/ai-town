using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AiTown.EditorTools
{
    /// <summary>
    /// 分区表：10×10m 网格 cell → 功能区归属（唯一空间真源）。
    /// 数据源 .codely/zones.csv（随场景演进手改+复核）。
    /// placement lint 查此表判定"物体该不该在",替代硬编码豁免前缀。
    /// </summary>
    public static class ZoneMap
    {
        private static Dictionary<(int, int), string> _cells;

        /// <summary>世界坐标 → 功能区。未登记的 cell 返回 "unmapped"。</summary>
        public static string ZoneOf(float worldX, float worldZ)
        {
            EnsureLoaded();
            var key = ((int)MathF.Floor(worldX / 10f), (int)MathF.Floor(worldZ / 10f));
            return _cells.TryGetValue(key, out var z) ? z : "unmapped";
        }

        /// <summary>是否市集/广场类（家具+灯合法区）。</summary>
        public static bool IsMarketLike(string zone)
            => zone == "core_plaza" || zone == "market_east" || zone == "qilou_street" || zone == "ns_street" || zone == "west_lane";

        /// <summary>是否荒野类（树合法、市集家具违规）。</summary>
        public static bool IsOutskirt(string zone) => zone == "south_outskirt";

        public static string CsvPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".codely", "zones.csv"));

        private static void EnsureLoaded()
        {
            if (_cells != null) return;
            _cells = new Dictionary<(int, int), string>();
            if (!File.Exists(CsvPath))
            {
                Debug.LogWarning($"[ZoneMap] 分区表不存在: {CsvPath}（全部按 unmapped 处理）");
                return;
            }
            foreach (var line in File.ReadAllLines(CsvPath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("cell_x")) continue;
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (int.TryParse(parts[0], out var cx) && int.TryParse(parts[1], out var cz))
                    _cells[(cx, cz)] = parts[2].Trim();
            }
        }

        /// <summary>调试：打印每棵树/家具的归属（Tools → AI Town → Dump Zone Map）。</summary>
        [MenuItem("Tools/AI Town/Dump Zone Map")]
        public static void Dump()
        {
            EnsureLoaded();
            var sb = new StringBuilder("[ZoneMap] ");
            var props = GameObject.Find("_Props");
            if (props == null) { Debug.Log(sb.Append("无 _Props")); return; }
            foreach (Transform t in props.transform)
            {
                sb.Append($"\n  {t.name} @({t.position.x:0.0},{t.position.z:0.0}) → {ZoneOf(t.position.x, t.position.z)}");
            }
            Debug.Log(sb.ToString());
        }
    }
}
