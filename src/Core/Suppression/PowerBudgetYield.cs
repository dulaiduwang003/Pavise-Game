// @author bdth 2074055628@qq.com
// File purpose Laptop in-match yield of the shared power budget from CPU to GPU, verified after each yield, at most three per match
using System;
using System.Threading;

namespace PaviseApp
{
    internal enum YieldStage { Idle = 0, Observing = 1, Engaged = 2, Held = 3, Skipped = 4, Reverted = 5 }

    internal enum YieldAction { None = 0, Engage = 1, Keep = 2, Revert = 3, Release = 4 }

    // Verdict only routes logging; Inconclusive does not trip the breaker, next match can retry
    internal enum YieldVerdict { None = 0, Kept = 1, NoGain = 2, GpuHarm = 3, Inconclusive = 4 }

    // On laptops CPU and GPU share one power and thermal budget; when GPU-bound, every watt the CPU burns is a watt the GPU loses
    //   Firmware Dynamic Boost / DTT / SmartShift already does this at millisecond scale; we don't fight it for the wheel
    //   Every yield is re-observed and re-verified; revert on weak evidence, persist-disable only on consecutive confirmed negative results
    //
    // Why touch EPP instead of ProcThrottleMin
    //   ProcThrottleMin only decides whether it can drop; EPP decides whether it wants to
    //   With the floor open but EPP still 0 it stays strongly performance-biased and saves little, so EPP is the real lever
    //
    // Why watt-based verification comes first
    //   Verification must answer "was budget actually freed"; GPU wall-hit rate alone can lie both ways
    //   GPU may still hit the wall with a higher cap, and squeezing the CPU into the bottleneck drops GPU utilization and the hit rate with it
    //   Looks like success, actually failure, so without a power reading the frequency proxy evidence below is required
    //
    // Frequency proxy: degraded verification for machines with no watt reading
    //   EPP frees power through frequency: with EPP raised, under partial load the CPU is willing to run lower clocks, and power follows frequency cubed
    //   So whether frequency dropped is second-tier evidence for whether budget was yielded; frequency unchanged = EPP is a dead lever, tripping is correct
    //   Degraded verification has one more pitfall: frequency also depends on load, so if CPU utilization differs too much between verify and observe windows there is no comparison
    //   That case reverts without tripping; the scenario changed, not the machine's fault, retry next match
    //   The frequency-proxy breaker is tracked separately; on a machine with EMI or after the EMI driver is fixed the watt path is unaffected
    internal sealed class PowerBudgetYield
    {
        // Local bench result: i7-9750H Coffee Lake-H laptop, 6-thread 40% duty-cycle load
        //   EPP range 0~100, each step written and read back matched, so the writes did land
        //   EPP steps 0 32 48 84 100
        //   Package power 47.57 47.02 49.01 48.54 47.40 W, no trend, +/-1.5W is noise
        //   Actual frequency 153.8 153.8 153.8 153.9 153.8 %, flat across the whole range
        //   Meaning OS-side EPP never reaches the hardware on this machine; the lever is dead
        // So this feature is off by default, and on such machines verification necessarily fails, reverts and trips; that is correct behavior
        //   A 2019 Coffee Lake-H doesn't represent everything; HWP platforms from Alder Lake on need re-measuring
        //   Also this load is a duty-cycle pulse, not necessarily a good probe for EPP; worth re-testing with steady partial load
        internal const double MinGpuUtilToYield = 90.0;
        internal const double MaxCpuUtilToYield = 65.0;
        internal const long ObserveTicks = TimeSpan.TicksPerSecond * 20;
        internal const long VerifyTicks = TimeSpan.TicksPerSecond * 15;
        internal const uint YieldEpp = 48;          // 0 most performance-biased, 255 most efficiency-biased, 48 still leans performance
        internal const double MinWattsFreed = 3.0;
        internal const double MaxGpuUtilDrop = 3.0;
        internal const int MinSamples = 4;
        // Frequency proxy criteria: relative drop of 3% counts, load drift over 10 percentage points means no verdict
        internal const double MinFreqDropShare = 0.03;
        internal const double MaxCpuUtilShift = 10.0;

        // Steering criteria: keep watching after verification passes, hand budget back when the bottleneck moves back to CPU
        //   Release and entry thresholds form a hysteresis band, yield at 90 release at 80, to avoid flapping at the boundary
        //   After handing back, the GPU saturating again allows a re-yield, which must go through full observation plus verification; that is natural cooldown
        internal const double ReleaseGpuUtil = 80.0;
        internal const double ReleaseCpuUtil = 75.0;
        internal const long HoldWindowTicks = TimeSpan.TicksPerSecond * 30;
        internal const int MaxReengage = 3;

        private const string FuseKey = "PowerYieldFuse";
        private const string FreqFuseKey = "PowerYieldFreqFuse";

