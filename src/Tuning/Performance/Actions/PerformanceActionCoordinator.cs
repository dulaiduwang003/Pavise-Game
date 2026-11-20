using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal sealed class PerformanceActionCoordinator
    {
        private sealed class Request
        {
            internal bool Guard;

            internal bool Match;

            internal string Signature;
            internal long SessionSerial;
        }

        private sealed class Attempt
        {
            internal long Next;

            internal int Failures;
            internal Func<bool>[] RecoveryObservers;
        }

        private readonly object gate = new object();

        private readonly ManualResetEvent drained = new ManualResetEvent(true);

        private readonly ManualResetEvent sessionRestored = new ManualResetEvent(true);

        private volatile bool sessionRestoreOk = true;
        private sealed class RestoreSchedule
        {
            internal bool Reported;
            internal long Due;
            internal Func<bool>[] Observers;
        }
        private RestoreSchedule sessionRestoreSchedule;

        private readonly Dictionary<string, string> states = new Dictionary<string, string>();

        private readonly Dictionary<string, string> publishedDetails = new Dictionary<string, string>();

        private readonly Dictionary<string, Attempt> restoreAttempts = new Dictionary<string, Attempt>();

        private readonly Dictionary<string, string> restoreDetails = new Dictionary<string, string>();

        private readonly Dictionary<string, string> batchStates = new Dictionary<string, string>();

        private readonly Dictionary<string, List<string>> batchDetails = new Dictionary<string, List<string>>();

        private bool collectingBatch;

        private Request latest;

        private bool running;

        private bool closing;

        private bool guardWasOn;

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

        internal string State(string id)
        {
            lock (gate)
            {
                string value;
                return states.TryGetValue(id, out value) ? value : "";
            }
        }

        private void Record(string id, string value)
        {
            Record(id, value, null);
        }

        private void Record(string id, string value, string detail)
        {
            if (!collectingBatch)
            {
                Publish(id, value, detail);
                return;
            }
            string value2;
            if (batchStates.TryGetValue(id, out value2))
            {
                if (value2 == "restore-failed" || (value != "restore-failed" && (value2 == "stopped" || (value2 == "failed" && value != "stopped"))))
                {
                    value = value2;
                }
                else if (value == "unsupported" && (value2 == "configured" || value2 == "applied"))
                {
                    value = value2;
                }
            }
            batchStates[id] = value;
            if (!string.IsNullOrEmpty(detail))
            {
                detail = PsRunner.NormalizeError(detail);
                List<string> value3;
                if (!batchDetails.TryGetValue(id, out value3))
                {
                    value3 = (batchDetails[id] = new List<string>());
                }
                if (!value3.Contains(detail))
                {
                    value3.Add(detail);
                }
            }
        }

        private void PublishBatch()
        {
            foreach (KeyValuePair<string, string> batchState in batchStates)
            {
                List<string> value;
                Publish(batchState.Key, batchState.Value, batchDetails.TryGetValue(batchState.Key, out value) ? string.Join(" ; ", value.ToArray()) : null);
            }
        }

        private void Publish(string id, string value, string detail)
        {
            detail = (string.IsNullOrEmpty(detail) ? "" : PsRunner.NormalizeError(detail));
            lock (gate)
            {
                string value2;
                string value3;
                if (states.TryGetValue(id, out value2) && value2 == value && publishedDetails.TryGetValue(id, out value3) && value3 == detail)
                {
                    return;
                }
                states[id] = value;
                publishedDetails[id] = detail;
            }
            PerformanceAction performanceAction = PerformanceActionCatalog.Find(id);
            string labelOffset = "AUTO " + ((performanceAction == null) ? id : performanceAction.Title) + " | " + PerformanceTuning.Label(value);
            if (!string.IsNullOrEmpty(detail))
            {
                labelOffset = labelOffset + " | " + PsRunner.NormalizeError(detail);
            }
            switch (value)
            {
            case "failed":
            case "stopped":
            case "restore-failed":
                Logger.Warn(labelOffset);
                break;
            default:
                Logger.Info(labelOffset);
                break;
            }
        }

        internal void Queue(bool guard, bool match, bool visualMatch = false)
        {
            long sessionSerial = PerformanceTuning.RecoveryCycleSerial;
            string actionSelectionSignatureOffset = sessionSerial + "|" + guard + "|" + match + "|" + visualMatch;
            lock (gate)
            {
                long nowResult = Now();
                if (closing || (latest != null && latest.Signature == actionSelectionSignatureOffset && nowResult < nextCheck))
                {
                    return;
                }
                latest = new Request
                {
                    Guard = guard,
                    Match = match,
                    Signature = actionSelectionSignatureOffset
                    , SessionSerial = sessionSerial
                };
                sessionRestored.Reset();
                nextCheck = nowResult + TimeSpan.FromMinutes(1.0).Ticks;
                if (!running)
                {
                    running = true;
                    drained.Reset();
                    if (!ThreadPool.QueueUserWorkItem(delegate
                    {
                        Run();
                    }))
                    {
                        running = false;
                        drained.Set();
                    }
                }
            }
        }

        private bool RestoreSelected(string id, Predicate<PerformanceActionStore.Receipt> selected, bool force)
        {
            int count;
            return RestoreSelected(id, selected, force, out count);
        }

        private bool RestoreSelected(string id, Predicate<PerformanceActionStore.Receipt> selected, bool force, out int count)
        {
            bool result = true;
            count = 0;
            List<PerformanceActionStore.Receipt> list;
            try
            {
                if (!PerformanceTuning.ReadRecoveryReceipts(out list)) return false;
            }
            catch (Exception ex)
            {
                RestoreFailure(id, "journal:" + id, ex.Message);
                return false;
            }
            foreach (PerformanceActionStore.Receipt item in list)
            {
                if (item.Id != id || (selected != null && !selected(item)))
                {
                    continue;
                }
                string key = PerformanceTuning.RecoveryCycleSerial + "\n" + PerformanceActionStore.Slot(item.Id, item.Target) + "\n" + item.Original + "\n" + item.Expected;
                Attempt value;
                if (!restoreAttempts.TryGetValue(key, out value))
                {
                    value = new Attempt();
                    restoreAttempts[key] = value;
                }
                if (!force && Now() < value.Next)
                {
                    ReportRestoreSchedule(value);
                    result = false;
                    string value2;
                    restoreDetails.TryGetValue(key, out value2);
                    Record(id, "restore-failed", value2);
                    continue;
                }
                bool restored = false;
                string message = null;
                using (var attemptTrace = new RecoveryAttemptTrace())
                {
                    try { restored = PerformanceTuning.RestorePerformanceReceipt(item); }
                    catch (Exception ex2) { message = ex2.Message; }
                    if (!restored && attemptTrace.Reported && attemptTrace.Next == long.MaxValue)
                    {
                        value.Failures = 5;
                        value.RecoveryObservers = attemptTrace.PhaseObservers.ToArray();
                    }
                }
                if (restored)
                {
                    restoreAttempts.Remove(key);
                    restoreDetails.Remove(key);
                    count++;
                    continue;
                }
                result = false;
                if (value.Failures < 5)
                {
                    value.Failures++;
                }
                value.Next = Now() + TimeSpan.FromSeconds(Math.Min(300, 30 << value.Failures - 1)).Ticks;
                ReportRestoreSchedule(value);
                RestoreFailure(id, key, item.Target + " | " + (message ?? PerformancePolicy.Text("配置恢复未完成", "Configuration restore incomplete")));
            }
            return result;
        }

        private static void ReportRestoreSchedule(Attempt attempt)
        {
            if (attempt.Failures < 5) { RecoveryAttemptTrace.DeferredUntil(attempt.Next); return; }
            long session = PerformanceTuning.RecoveryCycleSerial;
            RecoveryAttemptTrace.Exhausted(() => PerformanceTuning.RecoveryCycleSerial != session);
            if (attempt.RecoveryObservers != null)
                foreach (Func<bool> observer in attempt.RecoveryObservers) RecoveryAttemptTrace.Exhausted(observer);
        }

        private static bool HasRetiredDefenderRecords()
        {
            try
            {
                List<PerformanceActionStore.Receipt> receipts;
                if (!PerformanceTuning.ReadRecoveryReceipts(out receipts)) return true;
                foreach (PerformanceActionStore.Receipt item in receipts)
                {
                    if (item.Id == "AutoDefenderSessionV1" || item.Id == "AutoDefenderResourcesV1")
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

        private bool RestoreRetired(bool force)
        {
            bool restoreRetiredSucceeded = true;
            // 已下线项按记录自动恢复 时间片和最后访问时间改的是系统设置 也在这里还原
            string[] textEntries = new string[] { "AutoDefenderSessionV1", "AutoDefenderResourcesV1", "AutoBackgroundFpsV1", "AutoNtfsShortNamesV1", "EnvNtfsLastAccessV1", "EnvQuantumProfileV1" };
            foreach (string id in textEntries)
            {
                int count;
                restoreRetiredSucceeded &= RestoreSelected(id, null, force, out count);
                if (count > 0)
                {
                    Record(id, "restored");
                }
            }
            return restoreRetiredSucceeded;
        }

        private void RestoreFailure(string id, string key, string detail)
        {
            detail = PsRunner.NormalizeError(detail);
            restoreDetails[key] = detail;
            Record(id, "restore-failed", detail);
        }

        private void Process(Request request)
        {
            using (var recoveryTrace = new RecoveryAttemptTrace())
            {
            if (!request.Match || !request.Guard) sessionRestoreOk = false;
            batchStates.Clear();
            batchDetails.Clear();
            collectingBatch = true;
            try
            {
                ProcessCore(request);
            }
            finally
            {
                collectingBatch = false;
                PublishBatch();
                if (!request.Match || !request.Guard)
                {
                    Volatile.Write(ref sessionRestoreSchedule, new RestoreSchedule { Reported = recoveryTrace.Reported,
                        Due = recoveryTrace.Next, Observers = recoveryTrace.PhaseObservers.ToArray() });
                    sessionRestored.Set();
                }
            }
            }
        }

        private void ProcessCore(Request request)
        {
            bool guardStopped = guardWasOn && !request.Guard;
            guardWasOn = request.Guard;
            // 拒绝开启已退役的桌面效果 保留依据回执恢复的能力
            bool restoreSelectedSucceeded = RestoreSelected("MatchVisualEffectsV1", null, false);
            if (!restoreSelectedSucceeded || State("MatchVisualEffectsV1").Length > 0)
            {
                Record("MatchVisualEffectsV1", (!restoreSelectedSucceeded) ? "restore-failed" : "off");
            }
            bool defenderRunningWithResidue = HasRetiredDefenderRecords() && PerformanceActionBackend.DefenderRunning();
            bool restoreRetiredDefender = defenderRunningWithResidue && !defenderWasRunning;
            defenderWasRunning = defenderRunningWithResidue;
            bool restoreRetiredSucceeded = RestoreRetired(restoreRetiredDefender || guardStopped);
            if (!request.Match || !request.Guard)
            {
                sessionRestoreOk = restoreRetiredSucceeded && restoreSelectedSucceeded;
            }
        }

        private void Run()
        {
            while (true)
            {
                Request request;
                lock (gate)
                {
                    request = latest;
                }
                try
                {
                    Process(request);
                }
                catch (Exception ex)
                {
                    Logger.Warn(Logger.Format("性能自动化 | {0}", "Performance automation | {0}", ex.Message));
                }
                lock (gate)
                {
                    if (request == latest)
                    {
                        running = false;
                        drained.Set();
                        break;
                    }
                }
            }
        }

        internal bool Close(int timeout)
        {
            lock (gate)
            {
                closing = true;
                latest = new Request
                {
                    Guard = false,
                    Match = false
                };
            }
            if (!drained.WaitOne(timeout))
            {
                return false;
            }
            bool restoreRetiredSucceeded = RestoreRetired(true);
            bool result = RestoreSelected("MatchVisualEffectsV1", null, true);
            if (restoreRetiredSucceeded)
            {
                return result;
            }
            return false;
        }

        internal bool WaitForSessionRestore(int timeout)
        {
            if (sessionRestored.WaitOne(timeout))
            {
                if (sessionRestoreOk) return true;
                RestoreSchedule schedule = Volatile.Read(ref sessionRestoreSchedule);
                if (schedule != null && schedule.Reported)
                {
                    if (schedule.Due == long.MaxValue) RecoveryAttemptTrace.Exhausted();
                    else RecoveryAttemptTrace.DeferredUntil(Math.Max(schedule.Due, DateTime.UtcNow.AddSeconds(1).Ticks));
                    if (schedule.Observers != null)
                        foreach (Func<bool> observer in schedule.Observers) RecoveryAttemptTrace.Exhausted(observer);
                }
                return false;
            }
            RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
            return false;
        }
    }
}
