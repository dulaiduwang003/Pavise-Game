// @author bdth 2074055628@qq.com
// 文件用途 枚举游戏进程已加载的模块 点名注入的覆盖层 纯只读 不动任何进程
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class OverlayScan
    {
        private static readonly string[][] Known =
        {
            new[] { "gameoverlayrenderer", "Steam" },
            new[] { "discordhook", "Discord" },
            new[] { "nvspcap", "NVIDIA ShadowPlay" },
            new[] { "rtsshooks", "RTSS/MSI Afterburner" },
            new[] { "graphics-hook", "OBS" },
            new[] { "overwolf", "Overwolf" },
            new[] { "ow-graphics", "Overwolf" },
            new[] { "nahimicosd", "Nahimic OSD" },
            new[] { "a-volute", "Nahimic/A-Volute" },
            new[] { "fraps", "Fraps" },
        };

        private static readonly string[] ProxyNames =
            { "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "opengl32.dll" };

        public static List<string> Scan(int pid, string gameDir, out bool denied)
        {
            denied = false;
            var hits = new List<string>();
            IntPtr h = Native.OpenProcess(
                ProcessQueryInformation | ProcessVmRead, false, pid);
            if (h == IntPtr.Zero) { denied = true; return hits; }
            try
            {
                var modules = new IntPtr[2048];
                uint needed;
                if (!EnumProcessModulesEx(h, modules, (uint)(modules.Length * IntPtr.Size),
                        out needed, ListModulesAll))
                {
                    denied = true;
                    return hits;
                }
                if (needed / IntPtr.Size > modules.Length)
                {
                    modules = new IntPtr[needed / IntPtr.Size + 64];
                    if (!EnumProcessModulesEx(h, modules, (uint)(modules.Length * IntPtr.Size),
                            out needed, ListModulesAll))
                    {
                        denied = true;
                        return hits;
                    }
                }
                int count = Math.Min(modules.Length, (int)(needed / IntPtr.Size));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sb = new System.Text.StringBuilder(520);
                for (int i = 0; i < count; i++)
                {
                    sb.Length = 0;
                    if (GetModuleFileNameExW(h, modules[i], sb, 512) == 0) continue;
                    string path = sb.ToString();
                    string name = System.IO.Path.GetFileName(path).ToLowerInvariant();
                    if (name.Length == 0) continue;
                    foreach (string[] entry in Known)
                        if (name.Contains(entry[0]) && seen.Add(entry[1]))
                            hits.Add(entry[1] + " (" + name + ")");
                    if (!string.IsNullOrEmpty(gameDir)
                        && Array.IndexOf(ProxyNames, name) >= 0
                        && path.StartsWith(gameDir, StringComparison.OrdinalIgnoreCase)
                        && seen.Add("proxy:" + name))
                        hits.Add(Lang.T("t.overlayscan.1") + " (" + name + ")");
                }
                return hits;
            }
            catch { return hits; }
            finally { Native.CloseHandle(h); }
        }

        private const int ProcessQueryInformation = 0x0400;
        private const int ProcessVmRead = 0x0010;
        private const uint ListModulesAll = 0x03;

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules,
            uint size, out uint needed, uint filter);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameExW(IntPtr process, IntPtr module,
            System.Text.StringBuilder fileName, uint size);
    }
}