        // Hard entry gates, all required; a wrong call costs the match, better to do nothing
        internal static bool Eligible(bool laptop, bool onAc, bool competitive,
            bool managedPlanActive, bool wattsReadable, bool fused)
        {
            if (fused) return false;               // this machine has been proven not to respond
            if (!laptop) return false;             // desktops have no shared budget to yield
            if (!onAc) return false;               // on battery the user wants explicit battery life or performance, stay out
            if (!competitive) return false;        // only the Esports tier writes the most aggressive CPU-side settings
            if (!managedPlanActive) return false;  // don't touch a power scheme the user chose
            if (!wattsReadable) return false;      // can't verify, don't do it
            return true;
        }

        // Only GPU saturated with CPU headroom shows the budget is spent in the wrong place
        internal static bool WorthYielding(double gpuUtil, double cpuUtil)
        {
            return ValidUtil(gpuUtil) && ValidUtil(cpuUtil)
                && gpuUtil >= MinGpuUtilToYield && cpuUtil <= MaxCpuUtilToYield;
        }

        // After yielding both must hold: package power actually dropped and the GPU wasn't dragged down
        internal static bool VerifyHold(double pkgBefore, double pkgAfter,
            double gpuBefore, double gpuAfter)
        {
            if (!ValidPositive(pkgBefore) || !ValidPositive(pkgAfter)
                || !ValidUtil(gpuBefore) || !ValidUtil(gpuAfter)) return false;
            if (pkgBefore - pkgAfter < MinWattsFreed) return false;
            if (gpuBefore - gpuAfter > MaxGpuUtilDrop) return false;
            return true;
        }

        public static bool Fused
        {
            get { return Settings.Load(FuseKey, false); }
        }

        public static bool FreqFused
        {
            get { return Settings.Load(FreqFuseKey, false); }
        }

        public static void ClearFuse()
        {
            if (Fused) Settings.Save(FuseKey, false);
            if (FreqFused) Settings.Save(FreqFuseKey, false);
        }

        // Samples with no watt reading must not count in the denominator, or the package power mean is diluted low
        //   A low baseline makes the drop VerifyHold sees too small, judging a machine that does gain as no-gain and tripping
        internal const int GiveUpSampleMultiple = 3;
        // Driver counters can briefly go missing during loading/screen switches; no longer end the match after ten seconds
        // Once EPP has been changed wait at most one minute; hand the budget back only if still not recovered
        internal const long EvidenceMaxAgeTicks = 60 * TimeSpan.TicksPerSecond;
        // Normal two-second sampling tolerates scheduling jitter; longer callback gaps must not feed a persistent hardware no-gain verdict
        internal const long EvidenceContinuityMaxGapTicks = 4 * TimeSpan.TicksPerSecond;
        private long lastEvidenceAt;
        private long lastAdvanceAt;
        private bool awaitingObservationData;
        private bool observationInterrupted;
        private bool verificationInterrupted;

        private YieldStage stage = YieldStage.Idle;
        private long stageAt;
        private bool proxyMode;
        private int samples;
        private int pkgSamples;
        private int freqSamples;
        private double gpuSum, cpuSum, pkgSum, freqSum;
        private double baseGpu, basePkg, baseCpu, baseFreq;
        private YieldVerdict verdict;
        private int engagements;

        public YieldStage Stage { get { return stage; } }
        public YieldVerdict Verdict { get { return verdict; } }
        public double BaselineWatts { get { return basePkg; } }
        public double BaselineGpuUtil { get { return baseGpu; } }

        public void Begin(long now, bool eligible)
        {
            Begin(now, eligible, false);
        }

        public void Begin(long now, bool eligible, bool proxy)
        {
            Reset();
            proxyMode = proxy;
            stage = eligible ? YieldStage.Observing : YieldStage.Skipped;
            stageAt = now;
            lastEvidenceAt = now;
            lastAdvanceAt = now;
        }

        public void End() { Reset(); }

        private void Reset()
        {
            lastEvidenceAt = 0;
            lastAdvanceAt = 0;
            awaitingObservationData = false;
            observationInterrupted = false;
            verificationInterrupted = false;
            stage = YieldStage.Idle; stageAt = 0; samples = 0; pkgSamples = 0; freqSamples = 0;
            gpuSum = cpuSum = pkgSum = freqSum = 0; baseGpu = basePkg = baseCpu = baseFreq = 0;
            proxyMode = false; verdict = YieldVerdict.None; engagements = 0;
        }

        private void EnterHold(long now)
        {
            stage = YieldStage.Held;
            verdict = YieldVerdict.Kept;
            stageAt = now;
            samples = 0; gpuSum = cpuSum = 0;
        }

        private void RestartObservationWindow(long now)
        {
            stageAt = lastEvidenceAt = now;
            awaitingObservationData = true;
            observationInterrupted = false;
            samples = pkgSamples = freqSamples = 0;
            gpuSum = cpuSum = pkgSum = freqSum = 0;
        }

        private YieldAction RevertInconclusive()
        {
            stage = YieldStage.Reverted;
            verdict = YieldVerdict.Inconclusive;
            return YieldAction.Revert;
        }

        private YieldAction RevertNegativeVerdict(YieldVerdict negativeVerdict, string fuseKey)
        {
            // Load before and after the gap may belong to different scenarios; revert EPP but this can't declare the machine ineffective
            if (verificationInterrupted) return RevertInconclusive();
            stage = YieldStage.Reverted;
            verdict = negativeVerdict;
            Settings.Save(fuseKey, true);
            return YieldAction.Revert;
        }

