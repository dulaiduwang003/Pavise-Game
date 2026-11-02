using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // Hard affinity is deliberately narrower than the general suppression catalog.
    // A process name alone does not establish a dedicated service role.
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
            if (EqualsAny(name, "SGuard", "SGuard64", "SGuardSvc", "SGuardSvc64", "ACE-Service64", "AntiCheatExpert", "AntiCheatExpert.Service", "ACE-BASE", "ACE-GAME")) return "ace";
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

        // Missing/reused parents terminate a chain; parent PID alone is not an identity.
        // Seed PIDs are retained even if a snapshot no longer contains that game process.
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
