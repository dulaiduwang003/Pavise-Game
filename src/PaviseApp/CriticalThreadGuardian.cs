using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static partial class CriticalThreadGuardian
    {

        internal const string Command = "--hard-thread-guardian";

        internal const string StoreDirectory = "CriticalThreadHardAffinityV1";

        private const string GuardianConfiguration = "<configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.7.2\"/></startup></configuration>";

        private static readonly object controllersGate = new object();

        private static readonly List<ParentGuardian> controllers = new List<ParentGuardian>();

        private static readonly object proofGate = new object();

        private static object issuingProof;

        private static readonly string[] SettledLeftoverNames = new string[3] { "Pavise.HardThreadGuardian.exe.config", "Pavise.HardThreadGuardian.exe", "scope.xml" };

        private static volatile bool settledLeftovers = true;

        private static readonly object historicalGate = new object();

        private static HistoricalRegistration issuingHistorical;
        private static readonly Dictionary<string, int> historicalFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> historicalInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HistoricalTargetObservation> historicalTargets = new Dictionary<string, HistoricalTargetObservation>(StringComparer.OrdinalIgnoreCase);

internal static bool RetainRecoveryAcrossTargetExit(Func<bool> settle, Action<int> wait, Func<bool> targetGone, Func<bool> waitForTargetExit, int initialDelay)
        {
            bool exitPhase = false;
            try { exitPhase = targetGone(); } catch { }
            int attempts = 0, delay = initialDelay;
            while (true)
            {
                bool gone = exitPhase;
                if (!gone) { try { gone = targetGone(); } catch { } }
                if (!exitPhase && gone)
                {
                    exitPhase = true; attempts = 0; delay = initialDelay;
                }
                bool settled = false;
                attempts++;
                try { settled = settle(); } catch { }
                if (settled) return true;
                Logger.Warn("守护恢复：阶段=" + (exitPhase ? "target-exit" : "live") + " 尝试=" + attempts + "/3；保留原所有权");
                if (attempts >= 3)
                {
                    if (exitPhase) return false;
                    bool exited = false;
                    try { exited = waitForTargetExit(); } catch { }
                    if (!exited) return false;
                    exitPhase = true; attempts = 0; delay = initialDelay;
                    continue;
                }
                try { wait(delay); } catch { Thread.Sleep(delay); }
                delay *= 2;
            }
        }

        internal static bool TryRun(string[] args, string originalStartupData, out int exitCode)
        {
            exitCode = 0;
            bool guardianRequested = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--hard-thread-guardian", StringComparison.OrdinalIgnoreCase))
                    {
                        guardianRequested = true;
                    }
                }
            }
            if (!guardianRequested)
            {
                return false;
            }
            exitCode = 13;
            return RunNative(args, originalStartupData, out exitCode);
        }

        internal static bool HasControllerDebt(string canonicalRoot)
        {
            string b = Canonical(canonicalRoot);
            lock (controllersGate)
            {
                foreach (ParentGuardian controller in controllers)
                {
                    if (!controller.controllerReleased && string.Equals(controller.ControlledRoot, b, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void RegisterController(ParentGuardian controller)
        {
            lock (controllersGate)
            {
                foreach (ParentGuardian controller2 in controllers)
                {
                    if (!controller2.controllerReleased && string.Equals(controller2.ControlledRoot, controller.ControlledRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "an original controller still owns this root");
                    }
                }
                controllers.Add(controller);
            }
        }

        private static void RemoveController(ParentGuardian controller)
        {
            lock (controllersGate)
            {
                controllers.Remove(controller);
            }
        }
    }
}
