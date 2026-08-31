// 文件用途 徽标记录的是观察到的活动 不是选中状态 也不是有个全屏窗口
// 采样有界 不在界面和控制循环上 并且和交接的 GPU 工作共用一道闸
using System;
using System.Collections.Generic;
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

#if PAVISE_SELFTEST
        internal Func<GameDetection, Func<bool>, IDictionary<int, double>> RendererTestActivityGpu;
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
                || selected.RequiresGpuConfirm || !enabled || stopping || panicReq || ProfileStoreSaveFailed
                || RendererForegroundPid() != selected.RendererPid
                || HasRendererObservation(selected.Profile)) return;
            long now = RendererNowMs();
            if (!RendererHandoffTracker.SameIdentity(rendererActivityTarget, selected))
            {
                rendererActivityTarget = RendererHandoffTracker.Copy(selected);
                rendererActivityAttempts = 0;
            }
            if (now < rendererActivityNextMs || Handoff.HasProbe
                || Interlocked.CompareExchange(ref rendererActivityBusy, 1, 0) != 0) return;
            if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0)
            {
                Interlocked.Exchange(ref rendererActivityBusy, 0);
                return;
            }
            rendererActivityAttempts++;
            rendererActivityNextMs = now + (rendererActivityAttempts <= 2 ? 10000L
                : rendererActivityAttempts <= 3 ? 30000L : rendererActivityAttempts <= 4 ? 60000L : 120000L);
            GameDetection target = RendererHandoffTracker.Copy(selected);
            int epoch = Volatile.Read(ref rendererHandoffEpoch);
            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        Func<bool> canceled = delegate { return !RendererEpochCurrent(epoch); };
                        if (canceled() || !VerifyRendererCandidate(target)) return;
                        RendererFileStamp stamp = RendererFileStamp.Read(target.RendererPath);
                        if (stamp == null) return;
                        IDictionary<int, double> values;
#if PAVISE_SELFTEST
                        if (RendererTestActivityGpu != null) values = RendererTestActivityGpu(target, canceled);
                        else
#endif
                            values = GpuEvidence.Sample3D(GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs, canceled);
                        double utilization;
                        // 不做名字排序 徽标描述的是这个进程实测到的 3D 工作量
                        // 不是在声称它就是唯一或者主要的游戏渲染进程
                        if (canceled() || values == null || !values.TryGetValue(target.RendererPid, out utilization)
                            || RendererForegroundPid() != target.RendererPid || !VerifyRendererCandidate(target)) return;
                        bool recorded = false;
                        lock (sync)
                        {
                            GameProfile live = FindProfileLocked(target.Profile.Id);
                            if (!RendererEpochCurrent(epoch) || live == null
                                || !string.Equals(live.ExecutablePath, target.RendererPath, StringComparison.OrdinalIgnoreCase)
                                || activeDetection == null || !RendererHandoffTracker.SameIdentity(activeDetection, target)
                                || !VerifyRendererCandidate(target)) return;
                            recorded = rendererObservations.RecordActivity(live.Id, target.RendererPath,
                                RendererObservationEvidence.Gpu3D, utilization, stamp);
                        }
                        if (recorded) RaiseLibraryChanged();
                    }
                    catch { }
                    finally
                    {
                        Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                        Interlocked.Exchange(ref rendererActivityBusy, 0);
                    }
                });
                if (!queued)
                {
                    Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                    Interlocked.Exchange(ref rendererActivityBusy, 0);
                }
            }
            catch
            {
                Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                Interlocked.Exchange(ref rendererActivityBusy, 0);
            }
        }

    }
}
