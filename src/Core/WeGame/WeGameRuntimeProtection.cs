using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class WeGameRuntimeProtection
    {
        private static readonly HashSet<string> RuntimeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "wegame", "wegame_env", "wegameclient", "pallas", "rail", "tcls_core" };
        private static readonly HashSet<string> HelperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "browser", "crashpad_handler", "msedgewebview2", "QtWebEngineProcess", "cef_subprocess" };

        internal static bool IsRuntimeName(string name)
        { return RuntimeNames.Contains(WhitelistRule.NormalizeName(name)); }

        internal static bool IsProtectedRuntimeName(string name)
        { return IsRuntimeName(name) && PlatformRuntimePolicy.Enabled("wegame"); }

        internal static bool IsCriticalRuntime(string name, string path)
        {
            if (!IsProtectedRuntimeName(name)) return false;
            // 查询权限不足不能取消已知平台运行时的保护。
            if (string.IsNullOrEmpty(path)) return true;
            string normalized = WhitelistRule.NormalizeImagePath(path);
            return normalized.Length != 0 && string.Equals(WhitelistRule.NormalizeName(name),
                WhitelistRule.NormalizeName(Path.GetFileName(normalized)), StringComparison.OrdinalIgnoreCase);
        }

        internal static HashSet<int> Collect(ProcessSnapshot snapshot, int selfPid, int ownerSession)
        {
            var result = new HashSet<int>();
            if (snapshot == null || ownerSession < 0) return result;
            var current = new Dictionary<int, ProcEntry>();
            var paths = new Dictionary<int, string>();
            var seen = new HashSet<int>();
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcEntry p in snapshot.Entries)
            {
                if (p == null) continue;
                if (!seen.Add(p.Pid)) { current.Remove(p.Pid); paths.Remove(p.Pid); continue; }
                if (p.Pid <= 4 || p.Pid == selfPid || p.Session != ownerSession || p.Creation <= 0) continue;
                string path = WhitelistRule.NormalizeImagePath(p.Path);
                if (path.Length == 0 || !string.Equals(WhitelistRule.NormalizeName(p.Name),
                    WhitelistRule.NormalizeName(Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase)) continue;
                current[p.Pid] = p; paths[p.Pid] = path;
            }
            foreach (ProcEntry p in current.Values)
            {
                if (!FamilyBoundary.CatalogProtected(p.Pid, p.Creation, p.Name, p.Path)) continue;
                result.Add(p.Pid);
                string directory = Path.GetDirectoryName(paths[p.Pid]);
                if (FamilyBoundary.SafeFamilyDir(directory)) roots.Add(directory);
            }
            // 平台目录只证明辅助角色归属，不能豁免目录内所有游戏。
            foreach (ProcEntry p in current.Values)
            {
                if (!HelperNames.Contains(WhitelistRule.NormalizeName(p.Name))) continue;
                foreach (string root in roots)
                    if (FamilyBoundary.UnderRoot(paths[p.Pid], root)) { result.Add(p.Pid); break; }
            }
            for (int depth = 0; depth < 24; depth++)
            {
                bool changed = false;
                foreach (ProcEntry p in current.Values)
                {
                    if (result.Contains(p.Pid)) continue;
                    ProcEntry parent;
                    if (result.Contains(p.ParentPid) && current.TryGetValue(p.ParentPid, out parent)
                        && parent.Creation < p.Creation)
                    { result.Add(p.Pid); changed = true; }
                }
                if (!changed) break;
            }
            return result;
        }
    }
}
