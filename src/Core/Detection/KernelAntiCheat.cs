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
            new[]{ "Ricochet", "cod", "modernwarfare", "blackops", "warzone" },
            new[]{ "HoYoKProtect", "yuanshen", "genshinimpact", "starrail", "zenlesszonezero", "bh3" },
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
                    if (exe.StartsWith(row[i], StringComparison.OrdinalIgnoreCase)) return row[0];
            return null;
        }

        public static string Describe(string rendererName)
        {
            string byExe = MatchByExe(rendererName);
            if (byExe != null) return byExe;
            return InstalledName();
        }
    }
}
