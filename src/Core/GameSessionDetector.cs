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
        public string RendererName;
        public string RendererPath;
        public bool RendererCandidateSelected;
        public bool RendererUserSelected;
        public readonly HashSet<string> FamilyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> FamilyPids = new HashSet<int>();
        public string Evidence;
    }

    internal static class GameSessionDetector
    {
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
            if (all == null || profiles == null || profiles.Count == 0) return null;
            int foreground = ForegroundPid();
            GameDetection best = null;
            int bestScore = int.MinValue;

            foreach (GameProfile profile in profiles)
            {
                if (profile == null) continue;
                var candidates = new List<Candidate>();
                var byPid = new Dictionary<int, Candidate>();
                var entryPids = new HashSet<int>();
                foreach (Process p in all)
                {
                    try
                    {
                        string name = p.ProcessName;
                        string path;
                        int parentPid;
                        ProcessIdentity(p.Id, out path, out parentPid);
                        bool rooted = profile.ContainsPath(path);
                        bool legacyEntry = string.IsNullOrEmpty(profile.Root) && profile.Entries.Contains(name);
                        if (IsAntiCheatLikeName(name)) continue;
                        bool fallbackEntry = false;
                        if (!rooted && !legacyEntry)
                        {
                            if (!IsFallbackEntryName(profile, name)) continue;
                            fallbackEntry = true;
                        }
                        bool exactEntry = !string.IsNullOrEmpty(profile.ExecutablePath)
                            ? SamePath(profile.ExecutablePath, path) : profile.Entries.Contains(name);
                        bool userSelected = (exactEntry || fallbackEntry) && !string.IsNullOrEmpty(profile.ExecutablePath);
                        if (!userSelected && IsNonGameRole(name, path)) continue;
                        var candidate = new Candidate
                        {
                            Pid = p.Id, ParentPid = parentPid, Name = name, Path = path,
                            Visible = HasVisibleWindow(p), Foreground = p.Id == foreground,
                            ExactEntry = exactEntry, FallbackEntry = fallbackEntry
                        };
                        candidates.Add(candidate);
                        byPid[candidate.Pid] = candidate;
                        if (exactEntry || fallbackEntry) entryPids.Add(candidate.Pid);
                    }
                    catch { }
                }
                if (candidates.Count == 0 || entryPids.Count == 0) continue;

                var family = new List<Candidate>();
                foreach (Candidate candidate in candidates)
                    if (candidate.ExactEntry || candidate.FallbackEntry || IsDescendantOfEntry(candidate, byPid, entryPids)
                        || IsStrongRendererCandidate(profile, candidate)) family.Add(candidate);

                var hit = new GameDetection { Profile = profile.Clone() };
                foreach (Candidate c in family) { hit.FamilyNames.Add(c.Name); hit.FamilyPids.Add(c.Pid); }
                int localBest = int.MinValue;
                Candidate selected = null;
                if (!string.IsNullOrEmpty(profile.ExecutablePath))
                {
                    foreach (Candidate c in family)
                    {
                        if (!c.ExactEntry && !c.FallbackEntry) continue;
                        int score = Score(profile, c);
                        if (score > localBest) { localBest = score; selected = c; }
                    }
                    if (selected == null) continue;
                }
                else
                {
                    foreach (Candidate c in family)
                    {
                        if (!IsStrongRendererCandidate(profile, c)) continue;
                        int score = Score(profile, c);
                        if (score > localBest) { localBest = score; selected = c; }
                    }
                    if (selected == null || localBest < 65) continue;
                }
                hit.RendererPid = selected.Pid;
                hit.RendererName = selected.Name;
                hit.RendererPath = selected.Path;
                hit.RendererUserSelected = selected.ExactEntry || selected.FallbackEntry;
                hit.RendererCandidateSelected = true;
                hit.Evidence = Evidence(profile, selected, localBest);
                int sessionScore = localBest;
                if (sessionScore <= bestScore) continue;
                best = hit;
                bestScore = sessionScore;
            }
            return best;
        }

        internal static int Score(GameProfile profile, string name, string path, bool visible, bool foreground)
        {
            return Score(profile, new Candidate { Name = name, Path = path, Visible = visible, Foreground = foreground });
        }

        internal static bool QualifiesRenderer(GameProfile profile, string name, string path, bool visible, bool foreground)
        {
            return IsStrongRendererCandidate(profile,
                new Candidate { Name = name, Path = path, Visible = visible, Foreground = foreground });
        }

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

        private static bool IsDescendantOfEntry(Candidate candidate, Dictionary<int, Candidate> byPid, HashSet<int> entryPids)
        {
            int parent = candidate.ParentPid;
            var visited = new HashSet<int>();
            for (int depth = 0; parent > 0 && depth < 24 && visited.Add(parent); depth++)
            {
                if (entryPids.Contains(parent)) return true;
                Candidate ancestor;
                if (!byPid.TryGetValue(parent, out ancestor)) return false;
                parent = ancestor.ParentPid;
            }
            return false;
        }

        private static void ProcessIdentity(int pid, out string path, out int parentPid)
        {
            path = null;
            parentPid = 0;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return;
            try { path = Native.ImagePath(h); parentPid = Native.ParentProcessId(h); }
            finally { Native.CloseHandle(h); }
        }

        private static bool HasVisibleWindow(Process p)
        {
            try
            {
                IntPtr h = p.MainWindowHandle;
                return h != IntPtr.Zero && IsWindowVisible(h) && !IsIconic(h);
            }
            catch { return false; }
        }

        internal static bool HasUserFacingWindow(Process p)
        {
            try
            {
                IntPtr h = p.MainWindowHandle;
                return h != IntPtr.Zero && IsWindowVisible(h);
            }
            catch { return false; }
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
            public string Name;
            public string Path;
            public bool Visible;
            public bool Foreground;
            public bool ExactEntry;
            public bool FallbackEntry;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    }
}
