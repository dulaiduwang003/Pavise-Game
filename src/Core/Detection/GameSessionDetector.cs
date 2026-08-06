// @author bdth 2074055628@qq.com
// 文件用途 证据选举制的游戏会话判定 用户的选择只圈定家族 渲染进程由硬证据现场选举

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
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
        public bool RendererLearnable;
        public bool RequiresGpuConfirm;
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
        public bool FullscreenLike;
    }

    internal static class GameSessionDetector
    {
        internal const int FullscreenCoveragePercent = 97;

        private static readonly string[] NonGameRoleTokens =
        {
            "anticheat", "anti-cheat", "ace-helper", "ace-base", "sguard", "tensafe",
            "easyanticheat", "beservice", "battleye", "gameguard", "gamemon", "vgtray",
            "crashreport", "crash_report", "crashpad", "crashhandler", "crashsender",
            "telemetry", "uninstall"
        };

        private static readonly HashSet<string> NeverGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "powerpnt", "winword", "excel", "outlook", "acrord32", "notepad", "mspaint",
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
            "explorer", "wegame", "wegame_env", "steam", "steamwebhelper", "epicgameslauncher",
            "battle.net", "agent", "galaxyclient", "ubisoftconnect",
            "vlc", "mpv", "wmplayer", "video.ui",
            "potplayermini64", "potplayermini", "mpc-hc64", "mpc-hc"
        };

        private static readonly HashSet<string> StorefrontShellNames = BuildStorefrontShellNames();

        private static HashSet<string> BuildStorefrontShellNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in GamePlatformCatalog.PlatformShellNames()) names.Add(name);
            return names;
        }

        private static readonly string[] ClientShellTokens = { "leagueclient", "riotclient" };

        private static readonly string[] AntiCheatTokens =
        {
            "anticheat", "anti-cheat", "sguard", "tensafe", "easyanticheat",
            "beservice", "battleye", "gameguard", "gamemon", "vgtray", "ace-helper", "ace-base"
        };

        public static GameDetection Detect(Process[] all, IList<GameProfile> profiles)
        {
            bool armed;
            return Detect(all, profiles, out armed);
        }

        public static GameDetection Detect(Process[] all, IList<GameProfile> profiles, out bool armed)
        {
            armed = false;
            int ownerSession;
            try
            {
                using (Process current = Process.GetCurrentProcess())
                    ownerSession = current.SessionId;
            }
            catch { return null; }
            return Detect(all, profiles, ownerSession, out armed);
        }

        public static GameDetection Detect(
            Process[] all, IList<GameProfile> profiles, int ownerSession)
        {
            bool armed;
            return Detect(all, profiles, ownerSession, out armed);
        }

        public static GameDetection Detect(
            Process[] all, IList<GameProfile> profiles,
            int ownerSession, out bool armed)
        {
            string armedProfile;
            GameDetection hit = Detect(all, profiles, ownerSession, out armedProfile);
            armed = armedProfile != null;
            return hit;
        }

        public static GameDetection Detect(
            Process[] all, IList<GameProfile> profiles,
            int ownerSession, out string armedProfile)
        {
            armedProfile = null;
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
            return DetectSnapshot(snapshot, profiles, out armedProfile);
        }

        internal static GameDetection DetectSnapshot(
            IList<GameProcessSnapshot> snapshot,
            IList<GameProfile> profiles)
        {
            bool armed;
            return DetectSnapshot(snapshot, profiles, out armed);
        }

        internal static GameDetection DetectSnapshot(
            IList<GameProcessSnapshot> snapshot,
            IList<GameProfile> profiles, out bool armed)
        {
            string armedProfile;
            GameDetection hit = DetectSnapshot(snapshot, profiles, out armedProfile);
            armed = armedProfile != null;
            return hit;
        }

        internal static GameDetection DetectSnapshot(
            IList<GameProcessSnapshot> snapshot,
            IList<GameProfile> profiles, out string armedProfile)
        {
            armedProfile = null;
            if (snapshot == null || profiles == null || profiles.Count == 0)
                return null;

            var seenPids = new HashSet<int>();
            var duplicatePids = new HashSet<int>();
            foreach (GameProcessSnapshot identity in snapshot)
                if (identity != null && identity.Pid > 0
                    && !seenPids.Add(identity.Pid))
                    duplicatePids.Add(identity.Pid);

            var byPid = new Dictionary<int, GameProcessSnapshot>();
            foreach (GameProcessSnapshot identity in snapshot)
            {
                if (identity == null || identity.Pid <= 0
                    || identity.Creation <= 0
                    || duplicatePids.Contains(identity.Pid)
                    || string.IsNullOrEmpty(identity.Name)
                    || string.IsNullOrEmpty(identity.Path))
                    continue;
                byPid[identity.Pid] = identity;
            }
            if (byPid.Count == 0) return null;

            GameDetection best = null;
            foreach (GameProfile profile in profiles)
            {
                if (profile == null) continue;
                var memberPids = new HashSet<int>();
                var members = new List<GameProcessSnapshot>();
                foreach (GameProcessSnapshot identity in byPid.Values)
                    if (IsDirectMember(profile, identity))
                    {
                        memberPids.Add(identity.Pid);
                        members.Add(identity);
                    }
                if (members.Count == 0) continue;
                foreach (GameProcessSnapshot identity in byPid.Values)
                    if (!memberPids.Contains(identity.Pid)
                        && HasMemberAncestor(identity, byPid, memberPids))
                    {
                        memberPids.Add(identity.Pid);
                        members.Add(identity);
                    }

                if (armedProfile == null) armedProfile = profile.Name;
                GameDetection hit = Elect(profile, members);
                if (hit == null) continue;
                foreach (GameProcessSnapshot member in members)
                {
                    hit.FamilyNames.Add(member.Name);
                    hit.FamilyPids.Add(member.Pid);
                }
                if (BetterHit(hit, best)) best = hit;
            }
            return best;
        }

        private static bool IsDirectMember(GameProfile profile, GameProcessSnapshot identity)
        {
            if (SamePath(profile.ExecutablePath, identity.Path)) return true;
            if (SamePath(profile.LearnedExecutablePath, identity.Path)) return true;
            return profile.ContainsPath(identity.Path);
        }

        private static bool HasMemberAncestor(
            GameProcessSnapshot identity,
            Dictionary<int, GameProcessSnapshot> byPid,
            HashSet<int> memberPids)
        {
            GameProcessSnapshot child = identity;
            var visited = new HashSet<int>();
            visited.Add(identity.Pid);
            for (int depth = 0; child.ParentPid > 0 && depth < 24; depth++)
            {
                int parentPid = child.ParentPid;
                if (!visited.Add(parentPid)) return false;
                GameProcessSnapshot parent;
                if (!byPid.TryGetValue(parentPid, out parent)) return false;
                if (child.Creation <= 0 || parent.Creation <= 0
                    || child.Creation < parent.Creation) return false;
                if (memberPids.Contains(parentPid)) return true;
                child = parent;
            }
            return false;
        }

        private static GameDetection Elect(GameProfile profile, List<GameProcessSnapshot> members)
        {
            GameProcessSnapshot learned = null;
            GameProcessSnapshot foreground = null;
            foreach (GameProcessSnapshot member in members)
            {
                if (!member.Visible && !member.Foreground) continue;
                if (ElectionVetoed(member.Name, member.Path)) continue;
                if (SamePath(profile.LearnedExecutablePath, member.Path)
                    && (learned == null || PreferSnapshot(member, learned)))
                    learned = member;
                if (member.Foreground
                    && (foreground == null || member.Creation > foreground.Creation))
                    foreground = member;
            }

            if (foreground != null && foreground.FullscreenLike)
                return Elected(profile, foreground,
                    !SamePath(profile.ExecutablePath, foreground.Path)
                        && !SamePath(profile.LearnedExecutablePath, foreground.Path),
                    Lang.T("detect.fullscreen"));
            if (learned != null)
                return Elected(profile, learned, false, Lang.T("detect.learned"));
            if (foreground == null) return null;
            if (SamePath(profile.ExecutablePath, foreground.Path))
                return Elected(profile, foreground, false, Lang.T("detect.window"));
            return PendingGpuConfirm(profile, foreground);
        }

        internal static bool ElectionVetoed(string name, string path)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (IsAntiCheatLikeName(name)) return true;
            if (NeverGames.Contains(name)) return true;
            if (IsLauncherLikeName(name)) return true;
            return IsNonGameRole(name, path);
        }

        private static GameDetection Elected(
            GameProfile profile, GameProcessSnapshot selected,
            bool learnable, string evidence)
        {
            return new GameDetection
            {
                Profile = profile.Clone(),
                RendererPid = selected.Pid,
                RendererCreation = selected.Creation,
                RendererName = selected.Name,
                RendererPath = selected.Path,
                RendererForeground = selected.Foreground,
                RendererCandidateSelected = true,
                RendererUserSelected =
                    SamePath(profile.ExecutablePath, selected.Path)
                        || SamePath(profile.LearnedExecutablePath, selected.Path),
                RendererLearnable = learnable,
                Evidence = evidence
            };
        }

        private static GameDetection PendingGpuConfirm(
            GameProfile profile, GameProcessSnapshot candidate)
        {
            return new GameDetection
            {
                Profile = profile.Clone(),
                RendererPid = candidate.Pid,
                RendererCreation = candidate.Creation,
                RendererName = candidate.Name,
                RendererPath = candidate.Path,
                RendererForeground = candidate.Foreground,
                RendererCandidateSelected = false,
                RendererUserSelected = false,
                RendererLearnable = true,
                RequiresGpuConfirm = true,
                Evidence = Lang.T("detect.gpu.pending")
            };
        }

        private static bool PreferSnapshot(
            GameProcessSnapshot candidate, GameProcessSnapshot current)
        {
            if (candidate.Foreground != current.Foreground) return candidate.Foreground;
            if (candidate.Creation != current.Creation)
                return candidate.Creation > current.Creation;
            return candidate.Pid < current.Pid;
        }

        private static bool BetterHit(GameDetection candidate, GameDetection current)
        {
            if (candidate == null) return false;
            if (current == null) return true;
            bool candidateElected = !candidate.RequiresGpuConfirm;
            bool currentElected = !current.RequiresGpuConfirm;
            if (candidateElected != currentElected) return candidateElected;
            if (candidate.RendererForeground != current.RendererForeground)
                return candidate.RendererForeground;
            if (candidate.RendererCreation != current.RendererCreation)
                return candidate.RendererCreation > current.RendererCreation;
            return candidate.RendererPid < current.RendererPid;
        }

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
            if (low.Length == 0) return false;
            if (StorefrontShellNames.Contains(low)) return true;
            foreach (string token in ClientShellTokens) if (low.Contains(token)) return true;
            return false;
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
            bool foregroundFullscreen = foreground > 0
                && ForegroundWindowFullscreenLike(foreground);
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
                identity.FullscreenLike =
                    foregroundClaim && foregroundFullscreen;
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

