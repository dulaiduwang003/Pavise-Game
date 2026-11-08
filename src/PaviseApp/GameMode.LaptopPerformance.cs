using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private volatile bool laptopPerfActive;
        private int laptopPerfWorker;
        private int laptopPerfPowerGeneration = -1;
        private int laptopPerfSessionGeneration;
        private int laptopPerfBackendSession = -1;
        private readonly object laptopPerfWorkGate = new object();
        private readonly ManualResetEvent laptopPerfIdle = new ManualResetEvent(true);
        private int laptopPerfResidueGeneration = int.MinValue;
        private bool laptopPerfResidue;
        private long laptopPerfNextWork;
        private volatile bool laptopPerfSupportKnown, laptopPerfSupported;
        private volatile string laptopPerfState = "off", laptopPerfStatus = "";

        public bool LaptopPerformanceEnabled
        { get { return Settings.LoadCached(LaptopPerfMode.PreferenceKey, false); } }

        public bool LaptopPerformanceSupportKnown { get { return laptopPerfSupportKnown; } }
        public bool LaptopPerformanceSupported { get { return laptopPerfSupported; } }
        public string LaptopPerformanceState { get { return laptopPerfState; } }
        public string LaptopPerformanceDetail { get { return LaptopPerformanceStatus; } }
        public string LaptopPerformanceStatus
        {
            get
            {
                string current = laptopPerfStatus;
                if (current.Length > 0) return current;
                return PerformancePolicy.Text("已关闭；仅支持部分联想、华硕插电笔记本", "Off; selected AC-powered Lenovo and ASUS laptops only");
            }
        }

        // Called by the UI's background probe, never by a paint/sync getter.
        internal void RefreshLaptopPerformanceSupport()
        {
            if (stopping) return;
            bool supported = false;
            try { supported = LaptopPerfMode.SupportedCached(); }
            catch { }
            laptopPerfSupported = supported;
            laptopPerfSupportKnown = true;
            if (!supported && !laptopPerfActive && !LaptopPerfMode.HasResidue)
                SetLaptopPerformanceState("unsupported", LaptopPerfMode.SupportDetail.Length > 0 ? LaptopPerfMode.SupportDetail
                    : PerformancePolicy.Text("本机没有可核验的联想/华硕笔记本性能档接口", "No verified Lenovo/ASUS laptop performance interface is available"));
            else if (supported && !LaptopPerformanceEnabled && !laptopPerfActive && !LaptopPerfMode.HasResidue)
                SetLaptopPerformanceState("off", PerformancePolicy.Text("已关闭；", "Off; ") + LaptopPerfMode.SupportDetail);
        }

        public bool TrySetLaptopPerformanceEnabled(bool value)
        {
            if (LaptopPerformanceEnabled == value) return !stopping;
            if (stopping || !Settings.SaveDurable(LaptopPerfMode.PreferenceKey, value)) return false;
            Interlocked.Exchange(ref laptopPerfNextWork, 0L);
            SetLaptopPerformanceState(value ? "waiting-game" : "off", value
                ? PerformancePolicy.Text("已开启；等待合格的插电游戏会话", "Enabled; waiting for an eligible AC-powered game session")
                : PerformancePolicy.Text("已关闭；已有厂商档位改动将按记录恢复", "Off; owned vendor-mode changes will be restored"));
            RequestPolicyApply("TrySetLaptopPerformanceEnabled");
            return true;
        }

        internal static bool LaptopPerformanceAdmitted(bool preference, bool guardEnabled, bool gameActive,
            bool onAc, bool noSwitch, bool shuttingDown, bool sessionEnding,
            bool profileReady, bool gameGone, bool powerConfirmed)
        {
            return preference && guardEnabled && gameActive && onAc && !noSwitch
                && !shuttingDown && !sessionEnding && profileReady && !gameGone && powerConfirmed;
        }

        private bool LaptopPerformanceAdmissionCurrent(int generation)
        {
            return LaptopPerformanceAdmitted(LaptopPerformanceEnabled, enabled, active,
                LaptopPerformanceOnAc(), PowerPlan.NoSwitchSelected,
                stopping || panicReq, powerSessionEnding, !ProfileStoreSaveFailed,
                gameGoneSinceTicks != 0, planActive && generation == Volatile.Read(ref powerSessionGen)
                    && generation == Volatile.Read(ref laptopPerfPowerGeneration)
                    && !ActionFailureStore.IsBlocked("PowerPlanOn", "system"));
        }

