using System;
using System.Threading;

namespace PaviseApp
{
    // 厂商性能档的工作线程只通过这个接口读游戏模式的状态和请它做事 不直接碰 GameMode 的字段
    internal interface ILaptopPerformanceHost
    {
        bool Stopping { get; }
        bool GuardEnabled { get; }
        bool GameActive { get; }
        // 正在停止或者收到了紧急恢复请求
        bool ShuttingDown { get; }
        bool PowerSessionEnding { get; }
        bool ProfileStoreReady { get; }
        bool GameGone { get; }
        bool PowerPlanActive { get; }
        int PowerSessionGeneration { get; }
        // 就是游戏模式自己的电源应用锁 两边共用一把 加锁顺序才不会乱
        object PowerApplyGate { get; }
        bool AutomaticWorkAllowed(string key);
        void RecordAutomaticWorkFailure(string key, string reason);
        void RecordAutomation(string key, string state);
        void RequestPolicyApply(string caller);
        void Kick();
        bool EnvStep(string key, bool want, bool active, Func<bool> activate, Func<bool> restore);
    }

    // 插电对局期间切换笔记本厂商性能档 实例归 GameMode 所有
    internal sealed class LaptopPerformanceCoordinator
    {
        private readonly ILaptopPerformanceHost host;
        private volatile bool active;
        private int worker;
        private int powerGeneration = -1;
        private int sessionGeneration;
        private int backendSession = -1;
        private readonly object workGate = new object();
        private readonly ManualResetEvent idle = new ManualResetEvent(true);
        private int residueGeneration = int.MinValue;
        private bool residue;
        private long nextWork;
        private volatile bool supportKnown, supported;
        private volatile string state = "off", status = "";

        internal LaptopPerformanceCoordinator(ILaptopPerformanceHost host)
        {
            if (host == null) throw new ArgumentNullException("host");
            this.host = host;
        }

        public bool Enabled
        { get { return Settings.LoadCached(LaptopPerfMode.PreferenceKey, false); } }

        public bool SupportKnown { get { return supportKnown; } }
        public bool Supported { get { return supported; } }
        public string State { get { return state; } }
        public string Detail { get { return Status; } }
        public string Status
        {
            get
            {
                string current = status;
                if (current.Length > 0) return current;
                return PerformancePolicy.Text("已关闭；仅支持部分联想、华硕插电笔记本", "Off; selected AC-powered Lenovo and ASUS laptops only");
            }
        }

        // 还有没做完的厂商档位切换或者排着队的工作线程时 电源计划先不恢复
        internal bool Active { get { return active; } }
        internal bool WorkerBusy { get { return Volatile.Read(ref worker) != 0; } }
        internal int PowerGeneration { get { return Volatile.Read(ref powerGeneration); } }

        internal void InvalidatePowerGeneration() { Interlocked.Exchange(ref powerGeneration, -1); }

        internal void ArmPowerGeneration(int generation)
        {
            Interlocked.Exchange(ref powerGeneration, generation);
            Interlocked.Exchange(ref nextWork, 0L);
        }

        // 仅由界面后台探测调用 不能从绘制或同步属性读取中调用
        internal void RefreshSupport()
        {
            if (host.Stopping) return;
            bool found = false;
            try { found = LaptopPerfMode.SupportedCached(); }
            catch { }
            supported = found;
            supportKnown = true;
            if (!found && !active && !LaptopPerfMode.HasResidue)
                SetState("unsupported", LaptopPerfMode.SupportDetail.Length > 0 ? LaptopPerfMode.SupportDetail
                    : PerformancePolicy.Text("本机没有可核验的联想/华硕笔记本性能档接口", "No verified Lenovo/ASUS laptop performance interface is available"));
            else if (found && !Enabled && !active && !LaptopPerfMode.HasResidue)
                SetState("off", PerformancePolicy.Text("已关闭；", "Off; ") + LaptopPerfMode.SupportDetail);
        }

        public bool TrySetEnabled(bool value)
        {
            if (Enabled == value) return !host.Stopping;
            if (host.Stopping || !Settings.SaveDurable(LaptopPerfMode.PreferenceKey, value)) return false;
            Interlocked.Exchange(ref nextWork, 0L);
            SetState(value ? "waiting-game" : "off", value
                ? PerformancePolicy.Text("已开启；等待合格的插电游戏会话", "Enabled; waiting for an eligible AC-powered game session")
                : PerformancePolicy.Text("已关闭；已有厂商档位改动将按记录恢复", "Off; owned vendor-mode changes will be restored"));
            host.RequestPolicyApply("TrySetLaptopPerformanceEnabled");
            return true;
        }

