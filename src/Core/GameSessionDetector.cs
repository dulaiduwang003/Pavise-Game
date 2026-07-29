// @author bdth 2074055628@qq.com
// 文件用途 判定候选进程形成有效游戏会话的条件

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AegisApp
{
    internal sealed class GameDetection
    {
        public GameProfile Profile;
        public int RendererPid;
        public long RendererCreation;
        public string RendererName;
        public string RendererPath;
        public bool RendererForeground;
        public bool RendererCandidateSelected;
        public bool RendererUserSelected;
        public readonly HashSet<string> FamilyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> FamilyPids = new HashSet<int>();
        public string Evidence;
    }

    internal sealed class GameProcessSnapshot
    {
        public int Pid;
        public int ParentPid;
        public long Creation;
        public string Name;
        public string Path;
        public bool Visible;
        public bool Foreground;
    }

    internal static class GameSessionDetector
    {
        internal const int DetachedMigrationObservationMs = 45000;
        private const int CreationClockSkewMs = 5000;

        private static readonly string[] RendererRejectTokens =
        {
            "launcher", "leagueclient", "riotclient", "updater", "update", "patch", "bootstrap", "crash",
            "reporter", "browser", "cef", "helper", "service", "install", "uninstall"
        };

        private static readonly string[] LauncherRendererTokens = { "launcher", "leagueclient", "riotclient" };

        private static readonly string[] NonGameRoleTokens =
        {
            "anticheat", "anti-cheat", "ace-helper", "ace-base", "sguard", "tensafe",
            "easyanticheat", "beservice", "battleye", "gameguard", "gamemon", "vgtray",
            "crashreport", "crash_report", "crashpad", "telemetry", "uninstall"
        };

        private static readonly HashSet<string> NeverGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "powerpnt", "winword", "excel", "outlook", "acrord32", "notepad", "mspaint",
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
            "explorer", "wegame", "wegame_env", "steam", "steamwebhelper", "epicgameslauncher",
            "battle.net", "agent", "galaxyclient", "ubisoftconnect"
        };

        public static GameDetection Detect(Process[] all, IList<GameProfile> profiles)
        {
            int ownerSession;
            try
            {
                using (Process current = Process.GetCurrentProcess())
                    ownerSession = current.SessionId;
            }
            catch { return null; }
            return Detect(all, profiles, ownerSession);
        }

        public static GameDetection Detect(
            Process[] all, IList<GameProfile> profiles,
            int ownerSession)
        {
            if (all == null || profiles == null
                || profiles.Count == 0 || ownerSession < 0)
                return null;

            var snapshot = new List<GameProcessSnapshot>();
            foreach (Process process in all)
            {
                try
                {
                    GameProcessSnapshot identity;
                    if (!TryCaptureProcessIdentity(
                            process.Id, ownerSession,
                            out identity))
                        continue;
                    if (IsAntiCheatLikeName(identity.Name))
                        continue;
                    snapshot.Add(identity);
                }
                catch { }
            }

            CaptureWindowEvidence(snapshot);

            long nowFileTime;
            try { nowFileTime = DateTime.UtcNow.ToFileTimeUtc(); }
            catch { return null; }
            return DetectSnapshot(snapshot, profiles, nowFileTime);
        }

        internal static GameDetection DetectSnapshot(
            IList<GameProcessSnapshot> snapshot,
            IList<GameProfile> profiles, long nowFileTime)
        {
            if (snapshot == null || profiles == null
                || profiles.Count == 0 || nowFileTime <= 0)
                return null;
            GameDetection best = null;
            Candidate bestSelected = null;
            int bestScore = int.MinValue;
            var seenSnapshotPids = new HashSet<int>();
            var duplicateSnapshotPids = new HashSet<int>();
            foreach (GameProcessSnapshot identity in snapshot)
                if (identity != null && identity.Pid > 0
                    && !seenSnapshotPids.Add(identity.Pid))
                    duplicateSnapshotPids.Add(identity.Pid);

            foreach (GameProfile profile in profiles)
            {
                if (profile == null) continue;
                var candidates = new List<Candidate>();
                var byPid = new Dictionary<int, Candidate>();
                var entries = new List<Candidate>();
                foreach (GameProcessSnapshot source in snapshot)
                {
                    try
                    {
                        if (source == null || source.Pid <= 0
                            || source.Creation <= 0
                            || duplicateSnapshotPids.Contains(
                                source.Pid)
                            || string.IsNullOrEmpty(source.Name))
                            continue;
                        string name = source.Name;
                        string path = source.Path;
                        bool rooted = profile.ContainsPath(path);
                        bool namedEntry = profile.Entries.Contains(name);
                        bool legacyEntry =
                            string.IsNullOrEmpty(profile.Root) && namedEntry;
                        bool exactEntry =
                            !string.IsNullOrEmpty(profile.ExecutablePath)
                                ? SamePath(profile.ExecutablePath, path)
                                : namedEntry
                                    && (string.IsNullOrEmpty(
                                            profile.Root)
                                        || rooted);

                        bool fallbackEntry = rooted && !exactEntry
                            && IsFallbackEntryName(profile, name);
                        if (!rooted && !legacyEntry && !exactEntry) continue;
                        bool userSelected = (exactEntry || fallbackEntry) && !string.IsNullOrEmpty(profile.ExecutablePath);
                        if (!userSelected && IsNonGameRole(name, path)) continue;
                        var candidate = new Candidate
                        {
                            Pid = source.Pid, ParentPid = source.ParentPid, Name = name, Path = path,
                            Creation = source.Creation,
                            Visible = source.Visible, Foreground = source.Foreground,
                            ExactEntry = exactEntry, FallbackEntry = fallbackEntry
                        };
                        candidates.Add(candidate);
                        byPid[candidate.Pid] = candidate;
                        if (exactEntry || fallbackEntry)
                            entries.Add(candidate);
                    }
                    catch { }
                }
                if (candidates.Count == 0 || entries.Count == 0)
                    continue;

                var entryPids = new HashSet<int>();
                foreach (Candidate entry in entries)
                    entryPids.Add(entry.Pid);

                var ownerByPid = new Dictionary<int, int>();
                foreach (Candidate candidate in candidates)
                {
                    int owner = OwningEntry(
                        candidate, byPid, entryPids);
                    if (owner > 0)
                        ownerByPid[candidate.Pid] = owner;
                }

                foreach (Candidate entry in entries)
                {
                    var family = new List<Candidate>();
                    foreach (Candidate candidate in candidates)
                    {
                        int owner;
                        if (ownerByPid.TryGetValue(
                                candidate.Pid, out owner)
                            && owner == entry.Pid)
                            family.Add(candidate);
                    }

                    Candidate detached = FindDetachedRenderer(
                        profile, entry, entries, candidates,
                        byPid, entryPids, ownerByPid,
                        nowFileTime);
                    if (detached != null)
                        AddDetachedTree(
                            detached, candidates, byPid,
                            entryPids, family);

                    int localBest;
                    Candidate selected;
                    if (!string.IsNullOrEmpty(
                            profile.ExecutablePath))
                    {
                        selected = entry;
                        localBest = Score(profile, entry);

                        if (IsLauncherLikeName(entry.Name))
                        {
                            Candidate renderer = BestRenderer(
                                profile, family, entry);
                            if (renderer != null)
                            {
                                selected = renderer;
                                localBest = Score(
                                    profile, renderer);
                            }
                        }
                    }
                    else
                    {
                        selected = BestRenderer(
                            profile, family, null);
                        if (selected == null) continue;
                        localBest = Score(profile, selected);
                        if (localBest < 65) continue;
                    }

                    var hit = new GameDetection
                    {
                        Profile = profile.Clone(),
                        RendererPid = selected.Pid,
                        RendererCreation = selected.Creation,
                        RendererName = selected.Name,
                        RendererPath = selected.Path,
                        RendererForeground =
                            selected.Foreground,
                        RendererUserSelected =
                            selected.ExactEntry
                                || selected.FallbackEntry,
                        RendererCandidateSelected = true,
                        Evidence = Evidence(
                            profile, selected, localBest)
                    };
                    foreach (Candidate member in family)
                    {
                        hit.FamilyNames.Add(member.Name);
                        hit.FamilyPids.Add(member.Pid);
                    }

                    if (localBest < bestScore
                        || localBest == bestScore
                            && !Preferred(
                                selected, bestSelected))
                        continue;
                    best = hit;
                    bestSelected = selected;
                    bestScore = localBest;
                }
            }
            return best;
        }

#if AEGIS_SELFTEST
        internal static int Score(GameProfile profile, string name, string path, bool visible, bool foreground)
        {
            return Score(profile, new Candidate { Name = name, Path = path, Visible = visible, Foreground = foreground });
        }

        internal static bool QualifiesRenderer(GameProfile profile, string name, string path, bool visible, bool foreground)
        {
            return IsStrongRendererCandidate(profile,
                new Candidate { Name = name, Path = path, Visible = visible, Foreground = foreground });
        }
#endif

        private static readonly HashSet<string> LauncherRendererNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wegame", "wegame_env", "wegameclient", "steam", "steamwebhelper",
            "epicgameslauncher", "battle.net", "galaxyclient", "ubisoftconnect"
        };

        private static readonly string[] AntiCheatTokens =
        {
            "anticheat", "anti-cheat", "sguard", "tensafe", "easyanticheat",
            "beservice", "battleye", "gameguard", "gamemon", "vgtray", "ace-helper", "ace-base"
        };

        internal static bool IsAntiCheatLikeName(string name)
        {
            if (AntiCheatCatalog.IsKnownProcess(name)) return true;
            string low = (name ?? "").ToLowerInvariant();
            foreach (string t in AntiCheatTokens) if (low.Contains(t)) return true;
            return false;
        }

        internal static bool IsLauncherLikeName(string name)
        {
            string low = (name ?? "").ToLowerInvariant();
            if (LauncherRendererNames.Contains(low)) return true;
            foreach (string token in LauncherRendererTokens) if (low.Contains(token)) return true;
            return false;
        }

        private static int Score(GameProfile profile, Candidate c)
        {
            bool selected = c.FallbackEntry
                || (!string.IsNullOrEmpty(profile.ExecutablePath) && SamePath(profile.ExecutablePath, c.Path));
            if (IsAntiCheatLikeName(c.Name)) return -1000;
            if (!selected && IsNonGameRole(c.Name, c.Path)) return -1000;
            int score = 0;
            if (selected) score += 35;
            if (c.Foreground) score += 45;
            if (c.Visible) score += 30;
            string low = ((c.Path ?? "") + "\\" + (c.Name ?? "")).ToLowerInvariant();
            if (low.Contains("win64") || low.Contains("shipping") || low.Contains("binaries") || low.Contains("\\game\\")) score += 20;
            if (!selected)
                foreach (string token in RendererRejectTokens) if (low.Contains(token)) return -1000;
            return score;
        }

        private static bool IsStrongRendererCandidate(GameProfile profile, Candidate candidate)
        {
            int score = Score(profile, candidate);
            if (score < 65) return false;
            string low = (candidate.Path ?? "").ToLowerInvariant();
            bool renderLayout = low.Contains("\\binaries\\") || low.Contains("\\win64\\")
                || low.Contains("shipping") || low.Contains("\\game\\");
            bool selectedExecutable = candidate.FallbackEntry || (!string.IsNullOrEmpty(profile.ExecutablePath)
                && SamePath(profile.ExecutablePath, candidate.Path));
            return (renderLayout || selectedExecutable) && (candidate.Visible || candidate.Foreground);
        }

        private static string Evidence(GameProfile profile, Candidate c, int score)
        {
            if (c.Foreground) return Lang.F("detect.foreground", score);
            if (c.Visible) return Lang.F("detect.visible", score);
            return Lang.F("detect.candidate", score);
        }

        internal static bool IsNonGameRole(string name, string path)
        {
            string n = (name ?? "").Trim();
            if (AntiCheatCatalog.IsKnownProcess(n) || NeverGames.Contains(n)) return true;
            string low = ((path ?? "") + "\\" + n).ToLowerInvariant();
            foreach (string token in NonGameRoleTokens)
                if (low.Contains(token)) return true;
            return false;
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
            if (!string.IsNullOrEmpty(profile.ExecutablePath))
            {
                if (SamePath(profile.ExecutablePath, path)) return true;
                return profile.ContainsPath(path)
                    && IsFallbackEntryName(profile, name);
            }
            return profile.Entries != null
                && profile.Entries.Contains(name)
                && (string.IsNullOrEmpty(profile.Root)
                    || profile.ContainsPath(path));
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

        private static int OwningEntry(
            Candidate candidate,
            Dictionary<int, Candidate> byPid,
            HashSet<int> entryPids)
        {
            if (candidate == null) return 0;
            if (entryPids.Contains(candidate.Pid))
                return candidate.Pid;

            Candidate child = candidate;
            var visited = new HashSet<int>();
            visited.Add(candidate.Pid);
            for (int depth = 0;
                child.ParentPid > 0 && depth < 24;
                depth++)
            {
                int parentPid = child.ParentPid;
                if (!visited.Add(parentPid)) return 0;
                Candidate parent;
                if (!byPid.TryGetValue(
                        parentPid, out parent))
                    return 0;
                if (!ValidParentEdge(child, parent))
                    return 0;
                if (entryPids.Contains(parentPid))
                    return parentPid;
                child = parent;
            }
            return 0;
        }

        private static bool ValidParentEdge(
            Candidate child, Candidate parent)
        {
            return child != null && parent != null
                && child.Creation > 0
                && parent.Creation > 0
                && child.Creation >= parent.Creation;
        }

        private static Candidate FindDetachedRenderer(
            GameProfile profile, Candidate entry,
            IList<Candidate> entries,
            IList<Candidate> candidates,
            Dictionary<int, Candidate> byPid,
            HashSet<int> entryPids,
            Dictionary<int, int> ownerByPid,
            long nowFileTime)
        {
            if (!IsLauncherLikeName(entry.Name))
                return null;

            Candidate match = null;
            foreach (Candidate candidate in candidates)
            {
                if (candidate.ExactEntry
                    || candidate.FallbackEntry
                    || ownerByPid.ContainsKey(candidate.Pid)
                    || !candidate.Foreground
                    || !IsStrongRendererCandidate(
                        profile, candidate)
                    || candidate.Creation < entry.Creation
                    || HasEntryAncestorIgnoringCreation(
                        candidate, byPid, entryPids))
                    continue;

                long age = nowFileTime - candidate.Creation;
                if (age < -CreationClockSkewMs
                        * TimeSpan.TicksPerMillisecond
                    || age > DetachedMigrationObservationMs
                        * TimeSpan.TicksPerMillisecond)
                    continue;

                Candidate soleLauncher = null;
                int eligibleLaunchers = 0;
                foreach (Candidate possible in entries)
                {
                    if (!IsLauncherLikeName(possible.Name)
                        || possible.Creation <= 0
                        || possible.Creation
                            > candidate.Creation)
                        continue;
                    soleLauncher = possible;
                    eligibleLaunchers++;
                }
                if (eligibleLaunchers != 1
                    || soleLauncher.Pid != entry.Pid)
                    continue;

                if (match != null) return null;
                match = candidate;
            }
            return match;
        }

        private static bool HasEntryAncestorIgnoringCreation(
            Candidate candidate,
            Dictionary<int, Candidate> byPid,
            HashSet<int> entryPids)
        {
            int parentPid = candidate.ParentPid;
            var visited = new HashSet<int>();
            for (int depth = 0;
                parentPid > 0 && depth < 24
                    && visited.Add(parentPid);
                depth++)
            {
                if (entryPids.Contains(parentPid))
                    return true;
                Candidate parent;
                if (!byPid.TryGetValue(parentPid, out parent))
                    return false;
                parentPid = parent.ParentPid;
            }
            return false;
        }

        private static void AddDetachedTree(
            Candidate renderer,
            IList<Candidate> candidates,
            Dictionary<int, Candidate> byPid,
            HashSet<int> entryPids,
            IList<Candidate> family)
        {
            foreach (Candidate candidate in candidates)
            {
                if (candidate.ExactEntry
                    || candidate.FallbackEntry)
                    continue;
                if (candidate.Pid != renderer.Pid
                    && !IsDescendantOfRoot(
                        candidate, renderer.Pid,
                        byPid, entryPids))
                    continue;
                if (!family.Contains(candidate))
                    family.Add(candidate);
            }
        }

        private static bool IsDescendantOfRoot(
            Candidate candidate, int rootPid,
            Dictionary<int, Candidate> byPid,
            HashSet<int> entryPids)
        {
            Candidate child = candidate;
            var visited = new HashSet<int>();
            visited.Add(candidate.Pid);
            for (int depth = 0;
                child.ParentPid > 0 && depth < 24;
                depth++)
            {
                int parentPid = child.ParentPid;
                if (!visited.Add(parentPid)) return false;
                Candidate parent;
                if (!byPid.TryGetValue(parentPid, out parent))
                    return false;
                if (!ValidParentEdge(child, parent))
                    return false;
                if (parentPid == rootPid) return true;
                if (entryPids.Contains(parentPid))
                    return false;
                child = parent;
            }
            return false;
        }

        private static Candidate BestRenderer(
            GameProfile profile,
            IList<Candidate> family,
            Candidate excludedEntry)
        {
            Candidate best = null;
            int bestScore = int.MinValue;
            foreach (Candidate candidate in family)
            {
                if (ReferenceEquals(candidate, excludedEntry)
                    || !IsStrongRendererCandidate(
                        profile, candidate))
                    continue;
                int score = Score(profile, candidate);
                if (score > bestScore
                    || score == bestScore
                        && Preferred(candidate, best))
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            return best;
        }

        private static bool Preferred(
            Candidate candidate, Candidate current)
        {
            if (candidate == null) return false;
            if (current == null) return true;
            if (candidate.Foreground != current.Foreground)
                return candidate.Foreground;
            if (candidate.Visible != current.Visible)
                return candidate.Visible;
            if (candidate.Creation != current.Creation)
                return candidate.Creation > current.Creation;
            return candidate.Pid < current.Pid;
        }

        internal static bool TryCaptureProcessIdentity(
            int pid, int ownerSession,
            out GameProcessSnapshot identity)
        {
            identity = null;
            if (pid <= 0 || ownerSession < 0) return false;
            IntPtr h = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION
                    | Native.SYNCHRONIZE,
                false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                if (!GetProcessTimes(
                        h, out creation, out exit,
                        out kernel, out user)
                    || creation <= 0)
                    return false;
                string path = Native.ImagePath(h);
                string name = ImageNameFromVerifiedPath(path);
                int session;
                if (string.IsNullOrEmpty(name)
                    || !Native.TryGetLiveProcessSessionId(
                        h, pid, out session)
                    || session != ownerSession)
                    return false;
                identity = new GameProcessSnapshot
                {
                    Pid = pid,
                    ParentPid = Native.ParentProcessId(h),
                    Creation = creation,
                    Name = name,
                    Path = path
                };
                return true;
            }
            finally { Native.CloseHandle(h); }
        }

        internal static string ImageNameFromVerifiedPath(
            string imagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath))
                    return null;
                string leaf = Path.GetFileName(imagePath.Trim());
                if (string.IsNullOrWhiteSpace(leaf))
                    return null;
                string name = Path.GetFileNameWithoutExtension(leaf);
                return string.IsNullOrWhiteSpace(name)
                    ? null : name.Trim();
            }
            catch { return null; }
        }

        private static void CaptureWindowEvidence(
            IList<GameProcessSnapshot> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0)
                return;
            int foreground = ForegroundPid();
            HashSet<int> visible = VisibleWindowPids(false);
            foreach (GameProcessSnapshot identity in snapshot)
            {
                if (identity == null || identity.Pid <= 0
                    || identity.Creation <= 0)
                    continue;
                bool foregroundClaim =
                    identity.Pid == foreground;
                bool visibleClaim =
                    visible.Contains(identity.Pid);
                if (!foregroundClaim && !visibleClaim)
                    continue;

                if (!IsLiveProcessCreation(
                        identity.Pid, identity.Creation))
                    continue;
                identity.Foreground = foregroundClaim;
                identity.Visible = visibleClaim;
            }
        }

        private static bool IsLiveProcessCreation(
            int pid, long expectedCreation)
        {
            if (pid <= 0 || expectedCreation <= 0)
                return false;
            IntPtr handle = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION
                    | Native.SYNCHRONIZE,
                false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                int session;
                return GetProcessTimes(
                        handle, out creation, out exit,
                        out kernel, out user)
                    && creation == expectedCreation
                    && Native.TryGetLiveProcessSessionId(
                        handle, pid, out session);
            }
            finally { Native.CloseHandle(handle); }
        }