#if PAVISE_SELFTEST
        internal Func<bool> LaptopAcPowerForTest;
#endif
        private bool LaptopPerformanceOnAc()
        {
#if PAVISE_SELFTEST
            return LaptopAcPowerForTest != null && LaptopAcPowerForTest();
#else
            return Native.ConfirmedAcPower();
#endif
        }

        private void SetLaptopPerformanceState(string state, string detail)
        { laptopPerfState = state; laptopPerfStatus = detail ?? ""; }

        private void BeginLaptopPerformanceSession()
        {
            // Only the worker accesses WMI/backend state; focus, power and policy changes do not reset a yield.
            Interlocked.Increment(ref laptopPerfSessionGeneration);
            Interlocked.Exchange(ref laptopPerfNextWork, 0L);
            if (LaptopPerformanceEnabled)
                SetLaptopPerformanceState("waiting-power", PerformancePolicy.Text("等待电源计划保存原值并完成应用", "Waiting for the Windows power plan to be recorded and applied"));
        }

        private void StepLaptopPerformancePolicy()
        {
            if (!AutomaticWorkAllowed("laptop-worker")) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < Interlocked.Read(ref laptopPerfNextWork)) return;
            int mutation = Settings.MutationGeneration;
            if (mutation != laptopPerfResidueGeneration)
            {
                laptopPerfResidue = LaptopPerfMode.HasResidue;
                laptopPerfResidueGeneration = mutation;
            }
            if (!LaptopPerformanceEnabled && !laptopPerfActive && !laptopPerfResidue) return;
            lock (laptopPerfWorkGate)
            {
            if (stopping || Interlocked.CompareExchange(ref laptopPerfWorker, 1, 0) != 0) return;
            laptopPerfIdle.Reset();
            int generation = Volatile.Read(ref powerSessionGen);
            // Bound pending checks as well as firmware audits; no every-loop WMI work.
            Interlocked.Exchange(ref laptopPerfNextWork, now + TimeSpan.TicksPerSecond);
            bool queued = false;
            try
            {
                queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    try { RunLaptopPerformanceStep(generation); }
                    catch (Exception ex)
                    {
                        RecordAutomaticWorkFailure("laptop-worker", ex.GetType().Name);
                        SetLaptopPerformanceState("failed", PerformancePolicy.Text("厂商性能档检查失败：", "Vendor mode check failed: ") + ex.Message);
                        Interlocked.Exchange(ref laptopPerfNextWork, DateTime.UtcNow.AddSeconds(30).Ticks);
                    }
                    finally
                    {
                        lock (laptopPerfWorkGate)
                        {
                            Interlocked.Exchange(ref laptopPerfWorker, 0);
                            laptopPerfIdle.Set();
                        }
                        try { kick.Set(); } catch { }
                    }
                });
            }
            catch { }
            if (!queued)
            {
                RecordAutomaticWorkFailure("laptop-worker", "queue-rejected");
                Interlocked.Exchange(ref laptopPerfWorker, 0);
                laptopPerfIdle.Set();
                SetLaptopPerformanceState("failed", PerformancePolicy.Text("厂商性能档检查暂时无法排队", "Vendor mode check could not be queued"));
            }
            }
        }

        private bool DrainLaptopPerformanceWork(int timeoutMs)
        {
            lock (laptopPerfWorkGate)
            {
                if (!stopping || timeoutMs < 0) return false;
            }
            return laptopPerfIdle.WaitOne(timeoutMs) && Volatile.Read(ref laptopPerfWorker) == 0;
        }

        private void RunLaptopPerformanceStep(int generation)
        {
            lock (powerApplyGate)
            {
                if (!LaptopPerformanceAdmissionCurrent(generation))
                {
                    if (!RestoreLaptopPerformanceForPowerPlan()) return;
                    if (LaptopPerformanceEnabled)
                        SetLaptopPerformanceState("waiting-game", PerformancePolicy.Text("等待插电游戏会话及电源计划；选择不切换时不接管", "Waiting for AC game and power-plan readiness; Don't switch leaves vendor control unchanged"));
                    return;
                }
                RefreshLaptopPerformanceSupport();
                if (!laptopPerfSupported)
                {
                    if (!RestoreLaptopPerformanceForPowerPlan()) return;
                    SetLaptopPerformanceState("unsupported", LaptopPerfMode.SupportDetail.Length > 0 ? LaptopPerfMode.SupportDetail
                        : PerformancePolicy.Text("本机厂商性能档接口不可用，未新增切档", "The vendor performance interface is unavailable; no new mode change"));
                    Interlocked.Exchange(ref laptopPerfNextWork, DateTime.UtcNow.AddSeconds(30).Ticks);
                    return;
                }
                int session = Volatile.Read(ref laptopPerfSessionGeneration);
                if (laptopPerfBackendSession != session)
                {
                    if (!RestoreLaptopPerformanceForPowerPlan()) return;
                    LaptopPerfMode.EndSession();
                    laptopPerfBackendSession = session;
                }
                string outcome = "";
                bool unchanged = false;
                bool verified = false;
                bool cancelled = false;
                bool pending = EnvStep("laptopperf", true, false, delegate
                {
                    if (!LaptopPerformanceAdmissionCurrent(generation)) { cancelled = true; throw new OperationCanceledException(); }
                    bool result = LaptopPerfMode.Activate(() => LaptopPerformanceAdmissionCurrent(generation));
                    outcome = LaptopPerfMode.LastStatus;
                    unchanged = result && (outcome == "external" || outcome == "already");
                    cancelled = outcome == "waiting-power" || !LaptopPerformanceAdmissionCurrent(generation);
                    if (unchanged || (!result && outcome == "unsupported")) throw new NotSupportedException();
                    if (cancelled) throw new OperationCanceledException();
                    verified = result && outcome == "applied";
                    return result;
                }, LaptopPerfMode.Restore);
                laptopPerfActive = pending || unchanged;
                if (LaptopPerfMode.HasResidue && (!verified || !pending || ActionFailureStore.IsBlocked(LaptopPerfMode.PreferenceKey, "system")))
                    SetLaptopPerformanceState("restore-failed", PerformancePolicy.Text("厂商原档位恢复未完成，保留记录并暂停电源计划恢复", "Vendor mode recovery is pending; records retained and Windows plan restoration deferred"));
                else if (unchanged)
                {
                    RecordAutomation("laptopperf", "unchanged");
                    SetLaptopPerformanceState("unchanged", outcome == "external"
                        ? PerformancePolicy.Text("检测到外部改档，本次会话保留外部选择", "An external mode change was detected; preserving it for this session")
                        : PerformancePolicy.Text("原本已是厂商性能档，未接管原设置", "Already in the vendor performance mode; original settings are not owned"));
                }
                else if (verified && pending && !ActionFailureStore.IsBlocked(LaptopPerfMode.PreferenceKey, "system"))
                    SetLaptopPerformanceState("applied", PerformancePolicy.Text("厂商性能档已应用，退局恢复原档位", "Vendor performance mode applied; the original will be restored after the session"));
                else if (cancelled)
                    SetLaptopPerformanceState("waiting-game", PerformancePolicy.Text("供电或会话状态已变更，已取消本次切档", "Power or session state changed; this mode switch was cancelled"));
                else
                    SetLaptopPerformanceState(LaptopPerfMode.HasResidue ? "restore-failed" : "failed", LaptopPerfMode.LastDetail);
                Interlocked.Exchange(ref laptopPerfNextWork, DateTime.UtcNow.AddSeconds(30).Ticks);
            }
        }

        private bool RestoreLaptopPerformanceForPowerPlan()
        {
            if (Volatile.Read(ref laptopPerfWorker) != 0 && !Monitor.IsEntered(powerApplyGate))
            { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
            lock (powerApplyGate)
            {
                bool owed = laptopPerfActive || LaptopPerfMode.HasResidue;
                if (!owed) return true;
                laptopPerfActive = EnvStep("laptopperf", false, true, null, LaptopPerfMode.Restore);
                bool restored = !laptopPerfActive && !LaptopPerfMode.HasResidue;
                SetLaptopPerformanceState(restored ? "restored" : "restore-failed", restored
                    ? PerformancePolicy.Text("厂商档位已按记录恢复，外部选择保留", "Owned vendor mode restored; external choices preserved")
                    : PerformancePolicy.Text("厂商原档位恢复未完成，保留记录并暂停电源计划恢复", "Vendor mode recovery is pending; records retained and Windows plan restoration deferred"));
                return restored;
            }
        }
    }
}
