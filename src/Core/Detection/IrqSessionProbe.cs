// @author bdth 2074055628@qq.com
// 文件用途 对局期常驻中断观测 打完一局落盘一段 供中断页按真实数据给建议
using System;

namespace PaviseApp
{
    internal sealed class IrqSessionProbe : IDisposable
    {
        internal const string EnabledKey = "IrqSessionProbe";
        private const long MaxProofAgeTicks = 1500L * TimeSpan.TicksPerMillisecond;

        private readonly object gate = new object();
        private readonly object takeGate = new object();
        private InterruptAttribution live;
        private long startTicks;
        private long lastProofTicks;
        private string gameName = "";
        private string bootStamp = "";
        private string topologyStamp = "";
        private ulong gameMask;
        private ulong systemMask;
        private int rendererPid;
        private long rendererCreation;
        private long generation;
        private int externalMutations;
        private bool armed;
        private bool disposed;
        private bool gameMaskInvalid;
        private bool completed = true;
        private bool sealedPending;
        private bool stopInProgress;
        // Seal 只停 ETW 并暂存；必须等退出宽限真正结束，
        // 才由 TakeSummary 提交。同一游戏在宽限内恢复时 Arm 会丢弃它。
        private IrqSessionRecord pendingRecord;
        private string pendingSummary;
        // 上一局的逐事件 DPC 时间线 供 GameMode 取走做 present 对齐 取走即清
        private System.Collections.Generic.List<InterruptAttribution.DpcTimelineEntry> pendingTimeline;
        private bool pendingTimelineTruncated;

        public static bool EnabledSetting
        {
            get { return Settings.Load(EnabledKey, false); }
            set { Settings.Save(EnabledKey, value); }
        }

        private bool warnedNoAdmin;

        // 开局只布防，不启动 ETW。必须等真实渲染进程的落核状态读回验证后，
        // 才从那个时刻开始采集，避免把验证前的系统 DPC 倒算到游戏核心上。
        public void Arm(string game, ulong availableSystemMask)
        {
            InterruptAttribution stale;
            bool enabled = EnabledSetting && availableSystemMask != 0;
            lock (gate)
            {
                if (disposed) return;
                stale = InvalidateLocked();
                gameName = game ?? "";
                systemMask = availableSystemMask;
                gameMaskInvalid = false;
                armed = enabled;
                completed = true;
            }
            StopAndDiscard(stale);
        }

        // armed 后以及采集中都要持续核验；采集中一旦失配，整局样本永久作废。
        public bool RequiresPlacementAudit
        {
            get { lock (gate) return !disposed && armed && !gameMaskInvalid; }
        }

        public bool IsCapturing
        {
            get { lock (gate) return !disposed && armed && !gameMaskInvalid && live != null && !completed; }
        }

        public bool HasSealedPending
        {
            get
            {
                lock (gate)
                    return !disposed && sealedPending;
            }
        }

        public bool ProofMatches(
            ulong verifiedMask, int verifiedRendererPid,
            long verifiedRendererCreation)
        {
            lock (gate)
                return !disposed && armed && !gameMaskInvalid
                    && live != null && !completed
                    && gameMask == verifiedMask
                    && rendererPid == verifiedRendererPid
                    && rendererCreation == verifiedRendererCreation;
        }

        internal static bool CanConfirmMask(
            ulong verifiedMask, ulong availableSystemMask,
            int verifiedRendererPid, long verifiedRendererCreation)
        {
            return CanConfirmMaskShape(verifiedMask, availableSystemMask)
                && verifiedRendererPid > 0
                && verifiedRendererCreation > 0;
        }

        internal static bool CanConfirmMaskShape(
            ulong verifiedMask, ulong availableSystemMask)
        {
            return verifiedMask != 0
                && availableSystemMask != 0
                && verifiedMask != availableSystemMask
                && (verifiedMask & ~availableSystemMask) == 0;
        }