        // Feed one sample and return what to do now; negative watts or frequency mean not read this time, just excluded from the mean
        public YieldAction Advance(long now, double gpuUtil, double cpuUtil, double pkgWatts)
        {
            return Advance(now, gpuUtil, cpuUtil, pkgWatts, -1);
        }

        public YieldAction Advance(long now, double gpuUtil, double cpuUtil,
            double pkgWatts, double freqPct)
        {
            if (stage != YieldStage.Observing && stage != YieldStage.Engaged
                && stage != YieldStage.Held) return YieldAction.None;
            bool validLoad = ValidUtil(gpuUtil) && ValidUtil(cpuUtil);
            bool validMeter = ValidPositive(proxyMode ? freqPct : pkgWatts);
            bool validEvidence = validLoad && (stage == YieldStage.Held || validMeter);
            bool timeReversed = now < lastAdvanceAt;
            long sampleGap = now - lastAdvanceAt;
            lastAdvanceAt = now;
            // Check the evidence gap before taking the new sample, so one late reading can't mask a long outage
            // In observation the power plan is untouched, so a full window can be re-awaited; a late-loading game doesn't forfeit the match
            if (timeReversed || now < lastEvidenceAt || now - lastEvidenceAt >= EvidenceMaxAgeTicks)
            {
                if (stage == YieldStage.Observing) RestartObservationWindow(now);
                else return RevertInconclusive();
            }
            // Waiting for counters to recover may take a minute, but old samples spanning an interrupted verification can't feed a hardware trip
            // Sporadic readings must not keep extending an unverified state where EPP is already changed
            if (stage == YieldStage.Engaged
                && (now - stageAt >= EvidenceMaxAgeTicks
                    || validEvidence && now - lastEvidenceAt >= VerifyTicks))
                return RevertInconclusive();
            if (stage == YieldStage.Engaged
                && (!validEvidence || sampleGap > EvidenceContinuityMaxGapTicks))
                verificationInterrupted = true;
            // Observation doesn't touch power; when the evidence gap exceeds one observe window drop the old scenario and wait for a full new one
            if (stage == YieldStage.Observing && now - lastEvidenceAt >= ObserveTicks)
                RestartObservationWindow(now);
            if (stage == YieldStage.Observing && validEvidence
                && (awaitingObservationData || samples == 0 && now - stageAt >= ObserveTicks))
            {
                RestartObservationWindow(now);
                awaitingObservationData = false;
            }
            // A gap after the baseline already has evidence means a negative verdict can't count as a hardware verdict
            // A sparse baseline can still try yielding; the discontinuity flag clears only after a full fresh observe window
            if (stage == YieldStage.Observing && samples > 0
                && (!validEvidence || sampleGap > EvidenceContinuityMaxGapTicks))
                observationInterrupted = true;
            // Held stage no longer verifies hardware gain; after sampling resumes open a new rolling window, never mix in pre-gap load
            if (stage == YieldStage.Held && validLoad && now - lastEvidenceAt >= HoldWindowTicks)
            {
                stageAt = now;
                samples = 0; gpuSum = cpuSum = 0;
            }
            if (validEvidence) lastEvidenceAt = now;
            if (!validLoad) return YieldAction.None;
            // Baseline uses only fully paired load and meter; sporadic valid meter readings keep accumulating, missing values are not zero
            if (stage == YieldStage.Observing && !validMeter)
                return YieldAction.None;
            // Steering Held stage: after yielding keep watching a 30-second rolling window
            //   GPU still saturated with CPU headroom means hold; bottleneck back on CPU means hand the budget back
            if (stage == YieldStage.Held)
            {
                samples++;
                gpuSum += gpuUtil; cpuSum += cpuUtil;
                if (now - stageAt < HoldWindowTicks || samples < MinSamples) return YieldAction.None;
                double gpuAvg = gpuSum / samples, cpuAvg = cpuSum / samples;
                samples = 0; gpuSum = cpuSum = 0; stageAt = now;
                if (gpuAvg >= ReleaseGpuUtil && cpuAvg <= ReleaseCpuUtil) return YieldAction.None;
                // Hand it back; if engagements remain, return to observing, GPU saturating again allows a re-yield
                //   A re-yield must redo full observation and verification; that is natural oscillation cooldown
                if (engagements >= MaxReengage)
                {
                    stage = YieldStage.Skipped;
                    return YieldAction.Release;
                }
                stage = YieldStage.Observing;
                observationInterrupted = false;
                pkgSamples = freqSamples = 0; pkgSum = freqSum = 0;
                return YieldAction.Release;
            }
            if (stage != YieldStage.Observing && stage != YieldStage.Engaged) return YieldAction.None;
            samples++;
            gpuSum += gpuUtil; cpuSum += cpuUtil;
            if (ValidPositive(pkgWatts)) { pkgSum += pkgWatts; pkgSamples++; }
            if (ValidPositive(freqPct)) { freqSum += freqPct; freqSamples++; }
            int meterSamples = proxyMode ? freqSamples : pkgSamples;

            long span = now - stageAt;
            if (stage == YieldStage.Observing)
            {
                if (span < ObserveTicks || samples < MinSamples) return YieldAction.None;
                double gpu = gpuSum / samples, cpu = cpuSum / samples;
                double meter = proxyMode ? freqSum / freqSamples : pkgSum / pkgSamples;
                if (!WorthYielding(gpu, cpu) || meter <= 0)
                {
                    stage = YieldStage.Skipped;
                    return YieldAction.None;
                }
                baseGpu = gpu; baseCpu = cpu;
                if (proxyMode) baseFreq = meter; else basePkg = meter;
                stage = YieldStage.Engaged; stageAt = now;
                lastEvidenceAt = now;
                verificationInterrupted = observationInterrupted;
                engagements++;
                samples = 0; pkgSamples = 0; freqSamples = 0;
                gpuSum = cpuSum = pkgSum = freqSum = 0;
                return YieldAction.Engage;
            }

            if (span < VerifyTicks || samples < MinSamples) return YieldAction.None;
            // Same in verification: no evidence means no verdict, but EPP is already changed here, so too few samples must revert instead of idling
            if (meterSamples < MinSamples)
            {
                if (samples < MinSamples * GiveUpSampleMultiple) return YieldAction.None;
                stage = YieldStage.Reverted;
                verdict = YieldVerdict.Inconclusive;
                return YieldAction.Revert;
            }
            double gpuNow = gpuSum / samples;
            if (!proxyMode)
            {
                // The watt path gets the same load-drift guard; when the verify window lands on a cutscene or loading screen
                //   the natural power drop is misread as "not yielded"; steering allows up to three verify windows per match
                //   so false-trip exposure is three times the old behavior, can't rely on luck anymore
                double cpuShift = Math.Abs(cpuSum / samples - baseCpu);
                if (cpuShift > MaxCpuUtilShift)
                {
                    stage = YieldStage.Reverted;
                    verdict = YieldVerdict.Inconclusive;
                    return YieldAction.Revert;
                }
            }
            if (proxyMode)
            {
                double cpuNow = cpuSum / samples, freqNow = freqSum / freqSamples;
                // Large load drift means no comparison; revert without tripping, the scenario changed, not the machine's fault
                if (Math.Abs(cpuNow - baseCpu) > MaxCpuUtilShift)
                {
                    stage = YieldStage.Reverted;
                    verdict = YieldVerdict.Inconclusive;
                    return YieldAction.Revert;
                }
                if (baseGpu - gpuNow > MaxGpuUtilDrop)
                    return RevertNegativeVerdict(YieldVerdict.GpuHarm, FreqFuseKey);
                if (baseFreq - freqNow < baseFreq * MinFreqDropShare)
                {
                    // Frequency unchanged = EPP is a dead lever on this machine, same ending as the author's i7-9750H bench
                    return RevertNegativeVerdict(YieldVerdict.NoGain, FreqFuseKey);
                }
                EnterHold(now);
                return YieldAction.Keep;
            }
            double pkgNow = pkgSum / pkgSamples;
            bool keep = VerifyHold(basePkg, pkgNow, baseGpu, gpuNow);
            if (keep) EnterHold(now);
            else return RevertNegativeVerdict(YieldVerdict.NoGain, FuseKey);
            return YieldAction.Keep;
        }

