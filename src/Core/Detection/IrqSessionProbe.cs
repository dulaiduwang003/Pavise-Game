// @author bdth 2074055628@qq.com
// File purpose Lifecycle of in-match interrupt sampling: arming, state, and proof comparison
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
        private long captureStartQpc, captureEndQpc;
        internal bool FrameWindow(out long begin, out long end)
        {
            lock (gate)
            { begin = captureStartQpc; end = captureEndQpc; return begin > 0 && end > begin && !gameMaskInvalid; }
        }
        private long lastProofTicks;
        private string gameName = "";
        private string stableGameId = "", configuration = "";
        private IrqFrameEvidence frameEvidence;
        private Dictionary<string,string> deviceConfigurations = new Dictionary<string,string>();

        internal void SetContext(string gameId, string fingerprint)
        {
            lock (gate)
            { stableGameId = gameId ?? ""; configuration = fingerprint ?? ""; frameEvidence = null; }
        }

        internal void SetFrameEvidence(IrqFrameEvidence evidence)
        {
            lock (gate)
            {
                frameEvidence = evidence;
                if (pendingRecord != null) pendingRecord.Frames = evidence;
            }
        }
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
        // Seal only stops ETW and stashes; it must wait for the exit grace period to truly end
        // before TakeSummary commits; if the same game recovers within the grace period, Arm discards it
        private IrqSessionRecord pendingRecord;
        private string pendingSummary;
        // Previous match's per-event DPC timeline, for GameMode to take for present alignment; taking clears it
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

        // Match start only arms; system observation only needs the renderer identity confirmed and writes no game affinity
        // Core-domain attribution still requires a complete placement proof; the two kinds of evidence must not be mixed into one record
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
                stableGameId = configuration = ""; frameEvidence = null;
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

        // Strict core domain is the evidence requirement for suggestions, not a prerequisite for recording a match; initialization gets a few
        // sweep chances, and if capture still has not started or the proof has lapsed, this match downgrades one-way to system observation
        // ETW or permission failure is not a placement failure and must not be used to re-request a session every round
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
                InvalidateLocked(); // The conditions above guarantee no live; the old core-domain timeline must not be carried into the new window
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

        // Verification must continue after armed and throughout capture; a mismatch during capture permanently voids the whole match sample
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
