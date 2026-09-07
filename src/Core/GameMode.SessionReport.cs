// @author bdth 2074055628@qq.com
// 文件用途 对局报告的登记 封存与结束汇总
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
            ResetBoostDomainEvidence();
            GpuThrottleProbe.Reset();
            VramSpillProbe.Reset();
            VramShield.Begin();
            // 只读判定 记账推迟到局末 短局不配消耗观测名额
            irqBudgetDecided = IrqSessionProbe.EnabledSetting;
            irqObserveThisSession = !irqBudgetDecided || IrqObservationBudget.Peek();
            ArmIrqObservation(game);
            // PRESENT 在严格核域 DPC epoch 真正起采后才开启 系统观测不需要它
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
                // 预算跳过的局不算"请求过观测" 局末不该按取消或失败报告
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
            // 渲染进程 pid 用来把 present 帧收敛到游戏本体 必须取本局独立快照
            //   换局检测会先把 activeDetection 切到新游戏 再结算旧局
            //   拿不到渲染 PID 时 present 证据判为不可用 退回纯 IrqVerdict
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

            // 主动停守护也走与游戏自然退出相同的封存边界 不能先等待
            // PRESENT 排空 再把等待时间当成 renderer 证明失效
            SealIrqObservation();

            TimeSpan dur = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - t0) / Stopwatch.Frequency);
            // 观测预算的局末记账 短于合格门槛的局不动计数 观测名额不被闪退秒退烧掉
            if (irqBudgetDecided)
            {
                IrqObservationBudget.CommitSession(irqObserveThisSession, (int)dur.TotalSeconds);
                irqBudgetDecided = false;
            }
            // 采集窗口要严格包含 开始是 DPC→present 结束必须 present→DPC
            // 先停 DPC 去做昂贵汇总会让仍在跑的 present 多出一段无 DPC 覆盖的尾巴
            // 那段里的长帧会被误判成“完整零命中”
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
            string irq = irqProbe.TakeSummary();
            msg += FormatIrqSessionResult(irq, irqRequested, irqProbe.StatusText);
            NotifyIrqObservationChanged(true);
            Logger.Log(Lang.T("log.gamemodesession.1") + msg);

            // present↔DPC 对齐只为增强设备中断判断 不往对局报告塞任何 present 内容
            //   本局逐事件 DPC 时间线 ∩ present 长帧区间 结果只喂给挪核建议(IRQ 页) 任一不可用则退回纯 IrqVerdict
            List<InterruptAttribution.DpcTimelineEntry> dpcTimeline = null;
            bool dpcTimelineTruncated = false;
            try { dpcTimeline = irqProbe.TakeDpcTimeline(out dpcTimelineTruncated); } catch { }
            PresentDpcAlignment align = null;
            try { align = PresentDpcAlignment.AlignDpcToLongFrames(longFrameIntervals, dpcTimeline); } catch { align = null; }

            // 60 秒门槛沿用气泡那条 不另立标准
            //   短于一分钟的多半是启动器闪一下造成的误判 摆到首页只会让人以为坏了
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

        // 回顾式挪核建议 复用中断页那套判定 不造新结构 不写注册表
        //   前提 对局观测开着(否则本局根本没采数据) 且样本够(沿用中断页 3 局门槛) 避免一两局的偶发噪声
        //   IrqVerdict 仍作判定之底 present↔DPC 对齐是增强证据 不替换:
        //     只有已分离目标 swapchain 时 Worth 与 present 撞长帧才可称 证实级
        //   当前 Event 184 只有 PID 多流合并可填平或伪造长帧 因而只记线索不参与裁决
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

                // present 对齐的正命中可以增强 Worth 但当前 DxgKrnl 184
                // 只给 PID/context/window 没有可靠 swapchain 身份 同 PID 辅助呈现流
                // 可以填平主渲染流的长帧 因而默认只把它当正证据 不把零命中当反证
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

                // 只有未来能证明是单一目标 swapchain 的采集链 完整零命中
                // 才可拦住主动提示 当前 PID 级数据的零命中回退 Worth 正命中仍有效
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
            // 每局的失败/取消原因也挂在带游戏名和时长的结束记录下
            // 不能只留一条会被下一局覆盖的最新状态 更不能捏造实测数值
            return requested && !string.IsNullOrEmpty(status)
                ? Lang.F("rep.irq.result", status) : "";
        }

        internal static int ResolveIrqReportedCount(bool presentUsable, bool swapchainIdentityReliable,
            bool dpcIncomplete, int longFrames, int matched, int worth)
        {
            if (!presentUsable) return worth;
            // PID 级多流合并既能填平长帧 也能用高频辅助流压低
            // median 而伪造长帧 没有 swapchain 身份时 正负结果都不许缩减 Worth
            if (!swapchainIdentityReliable) return worth;
            // present 明确没有长帧时 DPC 时间线再不完整也不可能藏住“撞长帧”
            if (longFrames <= 0 && matched <= 0) return 0;
            // 对齐链不完整时 已命中项不能反向否定其余 Worth 项
            return dpcIncomplete ? worth : matched;
        }
    }
}