        public bool ConfirmGameMask(
            ulong verifiedMask, int verifiedRendererPid,
            long verifiedRendererCreation)
        {
            bool enabled = EnabledSetting;
            bool accepted = false;
            bool logStarted = false;
            bool logStartFailed = false;
            bool logNoAdmin = false;
            InterruptAttribution discard = null;
            InterruptAttribution failedStart = null;
            lock (gate)
            {
                if (disposed || !armed || gameMaskInvalid) return false;
                // RenderLane 等异步调优若正在写 renderer，起采必须
                // 等它离开写区；该计数和开采在同一 gate 下，没有
                // “回调刚查完、ETW 就开了、setter 才落下”的窗口。
                if (externalMutations > 0) return false;
                if (!enabled || !CanConfirmMask(
                        verifiedMask, systemMask,
                        verifiedRendererPid, verifiedRendererCreation))
                {
                    discard = InvalidateLocked();
                }
                else if (live != null)
                {
                    if (!completed
                        && gameMask == verifiedMask
                        && rendererPid == verifiedRendererPid
                        && rendererCreation == verifiedRendererCreation)
                    {
                        long now = DateTime.UtcNow.Ticks;
                        bool continuous = lastProofTicks > 0
                            && now >= lastProofTicks
                            && now - lastProofTicks <= MaxProofAgeTicks;
                        if (continuous)
                        {
                            lastProofTicks = now;
                            accepted = true;
                        }
                        else
                        {
                            // 超过 proof 新鲜度的空窗无法在事后补证。
                            // 丢弃旧 ETW，但保持本局 armed，下轮从当前
                            // 已验证落核点重新开一个干净 epoch。
                            string resumeGame = gameName;
                            ulong resumeSystem = systemMask;
                            discard = InvalidateLocked();
                            gameName = resumeGame;
                            systemMask = resumeSystem;
                            gameMaskInvalid = false;
                            armed = enabled && resumeSystem != 0;
                            completed = true;
                        }
                    }
                    else discard = InvalidateLocked();
                }
                else if (!Native.IsElevated())
                {
                    if (!warnedNoAdmin) { warnedNoAdmin = true; logNoAdmin = true; }
                    discard = InvalidateLocked();
                }
                else
                {
                    var ia = new InterruptAttribution();
                    ia.EnableDpcTimeline();
                    bool started = false;
                    try { started = ia.Start(); } catch { }
                    if (!started)
                    {
                        failedStart = ia;
                        logStartFailed = !ia.Busy;
                        discard = InvalidateLocked();
                    }
                    else
                    {
                        live = ia;
                        gameMask = verifiedMask;
                        rendererPid = verifiedRendererPid;
                        rendererCreation = verifiedRendererCreation;
                        startTicks = DateTime.UtcNow.Ticks;
                        lastProofTicks = startTicks;
                        bootStamp = IrqAffinityEngine.BootStamp();
                        topologyStamp = CpuTopology.TopologyStamp();
                        completed = false;
                        accepted = true;
                        logStarted = true;
                    }
                }
            }
            StopAndDiscard(discard);
            StopAndDiscard(failedStart);
            if (logNoAdmin) Logger.Log(Lang.T("log.irqsession.4"));
            if (logStartFailed) Logger.Log(Lang.T("log.irqsession.1"));
            if (logStarted) Logger.Log(Lang.T("log.irqsession.2"));
            return accepted;
        }

        public void InvalidateGameMask()
        {
            InterruptAttribution discard;
            lock (gate) discard = InvalidateLocked();
            StopAndDiscard(discard);
        }

        // 本局调优状态需要重写时，丢弃 live 但保留 armed。
        // 调用方必须先等这个方法返回（旧 ETW 已停），再写入；
        // 写完后下一个 Confirm 从新证明点起采。
        public void RestartCurrentEpoch()
        {
            InterruptAttribution discard = null;
            lock (gate)
            {
                if (disposed || !armed || gameMaskInvalid
                    || live == null || completed) return;
                string resumeGame = gameName;
                ulong resumeSystem = systemMask;
                discard = InvalidateLocked();
                gameName = resumeGame;
                systemMask = resumeSystem;
                gameMaskInvalid = false;
                armed = EnabledSetting && resumeSystem != 0;
                completed = true;
            }
            StopAndDiscard(discard);
        }

