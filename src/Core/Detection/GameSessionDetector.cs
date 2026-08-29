// @author bdth 2074055628@qq.com
// 文件用途 通用游戏会话选举 路径与有效父链圈定家族 窗口选举不等于已经观测到渲染活动
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
        // 独立前台候选可以只享有临时安全保护，不得作为已选中的 renderer。
        // 强制接管档案里的陌生进程使用此标记；不采证接管，也不学习。
        public bool RendererSafetyOnly;
        public bool RequiresGpuConfirm;
        // 只随异步确认票据携带；0 表示原有全屏/精确入口等硬证据。
        // 不能把已过期的 GPU 结果当成永久有效的提交授权。
        public long RendererGpuProofExpiresMs;
        // 同一个 renderer 同时被多个档案引用时，用配置锚的精确度稳定决胜，
        // 避免最终选中哪个 profile（以及它的独立策略）取决于列表顺序。
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

    internal static class GameSessionDetector
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

        // 热路径只看当前前台窗口，不枚举所有顶层窗口。ProcEntry 的身份来自
        // 本轮系统快照；只有发现新的关联候选后才额外核验它仍是同一生命期。
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

            // 系统快照本应一 PID 一项；拒绝歧义快照，不能由 ByPid 的最后一项
            // 替重复 PID 选择身份，尤其不能把另一个登录会话的身份拼到父链里。
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

        // 与全局选举并行的挑战者通道：旧 renderer/旧 learned 不会吞掉当前
        // 前台的新候选。这里不改变 DetectSnapshot、BetterHit 或 sticky 的仲裁。
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

        // 只为同一个前台身份挑明确归属，不让另一个档案的 ready/learned
        // 目标抢先吞掉 pending。此通道不改变原有全局 Detect 的会话仲裁。
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
                // 一份快照只有一个当前前台；相互矛盾的证据不能凭创建时间猜。
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
            // Force 只表示尊重用户指定的入口，不覆盖系统/反作弊安全边界。
            // 对未知程序不按客户端、游戏或辅助程序的名字猜角色。
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
            // 两个 Root 都确实包含同一规范路径时，更长的 Root 只能是更窄的
            // 已声明子目录；不向上扩大目录，也不据启动器名称猜测归属。
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
                // 原生镜像路径是绝对规范路径；拒绝相对路径、.. 或名称拼接证据。
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
            string armedVia;
            return DetectSnapshot(snapshot, profiles, out armedProfile, out armedVia);
        }

        internal static GameDetection DetectSnapshot(
            IList<GameProcessSnapshot> snapshot,
            IList<GameProfile> profiles, out string armedProfile, out string armedVia,
            GameFamilyEvidence familyEvidence = null)
        {
            armedProfile = null;
            armedVia = null;
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
                if (!CandidateIdentityUsable(identity)
                    || duplicatePids.Contains(identity.Pid)
                    || ElectionVetoed(identity.Name, identity.Path))
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
                    if (IsDirectMember(profile, identity, familyEvidence))
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

                if (armedProfile == null)
                {
                    string via = ArmedVia(profile, members);
                    if (via != null)
                    {
                        armedProfile = profile.Name;
                        armedVia = via;
                    }
                }
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

        internal static string ArmedVia(GameProfile profile,
            IList<GameProcessSnapshot> members)
        {
            if (profile == null || members == null) return null;
            var viaNames = new List<string>();
            foreach (GameProcessSnapshot member in members)
            {
                if (!CandidateIdentityUsable(member)
                    || ElectionVetoed(member.Name, member.Path)) continue;
                if (!viaNames.Contains(member.Name)) viaNames.Add(member.Name);
                if (viaNames.Count >= 3) break;
            }
            return viaNames.Count == 0 ? null : string.Join(" ", viaNames.ToArray());
        }

        private static bool IsDirectMember(GameProfile profile, GameProcessSnapshot identity,
            GameFamilyEvidence familyEvidence = null)
        {
            if (SamePath(profile.ExecutablePath, identity.Path)) return true;
            if (SamePath(profile.LearnedExecutablePath, identity.Path)) return true;
            return profile.ContainsPath(identity.Path)
                || (familyEvidence != null && familyEvidence.Contains(profile,
                    identity.Pid, identity.Creation, identity.Path));
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
            if (profile.ForceTrigger) return ElectForced(profile, members);

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

        private static GameDetection ElectForced(GameProfile profile, List<GameProcessSnapshot> members)
        {
            GameProcessSnapshot best = null;
            foreach (GameProcessSnapshot member in members)
            {
                if (!SamePath(profile.ExecutablePath, member.Path)
                    && !SamePath(profile.LearnedExecutablePath, member.Path)) continue;
                if (best == null || PreferForced(member, best)) best = member;
            }
            if (best == null) return null;
            return new GameDetection
            {
                Profile = profile.Clone(),
                RendererPid = best.Pid,
                RendererCreation = best.Creation,
                RendererName = best.Name,
                RendererPath = best.Path,
                RendererForeground = best.Foreground,
                RendererCandidateSelected = true,
                RendererUserSelected = true,
                RendererLearnable = false,
                RendererMatchRank = MatchRank(profile, best.Path),
                Evidence = Lang.T("detect.forced")
            };
        }

        private static bool PreferForced(GameProcessSnapshot candidate, GameProcessSnapshot current)
        {
            if (candidate.Foreground != current.Foreground) return candidate.Foreground;
            if (candidate.Creation != current.Creation) return candidate.Creation < current.Creation;
            return candidate.Pid < current.Pid;
        }

        internal static bool ElectionVetoed(string name, string path)
        {
            if (string.IsNullOrEmpty(name)) return true;
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
                RendererMatchRank = MatchRank(profile, selected.Path),
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
                RendererMatchRank = MatchRank(profile, candidate.Path),
                Evidence = Lang.T("detect.gpu.pending")
            };
        }

        private static int MatchRank(GameProfile profile, string path)
        {
            if (profile == null || string.IsNullOrEmpty(path)) return 0;
            if (SamePath(profile.ExecutablePath, path)) return 3;
            if (SamePath(profile.LearnedExecutablePath, path)) return 2;
            return profile.ContainsPath(path) ? 1 : 0;
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
            // 只有两项实际指向同一进程时才用锚点精度破同分；不同 renderer 仍保持
            // 既有的前台/创建时间选举，不让本次修复改变正常多进程会话行为。
            if (candidate.RendererPid == current.RendererPid
                && candidate.RendererCreation == current.RendererCreation)
            {
                if (candidate.RendererMatchRank != current.RendererMatchRank)
                    return candidate.RendererMatchRank > current.RendererMatchRank;
                // 两个档案都把同一 G 学成 Learned 时，G 真正位于谁的
                // Root 是更强的所有权语义。若仍同分，用持久 profile 身份
                // 稳定决胜，绝不让独立策略随列表顺序漂移。
                bool candidateOwnsPath = candidate.Profile != null
                    && candidate.Profile.ContainsPath(candidate.RendererPath);
                bool currentOwnsPath = current.Profile != null
                    && current.Profile.ContainsPath(current.RendererPath);
                if (candidateOwnsPath != currentOwnsPath) return candidateOwnsPath;
                int profileOrder = CompareStableProfile(candidate.Profile, current.Profile);
                if (profileOrder != 0) return profileOrder < 0;
            }
            if (candidate.RendererCreation != current.RendererCreation)
                return candidate.RendererCreation > current.RendererCreation;
            return candidate.RendererPid < current.RendererPid;
        }

        private static int CompareStableProfile(GameProfile a, GameProfile b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            string[] ak = { a.Id, a.Root, a.ExecutablePath, a.LearnedExecutablePath, a.Name };
            string[] bk = { b.Id, b.Root, b.ExecutablePath, b.LearnedExecutablePath, b.Name };
            for (int i = 0; i < ak.Length; i++)
            {
                int c = string.Compare(ak[i] ?? "", bk[i] ?? "",
                    StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = string.CompareOrdinal(ak[i] ?? "", bk[i] ?? "");
                if (c != 0) return c;
            }
            return 0;
        }

        // 这里只承担禁止把安全组件作为调优目标的边界，不承担程序角色推断。
        // GUI、浏览器技术、文件名或安装平台都不能证明它不是游戏渲染程序。
        internal static bool IsNonGameRole(string name, string path)
        {
            string n = (name ?? "").Trim();
            if (AntiCheatCatalog.IsAntiCheatLikeName(n)) return true;
            // 系统壳/核心组件是调优安全边界，不是游戏名单；同名外部程序不受此限制。
            if (!string.IsNullOrEmpty(WindowsRootPrefix) && !string.IsNullOrEmpty(path)
                && path.StartsWith(WindowsRootPrefix, StringComparison.OrdinalIgnoreCase)
                && (SystemProcessCatalog.IsShellProcess(n)
                    || SystemProcessCatalog.IsCoreSystemProcess(n, path, WindowsRootPrefix))) return true;
            string low = ((path ?? "") + "\\" + n).ToLowerInvariant();
            return AntiCheatCatalog.ContainsToken(low);
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
            bool succeeded;
            return VisibleWindowPids(includeMinimized, out succeeded);
        }

        internal static HashSet<int> VisibleWindowPids(bool includeMinimized, out bool succeeded)
        {
            var result = new HashSet<int>();
            succeeded = false;
            try
            {
                succeeded = EnumWindows(delegate(IntPtr window, IntPtr state)
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
