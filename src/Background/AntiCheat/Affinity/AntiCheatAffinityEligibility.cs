using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    // 单凭进程名不能确认它是独立服务 纳管前要同时核对会话 0 服务表和正式安装位置

    internal static class AntiCheatAffinityEligibility
    {
        internal static bool Eligible(string name, int pid, int session,
            IEnumerable<KeyValuePair<string, int>> runningServices, HashSet<int> gameAncestors, out string reason)
        {
            if (!ServiceCandidate(name, pid, session, gameAncestors, out reason)) return false;
            if (runningServices == null) { reason = "running-services-unavailable"; return false; }
            bool found = false;
            try
            {
                foreach (KeyValuePair<string, int> service in runningServices)
                {
                    if (service.Value != pid) continue;
                    found = true;
                    if (!IsAceServiceName(service.Key))
                    { reason = "shared-or-unverified-service-host"; return false; }
                }
            }
            catch { reason = "running-services-unavailable"; return false; }
            if (!found) { reason = "process-not-in-running-services"; return false; }
            reason = "";
            return true;
        }

        // 不需要服务表的前置条件 通过后才去读映像路径和服务表
        internal static bool ServiceCandidate(string name, int pid, int session, HashSet<int> gameAncestors, out string reason)
        {
            reason = "unverified-service-role";
            if (pid <= 0) { reason = "invalid-process-identity"; return false; }
            if (gameAncestors == null) { reason = "game-ancestry-unavailable"; return false; }
            if (gameAncestors.Contains(pid)) { reason = "game-process-or-ancestor"; return false; }
            if (session != 0) { reason = "not-session-zero-service"; return false; }
            if (!IsAceServiceImage(name)) { reason = "launcher-helper-or-unknown-role"; return false; }
            reason = "";
            return true;
        }

        // 只有装在正式位置的映像才纳管 游戏目录里自带的同名组件和读不到路径的只保护
        // ACE 映像须在 Program Files 下的 AntiCheatExpert 目录内
        internal static bool InstalledImage(string name, string path, out string reason)
        {
            return InstalledImage(name, path, ProgramRoots(), out reason);
        }

        internal static bool InstalledImage(string name, string path, IEnumerable<string> programRoots, out string reason)
        {
            reason = "image-path-unavailable";
            string image = NormalizeImage(name);
            if (!IsAceServiceImage(image)) { reason = "launcher-helper-or-unknown-role"; return false; }
            string normalized = WhitelistRule.NormalizeImagePath(path);
            if (normalized.Length == 0) return false;
            reason = "not-installed-location";
            if (!string.Equals(Path.GetFileName(normalized), image + ".exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (programRoots == null) return false;
            foreach (string rawRoot in programRoots)
            {
                string root = WhitelistRule.NormalizeImagePath((rawRoot ?? "").TrimEnd('\\', '/')).TrimEnd('\\');
                if (root.Length < 3) continue;
                if (normalized.StartsWith(root + "\\AntiCheatExpert\\", StringComparison.OrdinalIgnoreCase)) { reason = ""; return true; }
            }
            return false;
        }

        private static IEnumerable<string> ProgramRoots()
        {
            var roots = new List<string>();
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                try
                {
                    string root = Environment.GetFolderPath(folder);
                    if (!string.IsNullOrEmpty(root)) roots.Add(root);
                }
                catch { }
            }
            try
            {
                string native = Environment.GetEnvironmentVariable("ProgramW6432");
                if (!string.IsNullOrEmpty(native)) roots.Add(native);
            }
            catch { }
            return roots;
        }

        // ACE 客户端由独立服务派生 但不在 SCM 的服务 PID 表中 只放行装在正式位置且父进程是 ACE 服务的直接子进程
        internal static bool TryAceClientService(ProcessSnapshot snapshot, string name, int pid, long birth, int session,
            HashSet<int> gameAncestors, out ProcEntry service, out string reason)
        {
            service = null;
            reason = "unverified-ace-client";
            if (gameAncestors == null || gameAncestors.Contains(pid) || snapshot == null || pid <= 4 || birth <= 0 || session <= 0)
                return false;
            string image = NormalizeImage(name);
            if (!EqualsAny(image, "SGuard", "SGuard64")) return false;
            ProcEntry client = snapshot.Find(pid);
            if (client == null || client.Creation != birth || client.Session != session
                || !string.Equals(NormalizeImage(client.Name), image, StringComparison.OrdinalIgnoreCase)) return false;
            string path = (client.Path ?? "").Replace('/', '\\');
            if (!AntiCheatCatalog.IsAntiCheatPath(path)
                || path.IndexOf("\\AntiCheatExpert\\", StringComparison.OrdinalIgnoreCase) < 0
                || !string.Equals(Path.GetFileName(path), image + ".exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (!InstalledImage(image, path, out reason)) return false;
            reason = "unverified-ace-client";
            ProcEntry parent = snapshot.Find(client.ParentPid);
            if (parent == null || parent.Pid == pid || parent.Creation <= 0 || parent.Creation > birth
                || parent.Session != 0 || !IsAceServiceImage(parent.Name) || gameAncestors.Contains(parent.Pid)) return false;
            service = parent;
            reason = "";
            return true;
        }

        private static bool IsAceServiceImage(string name)
        {
            return EqualsAny(NormalizeImage(name), "SGuard", "SGuard64", "SGuardSvc", "SGuardSvc64", "ACE-Service64", "AntiCheatExpert", "AntiCheatExpert.Service");
        }

        private static bool IsAceServiceName(string name)
        {
            return EqualsAny(name, "SGuard", "SGuard64", "SGuardSvc", "SGuardSvc64", "ACE-Service64", "AntiCheatExpert", "AntiCheatExpert.Service", "AntiCheatExpert Service", "ACE-BASE", "ACE-GAME");
        }

        private static bool EqualsAny(string name, params string[] values)
        {
            foreach (string value in values) if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string NormalizeImage(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(new char[] { '\\', '/', ':', '\0' }) >= 0) return "";
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        // 父进程缺失或 PID 复用时停止追溯 始终保留起始 PID 包括游戏已退出的快照

        internal static HashSet<int> GameAncestors(ProcessSnapshot snapshot, IEnumerable<int> gamePids)
        {
            if (snapshot == null || gamePids == null) return null;
            HashSet<int> result = new HashSet<int>();
            foreach (int pid in gamePids)
            {
                if (pid <= 0) continue;
                result.Add(pid);
                ProcEntry child = snapshot.Find(pid);
                HashSet<int> chain = new HashSet<int>();
                while (child != null && child.Pid > 0 && child.Creation > 0 && chain.Add(child.Pid))
                {
                    if (child.ParentPid <= 0 || child.ParentPid == child.Pid) break;
                    ProcEntry parent = snapshot.Find(child.ParentPid);
                    if (parent == null || parent.Creation <= 0 || parent.Creation > child.Creation || chain.Contains(parent.Pid)) break;
                    result.Add(parent.Pid);
                    child = parent;
                }
            }
            return result;
        }
    }
}