#if PAVISE_SELFTEST
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

        internal static bool TryForegroundFullscreen(out int pid)
        {
            pid = 0;
            try
            {
                IntPtr window = GetForegroundWindow();
                if (window == IntPtr.Zero) return false;
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner == 0 || owner > int.MaxValue) return false;
                pid = (int)owner;
                if (!IsWindowVisible(window) || IsIconic(window)) return false;
                NativeRect rect;
                if (!GetWindowRect(window, out rect)) return false;
                return IsFullscreenLikeWindow(window, rect);
            }
            catch { return false; }
        }

        private static bool ForegroundWindowFullscreenLike(int pid)
        {
            int owner;
            return TryForegroundFullscreen(out owner) && owner == pid;
        }

        internal static bool IsFullscreenLikeWindow(IntPtr window, NativeRect rect)
        {
            try
            {
                int style = GetWindowLong(window, GwlStyle);
                if ((style & WsCaption) == WsCaption) return false;
                IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return false;
                var info = new MonitorInfo();
                info.Size = Marshal.SizeOf(typeof(MonitorInfo));
                if (!GetMonitorInfo(monitor, ref info)) return false;
                return RectCoversMonitor(rect, info.Monitor);
            }
            catch { return false; }
        }

        internal static bool RectCoversMonitor(NativeRect rect, NativeRect monitor)
        {
            long monitorArea = (long)(monitor.Right - monitor.Left)
                * (monitor.Bottom - monitor.Top);
            if (monitorArea <= 0) return false;
            int left = Math.Max(rect.Left, monitor.Left);
            int top = Math.Max(rect.Top, monitor.Top);
            int right = Math.Min(rect.Right, monitor.Right);
            int bottom = Math.Min(rect.Bottom, monitor.Bottom);
            long covered = right > left && bottom > top
                ? (long)(right - left) * (bottom - top) : 0;
            return covered * 100 >= monitorArea * FullscreenCoveragePercent;
        }

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

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        private const uint GwOwner = 4;
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x80;
        private const int WsCaption = 0x00C00000;
        private const uint DwmwaCloaked = 14;
        private const uint MonitorDefaultToNearest = 2;
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
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(
            IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(
            IntPtr process, out long creation, out long exit,
            out long kernel, out long user);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(
            IntPtr window, uint attribute, out int value, int size);
    }
}
