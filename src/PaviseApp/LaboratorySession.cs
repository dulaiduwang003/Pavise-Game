using System;
using System.Threading;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class LaboratorySelection
    {
        internal bool Epp, Power, Clocks, Audio;
        internal string Idle = "default";
        internal int RendererPid;
        internal long RendererCreation;
        internal bool Any { get { return Epp || Power || Clocks || Audio || Idle != "default"; } }
        internal bool Same(LaboratorySelection other)
        { return other != null && Epp == other.Epp && Power == other.Power && Clocks == other.Clocks && Audio == other.Audio
            && Idle == other.Idle && RendererPid == other.RendererPid && RendererCreation == other.RendererCreation; }
        internal string Identity { get { return Epp + "|" + Power + "|" + Clocks + "|" + Audio + "|" + Idle + "|" + RendererPid + "|" + RendererCreation; } }
        internal static string CanonicalIdle(string value)
        { return value == "threshold" || value == "disabled" ? value : "default"; }
        internal static bool IsPreference(string key)
        { return key == LaboratoryPower.EppKey || key == LaboratoryPower.IdleKey || key == LaboratoryGpu.PowerKey
            || key == LaboratoryGpu.ClockKey || key == LaboratoryAudio.Key; }
        internal static LaboratorySelection Read(bool match, int rendererPid, long rendererCreation)
        {
            if (!match) return new LaboratorySelection();
            return new LaboratorySelection { Epp = Settings.Load(LaboratoryPower.EppKey, false),
                Power = Settings.Load(LaboratoryGpu.PowerKey, false), Clocks = Settings.Load(LaboratoryGpu.ClockKey, false),
                Audio = Settings.Load(LaboratoryAudio.Key, false),
                Idle = CanonicalIdle(Settings.LoadStr(LaboratoryPower.IdleKey, "default")),
                RendererPid = rendererPid, RendererCreation = rendererCreation };
        }
    }

    // The session worker serializes lab actions and drains restoration before plan release or reset.
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
        private bool powerDirty, gpuDirty, audioDirty;
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
            powerDirty = gpuDirty = audioDirty = !restored;
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
            next[LaboratoryAudio.Key] = LaboratoryAudio.State;
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
            lock (gate) { if (session != expectedSession || closing) return; powerDirty = gpuDirty = true; audioDirty |= value.Audio; recoveryNext = 0; }
            lock (powerGate)
            {
                if (value.Epp || value.Idle != "default") LaboratoryPower.Update(value.Epp, value.Idle, value.RendererPid, value.RendererCreation, expectedSession);
                else LaboratoryPower.Restore(expectedSession);
            }
            LaboratoryGpu.Update(value.Power, value.Clocks, expectedSession);
            lock (gate) if (session != expectedSession || closing) return;
            LaboratoryAudio.Update(value.Audio);
#endif
        }
        private bool Restore(long expectedSession)
        {
#if PAVISE_SELFTEST && !PAVISE_LAB_COMPOSITION
            if (RestoreForTest == null) throw new InvalidOperationException("Unmocked laboratory restore");
            return RestoreForTest();
#else
            bool powerPending, gpuPending, audioPending;
            lock (gate)
            {
                if (session != expectedSession) return false;
                powerPending = powerDirty; gpuPending = gpuDirty; audioPending = audioDirty;
            }
            bool powerOk = !powerPending, gpuOk = !gpuPending, audioOk = !audioPending;
            if (audioPending)
            {
                long owner = LaboratoryAudio.OwnerSerial;
                string identity = "audio|" + owner;
                bool admitted;
                lock (gate)
                {
                    admitted = session == expectedSession && recovery.CanAttempt(identity, Now, 3);
                    if (!admitted && session == expectedSession) recovery.ReportRecovery(identity, 3);
                }
                if (admitted)
                {
                    try { audioOk = LaboratoryAudio.Restore(owner); } catch { }
                    lock (gate) if (session == expectedSession)
                    {
                        if (audioOk) recovery.Neutral(identity);
                        else { recovery.Failed(identity, Now, 3, "audio"); recovery.ReportRecovery(identity, 3); }
                    }
                }
            }
            if (gpuPending) try { gpuOk = LaboratoryGpu.Restore(expectedSession); } catch { }
            if (powerPending) try { lock (powerGate) powerOk = LaboratoryPower.Restore(expectedSession); } catch { }
            lock (gate)
            {
                if (session != expectedSession) return false;
                if (powerOk) powerDirty = false;
                if (gpuOk) gpuDirty = false;
                if (audioOk) audioDirty = false;
            }
            return powerOk && gpuOk && audioOk;
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
        // Publish cached scheduling under gate; synchronous waiters must not acquire native controller locks.
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
            bool powerPending, gpuPending, audioPending;
            lock (gate) { powerPending = powerDirty; gpuPending = gpuDirty; audioPending = audioDirty; }
            bool audioStopped;
            long audioOwner = LaboratoryAudio.OwnerSerial;
            lock (gate) audioStopped = !audioPending || recovery.Exhausted("audio|" + audioOwner, 3);
            return (!powerPending || LaboratoryPower.RecoveryExhausted)
                && (!gpuPending || LaboratoryGpu.RecoveryExhausted) && audioStopped;
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
                    // Off/on and A-B-A publication do not renew the failed selection in this session.
                    lock (gate) if (ReferenceEquals(desired, value) && session == capturedSession)
                    { if (failedIntents.Add(value.Identity)) Logger.Warn("Laboratory selection failed; session=" + capturedSession + " selection=" + value.Identity + " reason=" + error.GetType().Name + "; apply exhausted"); desired = new LaboratorySelection(); }
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
