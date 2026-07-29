// @author bdth 2074055628@qq.com
// 文件用途 管理 Defender 扫描排除目录 只增删本程序自己加过的项

using System;
using System.Collections.Generic;
using System.IO;

namespace AegisApp
{
    internal enum DefenderState
    {
        Unavailable,
        Disabled,
        Active
    }

    internal static class DefenderExclusion
    {
        private const string TrackKey = "DefenderExclusions";
        private const string Label = "Defender 排除";

        public static DefenderState QueryState()
        {
            string outText;
            if (!PsRunner.Run(
                "$s = Get-MpComputerStatus -ErrorAction Stop\r\n" +
                "if ($s.RealTimeProtectionEnabled) { Write-Output ACTIVE } else { Write-Output DISABLED }\r\n",
                Label, 8000, out outText)) return DefenderState.Unavailable;
            if (outText.IndexOf("ACTIVE", StringComparison.OrdinalIgnoreCase) >= 0) return DefenderState.Active;
            if (outText.IndexOf("DISABLED", StringComparison.OrdinalIgnoreCase) >= 0) return DefenderState.Disabled;
            return DefenderState.Unavailable;
        }

        internal static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.Trim().Trim('"');
            if (p.Length == 0) return "";
            try { p = Path.GetFullPath(p); } catch { }
            if (p.Length > 3) p = p.TrimEnd('\\');
            return p;
        }

        private static List<string> LoadOwned()
        {
            var list = new List<string>();
            foreach (string s in Settings.LoadStr(TrackKey, "").Split('|'))
            {
                string n = Normalize(s);
                if (n.Length > 0 && !Contains(list, n)) list.Add(n);
            }
            return list;
        }

        private static void SaveOwned(List<string> list)
        {
            Settings.SaveStr(TrackKey, string.Join("|", list.ToArray()));
        }

        internal static bool Contains(List<string> list, string path)
        {
            string n = Normalize(path);
            foreach (string s in list)
                if (string.Equals(Normalize(s), n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static List<string> OwnedByAegis() { return LoadOwned(); }

        private static IDictionary<string, string> PathArg(string path)
        {
            return new Dictionary<string, string> { { "AEGIS_PATH", path } };
        }

        public static List<string> QuerySystem()
        {
            var list = new List<string>();
            string outText;
            if (!PsRunner.Run(
                "$ErrorActionPreference='Stop'\r\n" +
                "$p = Get-MpPreference\r\n" +
                "if ($p.ExclusionPath) { $p.ExclusionPath | ForEach-Object { Write-Output $_ } }\r\n",
                Label, 8000, out outText)) return null;
            foreach (string line in outText.Split('\n'))
            {
                string n = Normalize(line);
                if (n.Length > 0) list.Add(n);
            }
            return list;
        }

        public static bool IsExcludedInSystem(List<string> systemList, string path)
        {
            return systemList != null && Contains(systemList, path);
        }

        public static bool Add(string path)
        {
            string n = Normalize(path);
            if (n.Length == 0 || !Directory.Exists(n))
            {
                Logger.Log(Label + "：目录不存在，已拒绝 " + path);
                return false;
            }
            string outText;
            if (!PsRunner.Run(
                "$ErrorActionPreference='Stop'\r\n" +
                "Add-MpPreference -ExclusionPath $env:AEGIS_PATH\r\n" +
                "Write-Output DONE\r\n", Label, 20000, PathArg(n), out outText))
            {
                RemoveFromSystem(n);
                Logger.Log(Label + "：执行未确认完成，已撤回可能已加上的排除 " + n);
                return false;
            }
            if (outText.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) < 0)
            {
                RemoveFromSystem(n);
                Logger.Log(Label + "：退出码为 0 但收不到执行确认，已撤回可能已加上的排除 " + n);
                return false;
            }

            List<string> system = QuerySystem();
            if (system == null || !Contains(system, n))
            {
                RemoveFromSystem(n);
                Logger.Log(Label + "：写入后回读不到，已撤回刚加的排除 " + n);
                return false;
            }

            List<string> owned = LoadOwned();
            if (!Contains(owned, n))
            {
                owned.Add(n);
                SaveOwned(owned);
                if (!Contains(LoadOwned(), n))
                {
                    RemoveFromSystem(n);
                    Logger.Log(Label + "：记账无法持久化，已撤回刚加的排除 " + n);
                    return false;
                }
            }
            Logger.Log(Label + "：已排除 " + n + "（该目录不再被实时扫描）");
            return true;
        }

        private static bool RemoveFromSystem(string n)
        {
            string outText;
            if (!PsRunner.Run(
                "$ErrorActionPreference='Stop'\r\n" +
                "Remove-MpPreference -ExclusionPath $env:AEGIS_PATH\r\n" +
                "Write-Output DONE\r\n", Label, 20000, PathArg(n), out outText)) return false;
            return outText.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsOwned(string path) { return Contains(LoadOwned(), path); }

        public static bool Remove(string path)
        {
            string n = Normalize(path);
            List<string> owned = LoadOwned();
            if (!Contains(owned, n))
            {
                Logger.Log(Label + "：" + n + " 不是 Aegis 添加的，拒绝移除");
                return false;
            }
            if (!RemoveFromSystem(n)) return false;

            List<string> system = QuerySystem();
            if (system != null && Contains(system, n))
            {
                Logger.Log(Label + "：移除后仍能回读到，保留记账待重试 " + n);
                return false;
            }
            var next = new List<string>();
            foreach (string s in owned)
                if (!string.Equals(Normalize(s), n, StringComparison.OrdinalIgnoreCase)) next.Add(s);
            SaveOwned(next);
            Logger.Log(Label + "：已取消排除 " + n);
            return true;
        }

        public static int RemoveAllOwned()
        {
            int n = 0;
            foreach (string path in LoadOwned())
                if (Remove(path)) n++;
            return n;
        }
    }
}
