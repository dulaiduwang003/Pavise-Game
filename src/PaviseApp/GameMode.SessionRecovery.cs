using System;
using System.Threading;
namespace PaviseApp
{
    internal partial class GameMode
    {
        [ThreadStatic] private static GameMode recoveryCaptureOwner;
        [ThreadStatic] private static RecoveryRouteSet recoveryCaptureRoutes;
        private volatile RecoveryRouteSet sessionRecoveryRoutes;
        private volatile bool sessionRecoveryCaptured;
        private bool SessionRecoveryPending
        {
            get
            {
                RecoveryRouteSet routes = sessionRecoveryRoutes;
                return sessionRecoveryCaptured && routes != null && routes.Session == Interlocked.Read(ref gameSessionStarts) && !routes.Complete;
            }
        }
        private sealed class RecoveryCapture : IDisposable
        {
            private readonly GameMode previous;
            private readonly RecoveryRouteSet previousRoutes;
            internal RecoveryCapture(GameMode owner)
            { previous = recoveryCaptureOwner; previousRoutes = recoveryCaptureRoutes; recoveryCaptureOwner = owner; recoveryCaptureRoutes = owner.sessionRecoveryRoutes; }
            public void Dispose() { recoveryCaptureOwner = previous; recoveryCaptureRoutes = previousRoutes; }
        }
        private bool CapturingSessionRecovery { get { return object.ReferenceEquals(recoveryCaptureOwner, this); } }
        private IDisposable BeginSessionRecoveryCapture()
        {
            long serial = Interlocked.Read(ref gameSessionStarts);
            if (sessionRecoveryRoutes == null || sessionRecoveryRoutes.Session != serial || sessionRecoveryRoutes.Complete)
            { sessionRecoveryRoutes = new RecoveryRouteSet(serial, () => DateTime.UtcNow.Ticks); sessionRecoveryCaptured = false; }
            return new RecoveryCapture(this);
        }
        private bool RunSessionRecoveryRoute(string key, Func<bool> work, params string[] dependencies)
        {
            if (!CapturingSessionRecovery) return work();
            RecoveryRouteSet owner = recoveryCaptureRoutes;
            return owner.Capture(key, delegate
            {
                if (!object.ReferenceEquals(owner, sessionRecoveryRoutes) || owner.Session != Interlocked.Read(ref gameSessionStarts))
                { RecoveryAttemptTrace.Exhausted(); return false; }
                return work();
            }, dependencies, SessionRecoveryIntent(key));
        }
        private bool? SessionRecoveryIntent(string key)
        {
            if (key == "guard") return enabled && !stopping && !ProfileStoreSaveFailed;
            if (key == "nv-profiles") return NvGameResidueGate(true);
            if (key == "env:gpupref") return GpuPrefResidueGate(true);
            return null;
        }
        private void RefreshSessionRecoveryIntents()
        {
            RecoveryRouteSet routes = sessionRecoveryRoutes;
            if (routes == null || routes.Session != Interlocked.Read(ref gameSessionStarts)) return;
            bool changed = routes.UpdateIntent("guard", SessionRecoveryIntent("guard").Value)
                | routes.UpdateIntent("nv-profiles", SessionRecoveryIntent("nv-profiles").Value)
                | routes.UpdateIntent("env:gpupref", SessionRecoveryIntent("env:gpupref").Value);
            if (changed)
            {
                routes.InvalidateNeutral("verify");
                if (object.ReferenceEquals(routes, sessionRecoveryRoutes)) residueNextTryTicks = routes.Next;
            }
        }
        private bool RunRecoveryWait(string key, Func<bool> complete)
        {
            return RunSessionRecoveryRoute(key, delegate
            {
                if (complete()) return true;
                // Waiting on an existing worker is observation, not a restore attempt.
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false;
            });
        }
        // Wake for scheduled recovery; exhausted routes provide no deadline and remain idle.
        private int LoopWaitMs(int scanWaitMs)
        {
            if (!SessionRecoveryPending) return scanWaitMs;
            long due = Interlocked.Read(ref residueNextTryTicks);
            if (due == long.MaxValue) return scanWaitMs;
            long remaining = (due - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
            return (int)Math.Min(scanWaitMs, Math.Max(250L, remaining));
        }
        private bool RetrySessionRecoveryRoutes(string reason)
        {
            RecoveryRouteSet routes = sessionRecoveryRoutes;
            if (routes == null || !sessionRecoveryCaptured) return Deactivate(reason, true);
            RefreshSessionRecoveryIntents();
            bool restored = routes.RetryDue();
            if (!object.ReferenceEquals(routes, sessionRecoveryRoutes) || routes.Session != Interlocked.Read(ref gameSessionStarts)) return false;
            residueNextTryTicks = routes.Next;
            if (restored)
            {
                RecordAutomation("session", "restored");
            }
            else if (!routes.RetryScheduled)
            {
                // Every bounded retry is spent; ownership and journals stay retained.
                RecordAutomation("session", "restore-failed");
            }
            return restored;
        }
        private static string[] EnvironmentRecoveryDependencies(string key)
        {
            if (key == "gpupower" || key == "gpuclock") return new[] { "laboratory" };
            if (key == "powerplan" || key == "powerpark") return new[] { "laboratory", "laptop" };
            return null;
        }
        private void SessionEnvironmentNeutral(string key)
        {
            switch (key)
            {
                case "do": doActive = false; break;
                case "wu": wuActive = false; break;
                case "maint": maintActive = false; break;
                case "services": optionalServicesActive = false; break;
                case "pqos": pqosActive = false; break;
                case "awake": awakeActive = false; break;
                case "dwmboost": dwmBoostActive = false; break;
                case "rsr": rsrActive = false; break;
                case "gpupower": gpwActive = false; break;
                case "nvvrr": nvVrrActive = false; break;
                case "intelend": intelEndActive = false; break;
                case "amdalag": amdAlagActive = false; break;
                case "amdafmf": amdAfmfActive = false; break;
                case "powerplan": planActive = false; lastPowerPolicyKey = -1; Interlocked.Exchange(ref nextPowerAuditTicks, 0L); break;
            }
        }
    }
}