        internal static bool ValidUtil(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 100; }

        private static bool ValidPositive(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0; }
    }

    // Runtime: own sampling thread, alive only during the match, always restores EPP on exit
    //   Sampling on a dedicated thread, not the scan loop, every 2 seconds, at most one decision per match
    internal static class PowerBudgetYieldRunner
    {
        internal const string EnabledKey = "GmPowerYield";
        internal const int SampleIntervalMs = 2000;
        internal const int GpuWindowMs = 500;
        internal const int SamplerReopenAfterDry = 15;

        private static readonly object gate = new object();
        private static readonly object operationGate = new object();
        private static Action mutationBegin;
        private static Action mutationEnd;
        private static Thread worker;
        private static volatile bool running;
        private static PowerBudgetYield state;
        private static int generation;
        private static bool stopInProgress;
        private static bool shutdownClosed;
        private static volatile bool proxyRun;
        private static int targetPid;
        private static long targetCreation;
        private static bool policyEnabled, policyCompetitive;
        private static Func<bool> policyAdmission;

        public static bool EnabledSetting { get { return Settings.LoadCached(EnabledKey, false); } }

        // Frequency proxy availability: probe once, remember forever, counter presence doesn't change mid-run
#if PAVISE_SELFTEST
        // Isolated tests don't really probe PDH, default unavailable, existing cases keep exactly the same semantics
        internal static bool FreqProxyForTest;
        internal static Func<bool> RuntimeEnvironmentForTest;

        public static bool FreqProxyAvailable { get { return FreqProxyForTest; } }
#else
        private static int freqCounterState;

        public static bool FreqProxyAvailable
        {
            get
            {
                if (freqCounterState == 0)
                {
                    var sampler = new FreqSampler();
                    freqCounterState = sampler.Open() ? 1 : -1;
                    sampler.Close();
                }
                return freqCounterState > 0;
            }
        }
#endif

