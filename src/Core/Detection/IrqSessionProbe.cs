// @author bdth 2074055628@qq.com
// 文件用途 对局期常驻中断观测 打完一局落盘一段 供中断页按真实数据给建议
using System;

namespace PaviseApp
{
    internal sealed class IrqSessionProbe : IDisposable
    {
        internal const string EnabledKey = "IrqSessionProbe";
        internal const int PlacementInitializationScans = 3;
        private const long MaxProofAgeTicks = 1500L * TimeSpan.TicksPerMillisecond;

        private readonly object gate = new object();
        private readonly object takeGate = new object();
        private readonly IIrqSessionPlatform platform;
        private IIrqSessionCapture live;
        private IrqCoreLoadCapture coreLoads;
        private bool systemObservation;
        private string statusKey = "";
        private string statusDetail = "";
        private int statusSeconds;
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
        private int placementWaitScans;
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

        public IrqSessionProbe() : this(new WindowsIrqSessionPlatform()) { }

        internal IrqSessionProbe(IIrqSessionPlatform platform)
        {
            if (platform == null) throw new ArgumentNullException("platform");
            this.platform = platform;
            try
            {
                string[] fields = (platform.LoadLastResult() ?? "").Split('|');
                int seconds;
                if (fields.Length == 3 && IsTerminalStatus(fields[0])
                    && int.TryParse(fields[1], out seconds) && seconds >= 0)
                {
                    statusKey = fields[0];
                    statusSeconds = seconds;
                    statusDetail = System.Text.Encoding.UTF8.GetString(
                        Convert.FromBase64String(fields[2]));
                }
            }
            catch { }
        }

        public string StatusText
        {
            get { lock (gate) return StatusTextLocked(); }
        }

        public bool StatusWarning
        {
            get
            {
                lock (gate)
                    return statusKey.Length > 0 && statusKey != "waiting"
                        && statusKey != "system" && statusKey != "placed"
                        && statusKey != "saved.system" && statusKey != "saved.placed"
                        && statusKey != "disabled";
            }
        }

        private string StatusTextLocked()
        {
            if (statusKey.Length == 0) return "";
            return statusKey == "saved.system" || statusKey == "saved.placed"
                ? Lang.F("irq.capture." + statusKey, statusSeconds)
                : Lang.F("irq.capture." + statusKey, statusDetail);
        }

        private static bool IsTerminalStatus(string key)
        {
            return key == "saved.system" || key == "saved.placed"
                || key == "needadmin" || key == "startfailed"
                || key == "invalidated" || key == "nodata" || key == "lost"
                || key == "incomplete" || key == "savefailed" || key == "unavailable";
        }

        private void SetStatusLocked(string key, string detail, int seconds)
        {
            detail = detail ?? "";
            if (statusKey == key && statusDetail == detail && statusSeconds == seconds) return;
            statusKey = key;
            statusDetail = detail;
            statusSeconds = seconds;
            try { platform.Log("IRQ " + StatusTextLocked()); } catch { }
            if (!IsTerminalStatus(key)) return;
            try
            {
                platform.SaveLastResult(key + "|" + seconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + "|"
                    + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(detail)));
            }
            catch { }
        }

        public static bool EnabledSetting
        {
            get { return Settings.Load(EnabledKey, false); }
            set { Settings.Save(EnabledKey, value); }
        }

        private bool warnedNoAdmin;

        // 开局只布防。系统观测只需确认 renderer 身份，不写游戏亲和性；
        // 核域归因仍必须有完整的落核证明，两种证据不能混成一条记录。
        public void Arm(string game, ulong availableSystemMask)
        {
            Arm(game, availableSystemMask, false);
        }

        public void Arm(string game, ulong availableSystemMask, bool observeSystem)
        {
            IIrqSessionCapture stale;
            bool enabled = platform.Enabled && availableSystemMask != 0;
            lock (gate)
            {
                if (disposed) return;
                stale = InvalidateLocked();
                gameName = game ?? "";
                systemMask = availableSystemMask;
                systemObservation = observeSystem;
                placementWaitScans = 0;
                gameMaskInvalid = false;
                armed = enabled;
                completed = true;
                SetStatusLocked(enabled ? "waiting" : "disabled", "", 0);
            }
            StopAndDiscard(stale);
        }