        internal static bool Admitted(bool preference, bool guardEnabled, bool gameActive,
            bool onAc, bool noSwitch, bool shuttingDown, bool sessionEnding,
            bool profileReady, bool gameGone, bool powerConfirmed)
        {
            return preference && guardEnabled && gameActive && onAc && !noSwitch
                && !shuttingDown && !sessionEnding && profileReady && !gameGone && powerConfirmed;
        }

        // 每次都按同样的顺序把各项条件全部读一遍 不因为前面某项为假就跳过后面的读取
        private bool AdmissionCurrent(int generation)
        {
            return Admitted(Enabled, host.GuardEnabled, host.GameActive,
                OnAcPower(), PowerPlan.NoSwitchSelected,
                host.ShuttingDown, host.PowerSessionEnding, host.ProfileStoreReady,
                host.GameGone, host.PowerPlanActive && generation == host.PowerSessionGeneration
                    && generation == Volatile.Read(ref powerGeneration)
                    && !ActionFailureStore.IsBlocked("PowerPlanOn", "system"));
        }

#if PAVISE_SELFTEST
        internal Func<bool> AcPowerForTest;
#endif
        private bool OnAcPower()
        {
#if PAVISE_SELFTEST
            return AcPowerForTest != null && AcPowerForTest();
#else
            return Native.ConfirmedAcPower();
#endif
        }

        private void SetState(string value, string detail)
        { state = value; status = detail ?? ""; }

        internal void BeginSession()
        {
            // 仅工作线程访问 WMI 和后端状态 前台 电源和策略变化不能重置退让状态
            Interlocked.Increment(ref sessionGeneration);
            Interlocked.Exchange(ref nextWork, 0L);
            if (Enabled)
                SetState("waiting-power", PerformancePolicy.Text("等待电源计划保存原值并完成应用", "Waiting for the Windows power plan to be recorded and applied"));
        }

