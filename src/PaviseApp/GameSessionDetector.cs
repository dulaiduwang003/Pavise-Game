using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class GameSessionDetector
    {
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

        internal struct NativeRect
        {
            public int Left;

            public int Top;

            public int Right;

            public int Bottom;
        }

        private struct NativePoint
        {
            public int X;

            public int Y;
        }

        private struct MonitorInfo
        {
            public int Size;

            public NativeRect Monitor;

            public NativeRect Work;

            public uint Flags;
        }

        internal const int FullscreenCoveragePercent = 97;

        private static readonly string WindowsRootPrefix = CaptureWindowsRootPrefix();

        private static readonly object visibleCacheSync = new object();

        private static HashSet<int> visibleCacheAll;

        private static HashSet<int> visibleCacheShown;

        private static long visibleCacheAllTicks;

        private static long visibleCacheShownTicks;

        private static string CaptureWindowsRootPrefix()
        {
            try
            {
                string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return string.IsNullOrEmpty(folderPath) ? null : (folderPath.TrimEnd('\\') + "\\");
            }
            catch
            {
                return null;
            }
        }

        public static GameDetection Detect(Process[] all, IList<GameProfile> profiles)
        {
            bool armed;
            return Detect(all, profiles, out armed);
        }

        public static GameDetection Detect(Process[] all, IList<GameProfile> profiles, out bool armed)
        {
            armed = false;
            int sessionId;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    sessionId = process.SessionId;
                }
            }
            catch
            {
                return null;
            }
            return Detect(all, profiles, sessionId, out armed);
        }

        public static GameDetection Detect(Process[] all, IList<GameProfile> profiles, int ownerSession)
        {
            bool armed;
            return Detect(all, profiles, ownerSession, out armed);
        }

        public static GameDetection Detect(Process[] all, IList<GameProfile> profiles, int ownerSession, out bool armed)
        {
            string armedProfile;
            GameDetection result = Detect(all, profiles, ownerSession, out armedProfile);
            armed = armedProfile != null;
            return result;
        }

        public static GameDetection Detect(Process[] all, IList<GameProfile> profiles, int ownerSession, out string armedProfile)
        {
            armedProfile = null;
            if (all == null || profiles == null || profiles.Count == 0 || ownerSession < 0)
            {
                return null;
            }
            List<GameProcessSnapshot> identities = new List<GameProcessSnapshot>();
            foreach (Process process in all)
            {
                try
                {
                    GameProcessSnapshot identity;
                    if (TryCaptureProcessIdentity(process.Id, ownerSession, out identity) && !AntiCheatCatalog.IsAntiCheatProcess(identity.Name, identity.Path))
                    {
                        identities.Add(identity);
                    }
                }
                catch
                {
                }
            }
            CaptureWindowEvidence(identities);
            return DetectSnapshot((IList<GameProcessSnapshot>)identities, profiles, out armedProfile);
        }

        public static GameDetection Detect(ProcessSnapshot processes, IList<GameProfile> profiles, int ownerSession, out string armedProfile)
        {
            string armedVia;
            return Detect(processes, profiles, ownerSession, out armedProfile, out armedVia);
        }

        public static GameDetection Detect(ProcessSnapshot processes, IList<GameProfile> profiles, int ownerSession, out string armedProfile, out string armedVia, GameFamilyEvidence familyEvidence = null)
        {
            armedProfile = null;
            armedVia = null;
            if (processes == null || profiles == null || profiles.Count == 0 || ownerSession < 0)
            {
                return null;
            }
            List<GameProcessSnapshot> identities = new List<GameProcessSnapshot>();
            foreach (ProcEntry entry in processes.Entries)
            {
                GameProcessSnapshot identity;
                if (TryCaptureProcessIdentity(entry, ownerSession, out identity) && !AntiCheatCatalog.IsAntiCheatProcess(identity.Name, identity.Path))
                {
                    identities.Add(identity);
                }
            }
            CaptureWindowEvidence(identities);
            return DetectSnapshot(identities, profiles, out armedProfile, out armedVia, familyEvidence);
        }

        internal static bool TryCaptureProcessIdentity(ProcEntry entry, int ownerSession, out GameProcessSnapshot identity)
        {
            identity = null;
            if (entry == null || entry.Pid <= 0 || ownerSession < 0)
            {
                return false;
            }
            if (entry.Creation <= 0 || entry.Session != ownerSession)
            {
                return false;
            }
            string imageNameFromVerifiedPathResult = ImageNameFromVerifiedPath(entry.Path);
            if (string.IsNullOrEmpty(imageNameFromVerifiedPathResult))
            {
                return false;
            }
            identity = new GameProcessSnapshot
            {
                Pid = entry.Pid,
                ParentPid = entry.ParentPid,
                Creation = entry.Creation,
                Name = imageNameFromVerifiedPathResult,
                Path = entry.Path
            };
            return true;
        }

        internal static GameDetection CaptureForegroundCandidate(ProcessSnapshot processes, int ownerSession, GameProfile profile, GameDetection incumbent)
        {
            if (profile == null)
            {
                return null;
            }
            return CaptureForegroundCandidate(processes, ownerSession, new GameProfile[1] { profile }, incumbent);
        }

        internal static GameDetection CaptureForegroundCandidate(ProcessSnapshot processes, int ownerSession, IList<GameProfile> profiles, GameDetection incumbent, GameFamilyEvidence familyEvidence = null)
        {
            if (processes == null || ownerSession < 0 || profiles == null || profiles.Count == 0)
            {
                return null;
            }
            int foregroundPidResult = ForegroundPid();
            if (foregroundPidResult <= 0)
            {
                return null;
            }
            ProcEntry entry = processes.Find(foregroundPidResult);
            GameProcessSnapshot identity;
            if (!TryCaptureProcessIdentity(entry, ownerSession, out identity) || SameRendererIdentity(incumbent, identity))
            {
                return null;
            }
            if (!CandidateIdentityUsable(identity) || AntiCheatCatalog.IsAntiCheatProcess(identity.Name, identity.Path))
            {
                return null;
            }
            if (ElectionVetoed(identity.Name, identity.Path))
            {
                return null;
            }
            int pid;
            bool fullscreen;
            if (!TryCaptureCandidateWindow(out pid, out fullscreen) || pid != foregroundPidResult)
            {
                return null;
            }
            if (processes.ByPid.Count != processes.Count)
            {
                return null;
            }
            List<GameProcessSnapshot> gameProcessSnapshots = new List<GameProcessSnapshot>();
            foreach (ProcEntry entry2 in processes.Entries)
            {
                GameProcessSnapshot identity2;
                if (TryCaptureProcessIdentity(entry2, ownerSession, out identity2))
                {
                    identity2.Foreground = identity2.Pid == foregroundPidResult;
                    identity2.Visible = identity2.Foreground;
                    identity2.FullscreenLike = identity2.Foreground && fullscreen;
                    gameProcessSnapshots.Add(identity2);
                }
            }
            GameDetection gameDetection = FindForegroundCandidateSnapshot(gameProcessSnapshots, profiles, incumbent, familyEvidence);
            if (gameDetection == null)
            {
                return null;
            }
            GameProcessSnapshot identity3;
            if (!TryCaptureProcessIdentity(gameDetection.RendererPid, ownerSession, out identity3) || identity3.Creation != gameDetection.RendererCreation || !SamePath(identity3.Path, gameDetection.RendererPath) || !string.Equals(identity3.Name, gameDetection.RendererName, StringComparison.OrdinalIgnoreCase) || ForegroundPid() != gameDetection.RendererPid)
            {
                return null;
            }
            return gameDetection;
        }

        internal static GameDetection FindForegroundCandidateSnapshot(IList<GameProcessSnapshot> snapshot, GameProfile profile, GameDetection incumbent, GameFamilyEvidence familyEvidence = null)
        {
            if (profile == null)
            {
                return null;
            }
            Dictionary<int, GameProcessSnapshot> byPid;
            GameProcessSnapshot foreground;
            if (!TryIndexForegroundCandidate(snapshot, out byPid, out foreground) || SameRendererIdentity(incumbent, foreground))
            {
                return null;
            }
            return FindForegroundCandidateInProfile(byPid, foreground, profile, familyEvidence, incumbent);
        }

        internal static GameDetection FindForegroundCandidateSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles, GameDetection incumbent, GameFamilyEvidence familyEvidence = null)
        {
            if (profiles == null || profiles.Count == 0)
            {
                return null;
            }
            Dictionary<int, GameProcessSnapshot> byPid;
            GameProcessSnapshot foreground;
            if (!TryIndexForegroundCandidate(snapshot, out byPid, out foreground) || SameRendererIdentity(incumbent, foreground))
            {
                return null;
            }
            GameDetection gameDetection = null;
            bool hasAmbiguousOwner = false;
            foreach (GameProfile profile in profiles)
            {
                if (profile == null)
                {
                    continue;
                }
                GameDetection foregroundCandidateInProfile = FindForegroundCandidateInProfile(byPid, foreground, profile, familyEvidence, incumbent);
                if (foregroundCandidateInProfile != null)
                {
                    int compareCandidateOwnershipResult = CompareCandidateOwnership(foregroundCandidateInProfile, gameDetection);
                    if (compareCandidateOwnershipResult > 0)
                    {
                        gameDetection = foregroundCandidateInProfile;
                        hasAmbiguousOwner = false;
                    }
                    else if (compareCandidateOwnershipResult == 0 && !SameCandidateOwner(foregroundCandidateInProfile.Profile, gameDetection.Profile))
                    {
                        hasAmbiguousOwner = true;
                    }
                }
            }
            if (!hasAmbiguousOwner)
            {
                return gameDetection;
            }
            return null;
        }

        private static bool TryIndexForegroundCandidate(IList<GameProcessSnapshot> snapshot, out Dictionary<int, GameProcessSnapshot> byPid, out GameProcessSnapshot foreground)
        {
            byPid = new Dictionary<int, GameProcessSnapshot>();
            foreground = null;
            if (snapshot == null)
            {
                return false;
            }
            HashSet<int> pids = new HashSet<int>();
            HashSet<int> currentPids = new HashSet<int>();
            foreach (GameProcessSnapshot currentSnapshot in snapshot)
            {
                if (currentSnapshot != null && currentSnapshot.Pid > 0 && !pids.Add(currentSnapshot.Pid))
                {
                    currentPids.Add(currentSnapshot.Pid);
                }
            }
            foreach (GameProcessSnapshot candidateSnapshot in snapshot)
            {
                if (!CandidateIdentityUsable(candidateSnapshot) || currentPids.Contains(candidateSnapshot.Pid) || AntiCheatCatalog.IsAntiCheatProcess(candidateSnapshot.Name, candidateSnapshot.Path))
                {
                    continue;
                }
                byPid.Add(candidateSnapshot.Pid, candidateSnapshot);
                if (candidateSnapshot.Foreground)
                {
                    if (foreground != null)
                    {
                        return false;
                    }
                    foreground = candidateSnapshot;
                }
            }
            return foreground != null;
        }

        private static GameDetection FindForegroundCandidateInProfile(Dictionary<int, GameProcessSnapshot> byPid, GameProcessSnapshot foreground, GameProfile profile, GameFamilyEvidence familyEvidence, GameDetection incumbent = null)
        {
            bool registeredExecutable = SamePath(profile.ExecutablePath, foreground.Path) || SamePath(profile.LearnedExecutablePath, foreground.Path);
            // 现有渲染进程仍存活时，不能仅凭全屏就提升新候选；还需 GPU 证据以排除全屏网页及 SDK 覆盖层。

            bool challengesLiveRenderer = !registeredExecutable && IncumbentRendererLive(incumbent, profile, foreground, byPid);
            if (ElectionVetoed(foreground.Name, foreground.Path))
            {
                return null;
            }
            HashSet<int> pids = new HashSet<int>();
            foreach (GameProcessSnapshot processSnapshot in byPid.Values)
            {
                if (IsDirectMember(profile, processSnapshot, familyEvidence))
                {
                    pids.Add(processSnapshot.Pid);
                }
            }
            if (!pids.Contains(foreground.Pid) && !HasMemberAncestor(foreground, byPid, pids))
            {
                return null;
            }
            GameDetection gameDetection;
            if (profile.ForceTrigger && registeredExecutable)
            {
                gameDetection = ElectForced(profile, new List<GameProcessSnapshot> { foreground });
            }
            else if (!profile.ForceTrigger)
            {
                gameDetection = (foreground.FullscreenLike && !challengesLiveRenderer ? Elected(profile, foreground, !registeredExecutable, Lang.T("detect.fullscreen")) : (SamePath(profile.LearnedExecutablePath, foreground.Path) ? Elected(profile, foreground, false, Lang.T("detect.learned")) : ((!SamePath(profile.ExecutablePath, foreground.Path)) ? PendingGpuConfirm(profile, foreground) : Elected(profile, foreground, false, Lang.T("detect.window")))));
            }
            else
            {
                gameDetection = PendingGpuConfirm(profile, foreground);
                gameDetection.RendererSafetyOnly = true;
                gameDetection.RequiresGpuConfirm = false;
                gameDetection.RendererLearnable = false;
                gameDetection.Evidence = null;
            }
            foreach (GameProcessSnapshot processSnapshot in byPid.Values)
            {
                if (pids.Contains(processSnapshot.Pid) || HasMemberAncestor(processSnapshot, byPid, pids))
                {
                    gameDetection.FamilyPids.Add(processSnapshot.Pid);
                    gameDetection.FamilyNames.Add(processSnapshot.Name);
                }
            }
            return gameDetection;
        }

        private static bool IncumbentRendererLive(GameDetection incumbent, GameProfile profile, GameProcessSnapshot foreground,
            Dictionary<int, GameProcessSnapshot> byPid)
        {
            if (incumbent == null || incumbent.Profile == null || profile == null || string.IsNullOrEmpty(profile.Id)
                || !string.Equals(incumbent.Profile.Id, profile.Id, StringComparison.OrdinalIgnoreCase)
                || !incumbent.RendererCandidateSelected || incumbent.RendererSafetyOnly || incumbent.RequiresGpuConfirm
                || incumbent.RendererPid <= 0 || incumbent.RendererCreation <= 0 || incumbent.RendererPid == foreground.Pid)
            {
                return false;
            }
            GameProcessSnapshot live;
            return byPid.TryGetValue(incumbent.RendererPid, out live) && live.Creation == incumbent.RendererCreation
                && SamePath(live.Path, incumbent.RendererPath);
        }

        private static int CompareCandidateOwnership(GameDetection candidate, GameDetection current)
        {
            if (current == null)
            {
                return 1;
            }
            int compareToResult = candidate.RendererMatchRank.CompareTo(current.RendererMatchRank);
            if (compareToResult != 0)
            {
                return compareToResult;
            }
            if (candidate.RendererMatchRank == 1)
            {
                int length = candidate.Profile.Root.TrimEnd('\\').Length;
                int length2 = current.Profile.Root.TrimEnd('\\').Length;
                return length.CompareTo(length2);
            }
            return 0;
        }

        private static bool SameCandidateOwner(GameProfile a, GameProfile b)
        {
            if (object.ReferenceEquals(a, b))
            {
                return true;
            }
            if (a != null && b != null && !string.IsNullOrEmpty(a.Id) && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase) && a.ForceTrigger == b.ForceTrigger && string.Equals(a.Root ?? "", b.Root ?? "", StringComparison.OrdinalIgnoreCase) && string.Equals(a.ExecutablePath ?? "", b.ExecutablePath ?? "", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(a.LearnedExecutablePath ?? "", b.LearnedExecutablePath ?? "", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool CandidateIdentityUsable(GameProcessSnapshot identity)
        {
            if (identity == null || identity.Pid <= 0 || identity.Creation <= 0 || string.IsNullOrEmpty(identity.Name) || string.IsNullOrEmpty(identity.Path))
            {
                return false;
            }
            try
            {
                if (!Path.IsPathRooted(identity.Path) || !SamePath(Path.GetFullPath(identity.Path), identity.Path))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
            return string.Equals(identity.Name, ImageNameFromVerifiedPath(identity.Path), StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameRendererIdentity(GameDetection incumbent, GameProcessSnapshot identity)
        {
            if (incumbent != null && identity != null && incumbent.RendererPid == identity.Pid && incumbent.RendererCreation > 0 && incumbent.RendererCreation == identity.Creation)
            {
                return SamePath(incumbent.RendererPath, identity.Path);
            }
            return false;
        }

        private static bool TryCaptureCandidateWindow(out int pid, out bool fullscreen)
        {
            pid = 0;
            fullscreen = false;
            try
            {
                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero || !IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow))
                {
                    return false;
                }
                uint pid2;
                GetWindowThreadProcessId(foregroundWindow, out pid2);
                if (pid2 == 0 || pid2 > int.MaxValue)
                {
                    return false;
                }
                pid = (int)pid2;
                NativeRect rect;
                fullscreen = GetWindowRect(foregroundWindow, out rect) && IsFullscreenLikeWindow(foregroundWindow, rect);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static GameDetection DetectSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles)
        {
            bool armed;
            return DetectSnapshot(snapshot, profiles, out armed);
        }

        internal static GameDetection DetectSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles, out bool armed)
        {
            string armedProfile;
            GameDetection result = DetectSnapshot(snapshot, profiles, out armedProfile);
            armed = armedProfile != null;
            return result;
        }

        internal static GameDetection DetectSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles, out string armedProfile)
        {
            string armedVia;
            return DetectSnapshot(snapshot, profiles, out armedProfile, out armedVia);
        }

        internal static GameDetection DetectSnapshot(IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles, out string armedProfile, out string armedVia, GameFamilyEvidence familyEvidence = null)
        {
            armedProfile = null;
            armedVia = null;
            if (snapshot == null || profiles == null || profiles.Count == 0)
            {
                return null;
            }
            HashSet<int> pids = new HashSet<int>();
            HashSet<int> currentPids = new HashSet<int>();
            foreach (GameProcessSnapshot currentSnapshot in snapshot)
            {
                if (currentSnapshot != null && currentSnapshot.Pid > 0 && !pids.Add(currentSnapshot.Pid))
                {
                    currentPids.Add(currentSnapshot.Pid);
                }
            }
            Dictionary<int, GameProcessSnapshot> gameProcessSnapshotsByPid = new Dictionary<int, GameProcessSnapshot>();
            foreach (GameProcessSnapshot candidateSnapshot in snapshot)
            {
                if (CandidateIdentityUsable(candidateSnapshot) && !currentPids.Contains(candidateSnapshot.Pid) && !ElectionVetoed(candidateSnapshot.Name, candidateSnapshot.Path))
                {
                    gameProcessSnapshotsByPid[candidateSnapshot.Pid] = candidateSnapshot;
                }
            }
            if (gameProcessSnapshotsByPid.Count == 0)
            {
                return null;
            }
            GameDetection gameDetection = null;
            foreach (GameProfile profile in profiles)
            {
                if (profile == null)
                {
                    continue;
                }
                HashSet<int> candidatePids = new HashSet<int>();
                List<GameProcessSnapshot> gameProcessSnapshots = new List<GameProcessSnapshot>();
                foreach (GameProcessSnapshot processSnapshot in gameProcessSnapshotsByPid.Values)
                {
                    if (IsDirectMember(profile, processSnapshot, familyEvidence))
                    {
                        candidatePids.Add(processSnapshot.Pid);
                        gameProcessSnapshots.Add(processSnapshot);
                    }
                }
                if (gameProcessSnapshots.Count == 0)
                {
                    continue;
                }
                foreach (GameProcessSnapshot processSnapshot in gameProcessSnapshotsByPid.Values)
                {
                    if (!candidatePids.Contains(processSnapshot.Pid) && HasMemberAncestor(processSnapshot, gameProcessSnapshotsByPid, candidatePids))
                    {
                        candidatePids.Add(processSnapshot.Pid);
                        gameProcessSnapshots.Add(processSnapshot);
                    }
                }
                if (armedProfile == null)
                {
                    string armedViaResult = ArmedVia(profile, gameProcessSnapshots);
                    if (armedViaResult != null)
                    {
                        armedProfile = profile.Name;
                        armedVia = armedViaResult;
                    }
                }
                GameDetection elect = Elect(profile, gameProcessSnapshots);
                if (elect == null)
                {
                    continue;
                }
                foreach (GameProcessSnapshot gameProcessSnapshot in gameProcessSnapshots)
                {
                    elect.FamilyNames.Add(gameProcessSnapshot.Name);
                    elect.FamilyPids.Add(gameProcessSnapshot.Pid);
                }
                if (BetterHit(elect, gameDetection))
                {
                    gameDetection = elect;
                }
            }
            return gameDetection;
        }

        internal static string ArmedVia(GameProfile profile, IList<GameProcessSnapshot> members)
        {
            if (profile == null || members == null)
            {
                return null;
            }
            List<string> names = new List<string>();
            foreach (GameProcessSnapshot member in members)
            {
                if (CandidateIdentityUsable(member) && !ElectionVetoed(member.Name, member.Path))
                {
                    // 游戏关闭后，常驻启动器及平台后代继续受保护，但不能因此维持已结束的游戏会话。
                    bool selected = SamePath(profile.ExecutablePath, member.Path) || SamePath(profile.LearnedExecutablePath, member.Path);
                    if (!selected && ((!member.Visible && !member.Foreground) || PlatformRuntimePolicy.RuntimePlatform(member.Name) != null))
                    {
                        continue;
                    }
                    if (!names.Contains(member.Name))
                    {
                        names.Add(member.Name);
                    }
                    if (names.Count >= 3)
                    {
                        break;
                    }
                }
            }
            if (names.Count != 0)
            {
                return string.Join(" ", names.ToArray());
            }
            return null;
        }

        private static bool IsDirectMember(GameProfile profile, GameProcessSnapshot identity, GameFamilyEvidence familyEvidence = null)
        {
            if (SamePath(profile.ExecutablePath, identity.Path))
            {
                return true;
            }
            if (SamePath(profile.LearnedExecutablePath, identity.Path))
            {
                return true;
            }
            if (!profile.ContainsPath(identity.Path))
            {
                if (familyEvidence != null)
                {
                    return familyEvidence.Contains(profile, identity.Pid, identity.Creation, identity.Path);
                }
                return false;
            }
            return true;
        }

        private static bool HasMemberAncestor(GameProcessSnapshot identity, Dictionary<int, GameProcessSnapshot> byPid, HashSet<int> memberPids)
        {
            GameProcessSnapshot gameProcessSnapshot = identity;
            HashSet<int> pids = new HashSet<int>();
            pids.Add(identity.Pid);
            int processedCount = 0;
            while (gameProcessSnapshot.ParentPid > 0 && processedCount < 24)
            {
                int parentPid = gameProcessSnapshot.ParentPid;
                if (!pids.Add(parentPid))
                {
                    return false;
                }
                GameProcessSnapshot currentByPid;
                if (!byPid.TryGetValue(parentPid, out currentByPid))
                {
                    return false;
                }
                if (gameProcessSnapshot.Creation <= 0 || currentByPid.Creation <= 0 || gameProcessSnapshot.Creation < currentByPid.Creation)
                {
                    return false;
                }
                if (memberPids.Contains(parentPid))
                {
                    return true;
                }
                gameProcessSnapshot = currentByPid;
                processedCount++;
            }
            return false;
        }

        private static GameDetection Elect(GameProfile profile, List<GameProcessSnapshot> members)
        {
            if (profile.ForceTrigger)
            {
                return ElectForced(profile, members);
            }
            GameProcessSnapshot gameProcessSnapshot = null;
            GameProcessSnapshot currentGameProcessSnapshot = null;
            foreach (GameProcessSnapshot member in members)
            {
                if ((member.Visible || member.Foreground) && !ElectionVetoed(member.Name, member.Path))
                {
                    if (SamePath(profile.LearnedExecutablePath, member.Path) && (gameProcessSnapshot == null || PreferSnapshot(member, gameProcessSnapshot)))
                    {
                        gameProcessSnapshot = member;
                    }
                    if (member.Foreground && (currentGameProcessSnapshot == null || member.Creation > currentGameProcessSnapshot.Creation))
                    {
                        currentGameProcessSnapshot = member;
                    }
                }
            }
            if (currentGameProcessSnapshot != null && currentGameProcessSnapshot.FullscreenLike)
            {
                return Elected(profile, currentGameProcessSnapshot, !SamePath(profile.ExecutablePath, currentGameProcessSnapshot.Path) && !SamePath(profile.LearnedExecutablePath, currentGameProcessSnapshot.Path), Lang.T("detect.fullscreen"));
            }
            if (gameProcessSnapshot != null)
            {
                return Elected(profile, gameProcessSnapshot, false, Lang.T("detect.learned"));
            }
            if (currentGameProcessSnapshot == null)
            {
                return null;
            }
            if (SamePath(profile.ExecutablePath, currentGameProcessSnapshot.Path))
            {
                return Elected(profile, currentGameProcessSnapshot, false, Lang.T("detect.window"));
            }
            return PendingGpuConfirm(profile, currentGameProcessSnapshot);
        }

        private static GameDetection ElectForced(GameProfile profile, List<GameProcessSnapshot> members)
        {
            GameProcessSnapshot gameProcessSnapshot = null;
            foreach (GameProcessSnapshot member in members)
            {
                if ((SamePath(profile.ExecutablePath, member.Path) || SamePath(profile.LearnedExecutablePath, member.Path)) && (gameProcessSnapshot == null || PreferForced(member, gameProcessSnapshot)))
                {
                    gameProcessSnapshot = member;
                }
            }
            if (gameProcessSnapshot == null)
            {
                return null;
            }
            GameDetection gameDetection = new GameDetection();
            gameDetection.Profile = profile.Clone();
            gameDetection.RendererPid = gameProcessSnapshot.Pid;
            gameDetection.RendererCreation = gameProcessSnapshot.Creation;
            gameDetection.RendererName = gameProcessSnapshot.Name;
            gameDetection.RendererPath = gameProcessSnapshot.Path;
            gameDetection.RendererForeground = gameProcessSnapshot.Foreground;
            gameDetection.RendererCandidateSelected = true;
            gameDetection.RendererUserSelected = true;
            gameDetection.RendererLearnable = false;
            gameDetection.RendererMatchRank = MatchRank(profile, gameProcessSnapshot.Path);
            gameDetection.Evidence = Lang.T("detect.forced");
            return gameDetection;
        }

        private static bool PreferForced(GameProcessSnapshot candidate, GameProcessSnapshot current)
        {
            if (candidate.Foreground != current.Foreground)
            {
                return candidate.Foreground;
            }
            if (candidate.Creation != current.Creation)
            {
                return candidate.Creation < current.Creation;
            }
            return candidate.Pid < current.Pid;
        }

        internal static bool ElectionVetoed(string name, string path)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }
            return IsNonGameRole(name, path);
        }

        private static GameDetection Elected(GameProfile profile, GameProcessSnapshot selected, bool learnable, string evidence)
        {
            GameDetection gameDetection = new GameDetection();
            gameDetection.Profile = profile.Clone();
            gameDetection.RendererPid = selected.Pid;
            gameDetection.RendererCreation = selected.Creation;
            gameDetection.RendererName = selected.Name;
            gameDetection.RendererPath = selected.Path;
            gameDetection.RendererForeground = selected.Foreground;
            gameDetection.RendererCandidateSelected = true;
            gameDetection.RendererUserSelected = SamePath(profile.ExecutablePath, selected.Path) || SamePath(profile.LearnedExecutablePath, selected.Path);
            gameDetection.RendererLearnable = learnable;
            gameDetection.RendererMatchRank = MatchRank(profile, selected.Path);
            gameDetection.Evidence = evidence;
            return gameDetection;
        }

        private static GameDetection PendingGpuConfirm(GameProfile profile, GameProcessSnapshot candidate)
        {
            GameDetection gameDetection = new GameDetection();
            gameDetection.Profile = profile.Clone();
            gameDetection.RendererPid = candidate.Pid;
            gameDetection.RendererCreation = candidate.Creation;
            gameDetection.RendererName = candidate.Name;
            gameDetection.RendererPath = candidate.Path;
            gameDetection.RendererForeground = candidate.Foreground;
            gameDetection.RendererCandidateSelected = false;
            gameDetection.RendererUserSelected = false;
            gameDetection.RendererLearnable = true;
            gameDetection.RequiresGpuConfirm = true;
            gameDetection.RendererMatchRank = MatchRank(profile, candidate.Path);
            gameDetection.Evidence = Lang.T("detect.gpu.pending");
            return gameDetection;
        }

        private static int MatchRank(GameProfile profile, string path)
        {
            if (profile == null || string.IsNullOrEmpty(path))
            {
                return 0;
            }
            if (SamePath(profile.ExecutablePath, path))
            {
                return 3;
            }
            if (SamePath(profile.LearnedExecutablePath, path))
            {
                return 2;
            }
            if (!profile.ContainsPath(path))
            {
                return 0;
            }
            return 1;
        }

        private static bool PreferSnapshot(GameProcessSnapshot candidate, GameProcessSnapshot current)
        {
            if (candidate.Foreground != current.Foreground)
            {
                return candidate.Foreground;
            }
            if (candidate.Creation != current.Creation)
            {
                return candidate.Creation > current.Creation;
            }
            return candidate.Pid < current.Pid;
        }

        private static bool BetterHit(GameDetection candidate, GameDetection current)
        {
            if (candidate == null)
            {
                return false;
            }
            if (current == null)
            {
                return true;
            }
            bool candidateConfirmed = !candidate.RequiresGpuConfirm;
            bool currentConfirmed = !current.RequiresGpuConfirm;
            if (candidateConfirmed != currentConfirmed)
            {
                return candidateConfirmed;
            }
            if (candidate.RendererForeground != current.RendererForeground)
            {
                return candidate.RendererForeground;
            }
            if (candidate.RendererPid == current.RendererPid && candidate.RendererCreation == current.RendererCreation)
            {
                if (candidate.RendererMatchRank != current.RendererMatchRank)
                {
                    return candidate.RendererMatchRank > current.RendererMatchRank;
                }
                bool candidateInProfile = candidate.Profile != null && candidate.Profile.ContainsPath(candidate.RendererPath);
                bool currentInProfile = current.Profile != null && current.Profile.ContainsPath(current.RendererPath);
                if (candidateInProfile != currentInProfile)
                {
                    return candidateInProfile;
                }
                int compareStableProfileResult = CompareStableProfile(candidate.Profile, current.Profile);
                if (compareStableProfileResult != 0)
                {
                    return compareStableProfileResult < 0;
                }
            }
            if (candidate.RendererCreation != current.RendererCreation)
            {
                return candidate.RendererCreation > current.RendererCreation;
            }
            return candidate.RendererPid < current.RendererPid;
        }

        private static int CompareStableProfile(GameProfile a, GameProfile b)
        {
            if (object.ReferenceEquals(a, b))
            {
                return 0;
            }
            if (a == null)
            {
                return 1;
            }
            if (b == null)
            {
                return -1;
            }
            string[] leftProfileFields = new string[5] { a.Id, a.Root, a.ExecutablePath, a.LearnedExecutablePath, a.Name };
            string[] rightProfileFields = new string[5] { b.Id, b.Root, b.ExecutablePath, b.LearnedExecutablePath, b.Name };
            for (int i = 0; i < leftProfileFields.Length; i++)
            {
                int compareResult = string.Compare(leftProfileFields[i] ?? "", rightProfileFields[i] ?? "", StringComparison.OrdinalIgnoreCase);
                if (compareResult != 0)
                {
                    return compareResult;
                }
                compareResult = string.CompareOrdinal(leftProfileFields[i] ?? "", rightProfileFields[i] ?? "");
                if (compareResult != 0)
                {
                    return compareResult;
                }
            }
            return 0;
        }

        internal static bool IsNonGameRole(string name, string path)
        {
            string trimmed = (name ?? "").Trim();
            if (AntiCheatCatalog.IsAntiCheatProcess(trimmed, path))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(WindowsRootPrefix) && !string.IsNullOrEmpty(path) && path.StartsWith(WindowsRootPrefix, StringComparison.OrdinalIgnoreCase) && (SystemProcessCatalog.IsShellProcess(trimmed) || SystemProcessCatalog.IsCoreSystemProcess(trimmed, path, WindowsRootPrefix)))
            {
                return true;
            }
            string lowered = ((path ?? "") + "\\" + trimmed).ToLowerInvariant();
            return AntiCheatCatalog.ContainsToken(lowered);
        }

        internal static bool IsLibraryCandidate(string name, string path, string windowsRoot)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
            {
                return false;
            }
            if (ElectionVetoed(name, path))
            {
                return false;
            }
            if (GamePerformanceTargetPolicy.KnownNonGame(name, path))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(windowsRoot))
            {
                return !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private static bool SamePath(string a, string b)
        {
            if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b))
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        internal static bool IsProfileEntryName(GameProfile profile, string name)
        {
            if (profile != null && !string.IsNullOrEmpty(name) && profile.Entries != null)
            {
                if (!profile.Entries.Contains(name))
                {
                    return IsFallbackEntryName(profile, name);
                }
                return true;
            }
            return false;
        }

        internal static bool IsProfileEntryProcess(GameProfile profile, string name, string path)
        {
            if (profile == null)
            {
                return false;
            }
            if (SamePath(profile.LearnedExecutablePath, path))
            {
                return true;
            }
            if (SamePath(profile.ExecutablePath, path))
            {
                return true;
            }
            return profile.ContainsPath(path);
        }

        private static bool IsFallbackEntryName(GameProfile profile, string name)
        {
            if (string.IsNullOrEmpty(profile.ExecutablePath) || string.IsNullOrEmpty(name))
            {
                return false;
            }
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(profile.ExecutablePath);
            if (string.IsNullOrEmpty(fileNameWithoutExtension) || fileNameWithoutExtension.Length < 3)
            {
                return false;
            }
            if (string.Equals(fileNameWithoutExtension, name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!name.StartsWith(fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return IsBitnessOrVersionSuffix(name.Substring(fileNameWithoutExtension.Length));
        }

        private static bool IsBitnessOrVersionSuffix(string suffix)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                return false;
            }
            int suffixOffset = ((suffix[0] == '_' || suffix[0] == '-') ? 1 : 0);
            if (suffixOffset >= suffix.Length)
            {
                return false;
            }
            string substringResult = suffix.Substring(suffixOffset);
            bool allVersionDigits = true;
            foreach (char c in substringResult)
            {
                if (!char.IsDigit(c))
                {
                    allVersionDigits = false;
                    break;
                }
            }
            if (allVersionDigits)
            {
                return true;
            }
            string toLowerInvariantResult = substringResult.ToLowerInvariant();
            if (toLowerInvariantResult == "x64" || toLowerInvariantResult == "x86")
            {
                return true;
            }
            if (toLowerInvariantResult.Length >= 2 && toLowerInvariantResult[0] == 'v')
            {
                bool allSuffixDigits = true;
                for (int j = 1; j < toLowerInvariantResult.Length; j++)
                {
                    if (!char.IsDigit(toLowerInvariantResult[j]))
                    {
                        allSuffixDigits = false;
                        break;
                    }
                }
                if (allSuffixDigits)
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool TryCaptureProcessIdentity(int pid, int ownerSession, out GameProcessSnapshot identity)
        {
            identity = null;
            if (pid <= 0 || ownerSession < 0)
            {
                return false;
            }
            IntPtr processHandle = Native.OpenProcess(1052672, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                if (!GetProcessTimes(processHandle, out creation, out exit, out kernel, out user) || creation <= 0)
                {
                    return false;
                }
                string imagePathResult = Native.ImagePath(processHandle);
                string imageNameFromVerifiedPathResult = ImageNameFromVerifiedPath(imagePathResult);
                int sessionId;
                if (string.IsNullOrEmpty(imageNameFromVerifiedPathResult) || !Native.TryGetLiveProcessSessionId(processHandle, pid, out sessionId) || sessionId != ownerSession)
                {
                    return false;
                }
                identity = new GameProcessSnapshot
                {
                    Pid = pid,
                    ParentPid = Native.ParentProcessId(processHandle),
                    Creation = creation,
                    Name = imageNameFromVerifiedPathResult,
                    Path = imagePathResult
                };
                return true;
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }

        internal static string ImageNameFromVerifiedPath(string imagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath))
                {
                    return null;
                }
                string fileName = Path.GetFileName(imagePath.Trim());
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    return null;
                }
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                return string.IsNullOrWhiteSpace(fileNameWithoutExtension) ? null : fileNameWithoutExtension.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static void CaptureWindowEvidence(IList<GameProcessSnapshot> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0)
            {
                return;
            }
            int foregroundPidResult = ForegroundPid();
            bool fullscreenForeground = foregroundPidResult > 0 && ForegroundWindowFullscreenLike(foregroundPidResult);
            HashSet<int> visiblePids = VisibleWindowPids(false);
            foreach (GameProcessSnapshot currentSnapshot in snapshot)
            {
                if (currentSnapshot != null && currentSnapshot.Pid > 0 && currentSnapshot.Creation > 0)
                {
                    bool foregroundValue = currentSnapshot.Pid == foregroundPidResult;
                    bool visibleValue = visiblePids.Contains(currentSnapshot.Pid);
                    if ((foregroundValue || visibleValue) && IsLiveProcessCreation(currentSnapshot.Pid, currentSnapshot.Creation))
                    {
                        currentSnapshot.Foreground = foregroundValue;
                        currentSnapshot.Visible = visibleValue;
                        currentSnapshot.FullscreenLike = foregroundValue && fullscreenForeground;
                    }
                }
            }
        }

        private static bool IsLiveProcessCreation(int pid, long expectedCreation)
        {
            if (pid <= 0 || expectedCreation <= 0)
            {
                return false;
            }
            IntPtr processHandle = Native.OpenProcess(1052672, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                int sessionId;
                return GetProcessTimes(processHandle, out creation, out exit, out kernel, out user) && creation == expectedCreation && Native.TryGetLiveProcessSessionId(processHandle, pid, out sessionId);
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }

        internal static bool TryForegroundFullscreen(out int pid)
        {
            pid = 0;
            try
            {
                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero)
                {
                    return false;
                }
                uint pid2;
                GetWindowThreadProcessId(foregroundWindow, out pid2);
                if (pid2 == 0 || pid2 > int.MaxValue)
                {
                    return false;
                }
                pid = (int)pid2;
                if (!IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow))
                {
                    return false;
                }
                NativeRect rect;
                if (!GetWindowRect(foregroundWindow, out rect))
                {
                    return false;
                }
                return IsFullscreenLikeWindow(foregroundWindow, rect);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryForegroundGameWindow(out ForegroundGameWindowSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero || !IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow) || ((uint)GetWindowLong(foregroundWindow, -20) & 0x80u) != 0)
                {
                    return false;
                }
                uint pid;
                if (GetWindowThreadProcessId(foregroundWindow, out pid) == 0 || pid == 0 || pid > int.MaxValue)
                {
                    return false;
                }
                int cloakedState;
                if (DwmGetWindowAttribute(foregroundWindow, 14u, out cloakedState, 4) == 0 && cloakedState != 0)
                {
                    return false;
                }
                NativeRect rect;
                NativeRect rect2;
                if (!GetWindowRect(foregroundWindow, out rect) || !RectHasArea(rect) || !TryGetScreenClientRect(foregroundWindow, out rect2))
                {
                    return false;
                }
                bool fullscreenLike = CaptureFullscreenEvidence(foregroundWindow, rect, rect2);
                uint pid2;
                if (GetForegroundWindow() != foregroundWindow || GetWindowThreadProcessId(foregroundWindow, out pid2) == 0 || pid2 != pid)
                {
                    return false;
                }
                snapshot = new ForegroundGameWindowSnapshot
                {
                    Pid = (int)pid,
                    FullscreenLike = fullscreenLike
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ForegroundWindowFullscreenLike(int pid)
        {
            int pid2;
            if (TryForegroundFullscreen(out pid2))
            {
                return pid2 == pid;
            }
            return false;
        }

        internal static bool IsFullscreenLikeWindow(IntPtr window, NativeRect rect)
        {
            try
            {
                int windowLong = GetWindowLong(window, -16);
                if ((windowLong & 0xC00000) == 12582912)
                {
                    return false;
                }
                IntPtr monitorFromWindowHandle = MonitorFromWindow(window, 2u);
                if (monitorFromWindowHandle == IntPtr.Zero)
                {
                    return false;
                }
                MonitorInfo info = default(MonitorInfo);
                info.Size = Marshal.SizeOf(typeof(MonitorInfo));
                return GetMonitorInfo(monitorFromWindowHandle, ref info) && RectCoversMonitor(rect, info.Monitor);
            }
            catch
            {
                return false;
            }
        }

        private static bool CaptureFullscreenEvidence(IntPtr window, NativeRect rect, NativeRect client)
        {
            try
            {
                int windowLong = GetWindowLong(window, -16);
                IntPtr monitorFromWindowHandle = MonitorFromWindow(window, 2u);
                if (monitorFromWindowHandle == IntPtr.Zero)
                {
                    return false;
                }
                MonitorInfo info = default(MonitorInfo);
                info.Size = Marshal.SizeOf(typeof(MonitorInfo));
                if (!GetMonitorInfo(monitorFromWindowHandle, ref info))
                {
                    return false;
                }
                bool hasCaption = (windowLong & 0xC00000) == 12582912;
                return IsFullscreenLikeGeometry(hasCaption, rect, client, info.Monitor, info.Work);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetScreenClientRect(IntPtr window, out NativeRect rect)
        {
            rect = default(NativeRect);
            try
            {
                NativeRect rect2;
                if (!GetClientRect(window, out rect2) || !RectHasArea(rect2))
                {
                    return false;
                }
                NativePoint point = default(NativePoint);
                point.X = rect2.Left;
                point.Y = rect2.Top;
                NativePoint point2 = default(NativePoint);
                point2.X = rect2.Right;
                point2.Y = rect2.Bottom;
                if (!ClientToScreen(window, ref point) || !ClientToScreen(window, ref point2))
                {
                    return false;
                }
                rect = new NativeRect
                {
                    Left = point.X,
                    Top = point.Y,
                    Right = point2.X,
                    Bottom = point2.Y
                };
                return RectHasArea(rect);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsFullscreenLikeGeometry(bool hasCaption, NativeRect windowRect, NativeRect clientRect, NativeRect monitor, NativeRect work)
        {
            if (!RectHasArea(windowRect) || !RectHasArea(clientRect) || !RectHasArea(monitor))
            {
                return false;
            }
            if (hasCaption)
            {
                if (RectCoversMonitor(clientRect, monitor))
                {
                    return RectEdgesMatch(clientRect, monitor);
                }
                return false;
            }
            if (RectCoversMonitor(windowRect, monitor))
            {
                return true;
            }
            if (RectHasArea(work) && work.Left >= monitor.Left && work.Top >= monitor.Top && work.Right <= monitor.Right && work.Bottom <= monitor.Bottom)
            {
                return RectEdgesMatch(windowRect, work);
            }
            return false;
        }

        private static bool RectHasArea(NativeRect rect)
        {
            if (rect.Right > rect.Left)
            {
                return rect.Bottom > rect.Top;
            }
            return false;
        }

        private static bool RectEdgesMatch(NativeRect rect, NativeRect bounds)
        {
            if (Math.Abs((long)rect.Left - (long)bounds.Left) <= 4 && Math.Abs((long)rect.Top - (long)bounds.Top) <= 4 && Math.Abs((long)rect.Right - (long)bounds.Right) <= 4)
            {
                return Math.Abs((long)rect.Bottom - (long)bounds.Bottom) <= 4;
            }
            return false;
        }

        internal static bool RectCoversMonitor(NativeRect rect, NativeRect monitor)
        {
            if (!RectHasArea(rect) || !RectHasArea(monitor))
            {
                return false;
            }
            double scaledValue = (double)((long)monitor.Right - (long)monitor.Left) * (double)((long)monitor.Bottom - (long)monitor.Top);
            int leftLimit = Math.Max(rect.Left, monitor.Left);
            int topLimit = Math.Max(rect.Top, monitor.Top);
            int rightLimit = Math.Min(rect.Right, monitor.Right);
            int bottomLimit = Math.Min(rect.Bottom, monitor.Bottom);
            double scaledValue2 = ((rightLimit > leftLimit && bottomLimit > topLimit) ? ((double)((long)rightLimit - (long)leftLimit) * (double)((long)bottomLimit - (long)topLimit)) : 0.0);
            return scaledValue2 * 100.0 >= scaledValue * 97.0;
        }

        internal static HashSet<int> VisibleWindowPids(bool includeMinimized)
        {
            long ticks = DateTime.UtcNow.Ticks;
            lock (visibleCacheSync)
            {
                HashSet<int> cachedVisiblePids = (includeMinimized ? visibleCacheAll : visibleCacheShown);
                long visibleCacheAllTicksSnapshot = (includeMinimized ? visibleCacheAllTicks : visibleCacheShownTicks);
                if (cachedVisiblePids != null && ticks >= visibleCacheAllTicksSnapshot && ticks - visibleCacheAllTicksSnapshot < 15000000)
                {
                    return cachedVisiblePids;
                }
            }
            bool succeeded;
            HashSet<int> result = VisibleWindowPids(includeMinimized, out succeeded);
            if (!succeeded)
            {
                return result;
            }
            lock (visibleCacheSync)
            {
                if (includeMinimized)
                {
                    visibleCacheAll = result;
                    visibleCacheAllTicks = ticks;
                }
                else
                {
                    visibleCacheShown = result;
                    visibleCacheShownTicks = ticks;
                }
            }
            return result;
        }

        internal static HashSet<int> VisibleWindowPids(bool includeMinimized, out bool succeeded)
        {
            HashSet<int> result = new HashSet<int>();
            succeeded = false;
            try
            {
                succeeded = EnumWindows(delegate(IntPtr window, IntPtr state)
                {
                    try
                    {
                        if (!IsWindowVisible(window) || GetWindow(window, 4u) != IntPtr.Zero || ((uint)GetWindowLong(window, -20) & 0x80u) != 0 || (!includeMinimized && IsIconic(window)))
                        {
                            return true;
                        }
                        int cloakedState;
                        if (DwmGetWindowAttribute(window, 14u, out cloakedState, 4) == 0 && cloakedState != 0)
                        {
                            return true;
                        }
                        NativeRect rect;
                        if (!GetWindowRect(window, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                        {
                            return true;
                        }
                        uint pid;
                        GetWindowThreadProcessId(window, out pid);
                        if (pid != 0 && pid <= int.MaxValue)
                        {
                            result.Add((int)pid);
                        }
                    }
                    catch
                    {
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }
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
            catch
            {
                return -1;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
    }
}