#if AEGIS_SELFTEST
        internal static bool HasUserFacingWindow(Process p)
        {
            try
            {
                IntPtr h = p.MainWindowHandle;
                return h != IntPtr.Zero && IsWindowVisible(h);
            }
            catch { return false; }
        }
#endif

        internal static HashSet<int> VisibleWindowPids(bool includeMinimized)
        {
            var result = new HashSet<int>();
            try
            {
                EnumWindows(delegate(IntPtr window, IntPtr state)
                {
                    try
                    {
                        if (!IsWindowVisible(window)
                            || GetWindow(window, GwOwner) != IntPtr.Zero
                            || (GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0
                            || !includeMinimized && IsIconic(window))
                            return true;
                        int cloaked;
                        if (DwmGetWindowAttribute(
                                window, DwmwaCloaked, out cloaked, sizeof(int)) == 0
                            && cloaked != 0)
                            return true;
                        NativeRect rect;
                        if (!GetWindowRect(window, out rect)
                            || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                            return true;
                        uint pid;
                        GetWindowThreadProcessId(window, out pid);
                        if (pid > 0 && pid <= int.MaxValue) result.Add((int)pid);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return result;
        }

        internal static int ForegroundPid()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return (int)pid;
            }
            catch { return -1; }
        }

        private sealed class Candidate
        {
            public int Pid;
            public int ParentPid;
            public long Creation;
            public string Name;
            public string Path;
            public bool Visible;
            public bool Foreground;
            public bool ExactEntry;
            public bool FallbackEntry;
        }

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint GwOwner = 4;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x80;
        private const uint DwmwaCloaked = 14;
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool EnumWindows(
            EnumWindowsCallback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(
            IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern int GetWindowLong(
            IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(
            IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(
            IntPtr process, out long creation, out long exit,
            out long kernel, out long user);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(
            IntPtr window, uint attribute, out int value, int size);
    }
}
