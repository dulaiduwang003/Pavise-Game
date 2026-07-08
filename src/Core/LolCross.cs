// @author bdth 2074055628@qq.com
// 文件用途 定位并安全清理国服英雄联盟 Cross 内容

using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class LolCross
    {
        public static string FindLolDir()
        {
            string p = RegStr(Registry.CurrentUser, @"Software\Tencent\LOL");
            if (Valid(p)) return p;
            p = RegStr(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Tencent\LOL");
            if (Valid(p)) return p;
            p = RegStr(Registry.LocalMachine, @"SOFTWARE\Tencent\LOL");
            if (Valid(p)) return p;
            return null;
        }

        private static string RegStr(RegistryKey root, string sub)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub))
                    return k != null ? k.GetValue("InstallPath") as string : null;
            }
            catch { return null; }
        }

        private static bool Valid(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "TCLS")); }
            catch { return false; }
        }

        public static long MeasureBytes(string lolDir)
        {
            return CacheSweep.MeasureDir(Path.Combine(lolDir, "Cross"));
        }

        public static CacheSweep.Result Clean(string lolDir)
        {
            if (AnyLolProcessAlive(lolDir))
            {
                var blocked = new CacheSweep.Result();
                blocked.FailedFiles = 1;
                return blocked;
            }
            return CacheSweep.CleanDir(Path.Combine(lolDir, "Cross"));
        }

        public static bool AnyLolProcessAlive(string lolDir)
        {
            string prefix = lolDir.TrimEnd('\\') + "\\";
            Process[] all = null;
            try { all = Process.GetProcesses(); } catch { return false; }
            bool alive = false;
            foreach (Process p in all)
            {
                try
                {
                    if (!alive)
                    {
                        string processName = null;
                        try { processName = p.ProcessName; } catch { }
                        if (string.Equals(processName, "wegame", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(processName, "wegame_env", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(processName, "wegameclient", StringComparison.OrdinalIgnoreCase))
                        {
                            alive = true;
                            continue;
                        }
                        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                        if (h != IntPtr.Zero)
                        {
                            try
                            {
                                string path = Native.ImagePath(h);
                                if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) alive = true;
                            }
                            finally { Native.CloseHandle(h); }
                        }
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return alive;
        }

    }
}
