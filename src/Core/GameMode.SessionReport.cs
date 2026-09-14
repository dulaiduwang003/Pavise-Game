// @author bdth 2074055628@qq.com
// File purpose Match report registration, sealing and end summary
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void ReportBegin(string game)
        {
            BeginCacheWarmSession();
            ResetBoostIdentity();
            GpuThrottleProbe.Reset();
            VramSpillProbe.Reset();
            VramShield.Begin();
            // Read-only decision, accounting is deferred to match end, a short match does not deserve to consume an observation slot
            irqBudgetDecided = IrqSessionProbe.EnabledSetting;
            irqObserveThisSession = !irqBudgetDecided || IrqObservationBudget.Peek();
            ArmIrqObservation(game);
            // PRESENT starts only after the strict core domain DPC epoch actually begins sampling, system observation does not need it
            long paviseCpu = CurrentProcessCpuTicks();
            lock (sync)
            {
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repSealed.Clear();
                repGame = game;
                repStart = Stopwatch.GetTimestamp();
                repPaviseCpuStart = paviseCpu;
                repProfileId = activeDetection != null && activeDetection.Profile != null
                    ? activeDetection.Profile.Id : null;
                repRendererPid = activeDetection != null ? activeDetection.RendererPid : 0;
                // A match skipped by budget does not count as having requested observation, match end must not report it as cancelled or failed
                repIrqRequested = IrqSessionProbe.EnabledSetting && irqObserveThisSession;
            }
        }

        private void ReportUntrack(int pid)
        {
            lock (sync)
            {
                repCpu.Remove(pid);
                repCreation.Remove(pid);
                repProc.Remove(pid);
                repSealed.Remove(pid);
            }
        }

        private void ReportSeal(int pid)
        {
            long start, creation;
            lock (sync)
            {
                if (!repCpu.TryGetValue(pid, out start)) return;
                repCreation.TryGetValue(pid, out creation);
                repCpu.Remove(pid);
                repCreation.Remove(pid);
            }
            long now, nowCreation, delta = 0;
            if (CpuTicks(pid, out now, out nowCreation)
                && nowCreation == creation && now > start)
                delta = now - start;
            lock (sync)
            {
                long prev;
                repSealed.TryGetValue(pid, out prev);
                repSealed[pid] = prev + delta;
            }
        }

        private void ReportTrack(int pid, string name)
        {
            lock (sync) { if (repGame == null || repCpu.ContainsKey(pid)) return; }
            long t, creation;
            if (!CpuTicks(pid, out t, out creation)) return;
            lock (sync)
            {
                if (!repCpu.ContainsKey(pid))
                {
                    repCpu[pid] = t; repCreation[pid] = creation; repProc[pid] = name;
                }
            }
        }

        private void ReportFinish()
        {
            VramSpillProbe.Seal();
            Dictionary<int, long> cpu;
            Dictionary<int, string> names;
            Dictionary<int, long> creations;
            Dictionary<int, long> used;
            string game;
            long t0;
            long paviseCpuStart;
            // The renderer pid narrows present frames to the game itself, must come from this match's own snapshot
            //   match switch detection moves activeDetection to the new game first, then settles the old match
            //   without a renderer PID the present evidence is ruled unusable, falling back to pure IrqVerdict
            int rendererPid;
            bool irqRequested;
            lock (sync)
            {
                game = repGame;
                t0 = repStart;
                rendererPid = repRendererPid;
                irqRequested = repIrqRequested;
                cpu = new Dictionary<int, long>(repCpu);
                names = new Dictionary<int, string>(repProc);
                creations = new Dictionary<int, long>(repCreation);
                used = new Dictionary<int, long>(repSealed);
                paviseCpuStart = repPaviseCpuStart;
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repSealed.Clear();
                repGame = null;
                repPaviseCpuStart = 0;
                repProfileId = null;
                repRendererPid = 0;
                repIrqRequested = false;
            }
            if (game == null) return;

            // Stopping the guard manually goes through the same seal boundary as a natural game exit, must not first wait for
            // the PRESENT drain and then count the wait time as renderer proof expiry
            SealIrqObservation();

            TimeSpan dur = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - t0) / Stopwatch.Frequency);
            // Match-end accounting for the observation budget, matches shorter than the qualifying threshold leave the count alone, so crashes and instant exits do not burn observation slots
            if (irqBudgetDecided)
            {
                IrqObservationBudget.CommitSession(irqObserveThisSession, (int)dur.TotalSeconds);
                irqBudgetDecided = false;
            }
            // The capture window must strictly contain: DPC starts before present, and present must stop before DPC
            // Stopping DPC first for the expensive summary leaves the still-running present with a tail no DPC covers
            // long frames in that tail would be misjudged as a complete zero-hit
            List<long[]> longFrameIntervals = null;
            try { CollectLongFrames(rendererPid, dur, out longFrameIntervals); } catch { }
            foreach (var kv in cpu)
            {
                long prev;
                if (!used.TryGetValue(kv.Key, out prev)) { prev = 0; used[kv.Key] = 0; }
                long now, creation;
                if (!CpuTicks(kv.Key, out now, out creation)) continue;
                long expectedCreation;
                if (!creations.TryGetValue(kv.Key, out expectedCreation) || creation != expectedCreation) continue;
                long d = now - kv.Value;
                if (d < 0) continue;
                used[kv.Key] = prev + d;
            }

            long total = 0, top = 0;
            string topName = null;
            foreach (var kv in used)
            {
                total += kv.Value;
                if (kv.Value > top)
                {
                    top = kv.Value;
                    string nm;
                    if (names.TryGetValue(kv.Key, out nm)) topName = nm;
                }
            }

            string msg = Lang.F("rep.done", game, FmtDur(dur), used.Count, FmtCpu(total));
            if (topName != null && top >= TimeSpan.TicksPerSecond)
                msg += Lang.F("rep.top", topName, FmtCpu(top));
            long paviseCpuEnd = CurrentProcessCpuTicks();
            long paviseCpuDelta = paviseCpuStart > 0
                && paviseCpuEnd >= paviseCpuStart
                ? paviseCpuEnd - paviseCpuStart : 0;
            double paviseCpuPercent = AverageCpuPercent(
                paviseCpuDelta, dur);
            msg += Lang.F(
                "rep.pavise.cpu",
                paviseCpuPercent.ToString("0.00", CultureInfo.InvariantCulture));
            string throttle = GpuThrottleProbe.Summarize();
            if (throttle != null) msg += Lang.F("rep.gputhrottle", throttle);
            string spill = VramSpillProbe.Summarize();
            if (spill != null) msg += Lang.F("rep.vramspill", spill);
            // present/DPC alignment exists only to strengthen the device interrupt verdict, no present content goes into the match report
            //   intersect this match's per-event DPC timeline with the present long frame intervals, the result feeds only the core move suggestion on the IRQ page, if either is unavailable fall back to pure IrqVerdict
            List<InterruptAttribution.DpcTimelineEntry> dpcTimeline = null;
            bool dpcTimelineTruncated = false;
            try { dpcTimeline = irqProbe.TakeDpcTimeline(out dpcTimelineTruncated, false); } catch { }
            PresentDpcAlignment align = null;
            try { align = PresentDpcAlignment.AlignDpcToLongFrames(longFrameIntervals, dpcTimeline); } catch { align = null; }

            if (irqFrameEvidence != null)
            {
                irqFrameEvidence.AlignmentComplete = !dpcTimelineTruncated && align != null && align.Ok;
                irqFrameEvidence.AlignmentModule = align == null ? "" : align.TopModule ?? "";
                irqFrameEvidence.AlignmentHits = align == null ? 0 : align.TopModuleLongFrameHits;
            }
            irqProbe.SetFrameEvidence(irqFrameEvidence);
            string irq = irqProbe.TakeSummary();
            msg += FormatIrqSessionResult(irq, irqRequested, irqProbe.StatusText);
            NotifyIrqObservationChanged(true);
            Logger.Log(Lang.T("log.gamemodesession.1") + msg);

            // The 60 second threshold follows the balloon rule, no separate standard
            //   anything under a minute is mostly a launcher flash misdetected, showing it on the home page would only look broken
            if (dur.TotalSeconds >= 60)
            {
                string brief = Lang.F("rep.brief", game, FmtDur(dur), used.Count);
                try { Settings.SaveStr(LastSessionKey, brief); } catch { }
                var b = SessionBriefed;
                if (b != null) { try { b(brief); } catch { } }
                var h = SessionEnded;
                if (h != null) { try { h(msg); } catch { } }

                MaybeSuggestIrqRelocation(align, dpcTimelineTruncated);
            }
        }

        // Retrospective IRQ core move suggestion, reuses the Interrupts page verdict logic, no new structures, no registry writes
        //   preconditions: match observation on (otherwise nothing was sampled this match) and enough samples, following the Interrupts page 3-match threshold to avoid noise from one or two matches
        //   IrqVerdict remains the base of the verdict, present/DPC alignment is reinforcing evidence, not a replacement
        //     only with the target swapchain isolated can Worth colliding with a present long frame be called confirmed-grade
        //   Event 184 currently gives only the PID, multi-stream merging can fill in or fake long frames, so it is recorded as a clue only and takes no part in the ruling
        private void MaybeSuggestIrqRelocation(PresentDpcAlignment align, bool dpcTruncated)
        {
            if (!IrqSessionProbe.EnabledSetting) return;
            try
            {
                int hz = 0;
                try { hz = DisplayGuard.CurrentRefreshRate(); } catch { }
                int usedSessions;
                List<IrqSessionRecord> records = IrqSessionLedger.Load();
                List<IrqDriverVerdict> verdicts = IrqVerdict.Evaluate(records, hz, out usedSessions);
                if (usedSessions < IrqSessionLedger.MinSessionsForVerdict) return;
                IrqDeviceInventory.VerifyCurrentVersions(verdicts);
                int worth = 0;
                foreach (IrqDriverVerdict v in verdicts)
                    if (v != null && v.Worth && v.VersionVerified) worth++;
                if (worth <= 0) return;

                // A positive present alignment hit can reinforce Worth, but DxgKrnl 184 currently
                // gives only PID/context/window with no reliable swapchain identity, auxiliary present streams on the same PID
                // can fill in the main render stream's long frames, so by default it only counts as positive evidence, zero hits are not counter-evidence
                bool presentUsable = align != null && align.Ok && align.LongFrameHits != null;
                bool swapchainIdentityReliable = presentUsable && align.SwapchainIdentityReliable;
                bool alignmentIncomplete = dpcTruncated
                    || (align != null && align.UnknownModuleInLongFrames);
                int matched = 0;
                if (presentUsable)
                {
                    foreach (IrqDriverVerdict v in verdicts)
                    {
                        if (v == null || !v.Worth || !v.VersionVerified
                            || string.IsNullOrEmpty(v.Driver)) continue;
                        int h;
                        if (align.LongFrameHits.TryGetValue(v.Driver, out h) && h > 0)
                        {
                            matched++;
                            int dc; align.DpcCounts.TryGetValue(v.Driver, out dc);
                            Logger.Log((swapchainIdentityReliable
                                    ? "挪核建议·证实级 " : "挪核建议·对齐线索 ") + v.Driver
                                + " 本局 DPC 撞长帧 " + h + " 次 长帧内 DPC " + dc + " 个"
                                + (swapchainIdentityReliable
                                    ? " (present↔DPC 对齐佐证 IrqVerdict 判定)"
                                    : " [同PID多呈现流未分离 不作证实]")
                                + (alignmentIncomplete ? " [DPC证据不完整 撞击数可能偏低]" : ""));
                        }
                        else
                        {
                            Logger.Log("挪核建议·疑似 " + v.Driver
                                + " 判定 Worth 但本局 present 未证实撞帧"
                                + (alignmentIncomplete
                                    ? " [DPC时间线或模块映射不完整 未命中不能排除相关性]"
                                    : !swapchainIdentityReliable
                                        ? " [同PID多呈现流未分离 零命中不能排除相关性]"
                                        : " 继续观察 不计入主动提示"));
                        }
                    }
                }

                // Only a future capture chain proven to be a single target swapchain may let a complete zero-hit
                // block the proactive hint, with current PID-level data a zero-hit falls back to Worth while positive hits still count
                int longFrames = align == null ? 0 : align.LongFrames;
                int reported = ResolveIrqReportedCount(
                    presentUsable, swapchainIdentityReliable, alignmentIncomplete,
                    longFrames, matched, worth);
                Logger.Log(Lang.F("log.irqsuggest.1", reported, usedSessions)
                    + (presentUsable ? " (present 可用 对齐 " + matched + "/Worth " + worth
                        + (swapchainIdentityReliable ? " swapchain可靠" : " 仅线索")
                        + (alignmentIncomplete ? " DPC证据不完整" : "") + ")" : ""));
                if (reported <= 0) return;
                var s = IrqSuggested;
                if (s != null) { try { s(reported); } catch { } }
            }
            catch { }
        }

        internal static string FormatIrqSessionResult(string summary, bool requested, string status)
        {
            if (!string.IsNullOrEmpty(summary)) return summary;
            // Each match's failure/cancel reason also hangs under the end record carrying game name and duration
            // not just one latest status that the next match overwrites, and never fabricated measurements
            return requested && !string.IsNullOrEmpty(status)
                ? Lang.F("rep.irq.result", status) : "";
        }

        internal static int ResolveIrqReportedCount(bool presentUsable, bool swapchainIdentityReliable,
            bool dpcIncomplete, int longFrames, int matched, int worth)
        {
            if (!presentUsable) return worth;
            // PID-level multi-stream merging can both fill in long frames and use a high-rate auxiliary stream to pull down
            // the median and fake long frames, without swapchain identity neither positive nor negative results may reduce Worth
            if (!swapchainIdentityReliable) return worth;
            // When present clearly has no long frames, even a broken DPC timeline cannot hide a long frame collision
            if (longFrames <= 0 && matched <= 0) return 0;
            // With an incomplete alignment chain, the matched items must not negate the remaining Worth items
            return dpcIncomplete ? worth : matched;
        }
    }
}