        public static void ConfigureMutationBoundary(Action begin, Action end)
        {
            lock (gate)
            {
                mutationBegin = begin;
                mutationEnd = end;
            }
        }

        private static void BeginMutation()
        {
            Action callback;
            lock (gate) callback = mutationBegin;
            if (callback != null) try { callback(); } catch { }
        }

        private static void EndMutation()
        {
            Action callback;
            lock (gate) callback = mutationEnd;
            if (callback != null) try { callback(); } catch { }
        }

        private static bool RunMutation(Func<bool> action)
        {
            BeginMutation();
            try { return action != null && action(); }
            catch { return false; }
            finally { EndMutation(); }
        }

        public static YieldStage Stage
        {
            get { lock (gate) return state == null ? YieldStage.Idle : state.Stage; }
        }

        private static bool RuntimeEnvironmentEligible()
        {
            try
            {
#if PAVISE_SELFTEST
                // Isolated runner cases use explicit simulated values only, never read this machine's power config
                Func<bool> test = RuntimeEnvironmentForTest;
                return test == null || test();
#else
                return Native.HasSystemBattery() && Native.OnAcPower() && PowerPlan.ManagedPlanIsActive;
#endif
            }
            catch { return false; }
        }

        private static bool RuntimeAdmissionEligible()
        {
            Func<bool> admission;
            lock (gate)
            {
                if (!policyEnabled || !policyCompetitive) return false;
                admission = policyAdmission;
            }
            try { return (admission == null || admission()) && RuntimeEnvironmentEligible(); }
            catch { return false; }
        }

        // livePolicyAdmission must not take GameMode.sync; the runtime re-checks it inside the native write gate
        public static void Start(bool enabled, bool competitive, int rendererPid, long rendererCreation,
            Func<bool> livePolicyAdmission = null)
        {
            bool retarget, hadWorker, wasRunning, pendingStop;
            int observedGeneration;
            lock (gate)
            {
                if (shutdownClosed) return;
                observedGeneration = generation;
                wasRunning = running;
                hadWorker = running || worker != null;
                pendingStop = stopInProgress;
                retarget = running && (targetPid != rendererPid || targetCreation != rendererCreation);
                // The new target's admission closure must not be lent to a still-running old target; revoke old eligibility first, then wait for stop
                if (retarget || !enabled || !competitive || rendererPid <= 0 || rendererCreation <= 0)
                    policyEnabled = false;
            }
            // A config token for the same PID can also expire; keep the old closure until the old generation fully stops, a new true must not extend the old baseline
            bool oldAdmitted = !wasRunning || RuntimeAdmissionEligible();
            bool nextAdmitted = false;
            try
            {
                nextAdmitted = enabled && competitive && rendererPid > 0 && rendererCreation > 0
                    && (livePolicyAdmission == null || livePolicyAdmission()) && RuntimeEnvironmentEligible();
            }
            catch { }
            if (hadWorker && (pendingStop || retarget || !oldAdmitted || !nextAdmitted))
            {
                lock (gate)
                {
                    if (running && generation != observedGeneration) return;
                    policyEnabled = false;
                }
                if (!StopCore(3000, false)) return;
            }
            if (!nextAdmitted)
            {
                if (PowerPlan.EppYielded) StopCore(3000, false);
                return;
            }
            // A failed-restore receipt from the previous generation's revocation must not be overwritten by a new observe/write round
            if (!GenerationIsRunning() && PowerPlan.EppYielded && !StopCore(3000, false)) return;

            lock (gate)
            {
                if (shutdownClosed || stopInProgress || running
                    || worker != null && worker.IsAlive) return;
                if (!enabled || rendererPid <= 0 || rendererCreation <= 0) return;
                // A delegate rebuilt each round isn't a policy change; a live, still-admitted worker keeps its closure and generation
                policyEnabled = enabled;
                policyCompetitive = competitive;
                policyAdmission = livePolicyAdmission;
                // Watts if available, else degraded verification via the frequency counter; each path trips its own breaker
                bool watts = EnergyMeter.Available;
                bool proxy = !watts && FreqProxyAvailable;
                bool eligible = PowerBudgetYield.Eligible(
                    Native.HasSystemBattery(), Native.OnAcPower(), competitive,
                    PowerPlan.ManagedPlanIsActive, watts || proxy,
                    watts ? PowerBudgetYield.Fused : PowerBudgetYield.FreqFused);
                if (!eligible) return;
                proxyRun = proxy;
                targetPid = rendererPid;
                targetCreation = rendererCreation;
                state = new PowerBudgetYield();
                state.Begin(DateTime.UtcNow.Ticks, true, proxy);
                running = true;
                int mine = ++generation;
                worker = new Thread(delegate () { Loop(mine); });
                worker.IsBackground = true;
                worker.Name = "Pavise.PowerYield";
                worker.Priority = ThreadPriority.BelowNormal;
                worker.Start();
                Logger.Log(Lang.T("log.poweryield.1"));
            }
        }

        public static bool Stop()
        {
            return StopCore(3000, false);
        }

        internal static bool CloseForShutdown(int timeoutMs)
        {
            return StopCore(timeoutMs, true);
        }

