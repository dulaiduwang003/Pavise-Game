using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    // 已下线动作只处理旧记录恢复，不再接受新的应用请求。
    internal static class LaboratoryManualActions
    {
        private static readonly object gate = new object();
        private static readonly ManualResetEvent idle = new ManualResetEvent(true);
        private static readonly Dictionary<string, LaboratoryManualState> states = new Dictionary<string, LaboratoryManualState>();
        private static FileFilterCandidate[] candidates = new FileFilterCandidate[0];
        private static Thread worker;
        private static bool closing;
        private static string busyKey = "";
#if PAVISE_SELFTEST
        internal static Func<string, LaboratoryManualState> InspectForTest;
        internal static Func<string, bool, FileFilterCandidate, string> ExecuteForTest;
        internal static Func<bool> RuntimeRestoreForTest, ResetRestoreForTest, StartupRestoreForTest;
        internal static void ResetForTest()
        {
            lock (gate)
            {
                if (worker != null) throw new InvalidOperationException("Manual laboratory worker is still running");
                states.Clear(); candidates = new FileFilterCandidate[0]; closing = false; busyKey = ""; idle.Set();
                InspectForTest = null; ExecuteForTest = null;
                RuntimeRestoreForTest = null; ResetRestoreForTest = null; StartupRestoreForTest = null;
            }
        }
#endif
        internal static bool Busy { get { lock (gate) return worker != null; } }
        internal static string BusyKey { get { lock (gate) return busyKey; } }
        internal static bool HasRecoveryRecords
        { get { return LaboratoryFileFilter.HasReceipt || LaboratoryAudioEffects.HasReceipt || LaboratoryClockRepair.HasReceipt; } }
        internal static LaboratoryManualState State(string key)
        {
            lock (gate)
            {
                LaboratoryManualState value;
                if (!states.TryGetValue(key, out value)) return new LaboratoryManualState { Summary = LaboratoryActionText.Pick("尚未检查", "Not checked") };
                return new LaboratoryManualState { CanApply = value.CanApply, CanRestore = value.CanRestore, Applied = value.Applied,
                    RestartRequired = value.RestartRequired, Summary = value.Summary, Reason = value.Reason, Failure = value.Failure, Details = value.Details };
            }
        }
        internal static FileFilterCandidate[] Candidates { get { lock (gate) return (FileFilterCandidate[])candidates.Clone(); } }
        private static string[] Keys { get { return new[] { LaboratoryFileFilter.Key, LaboratoryClockRepair.Key, LaboratoryAudioEffects.Key }; } }
        private static bool HasReceipt(string key)
        {
            try { return key == LaboratoryFileFilter.Key ? LaboratoryFileFilter.HasReceipt
                : key == LaboratoryClockRepair.Key ? LaboratoryClockRepair.HasReceipt
                : key == LaboratoryAudioEffects.Key && LaboratoryAudioEffects.HasReceipt; }
            catch { return true; }
        }
        private static LaboratoryManualState Failed(string key, Exception error)
        {
            return new LaboratoryManualState { CanRestore = HasReceipt(key), Failure = error.GetType().Name,
                Summary = LaboratoryActionText.Pick("执行失败，原记录保留", "Action failed; original records retained") };
        }
        private static void InspectAll()
        {
            foreach (string key in Keys)
            {
                if (!HasReceipt(key))
                {
                    lock (gate) states[key] = new LaboratoryManualState { Summary = "Retired" };
                    continue;
                }
                LaboratoryManualState laboratoryManualState;
                try
                {
#if PAVISE_SELFTEST
                    laboratoryManualState = InspectForTest != null ? InspectForTest(key) : new LaboratoryManualState { Summary = "Not checked" };
#else
                    laboratoryManualState = key == LaboratoryFileFilter.Key ? LaboratoryFileFilter.Inspect()
                        : key == LaboratoryClockRepair.Key ? LaboratoryClockRepair.Inspect() : LaboratoryAudioEffects.Inspect();
#endif
                }
                catch (Exception error) { laboratoryManualState = Failed(key, error); }
                laboratoryManualState = laboratoryManualState ?? new LaboratoryManualState();
                laboratoryManualState.CanApply = false;
                lock (gate) states[key] = laboratoryManualState;
            }
            lock (gate) candidates = new FileFilterCandidate[0];
        }
        private static bool Queue(string key, Action action, Action completed)
        {
            lock (gate)
            {
                if (closing || worker != null) return false;
                busyKey = key; idle.Reset();
                try
                {
                    worker = new Thread(delegate()
                    {
                        try { action(); }
                        catch (Exception error) { var failure = Failed(key, error); lock (gate) states[key] = failure; }
                        finally
                        {
                            lock (gate) { worker = null; busyKey = ""; idle.Set(); }
                            try { if (completed != null) completed(); } catch { }
                        }
                    }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Pavise.ManualLaboratory" };
                    worker.Start(); return true;
                }
                catch (Exception error)
                {
                    worker = null; busyKey = ""; idle.Set();
                    states[key] = new LaboratoryManualState { Failure = error.GetType().Name };
                    return false;
                }
            }
        }
        internal static bool Refresh(Action completed) { return Queue("inspection", InspectAll, completed); }
        internal static bool Run(string key, bool restore, FileFilterCandidate target, Func<Func<bool>, bool> admission, Action completed)
        {
            if (!restore || Array.IndexOf(Keys, key) < 0 || admission == null) return false;
            return Queue(key, delegate
            {
                string error = ""; bool entered = false;
                bool ok = admission(delegate
                {
                    entered = true;
#if PAVISE_SELFTEST
                    if (ExecuteForTest == null) { error = "Native writes are disabled in isolated checks"; return false; }
                    error = ExecuteForTest(key, restore, target) ?? ""; return error.Length == 0;
#else
                    if (key == LaboratoryFileFilter.Key) return LaboratoryFileFilter.Restore(out error);
                    if (key == LaboratoryClockRepair.Key) return LaboratoryClockRepair.Restore(out error);
                    return LaboratoryAudioEffects.Restore(out error);
#endif
                });
                InspectAll();
                if (!ok || !entered)
                {
                    if (!entered) error = LaboratoryActionText.Pick("请在对局外且其他维护结束后操作", "Use outside a match after other maintenance completes");
                    lock (gate)
                    {
                        LaboratoryManualState state;
                        if (!states.TryGetValue(key, out state)) state = new LaboratoryManualState();
                        state.Failure = string.IsNullOrEmpty(error) ? LaboratoryActionText.Pick("操作未完成", "Action incomplete") : error;
                        states[key] = state;
                    }
                }
            }, completed);
        }
        internal static bool RecoverStartup()
        {
#if PAVISE_SELFTEST
            if (StartupRestoreForTest != null) return StartupRestoreForTest();
            return !HasRecoveryRecords;
#else
            string error; bool ok = true;
            try { if (LaboratoryFileFilter.HasReceipt && !LaboratoryFileFilter.Restore(out error)) ok = false; } catch { ok = false; }
            try { if (LaboratoryAudioEffects.HasReceipt && !LaboratoryAudioEffects.Restore(out error)) ok = false; } catch { ok = false; }
            try { if (LaboratoryClockRepair.HasReceipt && !LaboratoryClockRepair.Restore(out error)) ok = false; } catch { ok = false; }
            return ok;
#endif
        }
        internal static bool CloseAndRestoreRuntime(int timeoutMs)
        {
            lock (gate) { closing = true; if (worker == Thread.CurrentThread) return false; }
            if (!idle.WaitOne(Math.Max(0, timeoutMs))) return false;
#if PAVISE_SELFTEST
            return RuntimeRestoreForTest != null ? RuntimeRestoreForTest() : !HasRecoveryRecords;
#else
            string error; bool ok = true;
            try { if (LaboratoryAudioEffects.HasReceipt && !LaboratoryAudioEffects.Restore(out error)) ok = false; } catch { ok = false; }
            try { if (LaboratoryFileFilter.HasReceipt && !LaboratoryFileFilter.Restore(out error)) ok = false; } catch { ok = false; }
            try { if (LaboratoryClockRepair.HasReceipt && !LaboratoryClockRepair.Restore(out error)) ok = false; } catch { ok = false; }
            return ok;
#endif
        }
        internal static bool RestoreAllForReset()
        {
            if (!CloseAndRestoreRuntime(8000)) return false;
#if PAVISE_SELFTEST
            return ResetRestoreForTest != null ? ResetRestoreForTest() : !LaboratoryClockRepair.HasReceipt;
#else
            string error;
            try { return !LaboratoryClockRepair.HasReceipt || LaboratoryClockRepair.Restore(out error); } catch { return false; }
#endif
        }
    }
}
