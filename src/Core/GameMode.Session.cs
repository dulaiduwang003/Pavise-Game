// @author bdth 2074055628@qq.com
// File purpose Switch sync, arming and sealing of interrupt observation during a match
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

        // The Overview page 'Last session' wants a short one-line summary, not the same thing as the long tray balloon text
        //   the long text carries Pavise's own usage, GPU-bound, VRAM spill and the interrupt ledger, does not fit on one line
        //   only three items here: game name, duration, suppressed process count, the rest still goes only to the log and balloon
        internal const string LastSessionKey = "LastSessionBrief";

        public static string LastSessionBrief
        {
            get { return Settings.LoadStr(LastSessionKey, ""); }
        }

        public event Action<string> SessionBriefed;

        // Retrospective IRQ core move suggestion at match end, runs one verdict over the persisted multi-match measurements only when this match was long enough and observation was on
        //   if any driver is Worth, hand the count to the UI for a highlighted hint, no automatic registry change, the user goes through the manual flow
        public event Action<int> IrqSuggested;

        // Raw record completion and whether there is a core move suggestion are two different things, zero suggestions and failure both need a page refresh
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
            // The user explicitly toggled the observation switch mid-match, that is an explicit request to observe this match, the budget yields
            irqObserveThisSession = true;
            ArmIrqObservation(game);
            if (IrqSessionProbe.EnabledSetting)
                lock (sync) repIrqRequested = true;
            if (irqProbe.RequiresPlacementAudit)
            {
                // On explicit mid-match enable, make the normal core placement cache go through strict proof initialization again
                // A mere sampling failure does not come through here, no forced retry or affinity rewrite every round
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

        // Whether to observe this match is decided once at match start, every re-arm point in the match shares that decision, no mid-match reversal
        //   grace recovery and seal-reopen also go through ArmIrqObservation, re-arming in a skipped match skips as well
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
            PolicySnapshot irqPolicy = sessionPolicy;
            irqProbe.SetContext(irqPolicy == null ? "" : irqPolicy.ProfileId,
                IrqAdjustmentLedger.ConfigurationOf(irqPolicy));
            NotifyIrqObservationChanged(false);
        }

        private void ObserveSystemIrq(int rendererPid, long rendererCreation)
        {
            if (!IrqSessionProbe.EnabledSetting)
            {
                if (irqProbe.IsCapturing) irqProbe.InvalidateGameMask();
                return;
            }
            // This match was skipped by the observation budget, the per-tick fallback re-arm must not pick it back up
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
                // Exit the temporary hard core pinning set up just for strict IRQ proof, normal game tuning stays as-is
                // Handles that failed to restore are still followed up by the existing recovery path, read-only system observation is not blocked
                RestoreAllIrqProofHardPins(false);
            }
            if (!irqProbe.CanObserveSystemNow) return;
            // System observation requests no SET rights, let alone changes game affinity just to get a measurement
            // If the pid+creation read-back fails, do not keep sampling with a stale identity
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
            // System observation yields no core move suggestion, no extra PRESENT capture needed, strict observation must also have
            // a DPC window first, old present is dropped on epoch change, no alignment across windows
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
            // Teardown order is the reverse of startup, stop present first then DPC, keep the present
            // instance so CollectLongFrames can read it after the grace period ends, without recording the desktop further
            PresentProbe p = presentProbe;
            if (p != null) { try { p.RequestStop(); } catch { } }
            irqProbe.Seal();
        }
    }
}