        private static bool StopCore(int timeoutMs, bool terminal)
        {
            // Whether or not this match started a sampling thread, always check that EPP was restored
            //   Residue from a failed restore last match must not be missed just because this match didn't participate
            if (timeoutMs < 0) return false;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Thread t;
            lock (gate)
            {
                if (terminal) shutdownClosed = true;
                stopInProgress = true;
                running = false;
                generation++;
                t = worker;
                if (state != null) { state.End(); state = null; }
            }
            // Keep the thread ref on join failure; clearing it would make the final stop later
            // falsely report there was no worker thread at all
            if (t != null)
            {
                try
                {
                    if (t == Thread.CurrentThread || !t.Join(RemainingStopMs(elapsed, timeoutMs))) return false;
                }
                catch { return false; }
            }
            if (Monitor.IsEntered(operationGate)
                || !Monitor.TryEnter(operationGate, RemainingStopMs(elapsed, timeoutMs))) return false;
            try
            {
                bool ok = true;
                if (PowerPlan.EppYielded)
                {
                    ok = RunMutation(PowerPlan.RestoreEpp);
                    Logger.Log(Lang.T(ok ? "log.poweryield.5" : "log.poweryield.6"));
                }
                lock (gate)
                {
                    if (object.ReferenceEquals(worker, t)) worker = null;
                    targetPid = 0;
                    targetCreation = 0;
                    stopInProgress = false;
                }
                return ok;
            }
            catch { return false; }
            finally { Monitor.Exit(operationGate); }
        }

        private static int RemainingStopMs(System.Diagnostics.Stopwatch elapsed, int timeoutMs)
        {
            return (int)Math.Max(0L, timeoutMs - elapsed.ElapsedMilliseconds);
        }

        private static bool GenerationRunning(int mine)
        {
            lock (gate) return running && !shutdownClosed && mine == generation;
        }

        private static bool GenerationIsRunning()
        { lock (gate) return running; }

        private static bool RevokeCurrentAdmission(int mine, Func<bool> restore)
        {
            lock (operationGate)
            {
                if (!GenerationRunning(mine)) return false;
                // Even on failure keep the underlying receipt for Start/StopCore to retry; the old generation may no longer write
                RunMutation(restore);
                lock (gate)
                    if (mine == generation)
                    {
                        running = false;
                        generation++;
                        if (state != null) { state.End(); state = null; }
                    }
                return false;
            }
        }

        private static bool EnsureRuntimeAdmission(int mine, Func<bool> restore)
        {
            if (!GenerationRunning(mine)) return false;
            return RuntimeAdmissionEligible() || RevokeCurrentAdmission(mine, restore);
        }

        private static bool RunCurrentEngagement(int mine, Func<bool> engage, Func<bool> restore)
        {
            lock (operationGate)
            {
                if (!GenerationRunning(mine)) return false;
                bool revoked = false;
                bool applied = RunMutation(delegate
                {
                    // BeginMutation may wait on other work; eligibility re-check must sit right next to the actual EPP write
                    if (!GenerationRunning(mine) || !RuntimeAdmissionEligible()) { revoked = true; return false; }
                    return engage != null && engage();
                });
                return revoked ? RevokeCurrentAdmission(mine, restore) : applied;
            }
        }

        private static bool RunCurrentMutation(int mine, Func<bool> mutation)
        {
            lock (operationGate)
            {
                if (!GenerationRunning(mine)) return false;
                return RunMutation(mutation);
            }
        }

        // Shares the whole sample-to-decision-to-write dispatch with isolated tests; an invalid GPU must go through here too
        internal static bool ProcessSample(int mine, long now, double gpu, double cpu,
            double watts, double freq, Func<bool> engage, Func<bool> restore,
            out YieldAction action, out YieldVerdict verdict)
        {
            action = YieldAction.None; verdict = YieldVerdict.None;
            if (!EnsureRuntimeAdmission(mine, restore)) return false;
            lock (gate)
            {
                if (!running || mine != generation || shutdownClosed || state == null) return false;
                action = state.Advance(now, gpu, cpu, watts, freq);
                verdict = state.Verdict;
                if (state.Stage == YieldStage.Skipped && action == YieldAction.None) return false;
            }
            if (action == YieldAction.Engage) return RunCurrentEngagement(mine, engage, restore);
            if (action == YieldAction.Revert)
            {
                RunCurrentMutation(mine, restore);
                return false; // on failure the underlying receipt stays, StopCore still owns final restore
            }
            if (action == YieldAction.Release) return RunCurrentMutation(mine, restore);
            return GenerationRunning(mine);
        }