        // 供 RenderLane 这类独立 worker 在真正 setter 前后标记。
        // 若已采集，先作废旧 epoch 并保留本局 armed；写入结束后
        // 下轮才能从新 proof 开始，不把 Pavise 自己的写入算入对局。
        public void BeginExternalMutation()
        {
            InterruptAttribution discard = null;
            lock (gate)
            {
                if (disposed) return;
                externalMutations++;
                if (stopInProgress
                    || (armed && !gameMaskInvalid && live != null && !completed))
                {
                    string resumeGame = gameName;
                    ulong resumeSystem = systemMask;
                    discard = InvalidateLocked();
                    gameName = resumeGame;
                    systemMask = resumeSystem;
                    gameMaskInvalid = false;
                    armed = EnabledSetting && resumeSystem != 0;
                    completed = true;
                }
            }
            StopAndDiscard(discard);
        }

        public void EndExternalMutation()
        {
            lock (gate)
                if (externalMutations > 0) externalMutations--;
        }

        private InterruptAttribution InvalidateLocked()
        {
            generation++;
            InterruptAttribution discard = live;
            live = null;
            startTicks = 0;
            lastProofTicks = 0;
            gameMask = 0;
            systemMask = 0;
            rendererPid = 0;
            rendererCreation = 0;
            bootStamp = "";
            topologyStamp = "";
            armed = false;
            gameMaskInvalid = true;
            completed = true;
            sealedPending = false;
            pendingRecord = null;
            pendingSummary = null;
            pendingTimeline = null;
            pendingTimelineTruncated = false;
            return discard;
        }

        private static void StopAndDiscard(InterruptAttribution ia)
        {
            if (ia == null) return;
            try { ia.Stop(); } catch { }
        }

        public string TakeSummary()
        {
            lock (takeGate)
            {
                Run(true);
                lock (gate)
                {
                    string held = pendingSummary;
                    pendingSummary = null;
                    return held;
                }
            }
        }

        // 首次检测到游戏消失时立刻封存，避免退出宽限期里的系统 DPC 混入。
        // 封存不消费 pending，8 秒后的 ReportFinish 仍可照常取摘要和时间线。
        public void Seal()
        {
            lock (takeGate) Run(false);
        }

        // 取走本局逐事件 DPC 时间线(取走即清) 未采到或非管理员返回 null 调用方据此优雅退回
        //   与 TakeSummary 同源 Run() 已完成后再调是幂等的(completed 后 Run 直接返回不动 pending)
        public System.Collections.Generic.List<InterruptAttribution.DpcTimelineEntry> TakeDpcTimeline(out bool truncated)
        {
            lock (takeGate)
            {
                Run(true);
                lock (gate)
                {
                    var held = pendingTimeline;
                    truncated = pendingTimelineTruncated;
                    pendingTimeline = null;
                    pendingTimelineTruncated = false;
                    return held;
                }
            }
        }

