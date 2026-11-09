using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class PerformanceTuning
    {
        private sealed class EnvironmentChangeResult
        {
            public bool Succeeded = true;
            public bool Changed;
            public bool Stopped;
            public bool RecoveryFailed;

            public string ResolveState(string id, bool on)
            {
                if (!Succeeded)
                {
                    if (!on || RecoveryFailed) return "restore-failed";
                    return Stopped ? "stopped" : "failed";
                }
                if (Changed) return on ? "configured" : "restored";
                return VerifiedEnvironmentState(id, on);
            }
        }

        internal static bool ApplyEnvironment(string id, bool on)
        {
            if (id == PerformanceActionCatalog.Quantum) QuantumLiveUpdate.Begin();
            if (EnvironmentWorkClosing) return false;

            var result = new EnvironmentChangeResult();
            if (on)
            {
                string[] targets = null;
                var discovery = new PerformanceActionStore.Receipt { Id = "environment-discovery:" + id, Target = "system" };
                if (!RestoreWithBudget(discovery, 3, delegate
                {
                    targets = PerformanceActionBackend.EnvironmentTargets(id);
                    return true;
                }))
                {
                    SetState(id, "stopped");
                    return false;
                }
                if (targets.Length == 0)
                {
                    SetState(id, "unsupported");
                    return false;
                }
                if (id == "EnvCpuMitigationsV1") return ApplyCpuMitigationsGroup(targets);

                foreach (string target in targets)
                {
                    ActionFailureStore.BeginAutomaticCycle(id, target, EnvironmentAutomaticCycle(id));
                    if (EnvironmentWorkClosing) return false;
                    ApplyEnvironmentTarget(id, target, result);
                }
            }
            else if (id == PerformanceActionCatalog.Quantum)
            {
                if (EnvironmentWorkClosing) return false;
                RestoreQuantumEnvironment(id, result);
            }
            else if (!RestoreEnvironmentTargets(id, result))
            {
                return false;
            }

            string state = result.ResolveState(id, on);
            if (result.Succeeded && id == PerformanceActionCatalog.Quantum)
                state = QuantumLiveUpdate.Complete(on, state);
            SetState(id, state);

            PerformanceAction action = PerformanceActionCatalog.Find(id);
            if (action != null)
            {
                string message = action.Title + " | " + Label(State(id));
                if (result.Succeeded) Logger.Info(message);
                else Logger.Warn(message);
            }
            return result.Succeeded;
        }

        private static void ApplyEnvironmentTarget(string id, string target, EnvironmentChangeResult result)
        {
            if (ActionFailureStore.IsIsolated(id, target) && !RestoreEnvironmentTarget(id, target))
            {
                ActionFailureStore.Trip(id, target, "Recovery incomplete before retry");
                result.Succeeded = false;
                result.Stopped = true;
                result.RecoveryFailed = true;
                return;
            }

            string reason;
            ActionFailureStore.BeginAutomaticCycle(id, target, EnvironmentAutomaticCycle(id));
            if (!ActionFailureStore.TryBeginApply(id, target, out reason))
            {
                result.Succeeded = false;
                result.Stopped = true;
                return;
            }

            try
            {
                if (id == PerformanceActionCatalog.Quantum && QuantumRecovery.HasLegacyRecords)
                {
                    if (!SaveEnvironmentBoot(id))
                    {
                        ActionFailureStore.CancelApply(id, target);
                        result.Succeeded = false;
                        return;
                    }
                    var legacy = new PerformanceActionStore.Receipt { Id = id, Target = "legacy-quantum" };
                    bool restored = RestoreWithBudget(legacy, 3, QuantumRecovery.RestoreAll);
                    result.Changed |= QuantumRecovery.LastWrote;
                    if (!restored)
                    {
                        ActionFailureStore.CancelApply(id, target);
                        Logger.Warn(id + " | " + QuantumRecovery.LastFailure);
                        result.Succeeded = false;
                        result.RecoveryFailed = true;
                        return;
                    }
                }

                string current = PerformanceActionBackend.Read(id, target);
                string desired = PerformanceActionBackend.WantedRegistry(id, current);
                if (desired != current)
                {
                    // 第一次系统写入前，先持久保存恢复上下文。
                    if (!SaveEnvironmentBoot(id))
                    {
                        ActionFailureStore.CancelApply(id, target);
                        result.Succeeded = false;
                        return;
                    }
                    result.Changed = true;
                }
                if (!PerformanceActionBackend.Apply(id, target, desired))
                    throw new InvalidOperationException(PerformanceActionBackend.ApplyFailureHint(id, target));

                if (!ActionFailureStore.CompleteApply(id, target))
                {
                    result.RecoveryFailed |= !RestoreEnvironmentTarget(id, target);
                    result.Succeeded = false;
                    result.Stopped = true;
                }
            }
            catch (NotSupportedException)
            {
                CancelEnvironmentTarget(id, target, "Recovery incomplete after unsupported apply", result);
            }
            catch (OperationCanceledException)
            {
                CancelEnvironmentTarget(id, target, "Recovery incomplete after cancelled apply", result);
            }
            catch (Exception error)
            {
                ActionFailureStore.Trip(id, target, error.Message);
                result.RecoveryFailed |= !RestoreEnvironmentTarget(id, target);
                result.Succeeded = false;
                result.Stopped = true;
                Logger.Warn(id + " | " + error.Message);
            }
        }

        private static bool SaveEnvironmentBoot(string id)
        {
            string boot = Boot();
            if (!BootSessionIdentity.Valid(boot)) return false;
            if (!Settings.SaveStrDurable(id + "Boot", boot))
                throw new InvalidOperationException("Boot journal persistence failed");
            return true;
        }

        private static bool RestoreEnvironmentTarget(string id, string target)
        {
            return RestoreWhere(receipt => receipt.Id == id
                && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase));
        }

        private static void CancelEnvironmentTarget(string id, string target, string recoveryFailure, EnvironmentChangeResult result)
        {
            if (RestoreEnvironmentTarget(id, target)) ActionFailureStore.CancelApply(id, target);
            else
            {
                ActionFailureStore.Trip(id, target, recoveryFailure);
                result.RecoveryFailed = true;
            }
            result.Succeeded = false;
        }

        private static void RestoreQuantumEnvironment(string id, EnvironmentChangeResult result)
        {
            try
            {
                if (!QuantumRecovery.HasRecords()) return;
                string boot = Boot();
                if (!BootSessionIdentity.Valid(boot) || !Settings.SaveStrDurable(id + "Boot", boot))
                {
                    result.Succeeded = false;
                    return;
                }
                result.Changed = true;
                var receipt = new PerformanceActionStore.Receipt { Id = id, Target = "legacy-quantum" };
                if (!RestoreWithBudget(receipt, 3, QuantumRecovery.RestoreAll)) result.Succeeded = false;
            }
            catch (Exception error)
            {
                result.Succeeded = false;
                Logger.Warn(id + " | " + error.Message);
            }
        }

        private static bool RestoreEnvironmentTargets(string id, EnvironmentChangeResult result)
        {
            List<PerformanceActionStore.Receipt> receipts;
            if (!ReadRecoveryReceipts(out receipts))
            {
                SetState(id, "restore-failed");
                return false;
            }
            foreach (PerformanceActionStore.Receipt receipt in receipts)
            {
                if (receipt.Id != id) continue;
                if (EnvironmentWorkClosing) return false;
                try
                {
                    result.Changed = true;
                    if (!RestorePerformanceReceipt(receipt, delegate
                    {
                        string boot = Boot();
                        return BootSessionIdentity.Valid(boot) && Settings.SaveStrDurable(id + "Boot", boot);
                    })) result.Succeeded = false;
                }
                catch (Exception error)
                {
                    result.Succeeded = false;
                    Logger.Warn(id + " | " + error.Message);
                }
            }
            return true;
        }
    }
}