        private static void Loop(int mine)
        {
            var cpu = new CpuSaturation();
            cpu.Sample(); // Prime GetSystemTimes before the first measurement window
            bool proxy = proxyRun;
            int rendererPid;
            long rendererCreation;
            lock (gate)
            {
                rendererPid = targetPid;
                rendererCreation = targetCreation;
            }
            bool adapterKnown = false;
            int adapterLuidHigh = 0;
            uint adapterLuidLow = 0;
            var freq = new FreqSampler();
            // GPU utilization via a persistent query, opened once per match, one collect per round, no sleep; fall back to one-shot parsing if it won't open
            var gpuSampler = new GpuEvidence.GpuEngineSampler();
            gpuSampler.Open();
            if (proxy && !freq.Open())
            {
                // A successful process-level probe doesn't mean this match can open it; a silent early death must log and still collect state
                freq.Close();
                gpuSampler.Close();
                Logger.Warn(Lang.T("log.poweryield.12"));
                lock (gate)
                    if (mine == generation)
                    {
                        running = false;
                        if (state != null) { state.End(); state = null; }
                    }
                return;
            }
            EnergyMeter.Sample prev = proxy ? null : EnergyMeter.Take();
            int drySamples = 0;
            try
            {
                while (GenerationRunning(mine))
                {
                    Thread.Sleep(SampleIntervalMs);
                    if (!GenerationRunning(mine)) break;
                    if (!EnsureRuntimeAdmission(mine, PowerPlan.RestoreEpp)) break;
                    bool targetChanged;
                    double gpu = SampleGpuUtil(gpuSampler, rendererPid, rendererCreation,
                        ref adapterKnown, ref adapterLuidHigh, ref adapterLuidLow,
                        out targetChanged);
                    // Reopen once when the persistent query hasn't seen this process's 3D instance for a long time, in case the wildcard instance table missed a late renderer device
                    if (gpu < 0 && !targetChanged)
                    {
                        if (++drySamples >= SamplerReopenAfterDry) { drySamples = 0; gpuSampler.Open(); }
                    }
                    else drySamples = 0;
                    if (targetChanged)
                    {
                        // PID reuse, renderer exit, or rendering moving to another GPU: the old baseline is unusable either way
                        // If EPP was already yielded try restoring immediately; StopCore at match end remains the failure fallback
                        if (PowerPlan.EppYielded) RunCurrentMutation(mine, PowerPlan.RestoreEpp);
                        Logger.Warn(Lang.T("log.poweryield.13"));
                        break;
                    }
                    double cpuPct = cpu.Sample() * 100.0;
                    double watts = -1, freqPct = -1;
                    if (proxy) freqPct = freq.Read();
                    else
                    {
                        EnergyMeter.Sample now = EnergyMeter.Take();
                        watts = EnergyMeter.Watts(prev, now, EnergyRail.Package);
                        if (now != null) prev = now;
                    }
                    YieldAction action;
                    YieldVerdict verdict;
                    bool keepRunning = ProcessSample(mine, DateTime.UtcNow.Ticks, gpu, cpuPct,
                        watts, freqPct, delegate { return PowerPlan.TryYieldEpp(PowerBudgetYield.YieldEpp); },
                        PowerPlan.RestoreEpp, out action, out verdict);
                    if (action == YieldAction.Engage)
                    {
                        bool ok = keepRunning;
                        Logger.Log(proxy
                            ? Lang.F(ok ? "log.poweryield.8" : "log.poweryield.3",
                                PowerBudgetYield.YieldEpp.ToString(),
                                gpu.ToString("F0"), cpuPct.ToString("F0"), freqPct.ToString("F0"))
                            : Lang.F(ok ? "log.poweryield.2" : "log.poweryield.3",
                                PowerBudgetYield.YieldEpp.ToString(),
                                gpu.ToString("F0"), cpuPct.ToString("F0"), watts.ToString("F1")));
                        if (!ok) break;
                    }
                    else if (action == YieldAction.Revert)
                    {
                        // No retry on restore failure here, EppYielded stays true, StopCore at match end is the fallback restore
                        Logger.Warn(Lang.T(verdict == YieldVerdict.Inconclusive
                            ? "log.poweryield.9" : "log.poweryield.4"));
                        break;
                    }
                    else if (action == YieldAction.Keep)
                    {
                        // Verification passing doesn't end the job; steering starts, hand the budget back when the bottleneck moves back to CPU
                        Logger.Log(Lang.T(proxy ? "log.poweryield.10" : "log.poweryield.7"));
                    }
                    else if (action == YieldAction.Release)
                    {
                        // Restore failure can't be ignored: state already assumes it was handed back, let the match-end fallback restore finish it
                        if (!keepRunning) break;
                        Logger.Log(Lang.T("log.poweryield.11"));
                    }
                    if (!keepRunning) break;
                }
            }
            finally { freq.Close(); gpuSampler.Close(); }
        }

        // Platform frequency percent, same counter the author's bench used, averaged between two collects
        private sealed class FreqSampler
        {
            private IntPtr query;
            private IntPtr counter;

            public bool Open()
            {
                try
                {
                    if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
                        return false;
                    if (PdhAddEnglishCounterW(query,
                            @"\Processor Information(_Total)\% Processor Performance",
                            IntPtr.Zero, out counter) != 0) return false;
                    return PdhCollectQueryData(query) == 0;
                }
                catch { return false; }
            }

            public double Read()
            {
                try
                {
                    if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return -1;
                    var fmt = new PdhFmtCounterValue();
                    uint type;
                    if (PdhGetFormattedCounterValue(counter, PdhFmtDouble, out type, out fmt) != 0
                        || fmt.CStatus != 0) return -1;
                    return fmt.DoubleValue > 0 ? fmt.DoubleValue : -1;
                }
                catch { return -1; }
            }

