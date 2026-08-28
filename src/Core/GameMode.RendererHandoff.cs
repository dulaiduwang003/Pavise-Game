// @author bdth 2074055628@qq.com
// 文件用途 局内渲染候选保护 异步确认与游戏库提交 不在采样线程执行任何调度写入
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private RendererHandoffTracker rendererHandoff;
        private GameDetection rendererDirectPending;
        private int rendererObservedForeground;
        private int rendererHandoffEpoch;

#if PAVISE_SELFTEST
        internal Func<int> RendererTestForeground;
        internal Func<ProcessSnapshot, IList<GameProfile>, GameDetection, GameDetection> RendererTestCandidate;
        internal Func<GameDetection, bool> RendererTestIdentity;
        internal Func<int, GameProcessSnapshot> RendererTestStickyIdentity;
        internal Func<int, long, string, BackgroundReleaseState> RendererTestRelease;
        internal Func<RendererProbeTicket, Func<bool>, IDictionary<int, double>> RendererTestGpu;
#endif

        private RendererHandoffTracker Handoff
        {
            get
            {
                if (rendererHandoff == null)
                    Interlocked.CompareExchange(ref rendererHandoff, new RendererHandoffTracker(), null);
                return rendererHandoff;
            }
        }

        private static long RendererNowMs()
        {
            long ticks = Stopwatch.GetTimestamp();
            long frequency = Stopwatch.Frequency;
            return (ticks / frequency) * 1000L + (ticks % frequency) * 1000L / frequency;
        }

        private int RendererForegroundPid()
        {
#if PAVISE_SELFTEST
            if (RendererTestForeground != null) return RendererTestForeground();
#endif
            return GameSessionDetector.ForegroundPid();
        }

        private void ObserveRendererForegroundChange()
        {
            int foreground = RendererForegroundPid();
            if (foreground == rendererObservedForeground) return;
            rendererObservedForeground = foreground;
            // 复用本轮已有进程快照；不因普通后台进程出生而把全量窗口枚举变成高频轮询。
            Interlocked.Exchange(ref gameDetectionDirty, 1);
        }

        private void InvalidateRendererHandoff()
        {
            // 与候选发布和游戏库提交共用 sync，失效不能穿过保存前的最终检查。
            lock (sync)
            {
                Interlocked.Increment(ref rendererHandoffEpoch);
                RendererHandoffTracker tracker = rendererHandoff;
                if (tracker != null) tracker.Clear();
                rendererDirectPending = null;
            }
        }

        private bool RendererEpochCurrent(int epoch)
        {
            return epoch == Volatile.Read(ref rendererHandoffEpoch)
                && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed;
        }

        private bool VerifyRendererCandidate(GameDetection candidate)
        {
#if PAVISE_SELFTEST
            if (RendererTestIdentity != null) return RendererTestIdentity(candidate);
#endif
            if (candidate == null || candidate.RendererPid <= 0 || candidate.RendererCreation <= 0) return false;
            GameProcessSnapshot live;
            return GameSessionDetector.TryCaptureProcessIdentity(candidate.RendererPid, selfSession, out live)
                && live.Creation == candidate.RendererCreation
                && string.Equals(live.Name, candidate.RendererName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(live.Path, candidate.RendererPath, StringComparison.OrdinalIgnoreCase);
        }

        private static GameProfile RendererProfile(IList<GameProfile> library, string id)
        {
            if (library == null || string.IsNullOrEmpty(id)) return null;
            foreach (GameProfile profile in library)
                if (profile != null && string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)) return profile;
            return null;
        }

        private GameDetection CaptureRendererChallenger(ProcessSnapshot all, IList<GameProfile> library, GameDetection incumbent)
        {
#if PAVISE_SELFTEST
            if (RendererTestCandidate != null) return RendererTestCandidate(all, library, incumbent);
#endif
            return GameSessionDetector.CaptureForegroundCandidate(all, selfSession, library, incumbent, FamilyEvidence);
        }

        private BackgroundReleaseState ReleaseRendererBackground(GameDetection candidate)
        {
#if PAVISE_SELFTEST
            if (RendererTestRelease != null)
                return RendererTestRelease(candidate.RendererPid, candidate.RendererCreation, candidate.RendererName);
#endif
            BackgroundReleaseState state = core.ReleaseBackgroundForRenderer(
                candidate.RendererPid, candidate.RendererCreation, candidate.RendererName);
            if (state == BackgroundReleaseState.IdentityMismatch && VerifyRendererCandidate(candidate)
                && core.DiscardReusedRendererTracking(candidate.RendererPid, candidate.RendererCreation, candidate.RendererName))
                state = core.ReleaseBackgroundForRenderer(
                    candidate.RendererPid, candidate.RendererCreation, candidate.RendererName);
            if (state == BackgroundReleaseState.Ready || state == BackgroundReleaseState.Gone)
                ReportUntrack(candidate.RendererPid);
            return state;
        }

        // 返回可进入原有 ApplyStickiness/同局交接路径的目标。null 保留旧锚，
        // 但候选保护是独立状态，不能因为 null 被丢掉，也不能受家族开关控制。
        private GameDetection ResolveRendererHandoff(ProcessSnapshot all, IList<GameProfile> library, GameDetection raw, out int epoch)
        {
            epoch = Volatile.Read(ref rendererHandoffEpoch);
            if (!enabled || stopping || panicReq || ProfileStoreSaveFailed)
            {
                InvalidateRendererHandoff();
                return null;
            }
            long now = RendererNowMs();
            GameDetection incumbent = stickyDetection;
            if (incumbent != null && incumbent.Profile != null
                && !RendererHandoffTracker.SameProfile(incumbent.Profile, RendererProfile(library, incumbent.Profile.Id)))
            {
                ClearSticky();
                InvalidateRendererHandoff();
                incumbent = null;
                epoch = Volatile.Read(ref rendererHandoffEpoch);
            }

            GameDetection held = Handoff.Current;
            if (held != null && (held.Profile == null || !RendererHandoffTracker.SameProfile(
                    held.Profile, RendererProfile(library, held.Profile.Id)))) Handoff.Clear();

            // 全库独立寻找当前前台：旧的 ready/Force 目标不能吞掉另一个档案的 pending 候选。
            GameDetection challenger = CaptureRendererChallenger(all, library, incumbent);
            GameDetection offered = challenger;
            // 前台归属不明确/终验失败时不能拿全局 Detect 的旧仲裁结果绕过拒绝。
            // 这里只保留原有 learned/Force 可从后台选中的硬目标。
            if (offered == null && raw != null && !raw.RendererForeground
                && raw.RendererCandidateSelected && !raw.RequiresGpuConfirm
                && !RendererHandoffTracker.SameIdentity(raw, incumbent)) offered = raw;
            lock (sync)
            {
                if (!RendererEpochCurrent(epoch)) return null;
                if (offered != null && offered.Profile != null && RendererHandoffTracker.SameProfile(
                        offered.Profile, FindProfileLocked(offered.Profile.Id)))
                    Handoff.Offer(offered, now);
            }

            held = Handoff.Current;
            if (held != null)
            {
                bool valid = RendererHandoffTracker.SameProfile(held.Profile, RendererProfile(library, held.Profile.Id))
                    && VerifyRendererCandidate(held);
                Handoff.Refresh(valid, RendererForegroundPid() == held.RendererPid, now);
                held = Handoff.Current;
                if (held != null)
                {
                    BackgroundReleaseState recovery = ReleaseRendererBackground(held);
                    if ((recovery == BackgroundReleaseState.Gone || recovery == BackgroundReleaseState.IdentityMismatch)
                        && !VerifyRendererCandidate(held))
                        Handoff.Clear();
                    else
                    {
                        Handoff.Recovery(held, recovery == BackgroundReleaseState.Ready);
                        TryStartRendererProbe(epoch);
                        if (Handoff.TakeUncertainNotice(now))
                            Logger.Log(Lang.T("log.renderer.unconfirmed") + held.RendererName);
                    }
                }
            }

            GameDetection confirmed = Handoff.Confirmed(RendererNowMs());
            if (confirmed != null && VerifyRendererCandidate(confirmed)
                && (!confirmed.RendererForeground || RendererForegroundPid() == confirmed.RendererPid)
                && RendererEpochCurrent(epoch)) return confirmed;

            // 某些 Force/已学习目标本来允许后台选举，同时前台有 SafetyOnly challenger。
            // 两者不能互相挤掉；硬目标尚有恢复债务时，额外保留一个身份精确的暂缓目标。
            GameDetection direct = raw != null && raw.RendererCandidateSelected
                && !raw.RendererForeground && !raw.RequiresGpuConfirm && !raw.RendererSafetyOnly ? raw : rendererDirectPending;
            if (direct != null && !RendererHandoffTracker.SameIdentity(direct, incumbent))
            {
                bool directValid = direct.Profile != null
                    && RendererHandoffTracker.SameProfile(direct.Profile, RendererProfile(library, direct.Profile.Id))
                    && VerifyRendererCandidate(direct)
                    && (!direct.RendererForeground || RendererForegroundPid() == direct.RendererPid);
                if (directValid)
                {
                    BackgroundReleaseState recovery = ReleaseRendererBackground(direct);
                    if (recovery == BackgroundReleaseState.Ready && RendererEpochCurrent(epoch))
                    {
                        rendererDirectPending = null;
                        return direct;
                    }
                    lock (sync)
                    {
                        if (!RendererEpochCurrent(epoch)) return null;
                        rendererDirectPending = VerifyRendererCandidate(direct) ? RendererHandoffTracker.Copy(direct) : null;
                    }
                }
                else rendererDirectPending = null;
            }
            else rendererDirectPending = null;
            if (!RendererEpochCurrent(epoch)) return null;
            return raw != null && raw.RendererCandidateSelected && !raw.RequiresGpuConfirm
                && !raw.RendererSafetyOnly && RendererHandoffTracker.SameIdentity(raw, incumbent) ? raw : null;
        }

        private void TryStartRendererProbe(int epoch)
        {
            if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0) return;
            RendererProbeTicket ticket;
            lock (sync)
            {
                if (!RendererEpochCurrent(epoch))
                {
                    Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                    return;
                }
                GameDetection candidate = Handoff.Current;
                if (candidate != null)
                    Handoff.Refresh(true, RendererForegroundPid() == candidate.RendererPid, RendererNowMs());
                ticket = Handoff.BeginProbe(RendererNowMs());
            }
            if (ticket == null)
            {
                Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                return;
            }
            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    IDictionary<int, double> utilization = null;
                    bool identityValid = false;
                    Func<bool> canceled = delegate { return ticket.Canceled || !RendererEpochCurrent(epoch); };
                    try
                    {
                        if (!canceled() && VerifyRendererCandidate(ticket.Detection))
                        {
#if PAVISE_SELFTEST
                            if (RendererTestGpu != null) utilization = RendererTestGpu(ticket, canceled);
                            else
#endif
                                utilization = GpuEvidence.Sample3D(GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs, canceled);
                            identityValid = !canceled() && VerifyRendererCandidate(ticket.Detection)
                                && RendererForegroundPid() == ticket.Detection.RendererPid;
                        }
                    }
                    catch { }
                    finally
                    {
                        Handoff.Complete(ticket, utilization, identityValid, RendererNowMs());
                        Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                        if (!stopping && enabled)
                        {
                            Interlocked.Exchange(ref urgentProcessScan, 1);
                            Interlocked.Exchange(ref processSetDirty, 1);
                            try { kick.Set(); } catch { }
                        }
                    }
                });
                if (!queued)
                {
                    Handoff.Complete(ticket, null, false, RendererNowMs());
                    Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                }
            }
            catch
            {
                Handoff.Complete(ticket, null, false, RendererNowMs());
                Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
            }
        }

        private bool FinalizeRendererSelection(GameDetection selected, int expectedEpoch)
        {
            if (selected == null || selected.Profile == null || !selected.RendererCandidateSelected
                || selected.RequiresGpuConfirm || selected.RendererSafetyOnly || stopping || !enabled || panicReq
                || ProfileStoreSaveFailed || expectedEpoch != Volatile.Read(ref rendererHandoffEpoch)
                || !VerifyRendererCandidate(selected) || !RendererCommitEvidenceCurrent(selected, expectedEpoch)) return false;
            GameProfile profile;
            lock (sync)
            {
                GameProfile found = FindProfileLocked(selected.Profile.Id);
                profile = found == null ? null : found.Clone();
            }
            if (!RendererHandoffTracker.SameProfile(selected.Profile, profile)) return false;
            GameDetection otherCandidate = Handoff.Current;
            bool candidateIsOther = otherCandidate != null && !RendererHandoffTracker.SameIdentity(otherCandidate, selected);
            bool learnedTarget = string.Equals(profile.LearnedExecutablePath, selected.RendererPath, StringComparison.OrdinalIgnoreCase);
            bool configuredTarget = string.Equals(profile.ExecutablePath, selected.RendererPath, StringComparison.OrdinalIgnoreCase);
            bool needsReplacement = !profile.ForceTrigger && !candidateIsOther
                && (selected.RendererLearnable || learnedTarget || (configuredTarget && !string.IsNullOrEmpty(profile.LearnedExecutablePath)));
            if (needsReplacement)
            {
                GameDetection toSave = RendererHandoffTracker.Copy(selected);
                toSave.RendererLearnable = true; // 已确认的旧 learned 也升级为唯一入口，清掉旧别名。
                if (!TryLearnConfirmedRenderer(toSave,
                        delegate { return RendererCommitEvidenceCurrent(selected, expectedEpoch); })) return false;
                lock (sync)
                {
                    GameProfile found = FindProfileLocked(selected.Profile.Id);
                    profile = found == null ? null : found.Clone();
                }
                if (profile == null) return false;
                selected.RendererLearnable = false;
                selected.RendererUserSelected = true;
            }
            selected.Profile = profile;
            return RendererCommitEvidenceCurrent(selected, expectedEpoch);
        }

        private bool RendererCommitEvidenceCurrent(GameDetection selected, int epoch)
        {
            if (!RendererEpochCurrent(epoch) || !VerifyRendererCandidate(selected)) return false;
            lock (sync)
            {
                if (selected.Profile == null || !RendererHandoffTracker.SameProfile(
                        selected.Profile, FindProfileLocked(selected.Profile.Id))) return false;
            }
            GameDetection incumbent = stickyDetection;
            if (RendererHandoffTracker.SameIdentity(incumbent, selected)) return RendererEpochCurrent(epoch);
            if (selected.RendererGpuProofExpiresMs > 0)
            {
                long now = RendererNowMs();
                GameDetection proof = Handoff.Confirmed(now);
                if (now > selected.RendererGpuProofExpiresMs
                    || !RendererHandoffTracker.SameIdentity(proof, selected)) return false;
            }
            // 采样/恢复/保存都有耗时，新交接必须在提交点复查前台；旧 sticky 的历史
            // Foreground 标记不能用于此检查，Alt-Tab 不应把正在进行的对局踢掉。
            if (selected.RendererForeground && RendererForegroundPid() != selected.RendererPid) return false;
            if (incumbent != null && incumbent.Profile != null && selected.Profile != null
                && string.Equals(incumbent.Profile.Id, selected.Profile.Id, StringComparison.OrdinalIgnoreCase)
                && VerifyRendererCandidate(incumbent)
                && !FreshRendererMayReplaceSticky(selected, selected.RendererName,
                    selected.RendererCreation, incumbent.RendererCreation)) return false;
            return RendererEpochCurrent(epoch);
        }

        private void CompleteRendererSelection(GameDetection selected)
        {
            GameDetection candidate = Handoff.Current;
            if (RendererHandoffTracker.SameIdentity(candidate, selected))
            {
                if (candidate.RequiresGpuConfirm)
                    Logger.Log(Lang.T("log.renderer.confirmed") + selected.Profile.Name + " / "
                        + selected.RendererName + " pid " + selected.RendererPid + " / " + selected.Evidence);
                Handoff.Clear();
            }
            if (RendererHandoffTracker.SameIdentity(rendererDirectPending, selected)) rendererDirectPending = null;
        }

        private bool IsRendererHandoffProtected(int pid, long creation, string path)
        {
            if (!enabled || stopping || panicReq || ProfileStoreSaveFailed) return false;
            RendererHandoffTracker tracker = rendererHandoff;
            if (tracker != null && tracker.Protects(pid, creation, path, RendererNowMs())) return true;
            GameDetection direct = rendererDirectPending;
            return direct != null && pid == direct.RendererPid && creation > 0 && creation == direct.RendererCreation
                && string.Equals(path, direct.RendererPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
