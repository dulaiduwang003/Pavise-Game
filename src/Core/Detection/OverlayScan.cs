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
            List<string> ignored;
            return Scan(pid, gameDir, out denied, out ignored);
        }

        public static List<string> Scan(int pid, string gameDir, out bool denied,
            out List<string> injectorModulePaths)
        {
            denied = false;
            injectorModulePaths = new List<string>();
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
                        {
                            hits.Add(entry[1] + " (" + name + ")");
                            injectorModulePaths.Add(path);
                        }
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

        // 从注入模块的路径推它宿主软件的安装根 例如
        //   C:\Users\X\AppData\Local\Discord\app-1.0\modules\hook.dll -> C:\Users\X\AppData\Local\Discord
        //   向上走到已知安装容器为止 取容器下第一层目录 推不出宁可返回空也不给一个过宽的根
        public static string ProductRootOf(string modulePath)
        {
            try
            {
                if (string.IsNullOrEmpty(modulePath)) return null;
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windows)
                    && modulePath.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return null;
                string dir = System.IO.Path.GetDirectoryName(modulePath);
                for (int i = 0; i < 32 && !string.IsNullOrEmpty(dir); i++)
                {
                    string parent = System.IO.Path.GetDirectoryName(dir);
                    // 到盘根还没遇到容器 说明模块直接摊在浅层目录 整盘不能当根
                    if (string.IsNullOrEmpty(parent)) return null;
                    if (IsInstallContainer(parent)) return TooBroadRoot(dir) ? null : dir;
                    dir = parent;
                }
            }
            catch { }
            return null;
        }

        private static bool IsInstallContainer(string dir)
        {
            // 盘根不算容器 D:\Tools\Overwolf 这种布局取 D:\Tools 当根会豁免半个盘
            //   自定义盘一路走到底都没遇到已知容器就放弃 保守不豁免 反转时长有提升机制兜底
            if (dir.Length <= 3) return false;
            string low = dir.TrimEnd('\\').ToLowerInvariant();
            if (low.EndsWith("\\program files") || low.EndsWith("\\program files (x86)")
                || low.EndsWith("\\programdata")
                || low.EndsWith("\\appdata\\local") || low.EndsWith("\\appdata\\roaming")
                || low.EndsWith("\\appdata\\locallow")) return true;
            // C:\Users\<name> 用户主目录也算容器 软件直接装在主目录下的情况
            string parent = System.IO.Path.GetDirectoryName(low);
            return parent != null && parent.TrimEnd('\\').EndsWith("\\users");
        }

        private static bool TooBroadRoot(string dir)
        {
            string low = dir.TrimEnd('\\').ToLowerInvariant();
            return low.Length <= 3
                || low.EndsWith("\\users") || low.EndsWith("\\appdata")
                || low.EndsWith("\\windows") || low.EndsWith("\\desktop")
                || low.EndsWith("\\downloads") || low.EndsWith("\\documents");
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