        private string Run(bool commit)
        {
            bool enabled = EnabledSetting;
            InterruptAttribution ia = null;
            InterruptAttribution stale = null;
            long began = 0;
            long ended = 0;
            long epoch = 0;
            string game = "";
            string boot = "";
            string topology = "";
            ulong mask = 0;
            ulong available = 0;
            int pid = 0;
            long creation = 0;
            lock (gate)
            {
                if (live == null || completed)
                    return commit ? CommitPendingLocked() : pendingSummary;
                long now = DateTime.UtcNow.Ticks;
                bool proofFresh = lastProofTicks > 0 && now >= lastProofTicks
                    && now - lastProofTicks <= MaxProofAgeTicks;
                if (disposed || !enabled || !proofFresh)
                    stale = InvalidateLocked();
                else
                {
                    completed = true;
                    armed = false;
                    sealedPending = !commit;
                    ia = live;
                    live = null;
                    stopInProgress = true;
                    began = startTicks;
                    ended = now;
                    epoch = generation;
                    game = gameName;
                    boot = bootStamp;
                    topology = topologyStamp;
                    mask = gameMask;
                    available = systemMask;
                    pid = rendererPid;
                    creation = rendererCreation;
                }
            }
            if (stale != null) { StopAndDiscard(stale); return null; }

            InterruptAttributionResult raw;
            try { raw = ia.Stop(); }
            catch
            {
                lock (gate) stopInProgress = false;
                return null;
            }
            lock (gate) stopInProgress = false;
            System.Collections.Generic.List<InterruptAttribution.DpcTimelineEntry> timeline = null;
            bool timelineTruncated = false;
            try
            {
                timeline = ia.DpcTimeline;
                // 到达内存上限或 ETW 自身丢事件，零命中都不能作为可靠的负证据。
                timelineTruncated = ia.DpcTimelineTruncated
                    || (raw != null && (raw.Lossy || raw.Incomplete));
            }
            catch { }

            lock (gate)
            {
                if (!CaptureStillValidLocked(epoch, mask, available, pid, creation)) return null;
                pendingTimeline = timeline;
                pendingTimelineTruncated = timelineTruncated;
            }
            if (raw == null || raw.Drivers == null || raw.Drivers.Count == 0) return null;
            if (raw.Lossy || raw.Incomplete) return null;

            var rec = new IrqSessionRecord();
            rec.StartUtcTicks = began;
            rec.DurationSeconds = CaptureDurationSeconds(began, ended);
            rec.GameName = game;
            rec.BootStamp = boot;
            rec.TopologyStamp = topology;
            rec.GameMask = mask;
            rec.SystemMask = available;
            foreach (DriverInterrupt d in raw.Drivers)
            {
                if (d == null || d.Dpc <= 0) continue;
                var r = new IrqDriverRecord();
                r.Driver = d.Driver ?? "?";
                r.DriverVersion = DriverVersionOf(r.Driver);
                r.Buckets = d.DpcBuckets;
                r.Dpc = d.Dpc;
                r.DpcTotalNs = (long)(d.DpcTotalUs * 1000.0);
                r.DpcMaxNs = (long)(d.DpcMaxUs * 1000.0);
                r.Over500Us = d.DpcOver500Us;
                r.Over1Ms = d.DpcOver1Ms;
                r.CpuMask = d.CpuMask;
                r.MaskTruncated = d.CpuMaskTruncated;
                rec.Drivers.Add(r);
            }
            if (rec.Drivers.Count == 0) return null;
            string summary = IrqVerdict.SummarizeSession(rec);
            lock (gate)
            {
                // Stop/汇总期间若发生新一局、禁用或失配，旧 epoch 绝不能留下。
                if (!CaptureStillValidLocked(epoch, mask, available, pid, creation)) return null;
                pendingRecord = rec;
                pendingSummary = summary;
                return commit ? CommitPendingLocked() : summary;
            }
        }

        // gate 内调用。Append 自己有独立文件锁，这里持有小范围状态锁
        // 保证 Arm/Invalidate 无法在“已判有效”和“落盘”之间插入新一局。
        private string CommitPendingLocked()
        {
            if (pendingRecord == null)
            {
                sealedPending = false;
                return pendingSummary;
            }
            if (!EnabledSetting || !IrqSessionLedger.Append(pendingRecord))
            {
                sealedPending = false;
                pendingRecord = null;
                pendingTimeline = null;
                pendingTimelineTruncated = false;
                pendingSummary = null;
                return null;
            }
            sealedPending = false;
            pendingRecord = null;
            return pendingSummary;
        }

        internal static int CaptureDurationSeconds(long began, long ended)
        {
            if (began <= 0 || ended <= began) return 0;
            long seconds = (ended - began) / TimeSpan.TicksPerSecond;
            return seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
        }

        private bool CaptureStillValidLocked(
            long epoch, ulong mask, ulong available,
            int pid, long creation)
        {
            return generation == epoch
                && !gameMaskInvalid
                && CanConfirmMask(mask, available, pid, creation)
                && gameMask == mask
                && systemMask == available
                && rendererPid == pid
                && rendererCreation == creation;
        }

        internal static string DriverVersionOf(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return "";
            string v = "";
            try
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string path = System.IO.Path.Combine(System.IO.Path.Combine(sys, "drivers"), moduleName);
                if (!System.IO.File.Exists(path)) path = System.IO.Path.Combine(sys, moduleName);
                if (System.IO.File.Exists(path))
                {
                    var fi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                    string ver = fi.FileVersion ?? "";
                    long stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks / TimeSpan.TicksPerSecond;
                    v = ver.Trim() + "#" + stamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch { v = ""; }
            return v;
        }

        public void Dispose()
        {
            // 正常结束由 ReportFinish 封账；进程退出/异常 Dispose 只丢弃半局，
            // 不把缺少最终落核复核的残片写进历史。
            lock (takeGate)
            {
                InterruptAttribution discard = null;
                try
                {
                    lock (gate)
                    {
                        disposed = true;
                        discard = InvalidateLocked();
                    }
                }
                catch { }
                StopAndDiscard(discard);
            }
        }
    }
}
