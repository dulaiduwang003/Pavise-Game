using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    // 渲染进程的后台取证只通过这个接口读游戏模式的状态和请它做事 不直接碰 GameMode 的字段
    internal interface IRendererProbeHost
    {
        // 就是游戏模式自己的 sync 锁 取证结论要和游戏库 当前会话在同一把锁下核对
        object Sync { get; }
        bool Stopping { get; }
        bool PanicRequested { get; }
        bool GuardEnabled { get; }
        bool ProfileStoreSaveFailed { get; }
        RendererObservationStore RendererObservations { get; }
        RendererHandoffTracker Handoff { get; }
        int RendererHandoffEpoch { get; }
        // 只在持有 Sync 时读
        GameDetection ActiveDetection { get; }
        bool RendererEpochCurrent(int epoch);
        bool VerifyRendererCandidate(GameDetection candidate);
        int RendererForegroundPid();
        long RendererNowMs();
        bool RendererGpuProofRequired(GameDetection candidate);
        bool HasRendererObservation(GameProfile profile);
        GameProfile FindProfileLocked(string profileId);
        List<GameProfile> GetProfiles();
        void RaiseLibraryChanged();
        void TraceRendererObservation(GameDetection target, string reason, string detail);
        // 探测结束后请主循环尽快重新扫描进程
        void KickProcessScan();
    }

    // 渲染进程的后台 GPU 取证 包括交接候选的入场探测 游戏库徽标的活动观察和观察记录核验 实例归 GameMode 所有
    internal sealed class RendererProbeCoordinator
    {
        private readonly IRendererProbeHost host;
        private readonly ProcessActionFailureBudget failureBudget = new ProcessActionFailureBudget();
        private int gpuSamplingBusy;
        private int validationBusy;
        private int activityBusy;
        private long activityNextMs;
        private GameDetection activityTarget;
        private int activityAttempts;
        private int activityEpoch;
        private long activityWaitLogMs;
        private long activityBudgetNextMs;

        internal RendererProbeCoordinator(IRendererProbeHost host)
        {
            if (host == null) throw new ArgumentNullException("host");
            this.host = host;
        }

#if PAVISE_SELFTEST
        internal Func<RendererProbeTicket, Func<bool>, IDictionary<int, double>> ProbeGpuForTest;
        internal Func<GameDetection, Func<bool>, IDictionary<int, double>> ActivityGpuForTest;
        internal Func<long> ObservationNowForTest;
#endif

        // 自动入库 入场探测和活动观察共用这一条 GPU 采样通道 同一时刻只跑一路采样
        internal bool TryEnterGpuSampling()
        {
            return Interlocked.CompareExchange(ref gpuSamplingBusy, 1, 0) == 0;
        }

        internal void LeaveGpuSampling()
        {
            Interlocked.Exchange(ref gpuSamplingBusy, 0);
        }

        internal bool GpuSamplingBusy
        {
            get { return Volatile.Read(ref gpuSamplingBusy) != 0; }
        }

        internal void TryStartHandoffProbe(int epoch)
        {
            if (Interlocked.CompareExchange(ref gpuSamplingBusy, 1, 0) != 0)
            {
                return;
            }
            RendererProbeTicket ticket;
            lock (host.Sync)
            {
                if (!host.RendererEpochCurrent(epoch))
                {
                    Interlocked.Exchange(ref gpuSamplingBusy, 0);
                    return;
                }
                GameDetection current = host.Handoff.Current;
                if (current != null)
                {
                    host.Handoff.Refresh(true, host.RendererForegroundPid() == current.RendererPid, host.RendererNowMs());
                }
                ticket = GameMode.TryBeginRendererProbe(failureBudget, host.Handoff, current, host.RendererNowMs());
            }
            if (ticket == null)
            {
                Interlocked.Exchange(ref gpuSamplingBusy, 0);
                return;
            }
            if (!failureBudget.Allowed(ticket.Detection.RendererPid, ticket.Detection.RendererCreation, ticket.Detection.RendererPath, "probe")
                || !failureBudget.Allowed(ticket.Detection.RendererPid, ticket.Detection.RendererCreation, ticket.Detection.RendererPath, "gpu"))
            {
                host.Handoff.Complete(ticket, null, false, host.RendererNowMs());
                Interlocked.Exchange(ref gpuSamplingBusy, 0);
                return;
            }
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    IDictionary<int, double> utilization = null;
                    PresentActivitySample presents = null;
                    bool identityValid = false;
                    bool sampled = false;
                    Func<bool> func = () => ticket.Canceled || !host.RendererEpochCurrent(epoch);
                    try
                    {
                        if (!func() && host.VerifyRendererCandidate(ticket.Detection))
                        {
                            sampled = true;
                            GameMode.ObserveEntryWindows(ticket.Detection, host.RendererGpuProofRequired(ticket.Detection), func, delegate
                            {
                                // 画面提交与 GPU 使用率采用同一观测窗口
                                IDictionary<int, double> windowUtilization = null;
                                PresentActivitySample windowPresents = null;
                                string presentFailure;
                                PresentActivityCapture capture = GameMode.TryStartRendererPresent(failureBudget, ticket.Detection, out presentFailure);
                                Stopwatch presentWindow = capture != null ? Stopwatch.StartNew() : null;
                                try
                                {
#if PAVISE_SELFTEST
                                    if (ProbeGpuForTest != null) windowUtilization = GameMode.SampleRendererGpu(failureBudget, ticket.Detection, func,
                                        () => ProbeGpuForTest(ticket, func));
                                    else
#endif
                                    windowUtilization = GameMode.SampleRendererGpu(failureBudget, ticket.Detection, func,
                                        () => GpuEvidence.Sample3D(2, 700, func));
                                }
                                finally
                                {
                                    if (presentWindow != null) GameMode.WaitRendererPresentWindow(presentWindow, func);
                                    windowPresents = GameMode.FinishRendererPresent(failureBudget, ticket.Detection, capture, presentFailure);
                                }
                                return new KeyValuePair<IDictionary<int, double>, PresentActivitySample>(windowUtilization, windowPresents);
                            }, out utilization, out presents);
                            identityValid = !func() && host.VerifyRendererCandidate(ticket.Detection) && host.RendererForegroundPid() == ticket.Detection.RendererPid;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        if (sampled && !func())
                        {
                            if (utilization == null && (presents == null || !presents.Valid)) RecordProbeFailure(ticket, "capture");
                        }
                        bool gpuRequired = host.RendererGpuProofRequired(ticket.Detection);
                        RendererProof verdict = host.Handoff.Complete(ticket, utilization, presents, identityValid, host.RendererNowMs(), gpuRequired);
                        if (sampled) LogProbe(ticket.Detection, verdict, utilization, presents, gpuRequired);
                        Interlocked.Exchange(ref gpuSamplingBusy, 0);
                        if (!host.Stopping && host.GuardEnabled)
                        {
                            host.KickProcessScan();
                        }
                    }
                }))
                {
                    host.Handoff.Complete(ticket, null, false, host.RendererNowMs());
                    RecordProbeFailure(ticket, "queue");
                    Interlocked.Exchange(ref gpuSamplingBusy, 0);
                }
            }
            catch
            {
                host.Handoff.Complete(ticket, null, false, host.RendererNowMs());
                RecordProbeFailure(ticket, "queue-exception");
                Interlocked.Exchange(ref gpuSamplingBusy, 0);
            }
        }
        private static void LogProbe(GameDetection candidate, RendererProof verdict, IDictionary<int, double> utilization, PresentActivitySample presents, bool gpuRequired)
        {
            if (candidate == null) return;
            int bestPid = 0, bestFrames = 0;
            if (presents != null && presents.Valid)
                foreach (int pid in candidate.FamilyPids)
                    if (pid != candidate.RendererPid && presents.FramesOf(pid) > bestFrames) { bestFrames = presents.FramesOf(pid); bestPid = pid; }
            double gpu;
            string gpuText = utilization != null && utilization.TryGetValue(candidate.RendererPid, out gpu) && !double.IsNaN(gpu) ? ((int)gpu).ToString() + "%" : "n/a";
            string presentText = presents == null ? "none" : presents.Valid
                ? "frames=" + presents.FramesOf(candidate.RendererPid) + " fps=" + ((int)Math.Round(presents.FpsOf(candidate.RendererPid))) + " windowMs=" + presents.WindowMs
                    + " familyBest=" + (bestPid > 0 ? bestPid + ":" + bestFrames : "none") + " presenters=" + presents.ProcessCount
                    + (presents.Events < 0 ? "" : " events=" + presents.Events)
                : "unavailable=" + presents.Failure;
            string line = RendererProbeLogFilter.Filter(Logger.Text("渲染进程探测 ", "RENDERER probe ") + candidate.RendererName + Logger.Text(" 进程编号=", " pid=") + candidate.RendererPid + Logger.Text(" 结论=", " verdict=") + verdict.ToString().ToLowerInvariant()
                + Logger.Text(" GPU三维占用=", " gpu3d=") + gpuText + " " + presentText + (gpuRequired ? Logger.Text(" 需GPU证据=是", " gpuRequired=yes") : ""), Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
            if (line != null) Logger.Info(line);
        }
        private void RecordProbeFailure(RendererProbeTicket ticket, string stage)
        {
            if (ticket.Canceled) return;
            GameDetection target = ticket.Detection;
            int count = failureBudget.Failed(target.RendererPid, target.RendererCreation, target.RendererPath, "probe");
            Logger.Warn(Logger.Format("渲染进程 GPU 探测失败={0}/3 步骤={1} 进程编号={2}", "Renderer GPU probe failure={0}/3 stage={1} pid={2}", count, stage, target.RendererPid));
        }
        internal void QueueObservationValidation()
        {
            if (host.Stopping || Interlocked.CompareExchange(ref validationBusy, 1, 0) != 0)
            {
                return;
            }
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    bool ready = false;
                    try
                    {
                        if (host.Stopping)
                        {
                            return;
                        }
                        foreach (GameProfile profile in host.GetProfiles())
                        {
                            if (host.Stopping)
                            {
                                break;
                            }
                            if (host.RendererObservations.NeedsValidation(profile.Id, profile.ExecutablePath, DateTime.UtcNow.Ticks))
                            {
                                ready |= host.RendererObservations.Validate(profile.Id, profile.ExecutablePath);
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (ready && !host.Stopping)
                            {
                                host.RendererObservations.Persist();
                                host.RaiseLibraryChanged();
                            }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref validationBusy, 0);
                        }
                    }
                }))
                {
                    Interlocked.Exchange(ref validationBusy, 0);
                }
            }
            catch
            {
                Interlocked.Exchange(ref validationBusy, 0);
            }
        }
        internal void MaybeObserveActivity(GameDetection selected)
        {
            if (host.RendererObservations == null || selected == null || selected.Profile == null || !selected.RendererCandidateSelected || selected.RendererSafetyOnly || selected.RequiresGpuConfirm || !host.GuardEnabled || host.Stopping || host.PanicRequested || host.ProfileStoreSaveFailed)
            {
                return;
            }
            int rendererForegroundPidResult = host.RendererForegroundPid();
            if ((rendererForegroundPidResult == selected.RendererPid) ? host.HasRendererObservation(selected.Profile) : host.RendererObservations.Has(selected.Profile.Id, selected.Profile.ExecutablePath))
            {
                return;
            }
            long rendererNowMsResult = host.RendererNowMs();
#if PAVISE_SELFTEST
            if (ObservationNowForTest != null) rendererNowMsResult = ObservationNowForTest();
#endif
            int epoch = host.RendererHandoffEpoch;
            if (!RendererHandoffTracker.SameIdentity(activityTarget, selected) || activityTarget.Profile == null || !string.Equals(activityTarget.Profile.Id, selected.Profile.Id, StringComparison.OrdinalIgnoreCase) || activityEpoch != epoch)
            {
                activityTarget = RendererHandoffTracker.Copy(selected);
                activityAttempts = 0;
                activityNextMs = 0L;
                activityEpoch = epoch;
            }
            if (activityAttempts >= 5)
            {
                return;
            }
            if (rendererForegroundPidResult != selected.RendererPid)
            {
                TraceObservationWait(selected, rendererNowMsResult, "ForegroundWaiting", "foreground=" + rendererForegroundPidResult);
            }
            else
            {
                if (rendererNowMsResult < activityNextMs || rendererNowMsResult < activityBudgetNextMs)
                {
                    return;
                }
                if (host.Handoff.HasProbe)
                {
                    TraceObservationWait(selected, rendererNowMsResult, "SamplingBusy", "owner=handoff");
                }
                else
                {
                    if (Interlocked.CompareExchange(ref activityBusy, 1, 0) != 0)
                    {
                        return;
                    }
                    if (Interlocked.CompareExchange(ref gpuSamplingBusy, 1, 0) == 0)
                    {
                        activityAttempts++;
                        activityBudgetNextMs = rendererNowMsResult + 10000;
                        activityNextMs = rendererNowMsResult + ((activityAttempts <= 2) ? 10000 : ((activityAttempts <= 3) ? 30000 : ((activityAttempts <= 4) ? 60000 : 120000)));
                        GameDetection target = RendererHandoffTracker.Copy(selected);
                        host.TraceRendererObservation(target, "Started", "attempt=" + activityAttempts + " retryMs=" + (activityNextMs - rendererNowMsResult) + " exe=" + target.RendererPath);
                        try
                        {
                            if (!ThreadPool.QueueUserWorkItem(delegate
                            {
                                string reason = "Canceled";
                                string message = null;
                                try
                                {
                                    Func<bool> func = () => !host.RendererEpochCurrent(epoch);
                                    if (!func())
                                    {
                                        if (!host.VerifyRendererCandidate(target))
                                        {
                                            reason = "IdentityUnavailable";
                                            message = "phase=before";
                                        }
                                        else
                                        {
                                            RendererFileStamp rendererFileStamp = RendererFileStamp.Read(target.RendererPath);
                                            if (rendererFileStamp == null)
                                            {
                                                reason = "FileUnavailable";
                                            }
                                            else
                                            {
                                                GpuSampleDiagnostics diagnostics = null;
                                                IDictionary<int, double> dictionary;
#if PAVISE_SELFTEST
                                                if (ActivityGpuForTest != null) dictionary = ActivityGpuForTest(target, func);
                                                else
#endif
                                                dictionary = GpuEvidence.Sample3D(2, 700, func, target.RendererPid, out diagnostics);
                                                message = ((diagnostics == null) ? null : diagnostics.ToString());
                                                if (!func())
                                                {
                                                    if (dictionary == null)
                                                    {
                                                        reason = "GpuUnavailable";
                                                    }
                                                    else
                                                    {
                                                        message = "pidCount=" + dictionary.Count + " " + message;
                                                        double value;
                                                        if (!dictionary.TryGetValue(target.RendererPid, out value))
                                                        {
                                                            reason = "PidMissing";
                                                        }
                                                        else
                                                        {
                                                            message = "gpu3d=" + value.ToString("R", CultureInfo.InvariantCulture) + "% " + message;
                                                            int sampledForegroundPid = host.RendererForegroundPid();
                                                            if (sampledForegroundPid != target.RendererPid)
                                                            {
                                                                reason = "ForegroundChanged";
                                                                message = message + " foreground=" + sampledForegroundPid;
                                                            }
                                                            else if (!host.VerifyRendererCandidate(target))
                                                            {
                                                                reason = "IdentityUnavailable";
                                                                message += " phase=after";
                                                            }
                                                            else if (double.IsNaN(value) || double.IsInfinity(value))
                                                            {
                                                                reason = "InvalidEvidence";
                                                            }
                                                            else if (value < 10.0)
                                                            {
                                                                reason = "BelowThreshold";
                                                                message = message + " minimum=" + 10.0.ToString(CultureInfo.InvariantCulture) + "%";
                                                            }
                                                            else
                                                            {
                                                                RendererObservationWriteResult rendererObservationWriteResult;
                                                                lock (host.Sync)
                                                                {
                                                                    GameProfile gameProfile = host.FindProfileLocked(target.Profile.Id);
                                                                    if (!host.RendererEpochCurrent(epoch))
                                                                    {
                                                                        return;
                                                                    }
                                                                    if (gameProfile == null || !string.Equals(gameProfile.ExecutablePath, target.RendererPath, StringComparison.OrdinalIgnoreCase))
                                                                    {
                                                                        reason = "ProfileChanged";
                                                                        return;
                                                                    }
                                                                    if (host.ActiveDetection == null || !RendererHandoffTracker.SameIdentity(host.ActiveDetection, target))
                                                                    {
                                                                        reason = "ActiveChanged";
                                                                        return;
                                                                    }
                                                                    if (!host.VerifyRendererCandidate(target))
                                                                    {
                                                                        reason = "IdentityUnavailable";
                                                                        message += " phase=commit";
                                                                        return;
                                                                    }
                                                                    rendererObservationWriteResult = host.RendererObservations.RecordActivityWithResult(gameProfile.Id, target.RendererPath, RendererObservationEvidence.Gpu3D, value, rendererFileStamp);
                                                                }
                                                                reason = rendererObservationWriteResult.ToString();
                                                                if (rendererObservationWriteResult == RendererObservationWriteResult.Recorded)
                                                                {
                                                                    host.RaiseLibraryChanged();
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex2)
                                {
                                    reason = "Error";
                                    message = ex2.GetType().Name;
                                }
                                finally
                                {
                                    host.TraceRendererObservation(target, reason, message);
                                    Interlocked.Exchange(ref gpuSamplingBusy, 0);
                                    Interlocked.Exchange(ref activityBusy, 0);
                                }
                            }))
                            {
                                host.TraceRendererObservation(target, "QueueFailed", null);
                                Interlocked.Exchange(ref gpuSamplingBusy, 0);
                                Interlocked.Exchange(ref activityBusy, 0);
                            }
                            return;
                        }
                        catch (Exception ex)
                        {
                            host.TraceRendererObservation(target, "QueueFailed", ex.GetType().Name);
                            Interlocked.Exchange(ref gpuSamplingBusy, 0);
                            Interlocked.Exchange(ref activityBusy, 0);
                            return;
                        }
                    }
                    Interlocked.Exchange(ref activityBusy, 0);
                    TraceObservationWait(selected, rendererNowMsResult, "SamplingBusy", "owner=gpu");
                }
            }
        }
        private void TraceObservationWait(GameDetection target, long now, string reason, string detail)
        {
            if (now >= activityWaitLogMs)
            {
                activityWaitLogMs = now + 30000;
                host.TraceRendererObservation(target, reason, detail);
            }
        }
    }
}
