using System;
using System.Threading;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class LaboratorySelection
    {
        internal sealed class ReadScope
        {
            private int generation;
            private long session;
            private LaboratorySelection previous;

            internal LaboratorySelection Read(bool match, int pid, long creation, long gameSession)
            {
                int current = Settings.MutationGeneration;
                if (match && previous != null && generation == current && session == gameSession
                    && previous.RendererPid == pid && previous.RendererCreation == creation)
                    return previous;
                LaboratorySelection value = LaboratorySelection.Read(match, pid, creation);
                previous = match && current == Settings.MutationGeneration ? value : null;
                generation = current;
                session = gameSession;
                return value;
            }
        }

        internal bool Epp, Power, Clocks;
        internal string Idle = "default";
        internal int RendererPid;
        internal long RendererCreation;
        internal bool Any { get { return Epp || Power || Clocks || Idle != "default"; } }
        internal bool Same(LaboratorySelection other)
        { return other != null && Epp == other.Epp && Power == other.Power && Clocks == other.Clocks
            && Idle == other.Idle && RendererPid == other.RendererPid && RendererCreation == other.RendererCreation; }
        internal string Identity { get { return Epp + "|" + Power + "|" + Clocks + "|" + Idle + "|" + RendererPid + "|" + RendererCreation; } }
        // 禁止 CPU 空闲这一档已去掉 新 CPU 上会压低睿频 旧值按跟随当前计划处理
        internal static string CanonicalIdle(string value)
        { return value == "threshold" ? value : "default"; }
        internal static bool IsPreference(string key)
        { return key == LaboratoryPower.EppKey || key == LaboratoryPower.IdleKey || key == LaboratoryGpu.PowerKey
            || key == LaboratoryGpu.ClockKey; }
        internal static LaboratorySelection Read(bool match, int rendererPid, long rendererCreation)
        {
            if (!match) return new LaboratorySelection();
            return new LaboratorySelection { Epp = Settings.Load(LaboratoryPower.EppKey, false),
                Power = Settings.Load(LaboratoryGpu.PowerKey, false), Clocks = Settings.Load(LaboratoryGpu.ClockKey, false),
                Idle = CanonicalIdle(Settings.LoadStr(LaboratoryPower.IdleKey, "default")),
                RendererPid = rendererPid, RendererCreation = rendererCreation };
        }
    }

    // 会话工作线程串行执行实验室动作 在释放计划或重置前先完成恢复
    internal sealed class LaboratorySession
    {
        private readonly object gate = new object(), powerGate;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread worker;
        private LaboratorySelection desired = new LaboratorySelection();
        private readonly HashSet<string> failedIntents = new HashSet<string>(StringComparer.Ordinal);
        private readonly LaboratoryFailureBudget recovery = new LaboratoryFailureBudget();
        private long session;
        private bool closing, restored = true;
        private bool powerDirty, gpuDirty;
        private bool recoveryRunning;
        private long recoveryNext;
        private string failure = "";
        private readonly Dictionary<string, string> states = new Dictionary<string, string>();
#if PAVISE_SELFTEST && !PAVISE_LAB_COMPOSITION
        internal Action<LaboratorySelection> ApplyForTest;
        internal Func<bool> RestoreForTest;
        internal Func<long> ClockForTest;
#endif
        private long Now
        {
            get
            {
#if PAVISE_SELFTEST && !PAVISE_LAB_COMPOSITION
                if (ClockForTest != null) return ClockForTest();
#endif
                return DateTime.UtcNow.Ticks;
            }
        }
        internal LaboratorySession(object powerGate, bool recoveryRequired = false)
        {
            this.powerGate = powerGate;
            restored = !recoveryRequired;
#if !PAVISE_SELFTEST || PAVISE_LAB_COMPOSITION
            restored = restored && !LaboratoryPower.HasResidue && !LaboratoryGpu.HasResidue;
#endif
            powerDirty = gpuDirty = !restored;
        }
        internal bool IsRestored { get { lock (gate) return restored; } }
        internal void BeginAutomaticCycle(long value)
        {
            lock (gate)
            {
                if (value <= session) return;
                session = value; failedIntents.Clear(); recovery.BeginSession(value); recoveryNext = 0;
            }
#if !PAVISE_SELFTEST || PAVISE_LAB_COMPOSITION
            LaboratoryPower.BeginAutomaticCycle(value);
            LaboratoryGpu.BeginAutomaticCycle(value);
#endif
            wake.Set();
        }
        internal string Failure { get { lock (gate) return failure; } }
        internal string State(string key)
        { lock (gate) { string state; return states.TryGetValue(key, out state) ? state : PerformancePolicy.Text("等待执行", "Pending"); } }
        private void PublishStates()
        {
#if !PAVISE_SELFTEST || PAVISE_LAB_COMPOSITION
            var next = new Dictionary<string, string>();
            next[LaboratoryPower.EppKey] = LaboratoryPower.State(LaboratoryPower.EppKey);
            next[LaboratoryPower.IdleKey] = LaboratoryPower.State(LaboratoryPower.IdleKey);
            next[LaboratoryGpu.PowerKey] = LaboratoryGpu.State(LaboratoryGpu.PowerKey);
            next[LaboratoryGpu.ClockKey] = LaboratoryGpu.State(LaboratoryGpu.ClockKey);
            lock (gate) foreach (var entry in next) states[entry.Key] = entry.Value;
#endif
        }
        internal void Queue(LaboratorySelection selection)
        {
            lock (gate)
            {
                if (closing) return;
                selection = selection ?? new LaboratorySelection();
                if (!selection.Any && restored) failure = "";
                if (selection.Any && failedIntents.Contains(selection.Identity)) return;
                if (desired.Same(selection) && (worker != null || restored)) return;
                desired = selection;
                failure = "";
                if (desired.Any) restored = false;
                if (worker == null && (desired.Any || !restored))
                {
                    worker = new Thread(Loop) { IsBackground = true, Name = "Pavise.Laboratory", Priority = ThreadPriority.BelowNormal };
                    worker.Start();
                }
                wake.Set();
            }
        }
        private void Apply(LaboratorySelection value, long expectedSession)
        {
#if PAVISE_SELFTEST && !PAVISE_LAB_COMPOSITION
            if (ApplyForTest == null) throw new InvalidOperationException("Unmocked laboratory apply");
            ApplyForTest(value); return;
#else
            lock (gate) { if (session != expectedSession || closing) return; powerDirty = gpuDirty = true; recoveryNext = 0; }
            lock (powerGate)
            {
                if (value.Epp || value.Idle != "default") LaboratoryPower.Update(value.Epp, value.Idle, value.RendererPid, value.RendererCreation, expectedSession);
                else LaboratoryPower.Restore(expectedSession);
            }
            LaboratoryGpu.Update(value.Power, value.Clocks, expectedSession);
#endif
        }
        private bool Restore(long expectedSession)
        {
#if PAVISE_SELFTEST && !PAVISE_LAB_COMPOSITION
            if (RestoreForTest == null) throw new InvalidOperationException("Unmocked laboratory restore");
            return RestoreForTest();
#else
            bool powerPending, gpuPending;
            lock (gate)
            {
                if (session != expectedSession) return false;
                powerPending = powerDirty; gpuPending = gpuDirty;
            }
            bool powerOk = !powerPending, gpuOk = !gpuPending;
            if (gpuPending) try { gpuOk = LaboratoryGpu.Restore(expectedSession); } catch { }
            if (powerPending) try { lock (powerGate) powerOk = LaboratoryPower.Restore(expectedSession); } catch { }
            lock (gate)
            {
                if (session != expectedSession) return false;
                if (powerOk) powerDirty = false;
                if (gpuOk) gpuDirty = false;
            }
            return powerOk && gpuOk;
#endif
        }
        private bool RestoreBounded()
        {
            long capturedSession;
            lock (gate) { capturedSession = session; recoveryRunning = true; }
            bool ok = false;
            using (var trace = new RecoveryAttemptTrace())
            {
                try { ok = RestoreControllers(); }
                finally
                {
                    lock (gate) if (session == capturedSession)
                    {
                        recoveryRunning = false;
                        recoveryNext = ok ? 0 : trace.Reported ? trace.Next : Now + TimeSpan.FromSeconds(1).Ticks;
                    }
                }
            }
            if (!ok) ReportRecoveryWait();
            return ok;
        }
        // 在 gate 内发布调度缓存 同步等待方不能取得原生控制器锁
        private void ReportRecoveryWait()
        {
            lock (gate)
            {
                if (!recoveryRunning && recoveryNext == long.MaxValue) RecoveryAttemptTrace.Exhausted();
                else if (!recoveryRunning && recoveryNext > Now) RecoveryAttemptTrace.DeferredUntil(recoveryNext);
                else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
            }
        }
        private bool RestoreControllers()
        {
            long capturedSession;
            lock (gate) capturedSession = session;
#if PAVISE_SELFTEST && !PAVISE_LAB_COMPOSITION
            lock (gate) if (!recovery.CanAttempt("restore", Now, 3)) { recovery.ReportRecovery("restore", 3); return false; }
            bool ok = false;
            try { ok = Restore(capturedSession); return ok; }
            finally
            {
                lock (gate) if (session == capturedSession)
                {
                    if (ok) recovery.Neutral("restore");
                    else { recovery.Failed("restore", Now); recovery.ReportRecovery("restore", 3); }
                }
            }
#else
            return Restore(capturedSession);
#endif
        }
        private bool RecoveryExhausted()
        {
#if PAVISE_SELFTEST && !PAVISE_LAB_COMPOSITION
            lock (gate) return recovery.Exhausted("restore", 3);
#else
            bool powerPending, gpuPending;
            lock (gate) { powerPending = powerDirty; gpuPending = gpuDirty; }
            return (!powerPending || LaboratoryPower.RecoveryExhausted)
                && (!gpuPending || LaboratoryGpu.RecoveryExhausted);
#endif
        }
        private void Loop()
        {
            while (true)
            {
                LaboratorySelection value; bool end, clean; long capturedSession;
                lock (gate) { value = desired; end = closing; clean = restored; capturedSession = session; }
                try
                {
                    if (!end && value.Any)
                    {
                        bool admitted;
                        lock (gate)
                        {
                            admitted = !closing && ReferenceEquals(desired, value) && session == capturedSession;
                            if (admitted) restored = false;
                        }
                        if (admitted) { Apply(value, capturedSession); PublishStates(); }
                    }
                    else if (!clean)
                    {
                        bool ok = RestoreBounded();
                        PublishStates();
                        lock (gate) { restored = ok; if (ok) failure = ""; Monitor.PulseAll(gate); }
                    }
                }
                catch (Exception error)
                {
                    lock (gate) failure = PerformancePolicy.Text("实验执行异常：", "Lab execution failed: ") + error.GetType().Name;
                    // 同一会话中关闭再开启或 A-B-A 的发布过程 不能为失败配置刷新预算
                    lock (gate) if (ReferenceEquals(desired, value) && session == capturedSession)
                    { if (failedIntents.Add(value.Identity)) Logger.Warn(Logger.Format("实验室选择失败，会话={0} 选择={1} 原因={2}；应用预算已耗尽", "Laboratory selection failed; session={0} selection={1} reason={2}; apply exhausted", capturedSession, value.Identity, error.GetType().Name)); desired = new LaboratorySelection(); }
                    try { bool ok = RestoreBounded(); lock (gate) { restored = ok; Monitor.PulseAll(gate); } } catch { }
                }
                if (end) break;
                bool recoveryExhausted;
                lock (gate) { clean = restored; value = desired; }
                recoveryExhausted = RecoveryExhausted();
                wake.WaitOne(value.Any ? 1000 : clean || recoveryExhausted ? Timeout.Infinite : 5000);
            }
        }
        internal bool WaitForRestore(int timeoutMs)
        {
            Queue(new LaboratorySelection());
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            lock (gate)
            {
                while (!restored)
                {
                    int remaining = Math.Max(0, timeoutMs - (int)elapsed.ElapsedMilliseconds);
                    if (remaining == 0) { ReportRecoveryWait(); return false; }
                    Monitor.Wait(gate, remaining);
                }
                return true;
            }
        }
        internal bool Close(int timeoutMs)
        {
            Thread current;
            lock (gate) { closing = true; desired = new LaboratorySelection(); current = worker; wake.Set(); }
            if (current != null && (current == Thread.CurrentThread || !current.Join(timeoutMs)))
            { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
            bool neutral = IsRestored;
            if (!neutral) ReportRecoveryWait();
            return neutral;
        }
        internal static bool Recover()
        {
            bool ok = true;
            try { if (!LaboratoryPower.Restore()) ok = false; } catch { ok = false; }
            try { if (!LaboratoryGpu.Restore()) ok = false; } catch { ok = false; }
            return ok;
        }
    }
}
