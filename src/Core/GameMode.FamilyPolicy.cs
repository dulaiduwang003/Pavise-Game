// 文件用途 逐库条目的家族策略 闸门把发布和最后一次后台写检查串起来
// 排队中的扫描不能把已经关掉的策略再应用一遍
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly object familyPolicyGate = new object();
        private int familyPolicyEpoch;

        public bool SetProfileFamilySuppression(string profileId, bool on)
        {
            return SetProfileFamilySuppression(profileId, on, null);
        }

        public bool SetProfileFamilySuppression(string profileId, bool on, string expectedExecutablePath)
        {
            bool changed = false;
            lock (familyPolicyGate)
            {
                lock (sync)
                {
                    GameProfile current = FindProfileLocked(profileId);
                    if (stopping || current == null || ProfileStoreSaveFailed) return false;
                    if (expectedExecutablePath != null && !string.Equals(current.ExecutablePath,
                        expectedExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
                    if (current.SuppressFamilyBackground == on) return true;
                    GameProfile replacement = current.Clone();
                    if (on) replacement.Overrides[PolicyCatalog.KeySuppressFamily] = "1";
                    else replacement.Overrides.Remove(PolicyCatalog.KeySuppressFamily);
                    var next = new List<GameProfile>(profiles);
                    int index = profiles.IndexOf(current);
                    next[index] = replacement;
                    if (!profileStore.Save(next))
                    {
                        SignalProfileStoreSaveFailure();
                        return false;
                    }
                    profiles[index] = replacement;
                    // 刷新供显示和策略使用的副本 不改渲染进程身份
                    if (activeDetection != null && activeDetection.Profile != null
                        && activeDetection.Profile.Id == profileId)
                        activeDetection.Profile = replacement.Clone();
                    if (stickyDetection != null && stickyDetection.Profile != null
                        && stickyDetection.Profile.Id == profileId)
                        stickyDetection.Profile = replacement.Clone();
                    Interlocked.Increment(ref familyPolicyEpoch);
                    firstSweep = true;
                    changed = true;
                }
            }
            if (changed)
            {
                // 工作线程只还原新豁免出来的 Background reason 界面线程
                // 不做原生进程写入 也不清任何无关的 reason
                RequestPolicyApply();
                RaiseLibraryChanged();
            }
            return true;
        }

        internal int FamilyPolicyEpoch { get { return Volatile.Read(ref familyPolicyEpoch); } }

        internal bool FamilyPolicyEpochCurrent(int epoch)
        {
            return epoch == Volatile.Read(ref familyPolicyEpoch)
                && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed && EffSuppress;
        }

        private bool RunBackgroundPolicy(int epoch, Action action)
        {
            lock (familyPolicyGate)
            {
                if (!FamilyPolicyEpochCurrent(epoch)) return false;
                action();
                return true;
            }
        }

        private void InvalidateFamilyPolicy()
        {
            // 工作线程 生命周期和界面都会调它 不做进程写入
            Interlocked.Increment(ref familyPolicyEpoch);
        }

        internal static bool FamilyExemptFor(GameProfile profile)
        {
            return profile == null || !profile.SuppressFamilyBackground;
        }

        // 保护属于每一个选择退出的档案 不只是当前前台那个游戏
        // 复用本轮 sweep 那份不可变进程快照 绝不能从游戏或客户端的
        // 可执行文件名去推断家族归属
        internal static HashSet<int> CollectProtectedLibraryFamily(IList<GameProfile> configured,
            ProcessSnapshot snapshot, int selfPid, int ownerSession, GameFamilyEvidence familyEvidence = null)
        {
            var result = new HashSet<int>();
            if (configured == null || snapshot == null || ownerSession < 0) return result;
            var roots = new List<string>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile profile in configured)
            {
                if (profile == null || !FamilyExemptFor(profile)) continue;
                if (!string.IsNullOrEmpty(profile.ExecutablePath)) paths.Add(profile.ExecutablePath);
                if (!string.IsNullOrEmpty(profile.LearnedExecutablePath)) paths.Add(profile.LearnedExecutablePath);
                if (SafeFamilyDir(profile.Root)) roots.Add(profile.Root);
            }
            if (paths.Count == 0 && roots.Count == 0) return result;
            var parents = new Dictionary<int, int>();
            var seeds = new HashSet<int>();
            foreach (ProcEntry child in snapshot.Entries)
            {
                if (child.Pid <= 4 || child.Pid == selfPid || child.Session != ownerSession
                    || child.Creation <= 0) continue;
                if (!string.IsNullOrEmpty(child.Path)
                    && (paths.Contains(child.Path) || LibraryRootOf(child.Path, roots) != null))
                    seeds.Add(child.Pid);
                if (familyEvidence != null)
                    foreach (GameProfile profile in configured)
                        if (profile != null && FamilyExemptFor(profile)
                            && familyEvidence.Contains(profile, child.Pid, child.Creation, child.Path))
                        { seeds.Add(child.Pid); break; }
                ProcEntry parent = snapshot.Find(child.ParentPid);
                // 不要把只看 PID 的老兜底逻辑带进这些新的跨根链接
                // 身份缺失或者 PID 被复用 就到此为止
                if (parent == null || parent.Pid <= 4 || parent.Pid == selfPid
                    || parent.Pid == child.Pid || parent.Session != ownerSession
                    || parent.Creation <= 0 || parent.Creation > child.Creation) continue;
                parents[child.Pid] = parent.Pid;
            }
            result.UnionWith(seeds);
            result.UnionWith(WalkDescendants(parents, seeds, selfPid, 24));
            foreach (int seed in seeds)
                result.UnionWith(WalkAncestorChain(parents, seed, selfPid, 24));
            // 祖先进程自己受保护 但绝不能当新种子 共享宿主
            // 不能顺带豁免它那些无关的兄弟进程或者别的游戏
            return result;
        }
    }
}
