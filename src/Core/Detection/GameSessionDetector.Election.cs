// @author bdth 2074055628@qq.com
// 文件用途 家族成员判定与游戏本体选举
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class GameSessionDetector
    {
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
            // 只有两项实际指向同一进程时才用锚点精度破同分 不同 renderer 仍保持
            // 既有的前台/创建时间选举 不让本次修复改变正常多进程会话行为
            if (candidate.RendererPid == current.RendererPid
                && candidate.RendererCreation == current.RendererCreation)
            {
                if (candidate.RendererMatchRank != current.RendererMatchRank)
                    return candidate.RendererMatchRank > current.RendererMatchRank;
                // 两个档案都把同一 G 学成 Learned 时 G 真正位于谁的
                // Root 是更强的所有权语义 若仍同分 用持久 profile 身份
                // 稳定决胜 绝不让独立策略随列表顺序漂移
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
    }
}