        // 严格核域是建议的证据要求，不是记录一局的前提。给初始化有限几轮
        // 扫描机会；仍未起采或证明已失效时，本局单向退为系统观测。
        // ETW/权限失败不是落核失败，不能借此每轮重新申请会话。
        public bool TryFallbackToSystemObservation(string game, ulong availableSystemMask)
        {
            lock (gate)
            {
                if (disposed || systemObservation || !platform.Enabled
                    || availableSystemMask == 0 || sealedPending || stopInProgress
                    || externalMutations > 0 || live != null || pendingRecord != null) return false;
                if (statusKey != "waiting" && statusKey != "invalidated") return false;
                if (statusKey == "waiting"
                    && ++placementWaitScans < PlacementInitializationScans) return false;
                InvalidateLocked(); // 上述条件保证无 live；不能把旧核域时间线带进新窗口。
                gameName = game ?? "";
                systemMask = availableSystemMask;
                systemObservation = true;
                gameMaskInvalid = false;
                armed = true;
                completed = true;
                SetStatusLocked("waiting", "", 0);
                try { platform.Log(Lang.T("irq.capture.fallback")); } catch { }
                return true;
            }
        }

        // armed 后以及采集中都要持续核验；采集中一旦失配，整局样本永久作废。
        public bool RequiresPlacementAudit
        {
            get { lock (gate) return !disposed && armed && !gameMaskInvalid && !systemObservation; }
        }

        public bool IsSystemObservation
        {
            get { lock (gate) return !disposed && systemObservation; }
        }

        public bool CanObserveSystemNow
        {
            get { lock (gate) return !disposed && systemObservation && armed && !gameMaskInvalid; }
        }

        public long CaptureEpoch { get { lock (gate) return generation; } }

        public bool IsPlacementCapturing
        {
            get { lock (gate) return !disposed && armed && !gameMaskInvalid
                && !systemObservation && live != null && !completed; }
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
            return ConfirmCapture(verifiedMask, verifiedRendererPid, verifiedRendererCreation, false);
        }

        public bool ConfirmSystemObservation(int verifiedRendererPid, long verifiedRendererCreation)
        {
            // 0 表示只观测系统中断，没有证明游戏的实际核域。
            return ConfirmCapture(0, verifiedRendererPid, verifiedRendererCreation, true);
        }

        internal static bool CanObserveSystem(ulong availableSystemMask, int pid, long creation)
        {
            return availableSystemMask != 0 && pid > 0 && creation > 0;
        }

