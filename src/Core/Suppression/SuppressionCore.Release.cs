// @author bdth 2074055628@qq.com
// 文件用途 解除压制 原值还原与待还原重试
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        public bool Release(int pid, SuppressReason reason)
        {
            bool had;
            ReleaseOne(pid, reason, out had);
            return had;
        }

        public bool ReleaseIfCreation(
            int pid, SuppressReason reason, long expectedCreation)
        {
            if (expectedCreation <= 0) return false;
            bool had;
            ReleaseOne(
                pid, reason, expectedCreation, true, out had);
            return had;
        }

        // 调用方必须先把这个身份从新的后台 Acquire/Reconcile 里排除
        // 并在 Ready 之后重新校验它的原生身份
        // 清掉一个 reason 不等于原始值已经还原完
        // Reasons=None 但条目还活着 那仍然是一笔恢复欠账
        internal BackgroundReleaseState ReleaseBackgroundForRenderer(
            int pid, long expectedCreation, string expectedName)
        {
            if (pid <= 0 || expectedCreation <= 0 || string.IsNullOrWhiteSpace(expectedName))
                return BackgroundReleaseState.IdentityMismatch;

            lock (sync)
            {
                Entry current;
                if (!map.TryGetValue(pid, out current)) return BackgroundReleaseState.Ready;
                if (current.Creation != expectedCreation || !SameName(current.Name, expectedName))
                    return BackgroundReleaseState.IdentityMismatch;
                if ((current.Reasons & SuppressReason.Background) == 0)
                    return RendererReleaseStateOf(current);
            }

            bool had;
            RestoreResult? restoreResult;
            ReleaseOne(pid, SuppressReason.Background, expectedCreation, true,
                expectedName, out had, out restoreResult);

            lock (sync)
            {
                Entry current;
                if (!map.TryGetValue(pid, out current))
                    return restoreResult == RestoreResult.Gone
                        ? BackgroundReleaseState.Gone : BackgroundReleaseState.Ready;
                if (current.Creation != expectedCreation || !SameName(current.Name, expectedName))
                    return BackgroundReleaseState.IdentityMismatch;
                return RendererReleaseStateOf(current);
            }
        }

        private static BackgroundReleaseState RendererReleaseStateOf(Entry entry)
        {
            if ((entry.Reasons & ~SuppressReason.Background) != SuppressReason.None)
                return BackgroundReleaseState.OtherReasonActive;
            // 这里不要调 TryRestore 只有第一次释放才启动恢复
            // 后续轮询只观察 RetryPending 和它受保护的退避
            return BackgroundReleaseState.Pending;
        }

        // 合法的渲染进程可能复用一个 PID 而 map 里那个进程已经死了
        // 只忘掉这条过期账 绝不能为了删旧身份就对新进程做
        // Acquire 或 Restore 调用方还需要 ReleaseBackgroundForRenderer
        // 以及最后一次原生身份检查
        internal bool DiscardReusedRendererTracking(int pid, long expectedCreation, string expectedName)
        {
            if (pid <= 0 || expectedCreation <= 0 || string.IsNullOrWhiteSpace(expectedName)) return false;
            Entry observed;
            long observedCreation;
            lock (sync)
            {
                map.TryGetValue(pid, out observed);
                observedCreation = observed == null ? 0 : observed.Creation;
            }
            if (!RendererDiscardIdentityMatches(pid, expectedCreation, expectedName)) return false;
            lock (sync)
            {
                Entry current;
                if (!map.TryGetValue(pid, out current)) return true;
                // 只读查询还在飞的时候 Acquire 可能替换掉一个条目
                // 或者就地填上一个 creation=0 的占位
                if (!ReferenceEquals(current, observed) || current.Creation != observedCreation) return false;
                bool reused = current.Creation > 0 && current.Creation != expectedCreation;
                bool untouched = current.Creation == 0 && current.OrigPri == uint.MaxValue
                    && !current.Applied && !current.Journaled;
                if (!reused && !untouched) return false;

                map.Remove(pid);
                batchApply.Remove(pid);
                batchApplyResults.Remove(pid);
                batchApplyErrors.Remove(pid);
                TryClearMarkLocked();
                RefreshThrottledCacheLocked();
                RefreshGroupCountsLocked();
                return true;
            }
        }

        private bool RendererDiscardIdentityMatches(int pid, long expectedCreation, string expectedName)
        {
#if PAVISE_SELFTEST
            if (RendererDiscardIdentityForTest != null)
                return RendererDiscardIdentityForTest(pid, expectedCreation, expectedName);
            // 没有身份接缝的内存夹具 必须一直留在内存里
            if (restoreForTest != null) return false;
#endif
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                string name = Native.ImageName(handle);
                long creation, cpu;
                ulong io;
                return SameName(name, expectedName)
                    && Native.QueryProcessSample(handle, out creation, out cpu, out io)
                    && creation == expectedCreation && Native.StillActive(handle);
            }
            catch { return false; }
            finally { Native.CloseHandle(handle); }
        }

        public int ReleaseReason(SuppressReason reason)
        {
            int restored = 0; bool had;
            List<int> pids = PidsWith(reason);
            BeginJournalDefer();
            try
            {
                foreach (int pid in pids) restored += ReleaseOne(pid, reason, out had);
            }
            finally { EndJournalDefer(); }
            return restored;
        }

        private int ReleaseOne(int pid, SuppressReason reason, out bool had)
        {
            return ReleaseOne(pid, reason, 0, false, out had);
        }

        private int ReleaseOne(
            int pid, SuppressReason reason,
            long expectedCreation, bool requireCreation, out bool had)
        {
            RestoreResult? ignored;
            return ReleaseOne(pid, reason, expectedCreation, requireCreation, null, out had, out ignored);
        }

        private int ReleaseOne(
            int pid, SuppressReason reason,
            long expectedCreation, bool requireCreation, string expectedName,
            out bool had, out RestoreResult? restoreResult)
        {
            restoreResult = null;
            Entry e;
            bool adjust = false;
            bool remaining = false;
            lock (sync)
            {
                had = map.TryGetValue(pid, out e) && (e.Reasons & reason) != 0;
                if (had && requireCreation)
                    had = e.Creation > 0
                        && e.Creation == expectedCreation;
                if (had && expectedName != null)
                    had = SameName(e.Name, expectedName);
                if (!had) return 0;
                SuppressionLevel previousLevel = e.Level;
                bool hadSqueeze = e.SqueezeAff != 0;
                e.Reasons &= ~reason;
                if ((reason & SuppressReason.AntiCheat) != 0) e.AntiCheatLevel = SuppressionLevel.None;
                // 后台原因一撤 绑核落点也清掉 下次再进后台由热度重新判 不许带着旧落点直接绑上
                //   条目仍带反作弊原因时落点归反作弊路 由那边的开关决定 这里不动
                if ((reason & SuppressReason.Background) != 0)
                {
                    e.BackgroundLevel = SuppressionLevel.None;
                    if ((e.Reasons & ~reason & SuppressReason.AntiCheat) == 0) e.SqueezeAff = 0;
                }
                e.Level = EffectiveLevel(e);
                if (e.Reasons != SuppressReason.None)
                {
                    remaining = true;
                    adjust = e.OrigPri != uint.MaxValue && e.Journaled
                        && (previousLevel != e.Level || !e.Applied || hadSqueeze);
                    PersistJournalLocked();
                }
                else if (e.OrigPri == uint.MaxValue) { map.Remove(pid); PersistJournalLocked(); return 0; }
                else if (!e.Journaled && !e.Applied)
                {

                    map.Remove(pid);
                    batchApply.Remove(pid);
                    PersistJournalLocked();
                    return 0;
                }
            }
            if (adjust)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                bool applied = false;
                if (h != IntPtr.Zero) { try { if (SameProcess(h, e)) applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), e.OrigBoost, AntiCheatThrottled(e), DesiredAffinityOf(e), e.SqueezeRefused); } finally { Native.CloseHandle(h); } }
                lock (sync)
                {
                    Entry cur;
                    if (map.TryGetValue(pid, out cur) && cur == e)
                    {
                        cur.Applied = applied;
                        ScheduleAfterApply(cur, applied, pid);
                    }
                }
                return 0;
            }
            if (remaining) return 0;
            RestoreResult result;
            bool restored = TryRestore(pid, e, out result);
            restoreResult = result;
            return restored ? 1 : 0;
        }

        private bool TryRestore(int pid, Entry e, bool respectBackoff)
        {
            RestoreResult ignored;
            return TryRestore(pid, e, respectBackoff, out ignored);
        }

        private bool TryRestore(int pid, Entry e, out RestoreResult result)
        {
            return TryRestore(pid, e, false, out result);
        }

        private bool TryRestore(int pid, Entry e, bool respectBackoff, out RestoreResult result)
        {
            result = RestoreResult.Protected;
            lock (sync)
            {
                Entry current;
                // RetryPending 基于快照工作 这个条目被移除 替换或者重新获取之后
                // 它不能再去启动一次旧的还原
                // 也不能和同一条目上第一次 ReleaseOne 的还原叠在一起
                if (!map.TryGetValue(pid, out current) || !ReferenceEquals(current, e)
                    || e.Reasons != SuppressReason.None || e.RestoreInFlight
                    || (respectBackoff && DateTime.UtcNow.Ticks < e.NextRetryTicks)) return false;
                e.RestoreInFlight = true;
            }
            try { return TryRestoreOwned(pid, e, out result); }
            finally { lock (sync) e.RestoreInFlight = false; }
        }

        private bool TryRestoreOwned(int pid, Entry e, out RestoreResult result)
        {
            RestoreResult r;
#if PAVISE_SELFTEST
            if (restoreForTest != null) r = restoreForTest(pid, e.Creation, e.Name);
            else
#endif
                r = e.GaveUpRestored ? RestoreResult.Restored : RestoreOne(pid, e);
            result = r;
            bool reThrottle = false;
            lock (sync)
            {
                Entry cur;
                if (map.TryGetValue(pid, out cur) && cur == e)
                {
                    if (e.Reasons == SuppressReason.None)
                    {
                        if (r != RestoreResult.Protected) map.Remove(pid);
                    }
                    else if (r == RestoreResult.Restored) reThrottle = true;
                }
                TryClearMarkLocked();
            }
            if (reThrottle)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                bool applied = false;
                if (h != IntPtr.Zero) { try { if (SameProcess(h, e)) applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), e.OrigBoost, AntiCheatThrottled(e), DesiredAffinityOf(e), e.SqueezeRefused); } finally { Native.CloseHandle(h); } }
                lock (sync)
                {
                    Entry cur;
                    if (map.TryGetValue(pid, out cur) && cur == e)
                    {
                        cur.Applied = applied;
                        ScheduleAfterApply(cur, applied, pid);
                    }
                }
                return false;
            }
            if (r == RestoreResult.Protected)
            {
                lock (sync)
                {
                    Entry cur;
                    if (map.TryGetValue(pid, out cur) && cur == e)
                    {
                        if (e.ProtectedRetries == 0 && ShouldLogProtected(e.Name))
                            Logger.Log(Lang.T("log.suppressioncore.1") + e.Name + " pid " + pid + Lang.T("log.suppressioncore.2"));
                        if (e.ProtectedRetries < ProtectedBackoffMax) e.ProtectedRetries++;
                        if (e.ProtectedRetries >= ProtectedBackoffMax)
                        {
                            e.NextRetryTicks = DateTime.MaxValue.Ticks;
                            if (ShouldLogProtected(e.Name + "-parked"))
                                Logger.Log(Lang.T("log.suppressioncore.1") + e.Name + " pid " + pid
                                    + Lang.T("log.suppressioncore.3"));
                        }
                        else
                        {
                            int delay = ProtectedBackoffBaseSeconds;
                            for (int i = 1; i < e.ProtectedRetries; i++)
                            {
                                delay *= 2;
                                if (delay >= ProtectedBackoffCapSeconds) break;
                            }
                            if (delay > ProtectedBackoffCapSeconds) delay = ProtectedBackoffCapSeconds;
                            e.NextRetryTicks = DateTime.UtcNow.AddSeconds(delay).Ticks;
                        }
                    }
                }
            }
            else if (r == RestoreResult.Restored && e.ProtectedRetries > 0)
                Logger.Log(Lang.T("log.suppressioncore.4") + e.Name + " pid " + pid + Lang.T("log.suppressioncore.5") + e.ProtectedRetries + Lang.T("t.gputhrottleprobe.5"));
            return r == RestoreResult.Restored;
        }

        public void RetryPending()
        {
            List<KeyValuePair<int, Entry>> pending = null;
            long now = DateTime.UtcNow.Ticks;
            lock (sync)
                foreach (var kv in map)
                    if (kv.Value.Reasons == SuppressReason.None && now >= kv.Value.NextRetryTicks)
                    {
                        if (pending == null) pending = new List<KeyValuePair<int, Entry>>();
                        pending.Add(kv);
                    }
            if (pending == null) return;
            foreach (var kv in pending)
                if (TryRestore(kv.Key, kv.Value, true) && kv.Value.ProtectedRetries == 0)
                    Logger.Log(Lang.T("log.suppressioncore.4") + kv.Value.Name + " pid " + kv.Key);
        }

        private void TryClearMarkLocked()
        {
            PersistJournalLocked();
            if (!marked) return;
            foreach (var kv in map) if (kv.Value.OrigPri != uint.MaxValue) return;
            marked = false;
            CrashGuard.ReleaseThrottle(throttleMask);
        }
    }
}
