// @author bdth 2074055628@qq.com
// 文件用途 统计单局游戏的压制成效并写入运行日志
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly Dictionary<int, long> repCpu = new Dictionary<int, long>();
        private readonly Dictionary<int, long> repCreation = new Dictionary<int, long>();
        private readonly Dictionary<int, string> repProc = new Dictionary<int, string>();
        private readonly Dictionary<int, long> repSealed = new Dictionary<int, long>();
        private long repStart;
        private string repGame;
        private long repPaviseCpuStart;
        private string repProfileId;
        private int repRendererPid;
        private bool repIrqRequested;

        public event Action<string> SessionEnded;

        // 概览页"最近一局"要的是一行能看完的短摘要 和托盘气泡那条长文不是一回事
        //   长文带 Pavise 自身占用 显卡受限 显存溢出 中断台账 塞不进一行
        //   这里只留游戏名 时长 压制进程数 三项 其余仍然只进日志和气泡
        internal const string LastSessionKey = "LastSessionBrief";

        public static string LastSessionBrief
        {
            get { return Settings.LoadStr(LastSessionKey, ""); }
        }

        public event Action<string> SessionBriefed;

        // 对局结束回顾式挪核建议 只在本局够长且观测开着时 基于已落盘的多局实测跑一次判定
        //   有 Worth 驱动就把数量抛给 UI 高亮提示 默认不自动改注册表 用户走手动流程
        //   唯一例外是用户明确开启并确认过的自动中断编排 由 IrqAutoPilot 在局后按裁决落收据
        public event Action<int> IrqSuggested;

        // 原始记录完成与“有挪核建议”是两回事 零建议和失败也要让页面刷新
        public event Action IrqObservationUpdated;
        public string IrqObservationStatusText { get { return irqProbe.StatusText; } }
        public bool IrqObservationStatusWarning { get { return irqProbe.StatusWarning; } }
        private string irqNotifiedStatus = "";
        private bool irqNotifiedWarning;
        private bool presentProbeAttempted;
        private long presentProbeEpoch = -1;
        private int irqSettingChanged;

        public void RequestIrqObservationSettingChanged()
        {
            System.Threading.Interlocked.Exchange(ref irqSettingChanged, 1);
            try { kick.Set(); } catch { }
        }

        private void ApplyIrqObservationSettingChange(string game)
        {
            if (System.Threading.Interlocked.Exchange(ref irqSettingChanged, 0) == 0) return;
            // 用户局中显式动了观测开关 那是明确的本局观测请求 预算让路
            irqObserveThisSession = true;
            ArmIrqObservation(game);
            if (IrqSessionProbe.EnabledSetting)
                lock (sync) repIrqRequested = true;
            if (irqProbe.RequiresPlacementAudit)
            {
                // 局中显式开启时 让普通落核缓存重新经过严格 proof 初始化
                // 单纯一次采样失败不会走这里 不会每轮强制重试或改写亲和性
                lock (sync)
                {
                    int pid = activeDetection == null ? 0 : activeDetection.RendererPid;
                    gamePlacement.Remove(pid);
                    gamePlacementStrict.Remove(pid);
                    gameBoostNextAudit.Remove(pid);
                }
            }
        }

        private void NotifyIrqObservationChanged(bool force)
        {
            string text = irqProbe.StatusText;
            bool warning = irqProbe.StatusWarning;
            if (!force && text == irqNotifiedStatus && warning == irqNotifiedWarning) return;
            irqNotifiedStatus = text;
            irqNotifiedWarning = warning;
            var changed = IrqObservationUpdated;
            if (changed != null) { try { changed(); } catch { } }
        }

        internal static bool NeedsSystemIrqObservation(
            bool boostEnabled, bool writeDenied, bool multiGroup,
            ulong desiredMask, ulong availableMask)
        {
            return !boostEnabled || writeDenied || multiGroup
                || !IrqSessionProbe.CanConfirmMaskShape(desiredMask, availableMask);
        }

        private bool NeedsSystemIrqObservation()
        {
            bool strict;
            ulong desired = EffectiveGameMask(sessionPolicy, out strict);
            string rendererName;
            lock (sync) rendererName = activeDetection == null ? null : activeDetection.RendererName;
            return NeedsSystemIrqObservation(EffBoost,
                ProtectedGameRoster.Contains(rendererName), CpuTopology.MultiGroup, desired, allMask);
        }

        // 本局要不要观测 开局判一次 局内所有重挂点共用这个决定 不许中途改判
        //   宽限恢复和封存重开走的也是 ArmIrqObservation 跳过局重挂时同样跳过
        private bool irqObserveThisSession = true;
        private bool irqBudgetDecided;

        private void ArmIrqObservation(string game)
        {
            DiscardPresentProbe();
            if (!irqObserveThisSession)
            {
                NotifyIrqObservationChanged(false);
                return;
            }
            irqProbe.Arm(game, allMask, NeedsSystemIrqObservation());
            NotifyIrqObservationChanged(false);
        }

        private void ObserveSystemIrq(int rendererPid, long rendererCreation)
        {
            if (!IrqSessionProbe.EnabledSetting)
            {
                if (irqProbe.IsCapturing) irqProbe.InvalidateGameMask();
                return;
            }
            // 本局按观测预算跳过 逐 tick 的回退重挂也不许把它捡回来
            if (!irqObserveThisSession) return;
            bool fallback = false;
            if (!irqProbe.IsSystemObservation)
            {
                if (NeedsSystemIrqObservation())
                {
                    ArmIrqObservation(repGame ?? activeGame);
                    fallback = true;
                }
                else fallback = irqProbe.TryFallbackToSystemObservation(
                    repGame ?? activeGame, allMask);
            }
            if (fallback)
            {
                DiscardPresentProbe();
                // 退出专为严格 IRQ 证明设置的临时硬绑核 普通游戏调优保持原样
                // 还原失败的句柄仍由已有恢复路径跟进 不阻止只读系统观测
                RestoreAllIrqProofHardPins();
            }
            if (!irqProbe.CanObserveSystemNow) return;
            // 系统观测不申请 SET 权限 更不为出现测量值而改动游戏亲和性
            // pid+creation 读回失败时不拿过期身份继续采样
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION,
                false, rendererPid);
            if (handle == IntPtr.Zero)
            {
                if (irqProbe.IsCapturing)
                {
                    if (Native.LastOpenProcessFailureWasNoSuchProcess()) SealIrqObservation();
                    else irqProbe.InvalidateGameMask();
                }
                return;
            }
            try
            {
                long creation, cpu;
                ulong disk;
                if (rendererPid > 0 && rendererCreation > 0
                    && Native.QueryProcessSample(handle, out creation, out cpu, out disk)
                    && creation == rendererCreation)
                    irqProbe.ConfirmSystemObservation(rendererPid, rendererCreation);
                else if (irqProbe.IsCapturing) irqProbe.InvalidateGameMask();
            }
            finally { Native.CloseHandle(handle); }
        }

        private void DiscardPresentProbe()
        {
            PresentProbe old = presentProbe;
            presentProbe = null;
            presentProbeAttempted = false;
            presentProbeEpoch = -1;
            if (old != null) { try { old.Stop(); } catch { } }
        }

        private void UpdateIrqPresentProbe()
        {
            // 系统观测不出挪核建议 无需额外抓 PRESENT 严格观测也必须先
            // 有 DPC 窗口 换 epoch 时丢弃旧 present 不能跨窗口对齐
            if (irqProbe.HasSealedPending) return;
            if (!irqProbe.IsPlacementCapturing || !IrqSessionProbe.EnabledSetting)
            {
                DiscardPresentProbe();
                return;
            }
            long epoch = irqProbe.CaptureEpoch;
            if (presentProbeEpoch != epoch)
            {
                DiscardPresentProbe();
                presentProbeEpoch = epoch;
            }
            if (presentProbeAttempted) return;
            presentProbeAttempted = true;
            StartPresentProbe();
        }

        private void SealIrqObservation()
        {
            // 收口顺序与起采相反 先停 present 再停 DPC 保留 present
            // 实例供宽限结束后的 CollectLongFrames 取数据 不继续录桌面
            PresentProbe p = presentProbe;
            if (p != null) { try { p.RequestStop(); } catch { } }
            irqProbe.Seal();
        }

        private void ReportBegin(string game)
        {
            GpuThrottleProbe.Reset();
            VramSpillProbe.Reset();
            VramShield.Begin();
            MemShield.Begin();
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
            try { align = AlignDpcToLongFrames(longFrameIntervals, dpcTimeline); } catch { align = null; }

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
                // 自动中断编排在建议门槛之前跑 旧计划的验收不该被"本局没有新建议"拦住
                //   目标掩码必须和裁决证据同源 裁决窗内各局的游戏掩码不一致说明混着玩了
                //   不同锁核方案的游戏 按 A 的证据钉到 B 的掩码外可能正钉进 A 的游戏核
                //   这种局面整局不编排 只验收
                ulong autoGameMask = 0, autoSystemMask = 0;
                int autoSeen = 0;
                for (int i = records.Count - 1; i >= 0
                    && autoSeen < IrqSessionLedger.VerdictWindow; i--)
                {
                    IrqSessionRecord rec = records[i];
                    if (rec == null || !rec.UsableForVerdict) continue;
                    autoSeen++;
                    if (rec.GameMask == 0) continue;
                    if (autoGameMask == 0)
                    {
                        autoGameMask = rec.GameMask;
                        autoSystemMask = rec.SystemMask;
                    }
                    else if (autoGameMask != rec.GameMask || autoSystemMask != rec.SystemMask)
                    {
                        autoGameMask = 0;
                        autoSystemMask = 0;
                        break;
                    }
                }
                try { IrqAutoPilot.RunAfterMatch(verdicts, usedSessions, autoGameMask, autoSystemMask); }
                catch { }
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

        // present 采集按局起停 每局新建实例(PresentProbe.frames 不自清 复用会跨局累积)
        //   需要管理员 非管理员或 Start 失败(内部已记日志)就不留引用 优雅跳过不崩
        private void StartPresentProbe()
        {
            try
            {
                PresentProbe old = presentProbe;
                presentProbe = null;
                if (old != null) try { old.Stop(); } catch { }
            }
            catch { }
            try
            {
                var p = new PresentProbe();
                presentProbe = p.Start() ? p : null;
            }
            catch { presentProbe = null; }
        }

        private const int MinPresentIntervalsForAlignment = 30;
        private const double MaxContinuousPresentGapMs = 2000.0;

        // 停掉本局 present 会话 只取渲染进程的帧 算出 长帧区间 [上一帧qpc,本帧qpc] 供与 DPC 时间线求交
        //   present↔DPC 对齐只为增强设备中断判断 不产出对局报告摘要(帧数/p99/1%low 都不要)
        //   必须是完整排空 无截断/丢事件且渲染 pid 自身至少有 30 个有效间隔 否则返回 null
        //   少量或其它进程的 present 不足以建立有意义的时间对齐样本
        //   区间按 QPC 递增且首尾相接 与 DPC 会话同一根 QPC 尺子(都 RawTimestamp)可直接对齐
        private void CollectLongFrames(int rendererPid, TimeSpan sessionDuration,
            out List<long[]> longFrameIntervals)
        {
            longFrameIntervals = null;
            PresentProbe p = presentProbe;
            presentProbe = null;
            if (p == null) return;

            List<PresentFrame> frames;
            long freq;
            try
            {
                p.Stop();
                if (!PresentCaptureComplete(p.DrainCompleted, p.ConsumerExitedEarly,
                    p.Truncated, p.EventsLost, p.BuffersLost)) return;
                frames = p.Frames;
                freq = p.QpcFrequency;
            }
            catch { return; }
            // 时间对齐样本至少要覆盖半局且不少于 2 秒 只抓到开局一小撮帧时不记线索
            double minCoverageSeconds = Math.Max(2.0, sessionDuration.TotalSeconds * 0.5);
            longFrameIntervals = BuildLongFrameIntervals(
                frames, freq, rendererPid, minCoverageSeconds);
        }

        internal static bool PresentCaptureComplete(bool drainCompleted, bool consumerExitedEarly,
            bool truncated, uint eventsLost, uint buffersLost)
        {
            return drainCompleted && !consumerExitedEarly && !truncated
                && eventsLost == 0 && buffersLost == 0;
        }

        internal static int UpdateSessionRendererPid(string sessionProfileId, int currentPid,
            string detectedProfileId, int detectedPid)
        {
            return !string.IsNullOrEmpty(sessionProfileId) && detectedPid > 0
                && string.Equals(sessionProfileId, detectedProfileId, StringComparison.OrdinalIgnoreCase)
                ? detectedPid : currentPid;
        }

        internal static bool SameReportedProfile(string sessionProfileId, string detectedProfileId)
        {
            return !string.IsNullOrEmpty(sessionProfileId)
                && !string.IsNullOrEmpty(detectedProfileId)
                && string.Equals(sessionProfileId, detectedProfileId, StringComparison.OrdinalIgnoreCase);
        }

        internal static List<long[]> BuildLongFrameIntervals(
            List<PresentFrame> frames, long freq, int rendererPid, double minCoverageSeconds)
        {
            if (frames == null || freq <= 0 || rendererPid <= 0 || minCoverageSeconds < 0
                || double.IsNaN(minCoverageSeconds) || double.IsInfinity(minCoverageSeconds)) return null;

            var qpcs = new List<long>(frames.Count);
            foreach (PresentFrame f in frames) if (f.Pid == rendererPid) qpcs.Add(f.Qpc);
            if (qpcs.Count < MinPresentIntervalsForAlignment + 1) return null;
            qpcs.Sort();

            double msPerTick = 1000.0 / freq;
            // Alt-Tab/最小化后几十秒不呈现不是一帧 按超大 gap 切段 只用一段
            // 连续活跃且样本足够的呈现流 避免空窗同时伪造 coverage 和“长帧”
            var segments = new List<List<long[]>>();
            var current = new List<long[]>();
            for (int i = 1; i < qpcs.Count; i++)
            {
                long d = qpcs[i] - qpcs[i - 1];
                if (d <= 0) continue;
                if (d * msPerTick > MaxContinuousPresentGapMs)
                {
                    if (current.Count > 0) segments.Add(current);
                    current = new List<long[]>();
                    continue;
                }
                current.Add(new long[] { qpcs[i - 1], qpcs[i] });
            }
            if (current.Count > 0) segments.Add(current);

            List<long[]> active = null;
            long activeCoverage = -1;
            foreach (List<long[]> segment in segments)
            {
                if (segment.Count < MinPresentIntervalsForAlignment) continue;
                long coverage = segment[segment.Count - 1][1] - segment[0][0];
                if (coverage > activeCoverage) { active = segment; activeCoverage = coverage; }
            }
            if (active == null || activeCoverage <= 0
                || activeCoverage / (double)freq < minCoverageSeconds) return null;

            // 时间序相邻帧间隔 loQ/hiQ 保留端点 QPC 供长帧区间对齐
            var ft = new List<double>(active.Count);
            foreach (long[] pair in active) ft.Add((pair[1] - pair[0]) * msPerTick);
            int n = ft.Count;

            var sorted = new List<double>(ft);
            sorted.Sort();
            double median = sorted[n / 2];
            if ((n & 1) == 0) median = (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
            double longThresh = median * 2.0;
            // 长帧区间从时间序 ft 取(sorted 会打乱相邻关系不能用)
            var intervals = new List<long[]>();
            for (int i = 0; i < n; i++)
                if (ft[i] > longThresh) intervals.Add(active[i]);
            return intervals;
        }

        // present 长帧区间 ∩ DPC 时间线 数每个长帧里落了哪些模块的 DPC 归类
        //   活跃集扫描 区间按 QPC 递增且不重叠 DPC 按有效 StartQpc 排序
        //   产出 撞长帧 top 模块 每模块记 撞了几帧(LongFrameHits) 与总 DPC 数(DpcCount)
        internal sealed class PresentDpcAlignment
        {
            public bool Ok;
            public int LongFrames;
            public int TotalDpcInLongFrames;
            public Dictionary<string, int> LongFrameHits;   // 模块 -> 命中多少个长帧区间
            public Dictionary<string, int> DpcCounts;       // 模块 -> 长帧内 DPC 总数
            public bool UnknownModuleInLongFrames;          // 区间内有无法映射到驱动的 DPC 零命中不可靠
            public bool SwapchainIdentityReliable;          // 只有已解决目标 swapchain 身份时才可置 true
            public string TopModule;
            public int TopModuleLongFrameHits;
            public int TopModuleDpcCount;
        }

        internal static PresentDpcAlignment AlignDpcToLongFrames(
            List<long[]> intervals, List<InterruptAttribution.DpcTimelineEntry> dpc,
            bool swapchainIdentityReliable = false)
        {
            // null 表示某条采集链根本不可用 非 null 空集合表示
            // 可用于正命中对齐但结果为零 是否能作负证据由 swapchain 可靠性单独决定
            if (intervals == null) return null;

            var r = new PresentDpcAlignment();
            r.Ok = true;
            r.SwapchainIdentityReliable = swapchainIdentityReliable;
            r.LongFrames = intervals.Count;
            r.LongFrameHits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            r.DpcCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // present 已明确没有长帧时 DPC 探针是否可用都不可能藏住“撞长帧”
            if (intervals.Count == 0) return r;
            if (dpc == null) return null;
            if (dpc.Count == 0) return r;

            dpc.Sort(delegate (InterruptAttribution.DpcTimelineEntry a, InterruptAttribution.DpcTimelineEntry b)
            {
                int byStart = a.StartQpc.CompareTo(b.StartQpc);
                return byStart != 0 ? byStart : a.EndQpc.CompareTo(b.EndQpc);
            });

            Dictionary<string, int> hits = r.LongFrameHits;
            Dictionary<string, int> counts = r.DpcCounts;
            int addIndex = 0, nDpc = dpc.Count, totalIn = 0;
            var activeDpc = new List<int>();
            var countedDpc = new HashSet<int>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (long[] iv in intervals)   // 区间已按 QPC 递增
            {
                if (iv == null || iv.Length < 2 || iv[1] <= iv[0]) return null;
                long lo = iv[0], hi = iv[1];
                while (addIndex < nDpc && dpc[addIndex].StartQpc < hi)
                    activeDpc.Add(addIndex++);
                for (int i = activeDpc.Count - 1; i >= 0; i--)
                    if (dpc[activeDpc[i]].EndQpc <= lo) activeDpc.RemoveAt(i);
                seen.Clear();
                foreach (int dpcIndex in activeDpc)
                {
                    InterruptAttribution.DpcTimelineEntry entry = dpc[dpcIndex];
                    // 半开重叠规则 DPC.Start < frame.End && DPC.End > frame.Start
                    // 跨过帧边界才是最需被捕获的 DPC 只看 EndQpc 会漏掉它
                    if (entry.StartQpc >= hi || entry.EndQpc <= lo) continue;
                    string m = entry.Module;
                    if (string.IsNullOrWhiteSpace(m) || m == "?")
                    {
                        m = "?";
                        r.UnknownModuleInLongFrames = true;
                    }
                    // 一条 DPC 可以横跨两个相邻长帧 帧命中应各算一次 但事件总数
                    // 只能算一次 否则日志会把“1 条跨帧 DPC”误报成“2 条”
                    if (countedDpc.Add(dpcIndex))
                    {
                        int c; counts.TryGetValue(m, out c); counts[m] = c + 1;
                        totalIn++;
                    }
                    if (seen.Add(m)) { int h; hits.TryGetValue(m, out h); hits[m] = h + 1; }
                }
            }

            r.TotalDpcInLongFrames = totalIn;
            foreach (KeyValuePair<string, int> kv in hits)
            {
                int dc; counts.TryGetValue(kv.Key, out dc);
                if (kv.Value > r.TopModuleLongFrameHits
                    || (kv.Value == r.TopModuleLongFrameHits && dc > r.TopModuleDpcCount))
                {
                    r.TopModule = kv.Key;
                    r.TopModuleLongFrameHits = kv.Value;
                    r.TopModuleDpcCount = dc;
                }
            }
            return r;
        }

        private static bool CpuTicks(int pid, out long ticks, out long creation)
        {
            ticks = 0; creation = 0;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                long e, k, u;
                if (!GetProcessTimes(h, out creation, out e, out k, out u)) return false;
                ticks = k + u;
                return true;
            }
            finally { Native.CloseHandle(h); }
        }

        private static long CurrentProcessCpuTicks()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return process.TotalProcessorTime.Ticks;
            }
            catch { return 0; }
        }

        internal static double AverageCpuPercent(
            long cpuTicks, TimeSpan duration)
        {
            if (cpuTicks <= 0 || duration.Ticks <= 0) return 0;
            int processors = Math.Max(1, Environment.ProcessorCount);
            double percent = cpuTicks * 100.0
                / (duration.Ticks * (double)processors);
            return Math.Max(0, Math.Min(100, percent));
        }

        private static string FmtDur(TimeSpan t)
        {
            if (t.TotalHours >= 1) return (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m";
            if (t.TotalMinutes >= 1) return t.Minutes + "m" + t.Seconds.ToString("00") + "s";
            return t.Seconds + "s";
        }

        private static string FmtCpu(long ticks)
        {
            TimeSpan t = TimeSpan.FromTicks(ticks);
            if (t.TotalSeconds < 1) return "<1s";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + "m" + t.Seconds.ToString("00") + "s";
            return t.TotalSeconds.ToString("0.0") + "s";
        }

#if PAVISE_SELFTEST
        internal void ProbeSessionBegin(string game) { ReportBegin(game); }

        internal void ProbeSessionTrack(int pid, string name) { ReportTrack(pid, name); }

        internal void ProbeSessionSeal(int pid) { ReportSeal(pid); }

        internal void ProbeSessionUntrack(int pid) { ReportUntrack(pid); }

        internal void ProbeSessionFinish() { ReportFinish(); }
#endif

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
    }
}
