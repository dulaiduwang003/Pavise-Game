using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class AutomaticFailurePhase
        {
            internal int Attempts, Failures;
            internal long Next;
            internal bool InFlight, Stopped;
        }

        private readonly Dictionary<string, AutomaticFailurePhase> automaticRestores = new Dictionary<string, AutomaticFailurePhase>(StringComparer.Ordinal);
        private readonly Dictionary<string, AutomaticFailurePhase> powerAttempts = new Dictionary<string, AutomaticFailurePhase>(StringComparer.Ordinal);
        private readonly Dictionary<string, AutomaticFailurePhase> automaticWorkFailures = new Dictionary<string, AutomaticFailurePhase>(StringComparer.Ordinal);
        private AutomaticFailurePhase powerAttemptInProgress;
        private string powerAttemptSignature;
        private long automaticSessionObserved = -1;

        private void SyncAutomaticRetrySession()
        {
            lock (sync)
            {
                long serial = Interlocked.Read(ref gameSessionStarts);
                if (serial <= automaticSessionObserved) return;
                automaticSessionObserved = serial;
                sessionRecoveryRoutes = null; sessionRecoveryCaptured = false;
                // This is only called at a real game-session boundary. Pending
                // ownership remains; the new session has its own finite budget.
                envNextAttempt.Clear();
                envFailures.Clear();
                residueNextTryTicks = 0;
            }
        }

        // Renderer handoffs retain the game serial; revisiting a configuration reuses its remaining budget.
        private string AutomaticPhaseKey(string signature)
        {
            return Interlocked.Read(ref gameSessionStarts).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + signature;
        }

        private bool AutomaticWorkAllowed(string key)
        {
            lock (sync)
            {
                AutomaticFailurePhase phase;
                return !automaticWorkFailures.TryGetValue(AutomaticPhaseKey(key), out phase)
                    || (phase.Failures < 3 && DateTime.UtcNow.Ticks >= phase.Next);
            }
        }

        private void RecordAutomaticWorkFailure(string key, string reason)
        {
            lock (sync)
            {
                string signature = AutomaticPhaseKey(key);
                AutomaticFailurePhase phase;
                if (!automaticWorkFailures.TryGetValue(signature, out phase)) automaticWorkFailures[signature] = phase = new AutomaticFailurePhase();
                if (phase.Failures >= 3) return;
                phase.Failures++;
                phase.Next = phase.Failures >= 3 ? long.MaxValue : DateTime.UtcNow.AddSeconds(30).Ticks;
                Logger.Warn("Automatic work: action=" + key + " failure=" + phase.Failures + "/3 reason=" + reason
                    + (phase.Failures >= 3 ? "; failed work stopped for this session" : ""));
            }
        }

        private AutomaticFailurePhase PowerAttemptPhase(int policyKey)
        {
            PowerPlan.BeginAutomaticSession(Interlocked.Read(ref gameSessionStarts));
            string signature = AutomaticPhaseKey(PowerPlan.AutomaticPolicySignature((policyKey & 1) != 0));
            ActionFailureStore.BeginAutomaticCycle("PowerPlanOn", "system", signature, 3);
            lock (sync)
            {
                AutomaticFailurePhase phase;
                if (!powerAttempts.TryGetValue(signature, out phase)) powerAttempts[signature] = phase = new AutomaticFailurePhase();
                return phase;
            }
        }

        private bool BeginPowerAttempt(int policyKey, out string reason)
        {
            AutomaticFailurePhase phase = PowerAttemptPhase(policyKey);
            lock (sync)
            {
                if (phase.Stopped || phase.Attempts >= 3 || phase.InFlight || DateTime.UtcNow.Ticks < phase.Next)
                { reason = "power-apply-budget-or-cooldown"; return false; }
            }
            if (!ActionFailureStore.TryBeginApply("PowerPlanOn", "system", out reason)) return false;
            lock (sync)
            {
                phase.Attempts++;
                phase.InFlight = true;
                powerAttemptInProgress = phase;
                powerAttemptSignature = PowerPlan.AutomaticPolicySignature((policyKey & 1) != 0);
            }
            return true;
        }

        private void CompletePowerAttempt(bool success, string reason, bool permanent = false)
        {
            lock (sync)
            {
                AutomaticFailurePhase phase = powerAttemptInProgress;
                if (phase == null || !phase.InFlight) return;
                int completedAttempt = phase.Attempts;
                phase.InFlight = false;
                if (!success)
                {
                    phase.Failures++;
                    phase.Stopped |= permanent || phase.Attempts >= 3;
                    phase.Next = phase.Stopped ? long.MaxValue : DateTime.UtcNow.AddSeconds(phase.Attempts == 1 ? 5 : 30).Ticks;
                }
                else
                {
                    // A verified transaction ends this failed-apply episode.
                    // Ordinary healthy audits never enter this method.
                    phase.Attempts = 0;
                    phase.Failures = 0;
                    phase.Next = 0;
                }
                Logger.Log("Power plan: signature=" + powerAttemptSignature + " attempt=" + completedAttempt + "/3"
                    + " result=" + (success ? "verified" : "failed") + " reason=" + reason
                    + " recovery=" + (envRestorePending.Contains("powerplan") ? "pending" : "verified-or-not-required")
                    + (phase.Stopped ? "; automatic apply stopped for this game/signature" : ""));
                powerAttemptInProgress = null;
            }
        }

        private bool RunAutomaticRestore(string key, Func<bool> restore)
        {
            bool skipped; string reason;
            return RunAutomaticRestore(key, restore, out skipped, out reason);
        }

        private bool RunAutomaticRestore(string key, Func<bool> restore, out bool skipped, out string reason)
        {
            if (!CapturingSessionRecovery) return RunAutomaticRestoreCore(key, restore, out skipped, out reason);
            bool localSkipped = true; string localReason = "recovery-owner-deferred";
            RecoveryRouteSet owner = recoveryCaptureRoutes;
            long serial = owner.Session;
            bool result = RunSessionRecoveryRoute("env:" + key, delegate
            {
                bool neutral = RunAutomaticRestoreCore(key, restore, out localSkipped, out localReason, serial, owner);
                if (neutral && serial == Interlocked.Read(ref gameSessionStarts) && object.ReferenceEquals(owner, sessionRecoveryRoutes)) SessionEnvironmentNeutral(key);
                return neutral;
            }, EnvironmentRecoveryDependencies(key));
            skipped = localSkipped; reason = localReason; return result;
        }

        private bool RunAutomaticRestoreCore(string key, Func<bool> restore, out bool skipped, out string reason, long expectedSession = -1, RecoveryRouteSet routeOwner = null)
        {
            long serial = expectedSession < 0 ? Interlocked.Read(ref gameSessionStarts) : expectedSession;
            string signature = serial.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + key;
            AutomaticFailurePhase phase;
            lock (sync)
            {
                if (serial != Interlocked.Read(ref gameSessionStarts) || (routeOwner != null && !object.ReferenceEquals(routeOwner, sessionRecoveryRoutes)))
                { RecoveryAttemptTrace.Exhausted(); skipped = true; reason = "superseded recovery owner"; return false; }
                if (!automaticRestores.TryGetValue(signature, out phase)) automaticRestores[signature] = phase = new AutomaticFailurePhase();
                if (phase.InFlight || phase.Failures >= 3 || DateTime.UtcNow.Ticks < phase.Next)
                {
                    if (phase.Failures >= 3) RecoveryAttemptTrace.Exhausted();
                    else if (phase.InFlight) RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                    else RecoveryAttemptTrace.DeferredUntil(phase.Next);
                    skipped = true; reason = phase.Failures >= 3 ? "restore-budget-exhausted; ownership retained" : "restore-pending-or-backoff"; return false;
                }
                phase.InFlight = true;
                phase.Attempts++;
            }
            bool restored = false;
            reason = "restore-interrupted";
            skipped = false;
            using (var child = new RecoveryAttemptTrace())
            {
            try { restored = RunEnvironmentAction(restore, out skipped, out reason); return restored; }
            finally
            {
                lock (sync)
                {
                    phase.InFlight = false;
                    bool current = serial == Interlocked.Read(ref gameSessionStarts)
                        && (routeOwner == null || object.ReferenceEquals(routeOwner, sessionRecoveryRoutes));
                    if (!restored && child.Reported)
                    {
                        // This callback reached a child owner with its own finite
                        // schedule. A deferred child is not a new outer failure.
                        if (current) { envRestorePending.Add(key); envNextAttempt[key] = child.Next; }
                    }
                    else if (!restored)
                    {
                        phase.Failures++;
                        phase.Next = phase.Failures >= 3 ? long.MaxValue : DateTime.UtcNow.AddSeconds(phase.Failures == 1 ? 4 : 8).Ticks;
                        if (current) { envFailures[key] = phase.Failures; envRestorePending.Add(key); envNextAttempt[key] = phase.Next; }
                        RecoveryAttemptTrace.FailureSchedule(phase.Failures, 3, phase.Next);
                        Logger.Warn("Automatic recovery: action=" + key + " failure=" + phase.Failures + "/3 reason=" + reason
                            + (phase.Failures >= 3 ? "; active attempts stopped; ownership retained" : "; recovery pending"));
                    }
                    else
                    {
                        // This callback must verify that the owned recovery debt
                        // is neutral; it is not a generic health observation.
                        phase.Failures = 0;
                        phase.Next = 0;
                        if (current) { envRestorePending.Remove(key); envNextAttempt.Remove(key); envFailures.Remove(key); }
                    }
                }
                if (!restored) child.Propagate();
            }
            }
        }
    }
}