        private bool ConfirmCapture(
            ulong verifiedMask, int verifiedRendererPid,
            long verifiedRendererCreation, bool observeSystem)
        {
            bool enabled = platform.Enabled;
            bool accepted = false;
            bool logStarted = false;
            bool logStartFailed = false;
            bool logNoAdmin = false;
            IIrqSessionCapture discard = null;
            IIrqSessionCapture failedStart = null;
            lock (gate)
            {
                if (disposed || !armed || gameMaskInvalid || systemObservation != observeSystem) return false;
                // RenderLane 等异步调优若正在写 renderer，起采必须
                // 等它离开写区；该计数和开采在同一 gate 下，没有
                // “回调刚查完、ETW 就开了、setter 才落下”的窗口。
                if (!observeSystem && externalMutations > 0) return false;
                bool identityValid = observeSystem
                    ? verifiedMask == 0 && CanObserveSystem(systemMask,
                        verifiedRendererPid, verifiedRendererCreation)
                    : CanConfirmMask(verifiedMask, systemMask,
                        verifiedRendererPid, verifiedRendererCreation);
                if (!enabled || !identityValid)
                {
                    discard = InvalidateLocked();
                    SetStatusLocked(enabled ? "unavailable" : "disabled", "", 0);
                }
                else if (live != null)
                {
                    if (!completed
                        && gameMask == verifiedMask
                        && rendererPid == verifiedRendererPid
                        && rendererCreation == verifiedRendererCreation)
                    {
                        long now = platform.UtcTicks;
                        bool continuous = lastProofTicks > 0
                            && now >= lastProofTicks
                            && now - lastProofTicks <= MaxProofAgeTicks;
                        if (continuous)
                        {
                            lastProofTicks = now;
                            if (coreLoads != null) coreLoads.Poll(now);
                            placementWaitScans = 0;
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
                            systemObservation = observeSystem;
                            gameMaskInvalid = false;
                            armed = enabled && resumeSystem != 0;
                            completed = true;
                            SetStatusLocked("waiting", "", 0);
                        }
                    }
                    else
                    {
                        discard = InvalidateLocked();
                        SetStatusLocked("invalidated", "", 0);
                    }
                }
                else if (!platform.IsElevated)
                {
                    if (!warnedNoAdmin) { warnedNoAdmin = true; logNoAdmin = true; }
                    discard = InvalidateLocked();
                    SetStatusLocked("needadmin", "", 0);
                }
                else
                {
                    IIrqSessionCapture ia = platform.CreateCapture(!observeSystem);
                    bool started = false;
                    try { started = ia.Start(); } catch { }
                    if (!started)
                    {
                        failedStart = ia;
                        logStartFailed = !ia.Busy;
                        discard = InvalidateLocked();
                        SetStatusLocked("startfailed", ia.Busy
                            ? Lang.T("irq.probe.busy") : ia.FailDetail, 0);
                    }
                    else
                    {
                        live = ia;
                        gameMask = verifiedMask;
                        rendererPid = verifiedRendererPid;
                        rendererCreation = verifiedRendererCreation;
                        startTicks = platform.UtcTicks;
                        lastProofTicks = startTicks;
                        bootStamp = platform.BootStamp;
                        topologyStamp = platform.TopologyStamp;
                        try { coreLoads = new IrqCoreLoadCapture(platform.OpenCoreLoadSource(), startTicks, systemMask); }
                        catch { coreLoads = null; }
                        completed = false;
                        placementWaitScans = 0;
                        accepted = true;
                        logStarted = true;
                        SetStatusLocked(observeSystem ? "system" : "placed", "", 0);
                    }
                }
            }
            StopAndDiscard(discard);
            StopAndDiscard(failedStart);
            if (logNoAdmin) platform.Log(Lang.T("log.irqsession.4"));
            if (logStartFailed) platform.Log(Lang.T("log.irqsession.1"));
            if (logStarted) platform.Log(Lang.T("log.irqsession.2"));
            return accepted;
        }

        public void InvalidateGameMask()
        {
            IIrqSessionCapture discard;
            lock (gate)
            {
                bool hadCapture = armed || live != null || pendingRecord != null;
                discard = InvalidateLocked();
                if (hadCapture) SetStatusLocked(platform.Enabled ? "invalidated" : "disabled", "", 0);
            }
            StopAndDiscard(discard);
        }

