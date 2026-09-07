// @author bdth 2074055628@qq.com
// 文件用途 进程名与路径的角色判别 不做程序用途推断
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class GameSessionDetector
    {
        // 这里只承担禁止把安全组件作为调优目标的边界 不承担程序角色推断
        // GUI 浏览器技术 文件名或安装平台都不能证明它不是游戏渲染程序
        internal static bool IsNonGameRole(string name, string path)
        {
            string n = (name ?? "").Trim();
            if (AntiCheatCatalog.IsAntiCheatProcess(n, path)) return true;
            // 系统壳/核心组件是调优安全边界 不是游戏名单 同名外部程序不受此限制
            if (!string.IsNullOrEmpty(WindowsRootPrefix) && !string.IsNullOrEmpty(path)
                && path.StartsWith(WindowsRootPrefix, StringComparison.OrdinalIgnoreCase)
                && (SystemProcessCatalog.IsShellProcess(n)
                    || SystemProcessCatalog.IsCoreSystemProcess(n, path, WindowsRootPrefix))) return true;
            string low = ((path ?? "") + "\\" + n).ToLowerInvariant();
            return AntiCheatCatalog.ContainsToken(low);
        }

        internal static bool IsLibraryCandidate(string name, string path, string windowsRoot)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return false;
            if (ElectionVetoed(name, path)) return false;
            return string.IsNullOrEmpty(windowsRoot)
                || !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsProfileEntryName(
            GameProfile profile, string name)
        {
            return profile != null && !string.IsNullOrEmpty(name)
                && profile.Entries != null
                && (profile.Entries.Contains(name)
                    || IsFallbackEntryName(profile, name));
        }

        internal static bool IsProfileEntryProcess(
            GameProfile profile, string name, string path)
        {
            if (profile == null) return false;
            if (SamePath(profile.LearnedExecutablePath, path)) return true;
            if (SamePath(profile.ExecutablePath, path)) return true;
            return profile.ContainsPath(path);
        }

        private static bool IsFallbackEntryName(GameProfile profile, string name)
        {
            if (string.IsNullOrEmpty(profile.ExecutablePath) || string.IsNullOrEmpty(name)) return false;
            string baseName = Path.GetFileNameWithoutExtension(profile.ExecutablePath);
            if (string.IsNullOrEmpty(baseName) || baseName.Length < 3) return false;
            if (string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase)) return false;
            if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) return false;
            return IsBitnessOrVersionSuffix(name.Substring(baseName.Length));
        }

        private static bool IsBitnessOrVersionSuffix(string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return false;
            int i = (suffix[0] == '_' || suffix[0] == '-') ? 1 : 0;
            if (i >= suffix.Length) return false;
            string rest = suffix.Substring(i);
            bool allDigits = true;
            foreach (char c in rest) if (!char.IsDigit(c)) { allDigits = false; break; }
            if (allDigits) return true;
            string low = rest.ToLowerInvariant();
            if (low == "x64" || low == "x86") return true;
            if (low.Length >= 2 && low[0] == 'v')
            {
                bool tailDigits = true;
                for (int k = 1; k < low.Length; k++) if (!char.IsDigit(low[k])) { tailDigits = false; break; }
                if (tailDigits) return true;
            }
            return false;
        }
    }
}
