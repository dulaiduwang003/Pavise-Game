using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static partial class PerformanceTuning
    {
        private static readonly object gate = new object();
        private static readonly HashSet<string> busy = new HashSet<string>();
        private static readonly Dictionary<string, string> states = new Dictionary<string, string>();
#if PAVISE_SELFTEST
        internal static Func<string> BootForTest;
#endif
        internal static string Boot()
        {
#if PAVISE_SELFTEST
            if (BootForTest == null) throw new InvalidOperationException("Unmocked boot identity");
            return BootForTest();
#else
            return BootSessionIdentity.Read();
#endif
        }
        internal static bool Busy(string id) { lock (gate) return busy.Contains(id); }
#if PAVISE_SELFTEST
        internal static void ResetForTest()
        { lock (gate) { if (busy.Count != 0) throw new InvalidOperationException("A performance action is still running"); states.Clear(); EnvironmentWorkClosing = false; EnvironmentQueueForTest = null; } }
#endif
        internal static string State(string id)
        {
            lock (gate)
            {
                if (busy.Contains(id)) return "working";
                string state;
                if (!states.TryGetValue(id, out state)) state = Settings.LoadStr(id + "State", "");
                if (state != "configured" && state != "restored") return state;
                IrqRebootState reboot = BootSessionIdentity.Compare(Settings.LoadStr(id + "Boot", ""), Boot());
                return reboot == IrqRebootState.Unknown ? "unverified"
                    : reboot == IrqRebootState.Rebooted ? (state == "configured" ? "ready" : "restored")
                    : state == "configured" ? "pending-reboot" : "restore-pending-reboot";
            }
        }
        internal static bool SetQuantumProfile(int index, Action completed)
        {
            if (index < 0 || index >= QuantumProfiles.Values.Length) return false;
            lock (gate)
            {
                if (EnvironmentWorkClosing || busy.Contains(PerformanceActionCatalog.Quantum) || busy.Contains(EnvironmentBundleKey)) return false;
                string previous = Settings.LoadStr(QuantumProfiles.SettingKey, "38");
                if (index > 0 && !Settings.SaveStr(QuantumProfiles.SettingKey, QuantumProfiles.Values[index].ToString(System.Globalization.CultureInfo.InvariantCulture))) return false;
                if (SetEnvironment(PerformanceActionCatalog.Quantum, index > 0, completed)) return true;
                Settings.SaveStr(QuantumProfiles.SettingKey, previous);
                return false;
            }
        }
        internal static bool SetEnvironment(string id, bool on, Action completed)
        {
            PerformanceAction action = PerformanceActionCatalog.Find(id);
            if (action == null || !action.Environment) return false;
            lock (gate)
            {
                if (EnvironmentWorkClosing || busy.Contains(id) || busy.Contains(EnvironmentBundleKey) || !Settings.Save(id, on)) return false;
                busy.Add(id);
            }
            bool queued = false;
            try { queued = QueueEnvironmentWork(delegate
            {
                try { ApplyEnvironment(id, on); }
                catch (Exception error) { SetState(id, on ? "failed" : "restore-failed"); Logger.Warn(action.Title + " | " + error.Message); }
                finally
                {
                    lock (gate) { busy.Remove(id); Monitor.PulseAll(gate); }
                    if (completed != null) try { completed(); } catch (Exception error) { Logger.Warn("Environment completion | " + error.Message); }
                }
            }); }
            catch (Exception error) { Logger.Warn(action.Title + " | " + error.Message); }
            if (!queued) { lock (gate) { busy.Remove(id); Monitor.PulseAll(gate); } SetState(id, on ? "failed" : "restore-failed"); }
            return queued;
        }
        internal static void SetState(string id, string state)
        {
            lock (gate) states[id] = state;
            Settings.SaveStr(id + "State", state);
        }
        internal static bool ApplyEnvironment(string id, bool on)
        {
            if (EnvironmentWorkClosing) return false;
            bool ok = true, changed = false, isolated = false, restorePending = false;
            if (on)
            {
                string[] targets = PerformanceActionBackend.EnvironmentTargets(id);
                if (targets.Length == 0) { SetState(id, "unsupported"); return false; }
                if (id == PerformanceActionCatalog.CpuMitigations) return ApplyCpuMitigationsGroup(targets);
                foreach (string target in targets)
                {
                    if (EnvironmentWorkClosing) return false;
                    string blocked;
                    if (ActionFailureStore.IsRetryPending(id, target)
                        && !RestoreWhere(delegate(PerformanceActionStore.Receipt receipt) { return receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase); }))
                    {
                        ActionFailureStore.Trip(id, target, "Recovery incomplete before retry"); ok = false; isolated = true; restorePending = true; continue;
                    }
                    if (!ActionFailureStore.TryBeginApply(id, target, out blocked)) { ok = false; isolated = true; continue; }
                    try
                    {
                        string current = PerformanceActionBackend.Read(id, target);
                        string wanted = PerformanceActionBackend.WantedRegistry(id, current);
                        if (wanted != current)
                        {
                            string boot = Boot();
                            if (!BootSessionIdentity.Valid(boot)) { ActionFailureStore.CancelApply(id, target); ok = false; continue; }
                            if (!Settings.SaveStrDurable(id + "Boot", boot)) throw new InvalidOperationException("Boot journal persistence failed");
                            changed = true;
                        }
                        if (!PerformanceActionBackend.Apply(id, target, wanted)) throw new InvalidOperationException(PerformanceActionBackend.ApplyFailureHint(id, target));
                        if (!ActionFailureStore.CompleteApply(id, target))
                        { restorePending |= !RestoreWhere(delegate(PerformanceActionStore.Receipt receipt) { return receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase); }); ok = false; isolated = true; }
                    }
                    catch (NotSupportedException)
                    { if (RestoreWhere(delegate(PerformanceActionStore.Receipt receipt) { return receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase); })) ActionFailureStore.CancelApply(id, target); else { ActionFailureStore.Trip(id, target, "Recovery incomplete after unsupported apply"); restorePending = true; } ok = false; }
                    catch (OperationCanceledException)
                    { if (RestoreWhere(delegate(PerformanceActionStore.Receipt receipt) { return receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase); })) ActionFailureStore.CancelApply(id, target); else { ActionFailureStore.Trip(id, target, "Recovery incomplete after cancelled apply"); restorePending = true; } ok = false; }
                    catch (Exception error)
                    {
                        ActionFailureStore.Trip(id, target, error.Message);
                        restorePending |= !RestoreWhere(delegate(PerformanceActionStore.Receipt receipt) { return receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase); });
                        ok = false; isolated = true; Logger.Warn(id + " | " + error.Message);
                    }
                }
            }
            else
            {
                foreach (PerformanceActionStore.Receipt r in PerformanceActionStore.All())
                    if (r.Id == id)
                    {
                        if (EnvironmentWorkClosing) return false;
                        try
                        {
                            string boot = Boot();
                            if (!BootSessionIdentity.Valid(boot) || !Settings.SaveStrDurable(id + "Boot", boot)) { ok = false; continue; }
                            changed = true;
                            if (!PerformanceActionBackend.Restore(r)) ok = false;
                        }
                        catch (Exception error) { ok = false; Logger.Warn(id + " | " + error.Message); }
                    }
            }
            string next = !ok ? (on ? restorePending ? "restore-failed" : isolated ? "stopped" : "failed" : "restore-failed") : changed ? (on ? "configured" : "restored")
                : VerifiedEnvironmentState(id, on);
            SetState(id, next);
            PerformanceAction action = PerformanceActionCatalog.Find(id);
            if (action != null)
            {
                string message = action.Title + " | " + Label(State(id));
                if (ok) Logger.Info(message); else Logger.Warn(message);
            }
            return ok;
        }
        private static string VerifiedEnvironmentState(string id, bool on)
        {
            string previous = Settings.LoadStr(id + "State", "");
            if (!on) return previous == "restored" ? previous : "off";
            if (previous == "configured") return previous;
            foreach (PerformanceActionStore.Receipt receipt in PerformanceActionStore.All())
                if (receipt.Id == id) return "configured";
            return "external";
        }
        private static bool ApplyCpuMitigationsGroup(string[] targets)
        {
            const string id = PerformanceActionCatalog.CpuMitigations, identity = "system";
            Predicate<PerformanceActionStore.Receipt> selected = delegate(PerformanceActionStore.Receipt receipt) { return receipt.Id == id; };
            if (ActionFailureStore.IsRetryPending(id, identity) && !RestoreWhere(selected))
            { ActionFailureStore.Trip(id, identity, "CPU mitigation recovery incomplete before retry"); SetState(id, "restore-failed"); return false; }
            string reason;
            if (!ActionFailureStore.TryBeginApply(id, identity, out reason)) { SetState(id, "stopped"); return false; }
            bool changed = false;
            try
            {
                foreach (string target in targets)
                {
                    if (EnvironmentWorkClosing) throw new OperationCanceledException();
                    string current = PerformanceActionBackend.Read(id, target), wanted = PerformanceActionBackend.WantedRegistry(id, current);
                    if (current != wanted)
                    {
                        string boot = Boot();
                        if (!BootSessionIdentity.Valid(boot)) throw new OperationCanceledException();
                        if (!Settings.SaveStrDurable(id + "Boot", boot)) throw new InvalidOperationException("CPU mitigation boot journal persistence failed");
                        changed = true;
                    }
                    if (!PerformanceActionBackend.Apply(id, target, wanted)) throw new InvalidOperationException(target + " | " + PerformanceActionBackend.ApplyFailureHint(id, target));
                }
                if (!ActionFailureStore.CompleteApply(id, identity))
                { bool restored = RestoreWhere(selected); SetState(id, restored ? "stopped" : "restore-failed"); return false; }
                SetState(id, changed ? "configured" : VerifiedEnvironmentState(id, true)); return true;
            }
            catch (NotSupportedException)
            {
                bool restored = RestoreWhere(selected);
                if (restored) ActionFailureStore.CancelApply(id, identity); else ActionFailureStore.Trip(id, identity, "CPU mitigation recovery incomplete after unsupported apply");
                SetState(id, restored ? "unsupported" : "restore-failed"); return false;
            }
            catch (OperationCanceledException)
            {
                bool restored = RestoreWhere(selected);
                if (restored) ActionFailureStore.CancelApply(id, identity); else ActionFailureStore.Trip(id, identity, "CPU mitigation recovery incomplete after cancelled apply");
                SetState(id, restored ? "unverified" : "restore-failed"); return false;
            }
            catch (Exception error)
            {
                ActionFailureStore.Trip(id, identity, error.Message);
                bool restored = RestoreWhere(selected); SetState(id, restored ? "stopped" : "restore-failed"); Logger.Warn(id + " | " + error.Message); return false;
            }
        }
        internal static bool RestoreWhere(Predicate<PerformanceActionStore.Receipt> selected)
        {
            return RestoreFailures(selected).Count == 0;
        }
        private static List<string> RestoreFailures(Predicate<PerformanceActionStore.Receipt> selected, Action<string> progress = null)
        {
            var failed = new List<string>();
            List<PerformanceActionStore.Receipt> receipts;
            try { receipts = PerformanceActionStore.All(); }
            catch (Exception error)
            {
                string message = AutomaticPolicy.Text("性能配置恢复记录读取失败", "Performance recovery journal could not be read") + " | " + PsRunner.NormalizeError(error.Message);
                failed.Add(message); Logger.Warn(message); return failed;
            }
            foreach (PerformanceActionStore.Receipt r in receipts)
                if (selected(r))
                {
                    PerformanceAction action = PerformanceActionCatalog.Find(r.Id);
                    string title = action == null ? r.Id : action.Title;
                    try { if (progress != null) progress(title); } catch { }
                    string reason = null;
                    try
                    {
                        if (PrivacyControl.IsAction(r.Id)) {
                            if (!PrivacyControl.RestoreReceipt(r)) reason = AutomaticPolicy.Text("隐私配置恢复未完成，记录已保留", "Privacy restoration is incomplete; records are retained");
                        }
                        else if (action == null) reason = AutomaticPolicy.Text("无法识别恢复项目，已保留恢复记录", "Unknown recovery action; recovery record retained");
                        else if (!PerformanceActionBackend.Restore(r)) reason = PerformanceActionBackend.RestoreFailureHint(r);
                    }
                    catch (Exception error) { reason = PsRunner.NormalizeError(error.Message); }
                    if (reason == null) continue;
                    string message = title + " | " + r.Target + " | " + reason;
                    failed.Add(message); Logger.Warn("Performance restore | " + message);
                }
            return failed;
        }
        internal static List<string> RestoreAllFailures(Action<string> progress = null)
        {
            lock (gate) if (busy.Count > 0)
                return new List<string> { AutomaticPolicy.Text("环境配置仍在运行，恢复记录已保留，请稍后重试", "Environment configuration is still running; recovery records retained. Retry shortly.") };
            return RestoreFailures(delegate { return true; }, progress);
        }
        internal static bool RestoreAll()
        {
            return RestoreAllFailures().Count == 0;
        }
        internal static bool RestoreSession()
        { return RestoreWhere(delegate(PerformanceActionStore.Receipt r) {
            return r.Id == PerformanceActionCatalog.LegacyDefenderSession || r.Id == PerformanceActionCatalog.BackgroundFps
                || r.Id == PerformanceActionCatalog.VisualEffects;
        }); }
        internal static string Label(string state)
        {
            switch (state)
            {
                case "working": return AutomaticPolicy.Text("正在配置", "Configuring");
                case "configured": return AutomaticPolicy.Text("已配置 · 下次启动生效", "Configured; next launch");
                case "applied": return AutomaticPolicy.Text("已执行", "Active");
                case "pending-reboot": return AutomaticPolicy.Text("已配置 · 需重启电脑", "Configured; PC restart required");
                case "restore-pending-reboot": return AutomaticPolicy.Text("已恢复 · 需重启电脑", "Restored; PC restart required");
                case "ready": return AutomaticPolicy.Text("已重启 · 配置已写入", "Restarted; configuration written");
                case "unverified": return AutomaticPolicy.Text("重启状态待确认", "Restart state unverified");
                case "external": return AutomaticPolicy.Text("系统已配置", "Already configured by system");
                case "off": return AutomaticPolicy.Text("已关闭", "Off");
                case "unsupported": return AutomaticPolicy.Text("无适用设备或接口", "No applicable target");
                case "inactive": return AutomaticPolicy.Text("Defender 未运行 · 已跳过", "Defender off; skipped");
                case "failed": return AutomaticPolicy.Text("配置未完成 · 查看记录", "Configuration incomplete; see records");
                case "stopped": return AutomaticPolicy.Text("执行失败 · 已隔离，需手动重试", "Apply failed; isolated until explicit retry");
                case "restore-failed": return AutomaticPolicy.Text("恢复未完成 · 查看记录", "Restore pending");
                case "restored": return AutomaticPolicy.Text("已恢复", "Restored");
                default: return AutomaticPolicy.Text("自动待命", "Automatic standby");
            }
        }
    }

    internal sealed class PerformanceActionCoordinator
    {
        private sealed class Request { internal bool Guard, Match, VisualMatch; internal string[] Games, Background; internal string Signature; internal Dictionary<string, string> Roots; }
        private sealed class Attempt { internal long Next; internal int Failures; internal string State = "", Resolved, Target, Detail; }
        private readonly object gate = new object();
        private readonly ManualResetEvent drained = new ManualResetEvent(true);
        private readonly ManualResetEvent sessionRestored = new ManualResetEvent(true);
        private volatile bool sessionRestoreOk = true;
        private readonly Dictionary<string, string> states = new Dictionary<string, string>();
        private readonly Dictionary<string, string> publishedDetails = new Dictionary<string, string>();
        private readonly Dictionary<string, Attempt> attempts = new Dictionary<string, Attempt>();
        private readonly Dictionary<string, Attempt> restoreAttempts = new Dictionary<string, Attempt>();
        private readonly Dictionary<string, string> restoreDetails = new Dictionary<string, string>();
        private readonly Dictionary<string, string> batchStates = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>> batchDetails = new Dictionary<string, List<string>>();
        private bool collectingBatch;
        private Request latest;
        private bool running, closing;
        private bool guardWasOn;
        private bool visualWasOn;
        private bool defenderWasRunning;
        private long nextCheck;
#if PAVISE_SELFTEST
        internal Func<long> ClockForTest;
        internal bool DrainForTest(int timeout) { return drained.WaitOne(timeout); }
#endif
        private long Now()
        {
#if PAVISE_SELFTEST
            if (ClockForTest != null) return ClockForTest();
#endif
            return DateTime.UtcNow.Ticks;
        }
        internal string State(string id) { lock (gate) { string value; return states.TryGetValue(id, out value) ? value : ""; } }
        private void Record(string id, string value) { Record(id, value, null); }
        private void Record(string id, string value, string detail)
        {
            if (!collectingBatch) { Publish(id, value, detail); return; }
            string inBatch;
            if (batchStates.TryGetValue(id, out inBatch))
            {
                if (inBatch == "restore-failed" || (value != "restore-failed" && (inBatch == "stopped" || (inBatch == "failed" && value != "stopped")))) value = inBatch;
                else if (value == "unsupported" && (inBatch == "configured" || inBatch == "applied")) value = inBatch;
            }
            batchStates[id] = value;
            if (!string.IsNullOrEmpty(detail))
            {
                detail = PsRunner.NormalizeError(detail);
                List<string> details;
                if (!batchDetails.TryGetValue(id, out details)) batchDetails[id] = details = new List<string>();
                if (!details.Contains(detail)) details.Add(detail);
            }
        }
        private void PublishBatch()
        {
            foreach (KeyValuePair<string, string> result in batchStates)
            {
                List<string> details;
                Publish(result.Key, result.Value, batchDetails.TryGetValue(result.Key, out details)
                    ? string.Join(" ; ", details.ToArray()) : null);
            }
        }
        private void Publish(string id, string value, string detail)
        {
            detail = string.IsNullOrEmpty(detail) ? "" : PsRunner.NormalizeError(detail);
            lock (gate)
            {
                string prior, priorDetail;
                if (states.TryGetValue(id, out prior) && prior == value
                    && publishedDetails.TryGetValue(id, out priorDetail) && priorDetail == detail) return;
                states[id] = value;
                publishedDetails[id] = detail;
            }
            PerformanceAction a = PerformanceActionCatalog.Find(id);
            string line = "AUTO " + (a == null ? id : a.Title) + " | " + PerformanceTuning.Label(value);
            if (!string.IsNullOrEmpty(detail)) line += " | " + PsRunner.NormalizeError(detail);
            if (value == "failed" || value == "stopped" || value == "restore-failed") Logger.Warn(line); else Logger.Info(line);
        }
        internal void Queue(bool guard, bool match, string[] games, string[] background, Dictionary<string, string> roots = null, bool visualMatch = false)
        {
            games = GamePerformanceTargetPolicy.FilterGameTargets(games);
            background = background == null ? new string[0] : (string[])background.Clone();
            Array.Sort(games, StringComparer.OrdinalIgnoreCase); Array.Sort(background, StringComparer.OrdinalIgnoreCase);
            string signature = guard + "|" + match + "|" + visualMatch + "|" + string.Join("\n", games) + "|" + string.Join("\n", background) + "|retry:" + ActionFailureStore.RetryGeneration;
            lock (gate)
            {
                long now = Now();
                if (closing || (latest != null && latest.Signature == signature && now < nextCheck)) return;
                latest = new Request { Guard = guard, Match = match, VisualMatch = visualMatch, Games = games, Background = background, Signature = signature,
                    Roots = roots == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(roots, StringComparer.OrdinalIgnoreCase) };
                sessionRestored.Reset();
                nextCheck = now + TimeSpan.FromMinutes(1).Ticks;
                if (running) return;
                running = true; drained.Reset();
                if (!ThreadPool.QueueUserWorkItem(delegate { Run(); })) { running = false; drained.Set(); }
            }
        }
        private bool Current(Request request) { lock (gate) return latest == request; }
        private bool Due(string id, string key, out Attempt attempt)
        {
            if (!attempts.TryGetValue(key, out attempt)) { attempt = new Attempt(); attempts[key] = attempt; }
            if (attempt.State != "stopped" && Now() >= attempt.Next) return true;
            if (attempt.State != "resolved") Record(id, attempt.State, attempt.Detail);
            return false;
        }
        private void Failure(string id, Attempt attempt, string detail)
        {
            attempt.Failures++;
            attempt.Next = Now() + TimeSpan.FromMinutes(5).Ticks;
            attempt.State = "stopped";
            attempt.Detail = (string.IsNullOrEmpty(attempt.Target) ? "" : attempt.Target + " | ")
                + (string.IsNullOrEmpty(detail) ? AutomaticPolicy.Text("配置未通过确认", "Configuration could not be confirmed") : PsRunner.NormalizeError(detail));
            ActionFailureStore.Trip(id, attempt.Target, detail);
            Record(id, attempt.State, attempt.Detail);
            RestoreSelected(id, delegate(PerformanceActionStore.Receipt receipt) { return string.Equals(receipt.Target, attempt.Target, StringComparison.OrdinalIgnoreCase); }, true);
        }
        private void Unsupported(string id, Attempt attempt, string state)
        { attempt.Failures = 0; attempt.State = state; attempt.Detail = null; attempt.Next = Now() + TimeSpan.FromMinutes(30).Ticks; Record(id, state); }
        private void DropAction(string id)
        {
            foreach (string key in new List<string>(attempts.Keys))
                if (key.StartsWith(id + "\n", StringComparison.Ordinal)) attempts.Remove(key);
        }
        private bool Resolve(string id, string target, Func<string> query, out string resolved)
        {
            string key = id + "\nresolve:" + target;
            Attempt attempt; resolved = null;
            if (!Due(id, key, out attempt)) { resolved = attempt.Resolved; return attempt.State == "resolved"; }
            attempt.Target = target;
            try
            {
                resolved = query();
                if (string.IsNullOrEmpty(resolved)) { Unsupported(id, attempt, "unsupported"); return false; }
                attempt.Resolved = resolved; attempt.State = "resolved"; attempt.Failures = 0; attempt.Detail = null;
                attempt.Next = Now() + TimeSpan.FromMinutes(30).Ticks; return true;
            }
            catch (PerformanceActionBackend.DefenderInactiveException) { attempts.Remove(key); Record(id, "inactive"); }
            catch (NotSupportedException) { Unsupported(id, attempt, "unsupported"); }
            catch (OperationCanceledException) { attempts.Remove(key); }
            catch (Exception error)
            {
                // Discovery has not changed the system: transient lack of a target is not an apply failure.
                attempt.State = "failed"; attempt.Detail = PsRunner.NormalizeError(error.Message);
                attempt.Next = Now() + TimeSpan.FromMinutes(5).Ticks; Record(id, "failed", attempt.Detail);
            }
            return false;
        }
        private void Try(string id, string target, string wanted, Request request, string success)
        {
            if (!Current(request)) return;
            if ((id == PerformanceActionCatalog.Mitigations || id == PerformanceActionCatalog.OpenGl)
                && !GamePerformanceTargetPolicy.AllowsGameTarget(target)) return;
            string key = id + "\napply:" + target + "\n" + wanted;
            Attempt attempt;
            if (ActionFailureStore.IsRetryPending(id, target))
            {
                if (!RestoreSelected(id, delegate(PerformanceActionStore.Receipt receipt) { return string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase); }, true))
                { ActionFailureStore.Trip(id, target, "Recovery incomplete before retry"); Record(id, "restore-failed", target); return; }
                if (attempts.TryGetValue(key, out attempt)) { attempt.Next = 0; attempt.State = ""; }
            }
            if (ActionFailureStore.IsBlocked(id, target))
            {
                bool restored = RestoreSelected(id, delegate(PerformanceActionStore.Receipt receipt) { return string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase); }, false);
                Record(id, restored ? "stopped" : "restore-failed", target + " | " + ActionFailureStore.FailureReason(id, target)); return;
            }
            if (!Due(id, key, out attempt)) return;
            attempt.Target = target;
            string blocked;
            if (!ActionFailureStore.TryBeginApply(id, target, out blocked)) { Record(id, "stopped", target + " | " + blocked); return; }
            try
            {
                if (!Current(request)) { ActionFailureStore.CancelApply(id, target); return; }
                bool ok = PerformanceActionBackend.Apply(id, target, wanted);
                if (!ok) { Failure(id, attempt, PerformanceActionBackend.ApplyFailureHint(id, target)); return; }
                if (!ActionFailureStore.CompleteApply(id, target)) { Failure(id, attempt, "Isolation record persistence failed"); return; }
                attempt.Next = Now() + TimeSpan.FromMinutes(30).Ticks;
                attempt.Failures = 0; attempt.State = success; attempt.Detail = null; Record(id, success);
            }
            catch (PerformanceActionBackend.DefenderInactiveException)
            { CancelAfterRestore(id, target); attempts.Remove(key); Record(id, "inactive"); }
            catch (NotSupportedException)
            { CancelAfterRestore(id, target); Unsupported(id, attempt, "unsupported"); }
            catch (OperationCanceledException)
            { CancelAfterRestore(id, target); attempts.Remove(key); }
            catch (Exception error)
            { Failure(id, attempt, error.Message); }
        }
        private void CancelAfterRestore(string id, string target)
        {
            if (RestoreSelected(id, delegate(PerformanceActionStore.Receipt receipt) { return string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase); }, true))
                ActionFailureStore.CancelApply(id, target);
            else ActionFailureStore.Trip(id, target, "Recovery incomplete after skipped apply");
        }
        private bool RestoreSelected(string id, Predicate<PerformanceActionStore.Receipt> selected, bool force)
        { int count; return RestoreSelected(id, selected, force, out count); }
        private bool RestoreSelected(string id, Predicate<PerformanceActionStore.Receipt> selected, bool force, out int count)
        {
            bool ok = true; count = 0;
            List<PerformanceActionStore.Receipt> receipts;
            try { receipts = PerformanceActionStore.All(); }
            catch (Exception error) { RestoreFailure(id, "journal:" + id, error.Message); return false; }
            foreach (PerformanceActionStore.Receipt receipt in receipts)
            {
                if (receipt.Id != id || (selected != null && !selected(receipt))) continue;
                string key = PerformanceActionStore.Slot(receipt.Id, receipt.Target);
                Attempt attempt;
                if (!restoreAttempts.TryGetValue(key, out attempt)) { attempt = new Attempt(); restoreAttempts[key] = attempt; }
                if (!force && Now() < attempt.Next)
                {
                    ok = false;
                    string prior;
                    restoreDetails.TryGetValue(key, out prior);
                    Record(id, "restore-failed", prior);
                    continue;
                }
                bool restored = false; string detail = null;
                try { restored = PerformanceActionBackend.Restore(receipt); }
                catch (Exception error) { detail = error.Message; }
                if (restored) { restoreAttempts.Remove(key); restoreDetails.Remove(key); count++; continue; }
                ok = false;
                if (attempt.Failures < 5) attempt.Failures++;
                attempt.Next = Now() + TimeSpan.FromSeconds(Math.Min(300, 30 << (attempt.Failures - 1))).Ticks;
                RestoreFailure(id, key, receipt.Target + " | " + (detail ?? AutomaticPolicy.Text("配置恢复未完成", "Configuration restore incomplete")));
            }
            return ok;
        }
        // 3.1.0.0 至 3.1.0.4 曾在对局中关闭 Defender；此项已移除，只恢复它留下的记录。
        private bool RestoreLegacyDefender(bool force)
        {
            int count;
            bool ok = RestoreSelected(PerformanceActionCatalog.LegacyDefenderSession, null, force, out count);
            if (count > 0) Record(PerformanceActionCatalog.LegacyDefenderSession, "restored");
            return ok;
        }
        private void RestoreFailure(string id, string key, string detail)
        {
            detail = PsRunner.NormalizeError(detail);
            restoreDetails[key] = detail;
            Record(id, "restore-failed", detail);
        }
        private void Process(Request request)
        {
            batchStates.Clear(); batchDetails.Clear(); collectingBatch = true;
            try { ProcessCore(request); }
            finally { collectingBatch = false; PublishBatch(); }
        }
        private void ProcessCore(Request request)
        {
            bool guardEnded = guardWasOn && !request.Guard;
            if (request.Guard && !guardWasOn) attempts.Clear();
            guardWasOn = request.Guard;
            bool visualEnded = visualWasOn && !request.VisualMatch;
            visualWasOn = request.VisualMatch;
            bool visualsRestored = true;
            if (request.VisualMatch)
            {
                foreach (MatchVisualEffects.Effect effect in MatchVisualEffects.Effects)
                    Try(PerformanceActionCatalog.VisualEffects, effect.Name, "0", request, "applied");
            }
            else
            {
                visualsRestored = RestoreSelected(PerformanceActionCatalog.VisualEffects, null, visualEnded);
                if (!visualsRestored || visualEnded || MatchVisualEffects.Enabled
                    || State(PerformanceActionCatalog.VisualEffects).Length > 0)
                    Record(PerformanceActionCatalog.VisualEffects, visualsRestored
                        ? (MatchVisualEffects.Enabled ? "restored" : "off") : "restore-failed");
                DropAction(PerformanceActionCatalog.VisualEffects);
            }
            bool defenderRunning = request.Guard && PerformanceActionBackend.DefenderRunning();
            bool defenderStarted = defenderRunning && !defenderWasRunning;
            defenderWasRunning = defenderRunning;
            // 旧记录与对局无关，每轮都尝试恢复，不等退局。
            bool defenderRestored = RestoreLegacyDefender(defenderStarted || guardEnded);
            if (!request.Match || !request.Guard)
            {
                bool fpsRestored = RestoreSelected(PerformanceActionCatalog.BackgroundFps, null, guardEnded);
                sessionRestoreOk = defenderRestored && fpsRestored && visualsRestored && !request.VisualMatch; sessionRestored.Set();
                Record(PerformanceActionCatalog.BackgroundFps, fpsRestored ? "restored" : "restore-failed");
                DropAction(PerformanceActionCatalog.BackgroundFps);
            }
            if (!request.Guard || !Current(request)) return;
            if (!defenderRunning)
            {
                Record(PerformanceActionCatalog.DefenderResources, "inactive");
                DropAction(PerformanceActionCatalog.DefenderResources);
            }
            if (request.Match)
            {
                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in request.Background) wanted.Add(Path.GetFileName(path));
                bool restored = RestoreSelected(PerformanceActionCatalog.BackgroundFps, delegate(PerformanceActionStore.Receipt r) {
                    return r.Id == PerformanceActionCatalog.BackgroundFps && !wanted.Contains(Path.GetFileName(r.Target));
                }, false);
                if (!restored) Record(PerformanceActionCatalog.BackgroundFps, "restore-failed");
                else if (request.Background.Length == 0) Record(PerformanceActionCatalog.BackgroundFps, "restored");
                foreach (KeyValuePair<string, Attempt> item in attempts)
                    if (item.Key.StartsWith(PerformanceActionCatalog.BackgroundFps + "\napply:", StringComparison.Ordinal)
                        && !wanted.Contains(Path.GetFileName(item.Value.Target))
                        && (item.Value.State == "configured" || item.Value.State == "applied"))
                    { item.Value.Next = 0; item.Value.State = ""; }
                foreach (string path in request.Background)
                    if (Current(request)) Try(PerformanceActionCatalog.BackgroundFps, path, "30", request, "configured");
                return;
            }
            string resourcePlatform = null;
            bool resourceReady = defenderRunning && request.Games.Length > 0
                && Resolve(PerformanceActionCatalog.DefenderResources, "platform", PerformanceActionBackend.ResourcePlatform, out resourcePlatform);
            foreach (string path in request.Games)
            {
                if (!Current(request)) return;
                if (!GamePerformanceTargetPolicy.AllowsGameTarget(path)) continue;
                Try(PerformanceActionCatalog.Mitigations, path, PerformanceActionBackend.Is32Bit(path) ? "off32" : "off64", request, "configured");
                if (!Current(request)) return;
                Try(PerformanceActionCatalog.OpenGl, path, "1", request, "configured");
                if (resourceReady)
                {
                    string resource;
                    string root; request.Roots.TryGetValue(path, out root);
                    if (Resolve(PerformanceActionCatalog.DefenderResources, path + "\n" + root + "\n" + resourcePlatform,
                        delegate { return PerformanceActionBackend.ResourceTarget(path, root, resourcePlatform == "contextual"); }, out resource))
                        Try(PerformanceActionCatalog.DefenderResources, resource, "1", request, "applied");
                }
                string volume;
                string drive;
                try { drive = Path.GetPathRoot(path); } catch { drive = path; }
                if (Resolve(PerformanceActionCatalog.ShortNames, drive, delegate { return PerformanceActionBackend.ShortNameVolume(path); }, out volume))
                    Try(PerformanceActionCatalog.ShortNames, volume, "1", request, "applied");
            }
        }
        private void Run()
        {
            while (true)
            {
                Request request; lock (gate) request = latest;
                try { Process(request); } catch (Exception error) { Logger.Warn("Performance automation | " + error.Message); }
                lock (gate) if (request == latest) { running = false; drained.Set(); return; }
            }
        }
        internal bool Close(int timeout)
        {
            lock (gate)
            {
                closing = true;
                latest = new Request { Guard = false, Match = false, Games = new string[0], Background = new string[0] };
            }
            if (!drained.WaitOne(timeout)) return false;
            bool defenderRestored = RestoreLegacyDefender(true);
            bool fpsRestored = RestoreSelected(PerformanceActionCatalog.BackgroundFps, null, true);
            bool visualsRestored = RestoreSelected(PerformanceActionCatalog.VisualEffects, null, true);
            return defenderRestored && fpsRestored && visualsRestored;
        }
        internal bool WaitForSessionRestore(int timeout)
        { return sessionRestored.WaitOne(timeout) && sessionRestoreOk; }
    }
}