        // 本局调优状态需要重写时，丢弃 live 但保留 armed。
        // 调用方必须先等这个方法返回（旧 ETW 已停），再写入；
        // 写完后下一个 Confirm 从新证明点起采。
        public void RestartCurrentEpoch()
        {
            IIrqSessionCapture discard = null;
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
                armed = platform.Enabled && resumeSystem != 0;
                completed = true;
                SetStatusLocked("waiting", "", 0);
            }
            StopAndDiscard(discard);
        }

        // 供 RenderLane 这类独立 worker 在真正 setter 前后标记。
        // 若已采集，先作废旧 epoch 并保留本局 armed；写入结束后
        // 下轮才能从新 proof 开始，不把 Pavise 自己的写入算入对局。
        public void BeginExternalMutation()
        {
            IIrqSessionCapture discard = null;
            lock (gate)
            {
                if (disposed) return;
                externalMutations++;
                // 系统观测记录真实整机 DPC，本来就包括正常后台活动；
                // 不宣称游戏核归因，因此新进程压制等写入不应把整局反复打碎。
                // 严格核域证据仍必须排除这些写入造成的观测污染。
                if (!systemObservation && (stopInProgress
                    || (armed && !gameMaskInvalid && live != null && !completed)))
                {
                    string resumeGame = gameName;
                    ulong resumeSystem = systemMask;
                    discard = InvalidateLocked();
                    gameName = resumeGame;
                    systemMask = resumeSystem;
                    gameMaskInvalid = false;
                    armed = platform.Enabled && resumeSystem != 0;
                    completed = true;
                    SetStatusLocked("waiting", "", 0);
                }
            }
            StopAndDiscard(discard);
        }

        public void EndExternalMutation()
        {
            lock (gate)
                if (externalMutations > 0) externalMutations--;
        }

        private IIrqSessionCapture InvalidateLocked()
        {
            if (coreLoads != null) coreLoads.Dispose();
            coreLoads = null;
            generation++;
            IIrqSessionCapture discard = live;
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

        private static void StopAndDiscard(IIrqSessionCapture ia)
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
            var loadRecord = new IrqSessionRecord();
            bool enabled = platform.Enabled;
            IIrqSessionCapture ia = null;
            IIrqSessionCapture stale = null;
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
                {
                    if (commit && armed && pendingRecord == null)
                    {
                        armed = false;
                        SetStatusLocked(enabled ? "unavailable" : "disabled", "", 0);
                    }
                    return commit ? CommitPendingLocked() : pendingSummary;
                }
                long now = platform.UtcTicks;
                bool proofFresh = lastProofTicks > 0 && now >= lastProofTicks
                    && now - lastProofTicks <= MaxProofAgeTicks;
                if (disposed || !enabled || !proofFresh)
                {
                    stale = InvalidateLocked();
                    SetStatusLocked(enabled ? "invalidated" : "disabled", "", 0);
                }
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
                    // Freeze CPU deltas before ETW Stop, restoration, or the exit grace.
                    if (coreLoads != null) coreLoads.Finish(now, loadRecord);
                    coreLoads = null;
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
                lock (gate)
                {
                    stopInProgress = false;
                    if (generation == epoch) SetStatusLocked("incomplete", "", 0);
                }
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
            if (raw == null || raw.Lossy || raw.Incomplete || raw.Drivers.Count == 0)
            {
                lock (gate)
                {
                    if (!CaptureStillValidLocked(epoch, mask, available, pid, creation)) return null;
                    if (raw != null && raw.Lossy)
                        SetStatusLocked("lost", "events=" + raw.EventsLost
                            + ", buffers=" + raw.BuffersLost, 0);
                    else if (raw != null && raw.Incomplete)
                        SetStatusLocked("incomplete", "", 0);
                    else SetStatusLocked("nodata", raw == null ? "" : raw.Error, 0);
                }
                return null;
            }

            var rec = new IrqSessionRecord();
            rec.StartUtcTicks = began;
            rec.DurationSeconds = CaptureDurationSeconds(began, ended);
            rec.GameName = game;
            rec.BootStamp = boot;
            rec.TopologyStamp = topology;
            rec.GameMask = mask;
            rec.SystemMask = available;
            rec.CoreLoadWindowTicks = loadRecord.CoreLoadWindowTicks;
            rec.CoreLoads.AddRange(loadRecord.CoreLoads);
            foreach (DriverInterrupt d in raw.Drivers)
            {
                if (d == null || d.Dpc <= 0) continue;
                var r = new IrqDriverRecord();
                r.Driver = d.Driver ?? "?";
                r.DriverVersion = platform.DriverVersion(r.Driver);
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
            if (rec.Drivers.Count == 0)
            {
                lock (gate)
                    if (CaptureStillValidLocked(epoch, mask, available, pid, creation))
                        SetStatusLocked("nodata", raw.Error, 0);
                return null;
            }
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
            bool enabled = platform.Enabled;
            bool saved = false;
            if (enabled)
                try { saved = platform.Append(pendingRecord); } catch { }
            if (!saved)
            {
                sealedPending = false;
                pendingRecord = null;
                pendingTimeline = null;
                pendingTimelineTruncated = false;
                pendingSummary = null;
                SetStatusLocked(enabled ? "savefailed" : "disabled", "", 0);
                return null;
            }
            SetStatusLocked(pendingRecord.GameMask == 0 ? "saved.system" : "saved.placed",
                "", pendingRecord.DurationSeconds);
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
                && (systemObservation
                    ? mask == 0 && CanObserveSystem(available, pid, creation)
                    : CanConfirmMask(mask, available, pid, creation))
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
                IIrqSessionCapture discard = null;
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
