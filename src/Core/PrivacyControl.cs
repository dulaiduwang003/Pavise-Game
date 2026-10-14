using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    // Lock order is system configuration -> module/store locks. Match service
    // pauses and persistent environment transactions share the same boundary.
    internal static class SystemConfigurationGate { internal static readonly object Sync = new object(); }
    internal sealed class PrivacyPlan
    {
        internal bool Supported, Limited, Managed;
        internal string Note, UnavailableReason;
        internal PrivacyTarget[] Targets;
    }
    internal sealed class PrivacyTarget
    {
        internal string Key, Wanted, Note;
        internal bool RestartRequired;
    }
    internal interface IPrivacyBackend
    {
        PrivacyPlan Inspect(string action, string selectedTarget);
        string Read(string action, string target);
        bool Write(string action, string target, string value);
    }
    internal interface IPrivacyRecoveryBackend
    {
        bool Satisfied(string action, string target, string current, string wanted);
        bool Restore(PerformanceActionStore.Receipt receipt);
    }
    internal sealed class PrivacyResult
    {
        internal string State, Detail, SelectedTarget, UnavailableReason;
        internal bool Configured, Busy, Limited, Supported, Managed, RestartRequired, RetryAllowed, HasRecovery;
    }

    // Environment settings are explicit user transactions, never match-session actions.
    // All mutations are serialized and reuse the durable, ownership-checked recovery store.
    internal static class PrivacyControl
    {
        internal const string PreferencePrefix = "PrivacySetting_";
        private static readonly object gate = new object();
        private static readonly Dictionary<string, PrivacyResult> states = new Dictionary<string, PrivacyResult>();
        private static IPrivacyBackend backend;
        private static bool working, closing;
        private static string activeAction;
        internal static Func<bool> AdmissionAllowed;
        internal static bool Busy { get { lock (gate) return working; } }
#if PAVISE_SELFTEST
        internal static Func<WaitCallback, bool> QueueForTest;
        internal static Func<string> BootForTest;
        internal static void ResetForTest()
        { lock (gate) { if (working) throw new InvalidOperationException("Privacy work is still running"); states.Clear(); closing = false; activeAction = null; backend = null; QueueForTest = null; BootForTest = null; AdmissionAllowed = null; } }
#endif
        internal static bool IsAction(string action) { return PrivacyActionCatalog.Find(action) != null; }
        internal static string CanonicalTarget(string target)
        {
            if (target == null) return null;
            if (!target.StartsWith("ctx|", StringComparison.Ordinal)) return target;
            string[] parts = target.Split(new[] { '|' }, 4); Guid machine;
            if (parts.Length != 4 || !Guid.TryParseExact(parts[1], "N", out machine) || machine == Guid.Empty
                || string.IsNullOrEmpty(parts[2]) || string.IsNullOrEmpty(parts[3]) || parts[3].StartsWith("ctx|", StringComparison.Ordinal))
                throw new FormatException("Invalid privacy target context");
            return parts[3];
        }
        private static IPrivacyBackend EnsureBackend()
        {
            lock (gate)
            {
                if (backend == null)
                {
#if PAVISE_SELFTEST
                    throw new InvalidOperationException("Unmocked privacy backend");
#else
                    backend = new PrivacyNativeBackend();
#endif
                }
                return backend;
            }
        }
        internal static void Bind(IPrivacyBackend value)
        { if (value == null) throw new ArgumentNullException("value"); lock (gate) { if (working) throw new InvalidOperationException("Privacy work is running"); backend = value; } }
        private static string PreferenceKey(string action)
        { return PreferencePrefix + action; }
        internal static bool PreferenceEnabled(string action) { return PreferenceEnabled(action, ""); }
        internal static bool PreferenceEnabled(string action, string selectedTarget)
        { string value; return Settings.TryLoadStr(PreferenceKey(action), out value) && value == "1"; }
        private static bool SavePreference(string action, bool enabled)
        {
            string value = enabled ? "1" : "0", observed;
            return Settings.SaveStrDurable(PreferenceKey(action), value)
                && Settings.TryLoadStr(PreferenceKey(action), out observed) && observed == value;
        }
        private static PrivacyResult Copy(PrivacyResult r)
        { return new PrivacyResult { State = r.State, Detail = r.Detail, SelectedTarget = r.SelectedTarget, UnavailableReason = r.UnavailableReason, Configured = r.Configured,
            Busy = r.Busy, Limited = r.Limited, Supported = r.Supported, Managed = r.Managed, RestartRequired = r.RestartRequired, RetryAllowed = r.RetryAllowed, HasRecovery = r.HasRecovery }; }
        internal static PrivacyResult State(string action) { return State(action, ""); }
        internal static PrivacyResult State(string action, string selectedTarget)
        {
            PrivacyResult result;
            lock (gate)
            {
                if (!states.TryGetValue(action ?? "", out result) || !string.Equals(result.SelectedTarget ?? "", selectedTarget ?? "", StringComparison.OrdinalIgnoreCase))
                    result = new PrivacyResult { State = "unverified", Detail = "", SelectedTarget = selectedTarget ?? "" };
                result = Copy(result);
                result.Busy = working; if (working && activeAction == action) result.State = "working";
            }
            result.Configured = PreferenceEnabled(action, selectedTarget);
            if (result.State == "stopped")
            {
                bool seen = false, allowed = true;
                try { foreach (ActionFailureRecord failure in ActionFailureStore.Records())
                    if (failure.Action == action)
                    { seen = true; allowed &= failure.RetryPending; } }
                catch { allowed = false; }
                result.RetryAllowed = seen && allowed;
            }
            return result;
        }
        private static bool HasRecovery(string action)
        {
            try { foreach (PerformanceActionStore.Receipt receipt in PerformanceActionStore.All()) if (receipt.Id == action) return true; return false; }
            catch { return true; } // Unreadable evidence must retain a recovery path.
        }
        private static bool RequiresReboot(string action)
        { return action == PrivacyActionCatalog.NtfsMemory || action == PrivacyActionCatalog.BackgroundGlobal; }
        private static string RebootState(string action, string state)
        {
            if (!RequiresReboot(action)) return state;
            if (state == "stopped" || state == "managed" || state == "unsupported" || state == "unverified") return state;
            string phase, saved;
            if (!Settings.TryLoadStr(PreferencePrefix + action + "Phase", out phase)
                || !Settings.TryLoadStr(PreferencePrefix + action + "Boot", out saved)) return "unverified";
            if (string.IsNullOrEmpty(phase) && string.IsNullOrEmpty(saved)) return state;
            if (phase != "apply" && phase != "restore") return "unverified";
            IrqRebootState reboot = BootSessionIdentity.Compare(saved, CurrentBoot());
            if (reboot == IrqRebootState.Unknown) return "unverified";
            if (phase == "restore" && (state == "ready" || state == "restored"))
                return reboot == IrqRebootState.AwaitingReboot ? "restore-pending-reboot" : "restored";
            if (phase == "apply" && (state == "configured" || state == "partial"))
                return reboot == IrqRebootState.AwaitingReboot ? "pending-reboot" : state;
            return state;
        }
        private static void SetResult(string action, string selectedTarget, PrivacyPlan plan, string state, string detail)
        {
            bool restart = false;
            if (plan != null && plan.Targets != null) foreach (PrivacyTarget target in plan.Targets) restart |= target.RestartRequired;
            bool recovery = HasRecovery(action);
            lock (gate) states[action] = new PrivacyResult { State = state, Detail = detail ?? "", SelectedTarget = selectedTarget ?? "", HasRecovery = recovery,
                UnavailableReason = plan == null ? "" : plan.UnavailableReason,
                Limited = plan != null && plan.Limited, Supported = plan != null && plan.Supported,
                Managed = plan != null && plan.Managed, RestartRequired = restart };
        }
        private static bool IsClosing { get { lock (gate) return closing; } }
        private static bool MayApply()
        { try { return AdmissionAllowed == null || AdmissionAllowed(); } catch (Exception error) { Logger.Warn("Privacy admission | " + error.Message); return false; } }
        private static bool Queue(string action, WaitCallback work)
        {
            lock (gate) { if (closing || working || !IsAction(action)) return false; working = true; activeAction = action; }
            bool queued = false;
            try
            {
#if PAVISE_SELFTEST
                if (QueueForTest != null) queued = QueueForTest(work);
                else
#endif
                    queued = ThreadPool.QueueUserWorkItem(work);
            }
            catch (Exception error) { Logger.Warn("Privacy queue | " + error.Message); }
            finally { if (!queued) Finish(); }
            return queued;
        }
        private static void Finish() { lock (gate) { working = false; activeAction = null; Monitor.PulseAll(gate); } }
        private static void Complete(Action callback)
        { if (callback != null) try { callback(); } catch (Exception error) { Logger.Warn("Privacy completion | " + error.Message); } }
        internal static bool Refresh(string action, string selectedTarget) { return Refresh(action, selectedTarget, null); }
        internal static bool Refresh(string action, string selectedTarget, Action<PrivacyPlan> completed)
        {
            return Queue(action, delegate(object ignored)
            {
                PrivacyPlan plan = null;
                try
                {
                    lock (SystemConfigurationGate.Sync)
                    {
                    plan = EnsureBackend().Inspect(action, selectedTarget); Validate(plan);
                    string state = RebootState(action, InspectState(action, plan));
                    SetResult(action, selectedTarget, plan, state, plan.Note);
                    }
                }
                catch (Exception error) { SetResult(action, selectedTarget, plan, "unverified", error.Message); }
                finally { Finish(); if (completed != null) try { completed(plan); } catch (Exception error) { Logger.Warn("Privacy inspection completion | " + error.Message); } }
            });
        }
        private static void Validate(PrivacyPlan plan)
        {
            if (plan == null) throw new InvalidOperationException("Privacy inspection returned no evidence");
            if (!plan.Supported || plan.Managed) return;
            if (plan.Targets == null || plan.Targets.Length == 0) throw new InvalidOperationException("Privacy inspection returned no targets");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PrivacyTarget target in plan.Targets)
                if (target == null || string.IsNullOrEmpty(target.Key) || target.Wanted == null || !seen.Add(target.Key))
                    throw new InvalidOperationException("Invalid or duplicate privacy target");
        }
        private static string InspectState(string action, PrivacyPlan plan)
        {
            if (plan.Managed) return "managed";
            if (!plan.Supported) return "unsupported";
            bool all = true, any = false, isolated = false;
            foreach (PrivacyTarget target in plan.Targets)
            {
                isolated |= ActionFailureStore.IsIsolated(action, target.Key);
                bool matches = Satisfied(action, target.Key, ReadTarget(action, target.Key), target.Wanted); all &= matches; any |= matches;
            }
            if (isolated) return "stopped";
            if (all) return plan.Limited ? "partial" : "configured";
            return any ? "partial" : "ready";
        }
        internal static bool Set(string action, bool enabled, string selectedTarget, Action completed)
        {
            if (!MayApply()) return false;
            return Queue(action, delegate(object ignored)
            {
                PrivacyPlan plan = null;
                try
                {
                    lock (SystemConfigurationGate.Sync)
                    {
                    if (IsClosing) throw new OperationCanceledException("Privacy work cancelled before inspection");
                    if (!MayApply()) throw new OperationCanceledException("A match or reset began before privacy work");
                    if (!enabled)
                    {
                        bool restored = RestoreAction(action, selectedTarget);
                        if (!restored) { SetResult(action, selectedTarget, null, "restore-failed", "Original settings remain pending recovery"); return; }
                        if (!SavePreference(action, false)) throw new InvalidOperationException("Could not persist restored preference");
                        bool reboot = RequiresReboot(action);
                        var restoredPlan = reboot ? new PrivacyPlan { Supported = true, Targets = new[] { new PrivacyTarget { RestartRequired = true } } } : null;
                        SetResult(action, selectedTarget, restoredPlan, RebootState(action, "restored"), ""); return;
                    }
                    plan = EnsureBackend().Inspect(action, selectedTarget); Validate(plan);
                    if (plan.Managed || !plan.Supported) { SetResult(action, selectedTarget, plan, plan.Managed ? "managed" : "unsupported", plan.Note); return; }
                    Apply(action, selectedTarget, plan);
                    }
                }
                catch (OperationCanceledException error) { SetResult(action, selectedTarget, plan, "unverified", error.Message); }
                catch (Exception error) { SetResult(action, selectedTarget, plan, "unverified", error.Message); Logger.Warn("Privacy | " + action + " | " + error.Message); }
                finally { Finish(); Complete(completed); }
            });
        }
        private static void Apply(string action, string selectedTarget, PrivacyPlan plan)
        {
            var admitted = new List<string>(); bool mutation = false, completedAll = false;
            string faultTarget = null;
            try
            {
                // Read all evidence before any mutation; a denied/unknown target cannot
                // leave a successful-looking half-configured group.
                foreach (PrivacyTarget target in plan.Targets)
                {
                    ReadTarget(action, target.Key);
                    if (ActionFailureStore.IsBlocked(action, target.Key))
                    { SetResult(action, selectedTarget, plan, "stopped", ActionFailureStore.FailureReason(action, target.Key)); return; }
                }
                // Restore stale target selections and failed attempts before a new retry.
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool retrying = false;
                foreach (PrivacyTarget target in plan.Targets) allowed.Add(target.Key);
                foreach (PrivacyTarget target in plan.Targets) retrying |= ActionFailureStore.IsRetryPending(action, target.Key);
                var existing = PerformanceActionStore.All(); existing.Reverse();
                foreach (PerformanceActionStore.Receipt receipt in existing)
                    if (receipt.Id == action && (retrying || !allowed.Contains(receipt.Target)))
                        if (!RestoreReceipt(receipt)) { faultTarget = receipt.Target; throw new InvalidOperationException("Recovery incomplete before privacy apply"); }
                if (!SavePreference(action, true)) throw new InvalidOperationException("Could not persist privacy preference; no native change attempted");
                foreach (PrivacyTarget target in plan.Targets)
                {
                    if (IsClosing || !MayApply()) throw new OperationCanceledException("Privacy work cancelled");
                    string reason;
                    if (!ActionFailureStore.TryBeginApply(action, target.Key, out reason))
                    { SetResult(action, selectedTarget, plan, "stopped", reason); return; }
                    admitted.Add(target.Key);
                }
                foreach (PrivacyTarget target in plan.Targets)
                {
                    faultTarget = target.Key;
                    if (IsClosing || !MayApply()) throw new OperationCanceledException("Privacy work cancelled");
                    string wanted = target.Wanted; bool targetWriteAttempted = false;
                    bool ok = PerformanceActionStore.Change(action, target.Key, wanted,
                        delegate {
                            try { return ReadTarget(action, target.Key); }
                            catch (Exception error) { if (targetWriteAttempted) throw new InvalidOperationException("Post-write privacy verification unavailable", error); throw; }
                        },
                        delegate(string value) { mutation = true; targetWriteAttempted = true; return WriteTarget(action, target.Key, value); },
                        delegate(string value) {
                            try { return Satisfied(action, target.Key, value, wanted); }
                            catch (Exception error) { if (targetWriteAttempted) throw new InvalidOperationException("Post-write privacy verification unavailable", error); throw; }
                        });
                    if (!ok) throw new InvalidOperationException(PerformanceActionStore.LastChangeFailure ?? "Privacy change failed");
                }
                if (IsClosing || !MayApply()) throw new OperationCanceledException("Privacy work cancelled after writing");
                // Later targets can affect earlier ones (for example SCM startup and
                // runtime state). Keep every execution intent until the entire group
                // has been read back as a consistent configuration.
                foreach (PrivacyTarget target in plan.Targets)
                {
                    faultTarget = target.Key;
                    try {
                        if (!Satisfied(action, target.Key, ReadTarget(action, target.Key), target.Wanted))
                            throw new InvalidOperationException("Privacy group changed before completion");
                    }
                    catch (Exception error) { throw new InvalidOperationException("Post-write privacy group verification unavailable", error); }
                }
                if (IsClosing || !MayApply()) throw new OperationCanceledException("Privacy work cancelled during final verification");
                foreach (string target in admitted)
                { faultTarget = target; if (!ActionFailureStore.CompleteApply(action, target)) throw new InvalidOperationException("Privacy completion could not be persisted"); }
                completedAll = true;
                bool restart = false; foreach (PrivacyTarget target in plan.Targets) restart |= target.RestartRequired;
                string success = restart ? "pending-reboot" : plan.Limited ? "partial" : "configured";
                if (RequiresReboot(action)) success = RebootState(action, plan.Limited ? "partial" : "configured");
                SetResult(action, selectedTarget, plan, success, plan.Note);
            }
            catch (OperationCanceledException error)
            {
                bool restored = RestoreAction(action, selectedTarget);
                if (!restored && faultTarget != null) ActionFailureStore.Trip(action, faultTarget, "Recovery incomplete after cancelled privacy change");
                SetResult(action, selectedTarget, plan, restored ? "unverified" : "restore-failed", error.Message);
            }
            catch (NotSupportedException error)
            {
                bool restored = RestoreAction(action, selectedTarget);
                if (!restored && faultTarget != null) ActionFailureStore.Trip(action, faultTarget, "Recovery incomplete after unsupported privacy change");
                SetResult(action, selectedTarget, plan, restored ? "unsupported" : "restore-failed", error.Message);
            }
            catch (Exception error)
            {
                if (faultTarget != null) ActionFailureStore.Trip(action, faultTarget, error.Message);
                bool restored = RestoreAction(action, selectedTarget);
                foreach (PrivacyTarget target in plan.Targets)
                    if (ActionFailureStore.IsRetryPending(action, target.Key)) ActionFailureStore.WithdrawRetry(action, target.Key);
                SetResult(action, selectedTarget, plan, restored ? faultTarget == null ? "unverified" : "stopped" : "restore-failed", error.Message);
                Logger.Warn("Privacy | " + action + " | " + error.Message);
            }
            finally
            {
                if (!completedAll) foreach (string target in admitted)
                {
                    if (!ActionFailureStore.IsIsolated(action, target)) ActionFailureStore.CancelApply(action, target);
                    if (mutation) ActionFailureStore.WithdrawRetry(action, target);
                }
            }
        }
        internal static string ReadTarget(string action, string target)
        {
            if (!IsAction(action) || string.IsNullOrEmpty(target)) throw new InvalidOperationException("Unknown privacy target");
            string value = EnsureBackend().Read(action, target);
            if (value == null) throw new InvalidOperationException("Privacy setting is unreadable");
            return value;
        }
        internal static bool WriteTarget(string action, string target, string value)
        { return WriteTarget(action, target, value, false); }
        private static bool WriteTarget(string action, string target, string value, bool restoring)
        {
            if (!IsAction(action) || string.IsNullOrEmpty(target) || value == null) return false;
            if (RequiresReboot(action))
            {
                string boot = CurrentBoot();
                if (!BootSessionIdentity.Valid(boot)) throw new InvalidOperationException("Boot identity unavailable; restart requirement cannot be tracked");
                string observed, phase = restoring ? "restore" : "apply";
                if (!Settings.SaveStrDurable(PreferencePrefix + action + "Boot", boot)
                    || !Settings.TryLoadStr(PreferencePrefix + action + "Boot", out observed) || observed != boot
                    || !Settings.SaveStrDurable(PreferencePrefix + action + "Phase", phase)
                    || !Settings.TryLoadStr(PreferencePrefix + action + "Phase", out observed) || observed != phase)
                    throw new InvalidOperationException("Reboot tracking could not be persisted");
            }
            return EnsureBackend().Write(action, target, value);
        }
        private static string CurrentBoot()
        {
#if PAVISE_SELFTEST
            if (BootForTest == null) throw new InvalidOperationException("Unmocked privacy boot identity");
            return BootForTest();
#else
            return BootSessionIdentity.Read();
#endif
        }
        private static bool Satisfied(string action, string target, string current, string wanted)
        { var custom = EnsureBackend() as IPrivacyRecoveryBackend; return custom == null ? current == wanted : custom.Satisfied(action, target, current, wanted); }
        internal static bool RestoreReceipt(PerformanceActionStore.Receipt receipt)
        {
            if (receipt == null || !IsAction(receipt.Id)) return false;
            try { lock (SystemConfigurationGate.Sync) {
                string canonical = CanonicalTarget(receipt.Target);
                if ((receipt.Id == PrivacyActionCatalog.Service && canonical == "svc|DiagTrack")
                    || (receipt.Id == PrivacyActionCatalog.Compatibility && canonical == "svc|PcaSvc"))
                {
                    // 索引丢失时也先恢复启动类型，再恢复服务运行状态。
                    string stored;
                    if (!Settings.TryLoadStr(PerformanceActionStore.Slot(receipt.Id, receipt.Target), out stored)) return false;
                    if (string.IsNullOrEmpty(stored)) return true;
                    if (stored != PerformanceActionStore.Pack(receipt)) return false;
                    string siblingTarget = receipt.Target.Substring(0, receipt.Target.Length - canonical.Length) + "start|" + canonical.Substring(4);
                    foreach (PerformanceActionStore.Receipt sibling in PerformanceActionStore.All())
                        if (sibling.Id == receipt.Id && string.Equals(sibling.Target, siblingTarget, StringComparison.OrdinalIgnoreCase) && !RestoreReceipt(sibling)) return false;
                }
                var custom = EnsureBackend() as IPrivacyRecoveryBackend; if (custom != null && !RequiresReboot(receipt.Id)) return custom.Restore(receipt);
                return PerformanceActionStore.Restore(receipt, delegate { return ReadTarget(receipt.Id, receipt.Target); },
                delegate(string value) { return WriteTarget(receipt.Id, receipt.Target, value, true); }, delegate(string value) { return value == receipt.Expected; }); } }
            catch (Exception error) { Logger.Warn("Privacy restore | " + error.Message); return false; }
        }
        private static bool RestoreAction(string action, string selectedTarget)
        {
            bool ok = true;
            try { var receipts = PerformanceActionStore.All(); receipts.Reverse(); foreach (PerformanceActionStore.Receipt receipt in receipts) if (receipt.Id == action) ok &= RestoreReceipt(receipt); }
            catch (Exception error) { Logger.Warn("Privacy restore | " + error.Message); return false; }
            return ok;
        }
        internal static bool RestoreAll(out string failure)
        {
            failure = null;
            lock (gate) { if (working) { failure = "Privacy work is running"; return false; } working = true; activeAction = null; }
            try
            {
                lock (SystemConfigurationGate.Sync)
                {
                bool ok = true;
                var receipts = PerformanceActionStore.All(); receipts.Reverse();
                foreach (PerformanceActionStore.Receipt receipt in receipts)
                    if (IsAction(receipt.Id)) ok &= RestoreReceipt(receipt);
                if (!ok) failure = "Privacy originals remain pending recovery";
                return ok;
                }
            }
            catch (Exception error) { failure = error.Message; return false; }
            finally { Finish(); }
        }
        internal static bool CloseWork(int timeoutMilliseconds)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); int timeout = Math.Max(0, timeoutMilliseconds);
            lock (gate)
            {
                closing = true;
                while (working)
                {
                    int remaining = (int)Math.Max(0L, timeout - elapsed.ElapsedMilliseconds);
                    if (remaining == 0) return false;
                    Monitor.Wait(gate, remaining);
                }
                return true;
            }
        }
    }
}