        internal void Step()
        {
            if (!host.AutomaticWorkAllowed("laptop-worker")) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < Interlocked.Read(ref nextWork)) return;
            int mutation = Settings.MutationGeneration;
            if (mutation != residueGeneration)
            {
                residue = LaptopPerfMode.HasResidue;
                residueGeneration = mutation;
            }
            if (!Enabled && !active && !residue) return;
            lock (workGate)
            {
            if (host.Stopping || Interlocked.CompareExchange(ref worker, 1, 0) != 0) return;
            idle.Reset();
            int generation = host.PowerSessionGeneration;
            // 待完成检查与固件核验均需有限执行 不能每轮循环都查询 WMI
            Interlocked.Exchange(ref nextWork, now + TimeSpan.TicksPerSecond);
            bool queued = false;
            try
            {
                queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    try { RunStep(generation); }
                    catch (Exception ex)
                    {
                        host.RecordAutomaticWorkFailure("laptop-worker", ex.GetType().Name);
                        SetState("failed", PerformancePolicy.Text("厂商性能档检查失败：", "Vendor mode check failed: ") + ex.Message);
                        Interlocked.Exchange(ref nextWork, DateTime.UtcNow.AddSeconds(30).Ticks);
                    }
                    finally
                    {
                        lock (workGate)
                        {
                            Interlocked.Exchange(ref worker, 0);
                            idle.Set();
                        }
                        try { host.Kick(); } catch { }
                    }
                });
            }
            catch { }
            if (!queued)
            {
                host.RecordAutomaticWorkFailure("laptop-worker", "queue-rejected");
                Interlocked.Exchange(ref worker, 0);
                idle.Set();
                SetState("failed", PerformancePolicy.Text("厂商性能档检查暂时无法排队", "Vendor mode check could not be queued"));
            }
            }
        }

        internal bool Drain(int timeoutMs)
        {
            lock (workGate)
            {
                if (!host.Stopping || timeoutMs < 0) return false;
            }
            return idle.WaitOne(timeoutMs) && Volatile.Read(ref worker) == 0;
        }

        internal void RunStep(int generation)
        {
            lock (host.PowerApplyGate)
            {
                if (!AdmissionCurrent(generation))
                {
                    if (!RestoreForPowerPlan()) return;
                    if (Enabled)
                        SetState("waiting-game", PerformancePolicy.Text("等待插电游戏会话及电源计划；选择不切换时不接管", "Waiting for AC game and power-plan readiness; Don't switch leaves vendor control unchanged"));
                    return;
                }
                RefreshSupport();
                if (!supported)
                {
                    if (!RestoreForPowerPlan()) return;
                    SetState("unsupported", LaptopPerfMode.SupportDetail.Length > 0 ? LaptopPerfMode.SupportDetail
                        : PerformancePolicy.Text("本机厂商性能档接口不可用，未新增切档", "The vendor performance interface is unavailable; no new mode change"));
                    Interlocked.Exchange(ref nextWork, DateTime.UtcNow.AddSeconds(30).Ticks);
                    return;
                }
                int session = Volatile.Read(ref sessionGeneration);
                if (backendSession != session)
                {
                    if (!RestoreForPowerPlan()) return;
                    LaptopPerfMode.EndSession();
                    backendSession = session;
                }
                string outcome = "";
                bool unchanged = false;
                bool verified = false;
                bool cancelled = false;
                bool pending = host.EnvStep("laptopperf", true, false, delegate
                {
                    if (!AdmissionCurrent(generation)) { cancelled = true; throw new OperationCanceledException(); }
                    bool result = LaptopPerfMode.Activate(() => AdmissionCurrent(generation));
                    outcome = LaptopPerfMode.LastStatus;
                    unchanged = result && (outcome == "external" || outcome == "already");
                    cancelled = outcome == "waiting-power" || !AdmissionCurrent(generation);
                    if (unchanged || (!result && outcome == "unsupported")) throw new NotSupportedException();
                    if (cancelled) throw new OperationCanceledException();
                    verified = result && outcome == "applied";
                    return result;
                }, LaptopPerfMode.Restore);
                active = pending || unchanged;
                if (LaptopPerfMode.HasResidue && (!verified || !pending || ActionFailureStore.IsBlocked(LaptopPerfMode.PreferenceKey, "system")))
                    SetState("restore-failed", PerformancePolicy.Text("厂商原档位恢复未完成，保留记录并暂停电源计划恢复", "Vendor mode recovery is pending; records retained and Windows plan restoration deferred"));
                else if (unchanged)
                {
                    host.RecordAutomation("laptopperf", "unchanged");
                    SetState("unchanged", outcome == "external"
                        ? PerformancePolicy.Text("检测到外部改档，本次会话保留外部选择", "An external mode change was detected; preserving it for this session")
                        : PerformancePolicy.Text("原本已是厂商性能档，未接管原设置", "Already in the vendor performance mode; original settings are not owned"));
                }
                else if (verified && pending && !ActionFailureStore.IsBlocked(LaptopPerfMode.PreferenceKey, "system"))
                    SetState("applied", PerformancePolicy.Text("厂商性能档已应用，退局恢复原档位", "Vendor performance mode applied; the original will be restored after the session"));
                else if (cancelled)
                    SetState("waiting-game", PerformancePolicy.Text("供电或会话状态已变更，已取消本次切档", "Power or session state changed; this mode switch was cancelled"));
                else
                    SetState(LaptopPerfMode.HasResidue ? "restore-failed" : "failed", LaptopPerfMode.LastDetail);
                Interlocked.Exchange(ref nextWork, DateTime.UtcNow.AddSeconds(30).Ticks);
            }
        }

        internal bool RestoreForPowerPlan()
        {
            object gate = host.PowerApplyGate;
            if (Volatile.Read(ref worker) != 0 && !Monitor.IsEntered(gate))
            { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
            lock (gate)
            {
                bool owed = active || LaptopPerfMode.HasResidue;
                if (!owed) return true;
                active = host.EnvStep("laptopperf", false, true, null, LaptopPerfMode.Restore);
                bool restored = !active && !LaptopPerfMode.HasResidue;
                SetState(restored ? "restored" : "restore-failed", restored
                    ? PerformancePolicy.Text("厂商档位已按记录恢复，外部选择保留", "Owned vendor mode restored; external choices preserved")
                    : PerformancePolicy.Text("厂商原档位恢复未完成，保留记录并暂停电源计划恢复", "Vendor mode recovery is pending; records retained and Windows plan restoration deferred"));
                return restored;
            }
        }
    }
}
