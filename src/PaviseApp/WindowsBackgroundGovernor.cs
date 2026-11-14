using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

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
            string identityFailure;
            IntPtr process = GameAffinityJobNative.OpenQueryIdentity(target.Process.Pid, target.Process.Creation, out identityFailure);
            if (process == IntPtr.Zero) { reason = "微软后台原始范围读取未完成：" + identityFailure; return false; }
            try
            {
                if (!Native.TryQueryAffinity(process, out mask))
                {
                    reason = "微软后台 GetProcessAffinityMask 失败，win32=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    return false;
                }
                if (mask == 0) { reason = "微软后台原始核心范围为空"; return false; }
                int identity = Native.QueryProcessIdentityState(process, target.Process.Creation);
                if (identity == 0) return true;
                mask = 0;
                reason = identity == 1 ? "微软后台读取期间原目标已退出或身份已变化"
                    : "微软后台读取后身份未能核验";
                return false;
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

    internal sealed class WindowsBackgroundActivitySnapshot
    {
        internal readonly long Session;
        internal readonly ReadOnlyCollection<KeyValuePair<int, long>> Processes;
        internal readonly bool Pending;
        internal readonly bool RecoveryPending;

        internal WindowsBackgroundActivitySnapshot(long session, KeyValuePair<int, long>[] processes, bool pending, bool recoveryPending)
        {
            Session = session;
            Processes = Array.AsReadOnly((KeyValuePair<int, long>[])processes.Clone());
            Pending = pending;
            RecoveryPending = recoveryPending;
        }
    }

    internal sealed class WindowsBackgroundGovernor
    {
        private sealed class TargetState
        {
            internal WindowsBackgroundTarget Target;
            internal ulong Original, Desired;
            internal int Mode;
            internal bool Active, Verified, Restoring;
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
        private WindowsBackgroundActivitySnapshot activity = new WindowsBackgroundActivitySnapshot(-1, new KeyValuePair<int, long>[0], false, false);
        private int restoreExceptions, stopExceptions;

        internal WindowsBackgroundGovernor(IWindowsBackgroundJobs jobs) { this.jobs = jobs; }
        internal bool HasRecovery { get { return jobs.HasActive || jobs.HasPending; } }
        internal int ActiveCount { get { return CaptureActivity().Processes.Count; } }
        internal WindowsBackgroundActivitySnapshot CaptureActivity() { return Volatile.Read(ref activity); }
        internal bool Reserved(int pid, long birth)
        { long found; return reserved.TryGetValue(pid, out found) && found == birth; }

        private FailurePhase Phase(string identity, int mode, ulong mask)
        {
            // 核心避让是同一策略的运行状态，不以自动掩码变化重新填充失败或恢复次数。
            string key = session + "/" + identity + "/" + mode;
            FailurePhase phase;
            if (!phases.TryGetValue(key, out phase)) phases[key] = phase = new FailurePhase();
            return phase;
        }

        internal void Update(long serial, int mode, ulong backgroundMask, Dictionary<int, WindowsBackgroundTarget> selected,
            Func<WindowsBackgroundTarget, bool> prepare, Func<bool> admitted, long now, bool preserveResourcesWithoutCore = false)
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
                state.Restoring = true;
                string reason;
                FailurePhase release = Phase(state.Target.Identity + "/退出名单", state.Mode, state.Desired);
                if (!release.Allowed(now)) { reserved[state.Target.Process.Pid] = state.Target.Process.Creation; continue; }
                state.Verified = false;
                try
                {
                    if (jobs.Release(state.Target, out reason)) targets.Remove(state.Target.Identity);
                    else { Failed(state.Target, release, now, reason); reserved[state.Target.Process.Pid] = state.Target.Process.Creation; }
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
                state.Restoring = false;
                string reason = null;
                FailurePhase operation = phase;
                try
                {
                    ulong wanted = backgroundMask & state.Original;
                    if (wanted == 0 && preserveResourcesWithoutCore) wanted = state.Original;
                    bool same = state.Active && state.Mode == mode && state.Desired == wanted;
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
                    if (backgroundMask == 0 && !preserveResourcesWithoutCore) { Failed(target, phase, now, "没有可核验的单组后台核心范围"); continue; }
                    if (!prepare(target)) { Failed(target, phase, now, "原后台治理所有权尚未恢复"); continue; }
                    if (state.Original == 0 && !jobs.ReadOriginalMask(target, out state.Original, out reason))
                    { Failed(target, phase, now, reason); continue; }
                    ulong desired = backgroundMask & state.Original;
                    bool coreUnavailable = desired == 0 && preserveResourcesWithoutCore;
                    if (coreUnavailable) desired = state.Original;
                    if (desired == 0) { Failed(target, phase, now, "原始范围与后台核心没有交集"); continue; }
                    if (!admitted()) { Restore(); return; }
                    if (!jobs.Ensure(target, state.Original, desired, mode, out reason))
                    { Failed(target, phase, now, reason); continue; }
                    state.Active = true; state.Verified = true; state.Mode = mode; state.Desired = desired;
                    Logger.Log("微软后台已纳入治理：" + target.Category + " " + target.Process.Name
                        + " 进程编号=" + target.Process.Pid + " 档位=" + (mode == BackgroundGovernancePolicy.Aggressive ? "激进" : "正常")
                        + " CPU配额=" + WindowsBackgroundLimits.FormatCpuPercent(WindowsBackgroundLimits.RateFor(mode)) + "% 后台核心范围=0x" + desired.ToString("X")
                        + (coreUnavailable ? "；避让后无合法余核，未收紧原范围，仅保留资源限额与降优先级" : ""));
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
            PublishActivity(false);
        }

        private void PublishActivity(bool recoveryPending)
        {
            var processes = new List<KeyValuePair<int, long>>();
            recoveryPending |= jobs.HasPending;
            bool pending = recoveryPending;
            foreach (TargetState state in targets.Values)
            {
                recoveryPending |= state.Restoring;
                if (state.Active && state.Verified)
                    processes.Add(new KeyValuePair<int, long>(state.Target.Process.Pid, state.Target.Process.Creation));
                else if (state.Active) pending = true;
            }
            // 工作线程完成本轮治理后整体发布；界面只读这份回执，不接触后端或可变目标表。
            Volatile.Write(ref activity, new WindowsBackgroundActivitySnapshot(session, processes.ToArray(), pending || recoveryPending, recoveryPending));
        }

        private void PublishRecoveryPending()
        {
            // 恢复可能只完成部分目标，旧核验结果不能继续当作本轮仍生效的证明。
            foreach (TargetState state in targets.Values) state.Verified = false;
            PublishActivity(true);
        }

        internal bool Restore()
        {
            if (restoreExceptions >= 3) { PublishRecoveryPending(); return false; }
            string reason;
            bool restored;
            try { restored = jobs.ReleaseAll(out reason); }
            catch (Exception error)
            {
                restoreExceptions++;
                Logger.Warn("微软后台恢复异常：尝试=" + restoreExceptions + "/3 原因=" + error.GetType().Name
                    + (restoreExceptions >= 3 ? "；保留记录及句柄，停止主动尝试" : ""));
                PublishRecoveryPending();
                return false;
            }
            if (restored)
            {
                int count = ActiveCount;
                if (count > 0) Logger.Log("微软后台治理已恢复：进程数=" + count + "；本方 CPU、核心、优先级及 I/O 限制已解除");
                targets.Clear(); reserved.Clear();
                PublishActivity(false);
            }
            else PublishRecoveryPending();
            return restored;
        }

        internal bool Stop()
        {
            if (stopExceptions >= 3 || !Restore()) return false;
            string reason;
            try
            {
                bool stopped = jobs.Stop(out reason);
                if (!stopped) PublishRecoveryPending();
                return stopped;
            }
            catch (Exception error)
            {
                stopExceptions++;
                Logger.Warn("微软后台守护收尾异常：尝试=" + stopExceptions + "/3 原因=" + error.GetType().Name
                    + (stopExceptions >= 3 ? "；保留记录及句柄，停止主动尝试" : ""));
                PublishRecoveryPending();
                return false;
            }
        }
    }
}
