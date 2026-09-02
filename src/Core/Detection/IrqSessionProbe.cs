// @author bdth 2074055628@qq.com
// 文件用途 对局期中断采样的生命周期 布防 状态与证明比对
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed partial class IrqSessionProbe : IDisposable
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
        // Seal 只停 ETW 并暂存 必须等退出宽限真正结束
        // 才由 TakeSummary 提交 同一游戏在宽限内恢复时 Arm 会丢弃它
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

        // 开局只布防 系统观测只需确认 renderer 身份 不写游戏亲和性
        // 核域归因仍必须有完整的落核证明 两种证据不能混成一条记录
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

        // 严格核域是建议的证据要求 不是记录一局的前提 给初始化有限几轮
        // 扫描机会 仍未起采或证明已失效时 本局单向退为系统观测
        // ETW/权限失败不是落核失败 不能借此每轮重新申请会话
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

        // armed 后以及采集中都要持续核验 采集中一旦失配 整局样本永久作废
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
    }
}
