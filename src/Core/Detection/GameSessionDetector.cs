// @author bdth 2074055628@qq.com
// 文件用途 游戏会话检测入口与前台候选捕获
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
        // 独立前台候选可以只享有临时安全保护 不得作为已选中的 renderer
        // 强制接管档案里的陌生进程使用此标记 不采证接管 也不学习
        public bool RendererSafetyOnly;
        public bool RequiresGpuConfirm;
        // 只随异步确认票据携带 0 表示原有全屏/精确入口等硬证据
        // 不能把已过期的 GPU 结果当成永久有效的提交授权
        public long RendererGpuProofExpiresMs;
        // 同一个 renderer 同时被多个档案引用时 用配置锚的精确度稳定决胜
        // 避免最终选中哪个 profile 以及它的独立策略 取决于列表顺序
        public int RendererMatchRank;
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

    internal static partial class GameSessionDetector
    {
        internal const int FullscreenCoveragePercent = 97;
        private static readonly string WindowsRootPrefix = CaptureWindowsRootPrefix();

        private static string CaptureWindowsRootPrefix()
        {
            try
            {
                string directory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return string.IsNullOrEmpty(directory) ? null : directory.TrimEnd('\\') + "\\";
            }
            catch { return null; }
        }

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
                    if (AntiCheatCatalog.IsAntiCheatLikeName(identity.Name))
                        continue;
                    snapshot.Add(identity);
                }
                catch { }
            }

            CaptureWindowEvidence(snapshot);
            return DetectSnapshot(snapshot, profiles, out armedProfile);
        }

        public static GameDetection Detect(
            ProcessSnapshot processes, IList<GameProfile> profiles,
            int ownerSession, out string armedProfile)
        {
            string armedVia;
            return Detect(processes, profiles, ownerSession, out armedProfile, out armedVia);
        }

        public static GameDetection Detect(
            ProcessSnapshot processes, IList<GameProfile> profiles,
            int ownerSession, out string armedProfile, out string armedVia,
            GameFamilyEvidence familyEvidence = null)
        {
            armedProfile = null;
            armedVia = null;
            if (processes == null || profiles == null
                || profiles.Count == 0 || ownerSession < 0)
                return null;

            var snapshot = new List<GameProcessSnapshot>();
            foreach (ProcEntry entry in processes.Entries)
            {
                GameProcessSnapshot identity;
                if (!TryCaptureProcessIdentity(entry, ownerSession, out identity))
                    continue;
                if (AntiCheatCatalog.IsAntiCheatLikeName(identity.Name)) continue;
                snapshot.Add(identity);
            }

            CaptureWindowEvidence(snapshot);
            return DetectSnapshot(snapshot, profiles, out armedProfile, out armedVia, familyEvidence);
        }

        internal static bool TryCaptureProcessIdentity(
            ProcEntry entry, int ownerSession,
            out GameProcessSnapshot identity)
        {
            identity = null;
            if (entry == null || entry.Pid <= 0 || ownerSession < 0) return false;
            if (entry.Creation <= 0 || entry.Session != ownerSession) return false;
            string name = ImageNameFromVerifiedPath(entry.Path);
            if (string.IsNullOrEmpty(name)) return false;
            identity = new GameProcessSnapshot
            {
                Pid = entry.Pid,
                ParentPid = entry.ParentPid,
                Creation = entry.Creation,
                Name = name,
                Path = entry.Path
            };
            return true;
        }

        // 热路径只看当前前台窗口 不枚举所有顶层窗口 ProcEntry 的身份来自
        // 本轮系统快照 只有发现新的关联候选后才额外核验它仍是同一生命期
        internal static GameDetection CaptureForegroundCandidate(
            ProcessSnapshot processes, int ownerSession,
            GameProfile profile, GameDetection incumbent)
        {
            if (profile == null) return null;
            return CaptureForegroundCandidate(processes, ownerSession,
                new[] { profile }, incumbent);
        }

        internal static GameDetection CaptureForegroundCandidate(
            ProcessSnapshot processes, int ownerSession,
            IList<GameProfile> profiles, GameDetection incumbent,
            GameFamilyEvidence familyEvidence = null)
        {
            if (processes == null || ownerSession < 0
                || profiles == null || profiles.Count == 0)
                return null;
            int foregroundPid = ForegroundPid();
            if (foregroundPid <= 0) return null;
            ProcEntry foregroundEntry = processes.Find(foregroundPid);
            GameProcessSnapshot foregroundIdentity;
            if (!TryCaptureProcessIdentity(
                    foregroundEntry, ownerSession, out foregroundIdentity)
                || SameRendererIdentity(incumbent, foregroundIdentity))
                return null;

            if (!CandidateIdentityUsable(foregroundIdentity)
                || AntiCheatCatalog.IsAntiCheatLikeName(foregroundIdentity.Name))
                return null;
            if (ElectionVetoed(foregroundIdentity.Name, foregroundIdentity.Path)) return null;

            int windowPid;
            bool fullscreen;
            if (!TryCaptureCandidateWindow(out windowPid, out fullscreen)
                || windowPid != foregroundPid)
                return null;

            // 系统快照本应一 PID 一项 拒绝歧义快照 不能由 ByPid 的最后一项
            // 替重复 PID 选择身份 尤其不能把另一个登录会话的身份拼到父链里
            if (processes.ByPid.Count != processes.Count) return null;
            var snapshot = new List<GameProcessSnapshot>();
            foreach (ProcEntry entry in processes.Entries)
            {
                GameProcessSnapshot identity;
                if (!TryCaptureProcessIdentity(entry, ownerSession, out identity))
                    continue;
                identity.Foreground = identity.Pid == foregroundPid;
                identity.Visible = identity.Foreground;
                identity.FullscreenLike = identity.Foreground && fullscreen;
                snapshot.Add(identity);
            }
            GameDetection candidate = FindForegroundCandidateSnapshot(
                snapshot, profiles, incumbent, familyEvidence);
            if (candidate == null) return null;

            GameProcessSnapshot live;
            if (!TryCaptureProcessIdentity(candidate.RendererPid, ownerSession, out live)
                || live.Creation != candidate.RendererCreation
                || !SamePath(live.Path, candidate.RendererPath)
                || !string.Equals(live.Name, candidate.RendererName,
                    StringComparison.OrdinalIgnoreCase)
                || ForegroundPid() != candidate.RendererPid)
                return null;
            return candidate;
        }

        // 与全局选举并行的挑战者通道 旧 renderer/旧 learned 不会吞掉当前
        // 前台的新候选 这里不改变 DetectSnapshot BetterHit 或 sticky 的仲裁
        internal static GameDetection FindForegroundCandidateSnapshot(
            IList<GameProcessSnapshot> snapshot,
            GameProfile profile, GameDetection incumbent,
            GameFamilyEvidence familyEvidence = null)
        {
            if (profile == null) return null;
            Dictionary<int, GameProcessSnapshot> byPid;
            GameProcessSnapshot foreground;
            if (!TryIndexForegroundCandidate(snapshot, out byPid, out foreground)
                || SameRendererIdentity(incumbent, foreground)) return null;
            return FindForegroundCandidateInProfile(byPid, foreground, profile, familyEvidence);
        }

        // 只为同一个前台身份挑明确归属 不让另一个档案的 ready/learned
        // 目标抢先吞掉 pending 此通道不改变原有全局 Detect 的会话仲裁
        internal static GameDetection FindForegroundCandidateSnapshot(
            IList<GameProcessSnapshot> snapshot,
            IList<GameProfile> profiles, GameDetection incumbent,
            GameFamilyEvidence familyEvidence = null)
        {
            if (profiles == null || profiles.Count == 0) return null;
            Dictionary<int, GameProcessSnapshot> byPid;
            GameProcessSnapshot foreground;
            if (!TryIndexForegroundCandidate(snapshot, out byPid, out foreground)
                || SameRendererIdentity(incumbent, foreground)) return null;

            GameDetection best = null;
            bool ambiguous = false;
            foreach (GameProfile profile in profiles)
            {
                if (profile == null) continue;
                GameDetection candidate = FindForegroundCandidateInProfile(byPid, foreground, profile, familyEvidence);
                if (candidate == null) continue;
                int precision = CompareCandidateOwnership(candidate, best);
                if (precision > 0)
                {
                    best = candidate;
                    ambiguous = false;
                }
                else if (precision == 0 && !SameCandidateOwner(candidate.Profile, best.Profile))
                    ambiguous = true;
            }
            return ambiguous ? null : best;
        }

        private static bool TryIndexForegroundCandidate(
            IList<GameProcessSnapshot> snapshot,
            out Dictionary<int, GameProcessSnapshot> byPid,
            out GameProcessSnapshot foreground)
        {
            byPid = new Dictionary<int, GameProcessSnapshot>();
            foreground = null;
            if (snapshot == null) return false;
            var seenPids = new HashSet<int>();
            var duplicatePids = new HashSet<int>();
            foreach (GameProcessSnapshot identity in snapshot)
                if (identity != null && identity.Pid > 0
                    && !seenPids.Add(identity.Pid))
                    duplicatePids.Add(identity.Pid);

            foreach (GameProcessSnapshot identity in snapshot)
            {
                if (!CandidateIdentityUsable(identity)
                    || duplicatePids.Contains(identity.Pid)
                    || AntiCheatCatalog.IsAntiCheatLikeName(identity.Name))
                    continue;
                byPid.Add(identity.Pid, identity);
                if (!identity.Foreground) continue;
                // 一份快照只有一个当前前台 相互矛盾的证据不能凭创建时间猜
                if (foreground != null) return false;
                foreground = identity;
            }
            return foreground != null;
        }

        private static GameDetection FindForegroundCandidateInProfile(
            Dictionary<int, GameProcessSnapshot> byPid,
            GameProcessSnapshot foreground, GameProfile profile, GameFamilyEvidence familyEvidence)
        {
            bool configured = SamePath(profile.ExecutablePath, foreground.Path)
                || SamePath(profile.LearnedExecutablePath, foreground.Path);
            // Force 只表示尊重用户指定的入口 不覆盖系统/反作弊安全边界
            // 对未知程序不按客户端 游戏或辅助程序的名字猜角色
            if (ElectionVetoed(foreground.Name, foreground.Path))
                return null;

            var directPids = new HashSet<int>();
            foreach (GameProcessSnapshot identity in byPid.Values)
                if (IsDirectMember(profile, identity, familyEvidence)) directPids.Add(identity.Pid);
            if (!directPids.Contains(foreground.Pid)
                && !HasMemberAncestor(foreground, byPid, directPids))
                return null;

            GameDetection candidate;
            if (profile.ForceTrigger && configured)
                candidate = ElectForced(profile, new List<GameProcessSnapshot> { foreground });
            else if (profile.ForceTrigger)
            {
                candidate = PendingGpuConfirm(profile, foreground);
                candidate.RendererSafetyOnly = true;
                candidate.RequiresGpuConfirm = false;
                candidate.RendererLearnable = false;
                candidate.Evidence = null;
            }
            else if (foreground.FullscreenLike)
                candidate = Elected(profile, foreground, !configured, Lang.T("detect.fullscreen"));
            else if (SamePath(profile.LearnedExecutablePath, foreground.Path))
                candidate = Elected(profile, foreground, false, Lang.T("detect.learned"));
            else if (SamePath(profile.ExecutablePath, foreground.Path))
                candidate = Elected(profile, foreground, false, Lang.T("detect.window"));
            else
                candidate = PendingGpuConfirm(profile, foreground);

            foreach (GameProcessSnapshot identity in byPid.Values)
                if (directPids.Contains(identity.Pid)
                    || HasMemberAncestor(identity, byPid, directPids))
                {
                    candidate.FamilyPids.Add(identity.Pid);
                    candidate.FamilyNames.Add(identity.Name);
                }
            return candidate;
        }

        private static int CompareCandidateOwnership(GameDetection candidate, GameDetection current)
        {
            if (current == null) return 1;
            int rank = candidate.RendererMatchRank.CompareTo(current.RendererMatchRank);
            if (rank != 0) return rank;
            // 两个 Root 都确实包含同一规范路径时 更长的 Root 只能是更窄的
            // 已声明子目录 不向上扩大目录 也不据启动器名称猜测归属
            if (candidate.RendererMatchRank == 1)
            {
                int candidateLength = candidate.Profile.Root.TrimEnd('\\').Length;
                int currentLength = current.Profile.Root.TrimEnd('\\').Length;
                return candidateLength.CompareTo(currentLength);
            }
            return 0;
        }

        private static bool SameCandidateOwner(GameProfile a, GameProfile b)
        {
            if (ReferenceEquals(a, b)) return true;
            return a != null && b != null && !string.IsNullOrEmpty(a.Id)
                && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
                && a.ForceTrigger == b.ForceTrigger
                && string.Equals(a.Root ?? "", b.Root ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ExecutablePath ?? "", b.ExecutablePath ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.LearnedExecutablePath ?? "", b.LearnedExecutablePath ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CandidateIdentityUsable(GameProcessSnapshot identity)
        {
            if (identity == null || identity.Pid <= 0 || identity.Creation <= 0
                || string.IsNullOrEmpty(identity.Name) || string.IsNullOrEmpty(identity.Path))
                return false;
            try
            {
                // 原生镜像路径是绝对规范路径 拒绝相对路径 .. 或名称拼接证据
                if (!Path.IsPathRooted(identity.Path)
                    || !SamePath(Path.GetFullPath(identity.Path), identity.Path))
                    return false;
            }
            catch { return false; }
            return string.Equals(identity.Name, ImageNameFromVerifiedPath(identity.Path),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameRendererIdentity(
            GameDetection incumbent, GameProcessSnapshot identity)
        {
            return incumbent != null && identity != null
                && incumbent.RendererPid == identity.Pid
                && incumbent.RendererCreation > 0
                && incumbent.RendererCreation == identity.Creation
                && SamePath(incumbent.RendererPath, identity.Path);
        }

        private static bool TryCaptureCandidateWindow(out int pid, out bool fullscreen)
        {
            pid = 0;
            fullscreen = false;
            try
            {
                IntPtr window = GetForegroundWindow();
                if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window))
                    return false;
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner == 0 || owner > int.MaxValue) return false;
                pid = (int)owner;
                NativeRect rect;
                fullscreen = GetWindowRect(window, out rect)
                    && IsFullscreenLikeWindow(window, rect);
                return true;
            }
            catch { return false; }
        }

    }
}
