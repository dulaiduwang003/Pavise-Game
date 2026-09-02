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

    }
}
