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

    // A single session worker owns all lab actions. UI and game scanning only publish intent.
    // Restore is drained before the ordinary power plan is released or user data is reset.
    internal sealed class LaboratorySession
    {
        private readonly object gate = new object(), powerGate;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread worker;
        private LaboratorySelection desired = new LaboratorySelection();
        private LaboratorySelection failedIntent;
        private bool closing, restored = true;
        private string failure = "";
        private readonly Dictionary<string, string> states = new Dictionary<string, string>();
#if PAVISE_SELFTEST
        internal Action<LaboratorySelection> ApplyForTest;
        internal Func<bool> RestoreForTest;
#endif
        internal LaboratorySession(object powerGate, bool recoveryRequired = false)
        {
            this.powerGate = powerGate;
            restored = !recoveryRequired;
#if !PAVISE_SELFTEST
            restored = restored && !LaboratoryPower.HasResidue && !LaboratoryGpu.HasResidue;
#endif
        }
        internal bool IsRestored { get { lock (gate) return restored; } }
        internal string Failure { get { lock (gate) return failure; } }
        internal string State(string key)
        { lock (gate) { string state; return states.TryGetValue(key, out state) ? state : PerformancePolicy.Text("等待执行", "Pending"); } }
        private void PublishStates()
        {
#if !PAVISE_SELFTEST
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
                if (!selection.Any) { failedIntent = null; if (restored) failure = ""; }
                if (failedIntent != null && failedIntent.Same(selection)) return;
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
        private void Apply(LaboratorySelection value)
        {
#if PAVISE_SELFTEST
            if (ApplyForTest == null) throw new InvalidOperationException("Unmocked laboratory apply");
            ApplyForTest(value); return;
#else
            lock (powerGate)
            {
                if (value.Epp || value.Idle != "default") LaboratoryPower.Update(value.Epp, value.Idle, value.RendererPid, value.RendererCreation);
                else LaboratoryPower.Restore();
            }
            LaboratoryGpu.Update(value.Power, value.Clocks);
            LaboratoryAudio.Update(value.Audio);
#endif
        }
        private bool Restore()
        {
#if PAVISE_SELFTEST
            if (RestoreForTest == null) throw new InvalidOperationException("Unmocked laboratory restore");
            return RestoreForTest();
#else
            bool ok = true;
            try { if (!LaboratoryAudio.Restore()) ok = false; } catch { ok = false; }
            try { if (!LaboratoryGpu.Restore()) ok = false; } catch { ok = false; }
            try { lock (powerGate) if (!LaboratoryPower.Restore()) ok = false; } catch { ok = false; }
            return ok;
#endif
        }
        private void Loop()
        {
            while (true)
            {
                LaboratorySelection value; bool end, clean;
                lock (gate) { value = desired; end = closing; clean = restored; }
                try
                {
                    if (!end && value.Any)
                    {
                        bool admitted;
                        lock (gate)
                        {
                            admitted = !closing && ReferenceEquals(desired, value);
                            if (admitted) restored = false;
                        }
                        if (admitted) { Apply(value); PublishStates(); }
                    }
                    else if (!clean)
                    {
                        bool ok = Restore();
                        PublishStates();
                        lock (gate) { restored = ok; if (ok) failure = ""; Monitor.PulseAll(gate); }
                    }
                }
                catch (Exception error)
                {
                    lock (gate) failure = PerformancePolicy.Text("实验执行异常：", "Lab execution failed: ") + error.GetType().Name;
                    // Exceptions close this intent; only a fresh UI/session publication can enable it again.
                    lock (gate) if (ReferenceEquals(desired, value)) { failedIntent = value; desired = new LaboratorySelection(); }
                    try { bool ok = Restore(); lock (gate) { restored = ok; Monitor.PulseAll(gate); } } catch { }
                }
                if (end) break;
                lock (gate) { clean = restored; value = desired; }
                wake.WaitOne(value.Any ? 1000 : clean ? Timeout.Infinite : 5000);
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
                    if (remaining == 0) return false;
                    Monitor.Wait(gate, remaining);
                }
                return true;
            }
        }
        internal bool Close(int timeoutMs)
        {
            Thread current;
            lock (gate) { closing = true; desired = new LaboratorySelection(); current = worker; wake.Set(); }
            if (current != null && (current == Thread.CurrentThread || !current.Join(timeoutMs))) return false;
            return IsRestored;
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
