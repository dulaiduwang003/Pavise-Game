// @author bdth 2074055628@qq.com
// File purpose Role classification of process names and paths; no inference of program purpose
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class GameSessionDetector
    {
        // This only enforces the boundary that security components are never tuning targets; it does not infer program roles
        // GUI, browser technology, file name or install platform cannot prove something is not a game renderer
        internal static bool IsNonGameRole(string name, string path)
        {
            string n = (name ?? "").Trim();
            if (AntiCheatCatalog.IsAntiCheatProcess(n, path)) return true;
            // The system shell and core components are a tuning safety boundary, not a game list; external programs with the same name are unaffected
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
