// @author bdth 2074055628@qq.com
// 文件用途 管理 Defender 扫描排除目录 只增删本程序自己加过的项

using System;
using System.Collections.Generic;
using System.IO;

namespace AegisApp
{
    internal enum DefenderState
    {
        Unavailable,   // 查询不到（未安装 / 被第三方接管 / 权限不足）
        Disabled,      // 装着但实时保护关闭 —— 此时排除目录毫无意义
        Active
    }

    // 把游戏目录排除出 Defender 实时扫描能省掉加载时逐文件过扫的开销，
    // 但代价是这些路径下的恶意文件也不再被拦截——游戏目录恰恰是破解版、
    // 第三方 mod、修改器的聚集地，所以这里刻意做得很克制：
    //   - 只允许排除游戏库里已登记的目录，不接受任意路径
    //   - 逐目录手动勾选，没有"一键全加"
    //   - 只移除 Aegis 自己加过的项，绝不动用户手工加的排除
    internal static class DefenderExclusion
    {
        private const string TrackKey = "DefenderExclusions";
        private const string Label = "Defender 排除";

        // 先判断 Defender 到底在不在工作：实时保护关着的话，排除目录一点用都没有，
        // 与其让用户点了再失败，不如一进来就说清楚。
        // 这两个查询跑在 UI 线程上，超时给短一点：正常时是秒回，卡到十几秒说明它已经不健康。
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

        // 路径一律经环境变量传给脚本，不参与 PowerShell 的文本解析。
        // 拼字符串再转义单引号是挡不住的：PowerShell 把 U+2018/U+2019 等排版引号
        // 同样当作单引号定界符，而 Aegis 是以管理员身份运行的。
        private static IDictionary<string, string> PathArg(string path)
        {
            return new Dictionary<string, string> { { "AEGIS_PATH", path } };
        }

        // 读系统当前真实的排除列表，用来判断状态和做写入后的独立回读校验
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
                "Write-Output DONE\r\n", Label, 20000, PathArg(n), out outText)) return false;
            if (outText.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) < 0) return false;

            // 从这里往下，排除项已经真的加进系统了。任何一步失败都必须撤回，
            // 否则它既不在记账里（Remove 会拒绝）又留在系统上，等于一个谁也删不掉的安全缺口。
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

        // 只移除记账里有的项：用户手工加的排除永远不碰
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

        // 对话框里的"全部取消排除"调用：只撤 Aegis 自己加过的
        public static int RemoveAllOwned()
        {
            int n = 0;
            foreach (string path in LoadOwned())
                if (Remove(path)) n++;
            return n;
        }
    }
}
