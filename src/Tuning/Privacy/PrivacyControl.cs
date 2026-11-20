using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal static class PrivacyControl
    {
        internal const string PreferencePrefix = "PrivacySetting_";

        private static readonly object gate = new object();

        private static readonly Dictionary<string, PrivacyResult> states = new Dictionary<string, PrivacyResult>();

        private static IPrivacyBackend backend;

        private static bool working;

        private static bool closing;

        private static long retiredRecoveryAt;

        private static int retiredRecoveryFailures;

        // 退役项不会再产生新回执 确认一次已全部结清后 本次运行不再扫描
        private static bool retiredRecoveryClean;

        private static string activeAction;
#if PAVISE_SELFTEST
        internal static Func<WaitCallback, bool> QueueForTest;
        internal static Func<string> BootForTest;
        internal static void ResetForTest()
        { lock (gate) { if (working) throw new InvalidOperationException("Privacy work is still running"); states.Clear(); closing = false; activeAction = null; backend = null; QueueForTest = null; BootForTest = null; retiredRecoveryAt = 0; retiredRecoveryFailures = 0; } }
#endif

        internal static bool Busy
        {
            get
            {
                lock (gate)
                {
                    return working;
                }
            }
        }

        private static bool IsClosing
        {
            get
            {
                lock (gate)
                {
                    return closing;
                }
            }
        }

        internal static bool IsAction(string action)
        {
            return PrivacyActionCatalog.Find(action) != null;
        }

        private static PrivacyPlan RetiredPlan()
        {
            PrivacyPlan privacyPlan = new PrivacyPlan();
            privacyPlan.UnavailableReason = "retired";
            return privacyPlan;
        }

        internal static void ClearRetiredPreferences()
        {
            PrivacyAction[] retired = PrivacyActionCatalog.Retired;
            foreach (PrivacyAction privacyAction in retired)
            {
                Settings.Remove(PreferenceKey(privacyAction.Key));
            }
        }

        internal static string CanonicalTarget(string target)
        {
            if (target == null)
            {
                return null;
            }
            if (!target.StartsWith("ctx|", StringComparison.Ordinal))
            {
                return target;
            }
            string[] targetParts = target.Split(new char[1] { '|' }, 4);
            Guid result;
            if (targetParts.Length != 4 || !Guid.TryParseExact(targetParts[1], "N", out result) || result == Guid.Empty || string.IsNullOrEmpty(targetParts[2]) || string.IsNullOrEmpty(targetParts[3]) || targetParts[3].StartsWith("ctx|", StringComparison.Ordinal))
            {
                throw new FormatException("Invalid privacy target context");
            }
            return targetParts[3];
        }

        private static IPrivacyBackend EnsureBackend()
        {
            lock (gate)
            {
                if (backend == null)
                {
#if PAVISE_SELFTEST
                    throw new InvalidOperationException("Unmocked privacy backend");
#endif
                    backend = new PrivacyNativeBackend();
                }
                return backend;
            }
        }

        internal static void Bind(IPrivacyBackend value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }
            lock (gate)
            {
                if (working)
                {
                    throw new InvalidOperationException("Privacy work is running");
                }
                backend = value;
            }
        }

        private static string PreferenceKey(string action)
        {
            return "PrivacySetting_" + action;
        }

        internal static bool PreferenceEnabled(string action)
        {
            return PreferenceEnabled(action, "");
        }

        internal static bool PreferenceEnabled(string action, string selectedTarget)
        {
            string value;
            if (!PrivacyActionCatalog.IsRetired(action) && Settings.TryLoadStr(PreferenceKey(action), out value))
            {
                return value == "1";
            }
            return false;
        }

        private static PrivacyResult Copy(PrivacyResult r)
        {
            PrivacyResult privacyResult = new PrivacyResult();
            privacyResult.State = r.State;
            privacyResult.Detail = r.Detail;
            privacyResult.SelectedTarget = r.SelectedTarget;
            privacyResult.UnavailableReason = r.UnavailableReason;
            privacyResult.Configured = r.Configured;
            privacyResult.Busy = r.Busy;
            privacyResult.HasRecovery = r.HasRecovery;
            return privacyResult;
        }

        internal static PrivacyResult State(string action)
        {
            return State(action, "");
        }

        internal static PrivacyResult State(string action, string selectedTarget)
        {
            PrivacyResult value;
            lock (gate)
            {
                if (!states.TryGetValue(action ?? "", out value) || !string.Equals(value.SelectedTarget ?? "", selectedTarget ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    PrivacyResult privacyResult = new PrivacyResult();
                    privacyResult.State = "unverified";
                    privacyResult.Detail = "";
                    privacyResult.SelectedTarget = selectedTarget ?? "";
                    value = privacyResult;
                }
                value = Copy(value);
                value.Busy = working;
                if (working && activeAction == action)
                {
                    value.State = "working";
                }
            }
            value.Configured = PreferenceEnabled(action, selectedTarget);
            return value;
        }

        private static bool HasRecovery(string action)
        {
            try
            {
                foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
                {
                    if (item.Id == action)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool RequiresReboot(string action)
        {
            return action == PrivacyActionCatalog.NtfsMemory;
        }

        private static void SetResult(string action, string selectedTarget, PrivacyPlan plan, string state, string detail)
        {
            bool hasRecovery = HasRecovery(action);
            lock (gate)
            {
                states[action] = new PrivacyResult
                {
                    State = state,
                    Detail = (detail ?? ""),
                    SelectedTarget = (selectedTarget ?? ""),
                    HasRecovery = hasRecovery,
                    UnavailableReason = ((plan == null) ? "" : plan.UnavailableReason)
                };
            }
        }

        private static bool Queue(string action, WaitCallback work)
        {
            lock (gate)
            {
                if (closing || working || !IsAction(action))
                {
                    return false;
                }
                working = true;
                activeAction = action;
            }
            bool queueSucceeded = false;
            try
            {
#if PAVISE_SELFTEST
                if (QueueForTest != null) queueSucceeded = QueueForTest(work);
                else
#endif
                queueSucceeded = ThreadPool.QueueUserWorkItem(work);
            }
            catch (Exception ex)
            {
                Logger.Warn(Logger.Format("隐私项队列 | {0}", "Privacy queue | {0}", ex.Message));
            }
            finally
            {
                if (!queueSucceeded)
                {
                    Finish();
                }
            }
            return queueSucceeded;
        }

        private static void Finish()
        {
            lock (gate)
            {
                working = false;
                activeAction = null;
                Monitor.PulseAll(gate);
            }
        }

        internal static string ReadTarget(string action, string target)
        {
            if (!IsAction(action) || string.IsNullOrEmpty(target))
            {
                throw new InvalidOperationException("Unknown privacy target");
            }
            string readResult = EnsureBackend().Read(action, target);
            if (readResult == null)
            {
                throw new InvalidOperationException("Privacy setting is unreadable");
            }
            return readResult;
        }

        private static bool WriteTarget(string action, string target, string value, bool restoring)
        {
            if (!IsAction(action) || string.IsNullOrEmpty(target) || value == null)
            {
                return false;
            }
            if (!restoring && PrivacyActionCatalog.IsRetired(action))
            {
                return false;
            }
            TrackRebootWrite(action, restoring);
            return EnsureBackend().Write(action, target, value);
        }

        internal static void TrackRebootWrite(string action, bool restoring)
        {
            if (RequiresReboot(action))
            {
                string currentBootResult = CurrentBoot();
                if (!BootSessionIdentity.Valid(currentBootResult))
                {
                    throw new InvalidOperationException("Boot identity unavailable; restart requirement cannot be tracked");
                }
                string message = (restoring ? "restore" : "apply");
                string value;
                if (!Settings.SaveStrDurable("PrivacySetting_" + action + "Boot", currentBootResult) || !Settings.TryLoadStr("PrivacySetting_" + action + "Boot", out value) || value != currentBootResult || !Settings.SaveStrDurable("PrivacySetting_" + action + "Phase", message) || !Settings.TryLoadStr("PrivacySetting_" + action + "Phase", out value) || value != message)
                {
                    throw new InvalidOperationException("Reboot tracking could not be persisted");
                }
            }
        }

        private static string CurrentBoot()
        {
#if PAVISE_SELFTEST
            if (BootForTest == null) throw new InvalidOperationException("Unmocked privacy boot identity");
            return BootForTest();
#endif
            return BootSessionIdentity.Read();
        }

        internal static bool RestoreReceipt(PerformanceActionStore.Receipt receipt)
        {
            bool restored = PerformanceTuning.RestoreWithBudget(receipt, 3, () => RestoreReceiptCore(receipt));
            if (restored) ActionFailureStore.CancelApply(receipt.Id, receipt.Target);
            return restored;
        }

        private static bool RestoreReceiptCore(PerformanceActionStore.Receipt receipt)
        {
            if (receipt == null || !IsAction(receipt.Id))
            {
                return false;
            }
            try
            {
                lock (SystemConfigurationGate.Sync)
                {
                    string canonicalTargetResult = CanonicalTarget(receipt.Target);
                    if ((receipt.Id == "EnvPrivacyServiceV1" && canonicalTargetResult == "svc|DiagTrack") || (receipt.Id == "EnvPrivacyCompatibilityV1" && canonicalTargetResult == "svc|PcaSvc"))
                    {
                        string value2;
                        if (!Settings.TryLoadStr(PerformanceActionStore.Slot(receipt.Id, receipt.Target), out value2))
                        {
                            return false;
                        }
                        if (string.IsNullOrEmpty(value2))
                        {
                            return true;
                        }
                        if (value2 != PerformanceActionStore.Pack(receipt))
                        {
                            return false;
                        }
                        string b = receipt.Target.Substring(0, receipt.Target.Length - canonicalTargetResult.Length) + "start|" + canonicalTargetResult.Substring(4);
                        foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
                        {
                            if (item.Id == receipt.Id && string.Equals(item.Target, b, StringComparison.OrdinalIgnoreCase) && !RestoreReceipt(item))
                            {
                                return false;
                            }
                        }
                    }
                    IPrivacyRecoveryBackend privacyRecoveryBackend = EnsureBackend() as IPrivacyRecoveryBackend;
                    if (privacyRecoveryBackend != null)
                    {
                        return privacyRecoveryBackend.Restore(receipt);
                    }
                    return PerformanceActionStore.Restore(receipt, () => ReadTarget(receipt.Id, receipt.Target), (string value) => WriteTarget(receipt.Id, receipt.Target, value, true), (string value) => value == receipt.Expected);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(Logger.Format("隐私项恢复 | {0}", "Privacy restore | {0}", ex.Message));
                return false;
            }
        }

        private static bool RecoveryAdmitted(Func<bool> mayContinue)
        {
            try
            {
                return mayContinue != null && mayContinue();
            }
            catch
            {
                return false;
            }
        }

        internal static void BeginRecoveryCycle(long serial)
        {
            lock (gate) { if (!retiredRecoveryClean) { retiredRecoveryFailures = 0; retiredRecoveryAt = 0; } }
        }

        internal static bool TryRestoreRetired(Func<bool> mayContinue)
        {
            if (!RecoveryAdmitted(mayContinue))
            {
                return false;
            }
            lock (gate)
            {
                if (closing || working || retiredRecoveryClean || retiredRecoveryFailures >= 5 || DateTime.UtcNow.Ticks < retiredRecoveryAt)
                {
                    return false;
                }
                retiredRecoveryAt = DateTime.UtcNow.AddMinutes(1.0).Ticks;
            }
            return Queue("EnvPrivacyServiceV1", delegate
            {
                bool ready = true, completed = false;
                try
                {
                    lock (SystemConfigurationGate.Sync)
                    {
                        if (IsClosing || !RecoveryAdmitted(mayContinue))
                        {
                            return;
                        }
                        List<PerformanceActionStore.Receipt> list = PerformanceActionStore.All();
                        list.Reverse();
                        foreach (PerformanceActionStore.Receipt item in list)
                        {
                            if (PrivacyActionCatalog.IsRetired(item.Id))
                            {
                                if (IsClosing || !RecoveryAdmitted(mayContinue))
                                {
                                    return;
                                }
                                bool restoreReceiptSucceeded = RestoreReceipt(item);
                                ready = ready && restoreReceiptSucceeded;
                                SetResult(item.Id, "", RetiredPlan(), restoreReceiptSucceeded ? "restored" : "restore-failed", restoreReceiptSucceeded ? "" : PerformancePolicy.Text("旧配置恢复未完成，记录已保留。", "Legacy settings remain pending restoration; records are retained."));
                            }
                        }
                        completed = true;
                    }
                }
                catch (Exception ex)
                {
                    ready = false;
                    Logger.Warn(Logger.Format("退役系统环境恢复 | {0}", "Retired environment recovery | {0}", ex.Message));
                }
                finally
                {
                    lock (gate)
                    {
                        if (!ready) retiredRecoveryFailures = Math.Min(5, retiredRecoveryFailures + 1);
                        if (ready && completed) retiredRecoveryClean = true;
                        retiredRecoveryAt = DateTime.UtcNow.AddSeconds(ready ? 60 : Math.Min(300, 30 * (1 << retiredRecoveryFailures))).Ticks;
                    }
                    Finish();
                }
            });
        }

        internal static bool RestoreAll(out string failure)
        {
            failure = null;
            lock (gate)
            {
                if (working)
                {
                    failure = "Privacy work is running";
                    return false;
                }
                working = true;
                activeAction = null;
            }
            try
            {
                lock (SystemConfigurationGate.Sync)
                {
                    bool restoreAllSucceeded = true;
                    List<PerformanceActionStore.Receipt> list = PerformanceActionStore.All();
                    list.Reverse();
                    foreach (PerformanceActionStore.Receipt item in list)
                    {
                        if (IsAction(item.Id))
                        {
                            restoreAllSucceeded &= RestoreReceipt(item);
                        }
                    }
                    if (!restoreAllSucceeded)
                    {
                        failure = "Privacy originals remain pending recovery";
                    }
                    return restoreAllSucceeded;
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
            finally
            {
                Finish();
            }
        }

        internal static bool CloseWork(int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            int timeoutMillisecondsLimit = Math.Max(0, timeoutMilliseconds);
            lock (gate)
            {
                closing = true;
                while (working)
                {
                    int lowerBound = (int)Math.Max(0L, timeoutMillisecondsLimit - stopwatch.ElapsedMilliseconds);
                    if (lowerBound == 0)
                    {
                        return false;
                    }
                    Monitor.Wait(gate, lowerBound);
                }
                return true;
            }
        }
    }
}
