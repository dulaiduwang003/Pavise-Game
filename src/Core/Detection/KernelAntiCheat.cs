// @author bdth 2074055628@qq.com
// File purpose Identify kernel-mode anti-cheat installed on this machine, only to explain access-denied logs
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
            // Leading equals sign means whole-name equality; cod.exe is the real main executable of Call of Duty HQ, but a three-letter prefix would match CodeVein
            new[]{ "Ricochet", "=cod", "modernwarfare", "blackops", "warzone" },
            new[]{ "HoYoKProtect", "yuanshen", "genshinimpact", "starrail", "zenlesszonezero", "bh3" },
            // Only list process names with hard evidence; under prefix matching fc2 would swallow fc25 and nfs would mismatch nfsclient
            //   Better to miss and take the unnamed fallback than to name it on a machine without EA anti-cheat installed
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

        // Attribution for the log: name it only when recognized by game; do not name it when only the local install inventory is available
        //   InstalledName scans which kernel anti-cheats this machine has installed, unrelated to which game is running now
        //   Logging it as the culprit points the wrong way; the user would go disable an anti-cheat group that was never involved
        public static string DescribeForLog(string rendererName)
        {
            string byExe = MatchByExe(rendererName);
            if (byExe != null) return byExe;
            string installed = InstalledName();
            return installed == null ? null : Lang.F("log.kernelac.installed", installed);
        }
    }
}
