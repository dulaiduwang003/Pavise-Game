using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal interface IWindowsBackgroundJobs
    {
        bool HasActive { get; }
        bool HasPending { get; }
        bool ReadOriginalMask(WindowsBackgroundTarget target, out ulong mask, out string reason);
        bool Ensure(WindowsBackgroundTarget target, ulong original, ulong desired, int mode, out string reason);
        bool Verify(WindowsBackgroundTarget target, ulong desired, out string reason);
        bool Release(WindowsBackgroundTarget target, out string reason);
        bool ReleaseAll(out string reason);
        bool Stop(out string reason);
    }

    internal sealed class WindowsBackgroundJobs : IWindowsBackgroundJobs
    {
        private readonly GameAffinityJobLease leases = new GameAffinityJobLease();
        public bool HasActive { get { return leases.HasActive; } }
        public bool HasPending { get { return leases.HasPending; } }
        public bool ReadOriginalMask(WindowsBackgroundTarget target, out ulong mask, out string reason)
        {
            mask = 0; reason = null;
            IntPtr process = GameAffinityJobNative.OpenIdentity(target.Process.Pid, target.Process.Creation);
            if (process == IntPtr.Zero) { reason = "微软后台原始范围读取权限不足"; return false; }
            try
            {
                if (Native.TryQueryAffinity(process, out mask) && mask != 0) return true;
                reason = "微软后台原始范围不可确认"; return false;
            }
            finally { Native.CloseHandle(process); }
        }
        public bool Ensure(WindowsBackgroundTarget target, ulong original, ulong desired, int mode, out string reason)
        { return leases.EnsureWindowsBackground(target.Process.Pid, target.Process.Creation, target.Process.Name, original, desired, mode, out reason); }
        public bool Verify(WindowsBackgroundTarget target, ulong desired, out string reason)
        { return leases.Verify(target.Process.Pid, target.Process.Creation, desired, out reason); }
        public bool Release(WindowsBackgroundTarget target, out string reason)
        { return leases.Release(target.Process.Pid, target.Process.Creation, out reason); }
        public bool ReleaseAll(out string reason) { return leases.ReleaseAll(out reason); }
        public bool Stop(out string reason) { return leases.Stop(out reason); }
    }

    internal sealed class WindowsBackgroundGovernor
    {
        private sealed class TargetState
        {
            internal WindowsBackgroundTarget Target;
            internal ulong Original, Desired;
            internal int Mode;
            internal bool Active, Verified;
        }

        internal sealed class FailurePhase
        {
            internal int Failures, Corrections;
            internal long Next;
            internal bool Exhausted;
            internal bool Allowed(long now) { return !Exhausted && Failures < 3 && now >= Next; }
            internal void Fail(long now)
            { Failures++; Next = now + TimeSpan.FromSeconds(Failures * 2).Ticks; Exhausted |= Failures >= 3; }
        }

        private readonly IWindowsBackgroundJobs jobs;
        private readonly Dictionary<string, TargetState> targets = new Dictionary<string, TargetState>(StringComparer.Ordinal);
        private readonly Dictionary<string, FailurePhase> phases = new Dictionary<string, FailurePhase>(StringComparer.Ordinal);
        private readonly Dictionary<int, long> reserved = new Dictionary<int, long>();
        private long session = -1;
        private volatile int activeCount;
        private int restoreExceptions, stopExceptions;

        internal WindowsBackgroundGovernor(IWindowsBackgroundJobs jobs) { this.jobs = jobs; }
        internal bool HasRecovery { get { return jobs.HasActive || jobs.HasPending; } }
        internal int ActiveCount { get { return activeCount; } }
        internal bool Reserved(int pid, long birth)
        { long found; return reserved.TryGetValue(pid, out found) && found == birth; }

        private FailurePhase Phase(string identity, int mode, ulong mask)
        {
            string key = session + "/" + identity + "/" + mode + "/" + mask;
            FailurePhase phase;
            if (!phases.TryGetValue(key, out phase)) phases[key] = phase = new FailurePhase();
            return phase;
        }

        internal void Update(long serial, int mode, ulong backgroundMask, Dictionary<int, WindowsBackgroundTarget> selected,
            Func<WindowsBackgroundTarget, bool> prepare, Func<bool> admitted, long now)
        {
            if (serial != session)
            {
                // 仅真实新对局创建新预算；关闭再开启、重建扫描线程或回到旧档位都沿用本局预算。
                session = serial; phases.Clear(); restoreExceptions = 0; stopExceptions = 0;
            }
            mode = BackgroundGovernancePolicy.Normalize(mode);
            reserved.Clear();
            foreach (WindowsBackgroundTarget target in selected.Values) reserved[target.Process.Pid] = target.Process.Creation;
            foreach (TargetState state in new List<TargetState>(targets.Values))
            {
                WindowsBackgroundTarget current;
                if (selected.TryGetValue(state.Target.Process.Pid, out current) && current.Identity == state.Target.Identity) continue;
                string reason;
                FailurePhase release = Phase(state.Target.Identity + "/退出名单", state.Mode, state.Desired);
                if (!release.Allowed(now)) { reserved[state.Target.Process.Pid] = state.Target.Process.Creation; continue; }
                try
                {
                    if (jobs.Release(state.Target, out reason)) targets.Remove(state.Target.Identity);
                    else reserved[state.Target.Process.Pid] = state.Target.Process.Creation;
                }
                catch (Exception error)
                { Failed(state.Target, release, now, "恢复异常：" + error.GetType().Name); reserved[state.Target.Process.Pid] = state.Target.Process.Creation; }
            }
            foreach (WindowsBackgroundTarget target in selected.Values)
            {
                if (!admitted()) { Restore(); return; }
                FailurePhase phase = Phase(target.Identity, mode, backgroundMask);
                TargetState state;
                if (!targets.TryGetValue(target.Identity, out state))
                    targets[target.Identity] = state = new TargetState { Target = target };
                state.Target = target;
                string reason = null;
                FailurePhase operation = phase;
                try
                {
                    bool same = state.Active && state.Mode == mode && state.Desired == (backgroundMask & state.Original);
                    if (state.Active && !same)
                    {
                        // 旧档位先撤回；新档位即使已经耗尽，也不能继续留下另一档的限制。
                        state.Verified = false;
                        operation = Phase(target.Identity + "/切档恢复", state.Mode, state.Desired);
                        if (!operation.Allowed(now)) continue;
                        if (!jobs.Release(target, out reason)) { Failed(target, operation, now, reason); continue; }
                        state.Active = false; state.Original = 0;
                    }
                    operation = phase;
                    if (!phase.Allowed(now)) continue;
                    if (same && jobs.Verify(target, state.Desired, out reason)) { state.Verified = true; continue; }
                    state.Verified = false;
                    if (same)
                    {
                        if (phase.Corrections >= 3)
                        {
                            phase.Exhausted = true;
                            Logger.Warn("微软后台纠正预算耗尽：" + target.Category + " " + target.Process.Name
                                + " 进程编号=" + target.Process.Pid + "；保留恢复所有权，停止主动尝试");
                            continue;
                        }
                        phase.Corrections++;
                    }
                    if (state.Active)
                    {
                        if (!jobs.Release(target, out reason)) { Failed(target, phase, now, reason); continue; }
                        state.Active = false; state.Original = 0;
                    }
                    if (backgroundMask == 0) { Failed(target, phase, now, "没有可核验的单组后台核心范围"); continue; }
                    if (!prepare(target)) { Failed(target, phase, now, "原后台治理所有权尚未恢复"); continue; }
                    if (state.Original == 0 && !jobs.ReadOriginalMask(target, out state.Original, out reason))
                    { Failed(target, phase, now, reason); continue; }
                    ulong desired = backgroundMask & state.Original;
                    if (desired == 0) { Failed(target, phase, now, "原始范围与后台核心没有交集"); continue; }
                    if (!admitted()) { Restore(); return; }
                    if (!jobs.Ensure(target, state.Original, desired, mode, out reason))
                    { Failed(target, phase, now, reason); continue; }
                    state.Active = true; state.Verified = true; state.Mode = mode; state.Desired = desired;
                    Logger.Log("微软后台已纳入治理：" + target.Category + " " + target.Process.Name
                        + " 进程编号=" + target.Process.Pid + " 档位=" + (mode == BackgroundGovernancePolicy.Aggressive ? "激进" : "正常")
                        + " CPU配额=" + (WindowsBackgroundLimits.RateFor(mode) / 100) + "% 后台核心范围=0x" + desired.ToString("X"));
                }
                catch (Exception error) { state.Verified = false; Failed(target, operation, now, "执行异常：" + error.GetType().Name); }
            }
            PublishCount();
        }

        private static void Failed(WindowsBackgroundTarget target, FailurePhase phase, long now, string reason)
        {
            phase.Fail(now);
            Logger.Warn("微软后台治理未完成：" + target.Category + " " + target.Process.Name + " 进程编号=" + target.Process.Pid
                + " 尝试=" + phase.Failures + "/3 原因=" + reason
                + (phase.Exhausted ? "；预算耗尽，保留恢复记录及句柄，停止主动尝试" : ""));
        }

        private void PublishCount()
        {
            int count = 0;
            foreach (TargetState state in targets.Values) if (state.Active && state.Verified) count++;
            activeCount = count;
        }

        internal bool Restore()
        {
            if (restoreExceptions >= 3) return false;
            string reason;
            bool restored;
            try { restored = jobs.ReleaseAll(out reason); }
            catch (Exception error)
            {
                restoreExceptions++;
                Logger.Warn("微软后台恢复异常：尝试=" + restoreExceptions + "/3 原因=" + error.GetType().Name
                    + (restoreExceptions >= 3 ? "；保留记录及句柄，停止主动尝试" : ""));
                return false;
            }
            if (restored)
            {
                if (activeCount > 0) Logger.Log("微软后台治理已恢复：进程数=" + activeCount + "；本方 CPU、核心、优先级及 I/O 限制已解除");
                targets.Clear(); reserved.Clear(); activeCount = 0;
            }
            return restored;
        }

        internal bool Stop()
        {
            if (stopExceptions >= 3 || !Restore()) return false;
            string reason;
            try { return jobs.Stop(out reason); }
            catch (Exception error)
            {
                stopExceptions++;
                Logger.Warn("微软后台守护收尾异常：尝试=" + stopExceptions + "/3 原因=" + error.GetType().Name
                    + (stopExceptions >= 3 ? "；保留记录及句柄，停止主动尝试" : ""));
                return false;
            }
        }
    }
}
