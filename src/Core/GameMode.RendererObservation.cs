// 文件用途 徽标记录的是观察到的活动 不是选中状态 也不是有个全屏窗口
// 采样有界 不在界面和控制循环上 并且和交接的 GPU 工作共用一道闸
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private RendererObservationStore rendererObservations;
        private int rendererValidationBusy;
        private int rendererActivityBusy;
        private int rendererGpuSamplingBusy;
        private long rendererActivityNextMs;
        private GameDetection rendererActivityTarget;
        private int rendererActivityAttempts;
        private int rendererActivityEpoch;
        private long rendererActivityWaitLogMs;
        private long rendererActivityBudgetNextMs;
        private readonly object rendererObservationTraceGate = new object();
        private readonly Queue<string> rendererObservationTraceLines = new Queue<string>();
        private int rendererObservationTraceBusy;

#if PAVISE_SELFTEST
        internal Func<GameDetection, Func<bool>, IDictionary<int, double>> RendererTestActivityGpu;
        internal Func<long> RendererTestObservationNow;
#endif

        private void InitializeRendererObservations()
        {
            rendererObservations = new RendererObservationStore(dataDir);
        }

        public bool HasRendererObservation(GameProfile profile)
        {
            RendererObservationStore store = rendererObservations;
            if (store == null || profile == null) return false;
            if (!stopping && store.NeedsValidation(profile.Id, profile.ExecutablePath, DateTime.UtcNow.Ticks))
                QueueRendererObservationValidation();
            return store.Has(profile.Id, profile.ExecutablePath);
        }

        private void QueueRendererObservationValidation()
        {
            if (stopping || Interlocked.CompareExchange(ref rendererValidationBusy, 1, 0) != 0) return;
            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    bool changed = false;
                    try
                    {
                        if (stopping) return;
                        foreach (GameProfile profile in GetProfiles())
                        {
                            if (stopping) break;
                            if (rendererObservations.NeedsValidation(profile.Id, profile.ExecutablePath, DateTime.UtcNow.Ticks))
                                changed |= rendererObservations.Validate(profile.Id, profile.ExecutablePath);
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (changed && !stopping) { rendererObservations.Persist(); RaiseLibraryChanged(); }
                        }
                        finally { Interlocked.Exchange(ref rendererValidationBusy, 0); }
                    }
                });
                if (!queued) Interlocked.Exchange(ref rendererValidationBusy, 0);
            }
            catch { Interlocked.Exchange(ref rendererValidationBusy, 0); }
        }

        private void ForgetRendererObservation(string profileId)
        {
            if (stopping || rendererObservations == null) return;
            rendererObservations.Forget(profileId);
            // 这只是可选历史 不是界面或档案存储的同步事务
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    if (!stopping) rendererObservations.Persist();
                });
            }
            catch { }
        }

        private void MaybeObserveRendererActivity(GameDetection selected)
        {
            if (rendererObservations == null || selected == null || selected.Profile == null
                || !selected.RendererCandidateSelected || selected.RendererSafetyOnly
                || selected.RequiresGpuConfirm || !enabled || stopping || panicReq || ProfileStoreSaveFailed) return;
            int foreground = RendererForegroundPid();
            // 后台只看内存缓存，维持原来仅在前台排队校验 EXE 的开销边界。
            if (foreground == selected.RendererPid ? HasRendererObservation(selected.Profile)
                : rendererObservations.Has(selected.Profile.Id, selected.Profile.ExecutablePath)) return;
            long now = RendererNowMs();
#if PAVISE_SELFTEST
            if (RendererTestObservationNow != null) now = RendererTestObservationNow();
#endif
            int epoch = Volatile.Read(ref rendererHandoffEpoch);
            if (!RendererHandoffTracker.SameIdentity(rendererActivityTarget, selected)
                || rendererActivityTarget.Profile == null
                || !string.Equals(rendererActivityTarget.Profile.Id, selected.Profile.Id, StringComparison.OrdinalIgnoreCase)
                || rendererActivityEpoch != epoch)
            {
                rendererActivityTarget = RendererHandoffTracker.Copy(selected);
                rendererActivityAttempts = 0;
                // 等待时间属于一次目标观测，不能由旧游戏或旧会话带给新目标。
                rendererActivityNextMs = 0;
                rendererActivityEpoch = epoch;
            }
            if (foreground != selected.RendererPid)
            {
                TraceRendererObservationWait(selected, now, "ForegroundWaiting", "foreground=" + foreground);
                return;
            }
            // 新目标不继承旧目标的长退避，但所有目标仍共用每 10 秒一次的开销上限。
            if (now < rendererActivityNextMs || now < rendererActivityBudgetNextMs) return;
            if (Handoff.HasProbe)
            {
                TraceRendererObservationWait(selected, now, "SamplingBusy", "owner=handoff");
                return;
            }
            if (Interlocked.CompareExchange(ref rendererActivityBusy, 1, 0) != 0) return;
            if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0)
            {
                Interlocked.Exchange(ref rendererActivityBusy, 0);
                TraceRendererObservationWait(selected, now, "SamplingBusy", "owner=gpu");
                return;
            }
            rendererActivityAttempts = Math.Min(rendererActivityAttempts + 1, 5);
            rendererActivityBudgetNextMs = now + 10000L;
            rendererActivityNextMs = now + (rendererActivityAttempts <= 2 ? 10000L
                : rendererActivityAttempts <= 3 ? 30000L : rendererActivityAttempts <= 4 ? 60000L : 120000L);
            GameDetection target = RendererHandoffTracker.Copy(selected);
            TraceRendererObservation(target, "Started", "attempt=" + rendererActivityAttempts
                + " retryMs=" + (rendererActivityNextMs - now) + " exe=" + target.RendererPath);
            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    string outcome = "Canceled", detail = null;
                    try
                    {
                        Func<bool> canceled = delegate { return !RendererEpochCurrent(epoch); };
                        if (canceled()) return;
                        if (!VerifyRendererCandidate(target)) { outcome = "IdentityUnavailable"; detail = "phase=before"; return; }
                        RendererFileStamp stamp = RendererFileStamp.Read(target.RendererPath);
                        if (stamp == null) { outcome = "FileUnavailable"; return; }
                        IDictionary<int, double> values;
                        GpuSampleDiagnostics diagnostics = null;
#if PAVISE_SELFTEST
                        if (RendererTestActivityGpu != null) values = RendererTestActivityGpu(target, canceled);
                        else
#endif
                            values = GpuEvidence.Sample3D(GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs, canceled,
                                target.RendererPid, out diagnostics);
                        detail = diagnostics == null ? null : diagnostics.ToString();
                        if (canceled()) return;
                        if (values == null) { outcome = "GpuUnavailable"; return; }
                        detail = "pidCount=" + values.Count + " " + detail;
                        double utilization;
                        // 不做名字排序 徽标描述的是这个进程实测到的 3D 工作量
                        // 不是在声称它就是唯一或者主要的游戏渲染进程
                        if (!values.TryGetValue(target.RendererPid, out utilization)) { outcome = "PidMissing"; return; }
                        detail = "gpu3d=" + utilization.ToString("R", CultureInfo.InvariantCulture) + "% " + detail;
                        int currentForeground = RendererForegroundPid();
                        if (currentForeground != target.RendererPid)
                        { outcome = "ForegroundChanged"; detail += " foreground=" + currentForeground; return; }
                        if (!VerifyRendererCandidate(target)) { outcome = "IdentityUnavailable"; detail += " phase=after"; return; }
                        if (double.IsNaN(utilization) || double.IsInfinity(utilization)) { outcome = "InvalidEvidence"; return; }
                        if (utilization < GpuEvidence.MinElectUtilization)
                        {
                            outcome = "BelowThreshold";
                            detail += " minimum=" + GpuEvidence.MinElectUtilization.ToString(CultureInfo.InvariantCulture) + "%";
                            return;
                        }
                        RendererObservationWriteResult result;
                        lock (sync)
                        {
                            GameProfile live = FindProfileLocked(target.Profile.Id);
                            if (!RendererEpochCurrent(epoch)) return;
                            if (live == null || !string.Equals(live.ExecutablePath, target.RendererPath, StringComparison.OrdinalIgnoreCase))
                            { outcome = "ProfileChanged"; return; }
                            if (activeDetection == null || !RendererHandoffTracker.SameIdentity(activeDetection, target))
                            { outcome = "ActiveChanged"; return; }
                            if (!VerifyRendererCandidate(target)) { outcome = "IdentityUnavailable"; detail += " phase=commit"; return; }
                            result = rendererObservations.RecordActivityWithResult(live.Id, target.RendererPath,
                                RendererObservationEvidence.Gpu3D, utilization, stamp);
                        }
                        outcome = result.ToString();
                        if (result == RendererObservationWriteResult.Recorded) RaiseLibraryChanged();
                    }
                    catch (Exception error) { outcome = "Error"; detail = error.GetType().Name; }
                    finally
                    {
                        TraceRendererObservation(target, outcome, detail);
                        Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                        Interlocked.Exchange(ref rendererActivityBusy, 0);
                    }
                });
                if (!queued)
                {
                    TraceRendererObservation(target, "QueueFailed", null);
                    Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                    Interlocked.Exchange(ref rendererActivityBusy, 0);
                }
            }
            catch (Exception error)
            {
                TraceRendererObservation(target, "QueueFailed", error.GetType().Name);
                Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                Interlocked.Exchange(ref rendererActivityBusy, 0);
            }
        }

        private void TraceRendererObservationWait(GameDetection target, long now, string reason, string detail)
        {
            // 主循环可能每秒经过多次；等待状态最多每 30 秒记一条。
            if (now < rendererActivityWaitLogMs) return;
            rendererActivityWaitLogMs = now + 30000L;
            TraceRendererObservation(target, reason, detail);
        }

        private void TraceRendererObservation(GameDetection target, string reason, string detail)
        {
            try
            {
                string line = Lang.F("lib.render.observation.trace", target.RendererName, target.RendererPid,
                    Lang.T("lib.render.observation." + reason)) + " [reason=" + reason + "]"
                    + " profile=" + target.Profile.Id + (string.IsNullOrEmpty(detail) ? "" : " " + detail);
                // 磁盘或全局日志锁变慢时，不阻塞检测循环，也不占着 GPU 采样闸等待日志。
                // 每个 GameMode 最多一个日志任务、八条待写消息，没有常驻线程。
                lock (rendererObservationTraceGate)
                {
                    if (rendererObservationTraceLines.Count >= 8) rendererObservationTraceLines.Dequeue();
                    rendererObservationTraceLines.Enqueue(line);
                    if (rendererObservationTraceBusy != 0) return;
                    rendererObservationTraceBusy = 1;
                    try
                    {
                        if (ThreadPool.QueueUserWorkItem(delegate { DrainRendererObservationTrace(); })) return;
                    }
                    catch { }
                    rendererObservationTraceBusy = 0;
                    rendererObservationTraceLines.Clear();
                }
            }
            catch { } // 日志不可用不能影响采样闸的释放和游戏运行。
        }

        private void DrainRendererObservationTrace()
        {
            while (true)
            {
                string line;
                lock (rendererObservationTraceGate)
                {
                    if (rendererObservationTraceLines.Count == 0)
                    {
                        rendererObservationTraceBusy = 0;
                        return;
                    }
                    line = rendererObservationTraceLines.Dequeue();
                }
                Logger.Log(line);
            }
        }

    }
}