            public void Close()
            {
                if (query == IntPtr.Zero) return;
                try { PdhCloseQuery(query); } catch { }
                query = IntPtr.Zero;
            }

            private const uint PdhFmtDouble = 0x00000200;

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential)]
            private struct PdhFmtCounterValue
            {
                public uint CStatus;
                public double DoubleValue;
            }

            [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
            [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
            [System.Runtime.InteropServices.DllImport("pdh.dll")]
            private static extern uint PdhCollectQueryData(IntPtr query);
            [System.Runtime.InteropServices.DllImport("pdh.dll")]
            private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);
            [System.Runtime.InteropServices.DllImport("pdh.dll")]
            private static extern uint PdhCloseQuery(IntPtr query);
        }

#if PAVISE_SELFTEST
        internal static void SetRuntimeAdmissionForTest(int mine, Func<bool> admission,
            int rendererPid = 123, long rendererCreation = 456)
        {
            lock (gate)
            {
                if (mine != generation || !running) throw new InvalidOperationException("No test generation");
                policyAdmission = admission;
                targetPid = rendererPid;
                targetCreation = rendererCreation;
            }
        }

        internal static void SetSampleStateForTest(int mine, PowerBudgetYield value)
        {
            lock (gate)
            {
                if (mine != generation || !running) throw new InvalidOperationException("No test generation");
                state = value;
            }
        }

        internal static int StartShutdownWorkerForTest(Action<int> body)
        {
            lock (gate)
            {
                if (shutdownClosed || stopInProgress || running
                    || worker != null && worker.IsAlive) return -1;
                running = true;
                policyEnabled = policyCompetitive = true;
                policyAdmission = null;
                int mine = ++generation;
                worker = new Thread(delegate () { body(mine); });
                worker.IsBackground = true;
                worker.Start();
                return mine;
            }
        }

        internal static bool RunShutdownMutationForTest(int mine, Func<bool> action)
        {
            return RunCurrentMutation(mine, action);
        }

        internal static void ResetShutdownForTest()
        {
            lock (operationGate)
            lock (gate)
            {
                if (worker != null && worker.IsAlive)
                    throw new InvalidOperationException("Cannot reset a live isolated power-yield worker");
                worker = null;
                state = null;
                running = stopInProgress = shutdownClosed = false;
                targetPid = 0;
                targetCreation = 0;
                policyEnabled = policyCompetitive = false;
                policyAdmission = null;
                RuntimeEnvironmentForTest = null;
                generation++;
            }
        }
#endif

        private static bool SameProcessIdentity(int pid, long creation)
        {
            if (pid <= 0 || creation <= 0) return false;
            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                long actual, cpu;
                ulong io;
                return Native.QueryProcessSample(process, out actual, out cpu, out io)
                    && actual == creation;
            }
            catch { return false; }
            finally { Native.CloseHandle(process); }
        }

        private static double SampleGpuUtil(GpuEvidence.GpuEngineSampler sampler, int pid, long creation,
            ref bool adapterKnown, ref int adapterLuidHigh, ref uint adapterLuidLow,
            out bool targetChanged)
        {
            targetChanged = false;
            if (!SameProcessIdentity(pid, creation))
            {
                targetChanged = true;
                return -1;
            }
            try
            {
                RenderAdapter adapter = sampler != null && sampler.IsOpen
                    ? sampler.Resolve(pid)
                    : GpuEvidence.ResolveRenderAdapter(pid, GpuWindowMs);
                return AcceptTargetAdapterSample(adapter, ref adapterKnown,
                    ref adapterLuidHigh, ref adapterLuidLow, out targetChanged);
            }
            catch { return -1; }
        }

        // Accept only this renderer's single, stable render adapter
        // ResolveRenderAdapter already filters by PID; locking the LUID here too prevents reusing an old baseline after Optimus/multi-GPU migration
        internal static double AcceptTargetAdapterSample(RenderAdapter adapter,
            ref bool adapterKnown, ref int adapterLuidHigh, ref uint adapterLuidLow,
            out bool targetChanged)
        {
            targetChanged = false;
            if (adapter == null || adapter.Ambiguous) return -1;
            if (double.IsNaN(adapter.Util) || double.IsInfinity(adapter.Util) || adapter.Util < 0) return -1;
            if (adapterKnown
                && (adapter.LuidHigh != adapterLuidHigh || adapter.LuidLow != adapterLuidLow))
            {
                // When every GPU is at 0% PickAdapter just grabbed the first one; that's not migration, this round has no data
                if (adapter.Util < GpuEvidence.MinElectUtilization) return -1;
                targetChanged = true;
                return -1;
            }
            if (!adapterKnown)
            {
                // A 0% leftover PDH instance doesn't prove the render GPU; bind only once the renderer is really working on 3D
                if (adapter.Util < GpuEvidence.MinElectUtilization) return -1;
                adapterKnown = true;
                adapterLuidHigh = adapter.LuidHigh;
                adapterLuidLow = adapter.LuidLow;
            }
            return adapter.Util > 100.0 ? 100.0 : adapter.Util;
        }
    }
}
