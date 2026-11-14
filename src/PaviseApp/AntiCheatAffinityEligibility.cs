using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    // 硬亲和性的适用范围小于普通压制名单；单凭进程名不能确认它是独立服务。

    internal static class AntiCheatAffinityEligibility
    {
        internal static bool Eligible(string name, int pid, int session,
            IEnumerable<KeyValuePair<string, int>> runningServices, HashSet<int> gameAncestors, out string reason)
        {
            reason = "unverified-service-role";
            if (pid <= 0) { reason = "invalid-process-identity"; return false; }
            if (gameAncestors == null) { reason = "game-ancestry-unavailable"; return false; }
            if (gameAncestors.Contains(pid)) { reason = "game-process-or-ancestor"; return false; }
            if (session != 0) { reason = "not-session-zero-service"; return false; }
            string group = ImageGroup(name);
            if (group == null) { reason = "launcher-helper-or-unknown-role"; return false; }
            if (runningServices == null) { reason = "running-services-unavailable"; return false; }
            bool found = false;
            try
            {
                foreach (KeyValuePair<string, int> service in runningServices)
                {
                    if (service.Value != pid) continue;
                    found = true;
                    if (!string.Equals(ServiceGroup(service.Key), group, StringComparison.Ordinal))
                    { reason = "shared-or-unverified-service-host"; return false; }
                }
            }
            catch { reason = "running-services-unavailable"; return false; }
            if (!found) { reason = "process-not-in-running-services"; return false; }
            reason = "";
            return true;
        }

        // ACE 客户端由独立服务派生，但不在 SCM 的服务 PID 表中；仅放行可核验的直接子进程进行资源治理。
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
            ProcEntry parent = snapshot.Find(client.ParentPid);
            if (parent == null || parent.Pid == pid || parent.Creation <= 0 || parent.Creation > birth
                || parent.Session != 0 || ImageGroup(parent.Name) != "ace" || gameAncestors.Contains(parent.Pid)) return false;
            service = parent;
            reason = "";
            return true;
        }

        private static string ImageGroup(string name)
        {
            name = NormalizeImage(name);
            if (EqualsAny(name, "SGuard", "SGuard64", "SGuardSvc", "SGuardSvc64", "ACE-Service64", "AntiCheatExpert", "AntiCheatExpert.Service")) return "ace";
            if (EqualsAny(name, "EasyAntiCheat", "EasyAntiCheat_EOS")) return "eac";
            if (EqualsAny(name, "BEService", "BEService_x64")) return "battleye";
            return null;
        }

        private static string ServiceGroup(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (EqualsAny(name, "SGuard", "SGuard64", "SGuardSvc", "SGuardSvc64", "ACE-Service64", "AntiCheatExpert", "AntiCheatExpert.Service", "AntiCheatExpert Service", "ACE-BASE", "ACE-GAME")) return "ace";
            if (EqualsAny(name, "EasyAntiCheat", "EasyAntiCheat_EOS") || ProductService(name, "EasyAntiCheat_EOS_")) return "eac";
            if (EqualsAny(name, "BEService", "BEService_x64")) return "battleye";
            return null;
        }

        private static bool ProductService(string name, string prefix)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length <= prefix.Length) return false;
            for (int i = prefix.Length; i < name.Length; i++)
            {
                char c = name[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
            }
            return true;
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

        // 父进程缺失或 PID 复用时停止追溯；始终保留起始 PID，包括游戏已退出的快照。

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
