using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed class PlatformProcessOwner
    {
        internal string Platform, Name, Path;
        internal long Creation;
    }

    internal static class PlatformRuntimePolicy
    {
        internal const string RemovedKey = "PlatformRuntimeRemovedV1", RulePrefix = "platform:";
        internal static readonly string[] Ids = { "wegame", "steam" };
        private static readonly object gate = new object();
        private static int generation = -1, removed;
        private static readonly HashSet<string> SteamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "steam", "steamwebhelper", "gameoverlayui" };
        private static readonly HashSet<string> Helpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "browser", "crashpad_handler", "msedgewebview2", "QtWebEngineProcess", "cef_subprocess" };

        // 只在偏好变更后读配置 进程扫描复用缓存
        internal static int Mask
        {
            get
            {
                lock (gate)
                {
                    int next = Settings.MutationGeneration;
                    if (generation == next) return removed;
                    removed = 0;
                    foreach (string id in Settings.LoadStr(RemovedKey, "").Split(';'))
                        removed |= Bit(id);
                    generation = next;
                    return removed;
                }
            }
        }
        private static int Bit(string id)
        {
            if (string.Equals(id, "wegame", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(id, "steam", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }
        internal static bool Enabled(string id) { int bit = Bit(id); return bit != 0 && (Mask & bit) == 0; }
        internal static string DisplayName(string id) { return id == "wegame" ? "WeGame" : "Steam"; }
        internal static string RuntimePlatform(string name)
        {
            if (WeGameRuntimeProtection.IsRuntimeName(name)) return "wegame";
            return SteamNames.Contains(WhitelistRule.NormalizeName(name)) ? "steam" : null;
        }
        internal static bool TryRemove(string id)
        {
            lock (gate)
            {
                int bit = Bit(id);
                if (bit == 0) return false;
                return Save(Mask | bit);
            }
        }
        internal static bool TryRestore()
        { lock (gate) return Save(0); }
        private static bool Save(int mask)
        {
            var ids = new List<string>();
            foreach (string id in Ids) if ((mask & Bit(id)) != 0) ids.Add(id);
            return Settings.SaveStr(RemovedKey, string.Join(";", ids.ToArray()));
        }
        internal static string ProcessNames(string id)
        { return id == "wegame" ? "WeGame · WeGame_env · WeGameClient · Pallas · Rail · TCLS_core" : "Steam · SteamWebHelper · GameOverlayUI"; }

        internal static Dictionary<int, PlatformProcessOwner> Collect(ProcessSnapshot snapshot, int self, int session,
            HashSet<int> gameRoots, Dictionary<int, PlatformProcessOwner> retained)
        {
            var result = new Dictionary<int, PlatformProcessOwner>();
            if (snapshot == null || session < 0) return result;
            var current = new Dictionary<int, ProcEntry>();
            var seen = new HashSet<int>();
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcEntry p in snapshot.Entries)
            {
                if (p == null) continue;
                if (!seen.Add(p.Pid)) { current.Remove(p.Pid); continue; }
                if (p.Pid <= 4 || p.Pid == self || p.Session != session || p.Creation <= 0) continue;
                string path = WhitelistRule.NormalizeImagePath(p.Path);
                if (path.Length == 0 || !string.Equals(WhitelistRule.NormalizeName(p.Name),
                    WhitelistRule.NormalizeName(Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase)) continue;
                current[p.Pid] = p;
            }
            var blocked = new HashSet<int>(gameRoots ?? new HashSet<int>());
            for (int depth = 0; depth < 24; depth++)
            {
                bool changed = false;
                foreach (ProcEntry p in current.Values)
                {
                    ProcEntry parent;
                    if (!blocked.Contains(p.Pid) && blocked.Contains(p.ParentPid) && RuntimePlatform(p.Name) == null
                        && current.TryGetValue(p.ParentPid, out parent) && parent.Creation < p.Creation)
                    { blocked.Add(p.Pid); changed = true; }
                }
                if (!changed) break;
            }
            foreach (int pid in blocked) current.Remove(pid);
            foreach (ProcEntry p in current.Values)
            {
                string id = RuntimePlatform(p.Name);
                if (id != null)
                {
                    result[p.Pid] = Owner(p, id);
                    string directory = Path.GetDirectoryName(WhitelistRule.NormalizeImagePath(p.Path));
                    if (FamilyBoundary.SafeFamilyDir(directory) && (id != "steam"
                        || string.Equals(WhitelistRule.NormalizeName(p.Name), "steam", StringComparison.OrdinalIgnoreCase))) roots[directory] = id;
                }
                PlatformProcessOwner old;
                if (retained != null && retained.TryGetValue(p.Pid, out old) && old.Creation == p.Creation
                    && string.Equals(old.Name, p.Name, StringComparison.OrdinalIgnoreCase)
                    && WhitelistRule.PathEquals(old.Path, p.Path)) result[p.Pid] = old;
            }
            foreach (ProcEntry p in current.Values)
            {
                if (result.ContainsKey(p.Pid) || !Helpers.Contains(WhitelistRule.NormalizeName(p.Name))) continue;
                foreach (var root in roots)
                    if (FamilyBoundary.UnderRoot(WhitelistRule.NormalizeImagePath(p.Path), root.Key)) { result[p.Pid] = Owner(p, root.Value); break; }
            }
            for (int depth = 0; depth < 24; depth++)
            {
                bool changed = false;
                foreach (ProcEntry p in current.Values)
                {
                    if (result.ContainsKey(p.Pid)) continue;
                    ProcEntry parent;
                    PlatformProcessOwner owner;
                    if (result.TryGetValue(p.ParentPid, out owner) && current.TryGetValue(p.ParentPid, out parent)
                        && parent.Creation < p.Creation)
                    { result[p.Pid] = Owner(p, owner.Platform); changed = true; }
                }
                if (!changed) break;
            }
            return result;
        }
        private static PlatformProcessOwner Owner(ProcEntry p, string id)
        { return new PlatformProcessOwner { Platform = id, Creation = p.Creation, Name = p.Name, Path = p.Path }; }
    }
}
