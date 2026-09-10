// @author bdth 2074055628@qq.com
// 文件用途 识别本机已安装的内核态反作弊 只用于说明访问受限的日志
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class KernelAntiCheat
    {
        private sealed class Sig
        {
            public string Name;
            public string[] Services;
        }

        private static readonly Sig[] Known = new[]
        {
            new Sig { Name = "Vanguard",        Services = new[]{ "vgk", "vgc" } },
            new Sig { Name = "EasyAntiCheat",   Services = new[]{ "EasyAntiCheat", "EasyAntiCheat_EOS" } },
            new Sig { Name = "BattlEye",        Services = new[]{ "BEDaisy", "BEService" } },
            new Sig { Name = "EA Javelin",       Services = new[]{ "EAAntiCheat.GameService" } },
            new Sig { Name = "ACE-Guard",       Services = new[]{ "ACE-BASE", "ACE-GAME", "AntiCheatExpert" } },
            new Sig { Name = "nProtect GameGuard", Services = new[]{ "npggsvc" } },
            new Sig { Name = "Faceit AC",       Services = new[]{ "faceit" } },
            new Sig { Name = "TenProtect",      Services = new[]{ "TesSafe" } },
            new Sig { Name = "NEAC",            Services = new[]{ "NeacSafe64" } },
            new Sig { Name = "HoYoKProtect",    Services = new[]{ "HoYoKProtect", "mhyprot3", "mhyprot2" } },
            new Sig { Name = "B5 BBI",          Services = new[]{ "B5AntiCheat64", "B5AntiCheat32" } },
        };

        private static readonly string[][] ByExePrefix = new[]
        {
            // 等号开头表示整名相等 cod.exe 是 Call of Duty HQ 的真实主程序名 但三个字母做前缀会命中 CodeVein
            new[]{ "Ricochet", "=cod", "modernwarfare", "blackops", "warzone" },
            new[]{ "HoYoKProtect", "yuanshen", "genshinimpact", "starrail", "zenlesszonezero", "bh3" },
            // 只写有实据的进程名 前缀匹配下 fc2 会吞掉 fc25 nfs 会误配 nfsclient
            //   宁可认不出走不点名的兜底 也不能给没装 EA 反作弊的机器点名
            new[]{ "EA Javelin", "bf6", "bf2042" },
        };

        private const string ServiceRoot = @"SYSTEM\CurrentControlSet\Services";

        private static readonly object lk = new object();
        private static string cached;
        private static bool probed;

        internal static bool ServiceExists(string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ServiceRoot + "\\" + name))
                    return k != null;
            }
            catch { return false; }
        }

        public static string InstalledName()
        {
            lock (lk)
            {
                if (probed) return cached;
                probed = true;
                var hits = new List<string>();
                foreach (Sig s in Known)
                    foreach (string svc in s.Services)
                        if (ServiceExists(svc)) { hits.Add(s.Name); break; }
                cached = hits.Count == 0 ? null : string.Join(" ", hits.ToArray());
                return cached;
            }
        }

        internal static string MatchByExe(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName)) return null;
            string exe = rendererName;
            int slash = exe.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) exe = exe.Substring(slash + 1);
            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exe = exe.Substring(0, exe.Length - 4);
            foreach (string[] row in ByExePrefix)
                for (int i = 1; i < row.Length; i++)
                {
                    string token = row[i];
                    bool exact = token.Length > 1 && token[0] == '=';
                    if (exact
                        ? string.Equals(exe, token.Substring(1), StringComparison.OrdinalIgnoreCase)
                        : exe.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return row[0];
                }
            return null;
        }

        // 写日志用的归因 能按游戏认出来才点名 只能靠本机安装清单时不点名
        //   InstalledName 扫的是这台机器装了哪些内核反作弊 与当前跑哪个游戏无关
        //   拿它当肇事者写进日志会指错方向 用户会跑去关一个根本没参与的反作弊分组
        public static string DescribeForLog(string rendererName)
        {
            string byExe = MatchByExe(rendererName);
            if (byExe != null) return byExe;
            string installed = InstalledName();
            return installed == null ? null : Lang.F("log.kernelac.installed", installed);
        }
    }
}
