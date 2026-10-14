using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal const string GamePriorityAction = "GamePriority";
        internal const string GameIoAction = "GameIoPriority";
        internal const string GameGpuAction = "GameGpuPriority";
        internal const string GameEcoAction = "GameEcoQoS";
        private static readonly string[] BoostActions =
            { GamePriorityAction, GameIoAction, GameGpuAction, GameEcoAction };
        private readonly Dictionary<int, string> boostActionTargets = new Dictionary<int, string>();
        private readonly Dictionary<int, HashSet<string>> boostActionRestorePending =
            new Dictionary<int, HashSet<string>>();

#if PAVISE_SELFTEST
        internal static Func<string, IntPtr, long?> BoostActionReadForTest;
        internal static Func<string, IntPtr, long, int> BoostActionWriteForTest;
        internal static Func<IntPtr, long, int> BoostActionIdentityForTest;
        internal static Func<IntPtr, string> BoostActionPathForTest;
#endif

        private static int BoostActionIdentity(IntPtr handle, long creation)
        {
#if PAVISE_SELFTEST
            if (BoostActionIdentityForTest != null) return BoostActionIdentityForTest(handle, creation);
#endif
            return Native.QueryProcessIdentityState(handle, creation);
        }

        private static string BoostActionTarget(IntPtr handle)
        {
            string path;
#if PAVISE_SELFTEST
            if (BoostActionPathForTest != null) path = BoostActionPathForTest(handle);
            else
#endif
                path = Native.ImagePath(handle);
            try { return string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) ? null : Path.GetFullPath(path); }
            catch { return null; }
        }

        private static long QosValue(int control, int state)
        { return unchecked(((long)(uint)control << 32) | (uint)state); }

        private static long? ReadBoostAction(string action, IntPtr handle)
        {
#if PAVISE_SELFTEST
            if (BoostActionReadForTest != null) return BoostActionReadForTest(action, handle);
#endif
            switch (action)
            {
                case GamePriorityAction:
                    uint priority = Native.GetPriorityClass(handle);
                    return priority == 0 ? (long?)null : priority;
                case GameIoAction:
                    int io = Native.QueryIoPriority(handle);
                    return io < 0 ? (long?)null : io;
                case GameGpuAction:
                    int gpu;
                    return Native.D3DKMTGetProcessSchedulingPriorityClass(handle, out gpu) == 0 ? (long?)gpu : null;
                case GameEcoAction:
                    int control, state;
                    return Native.PowerThrottlingSupported && Native.TryQueryPowerThrottling(handle, out control, out state)
                        ? (long?)QosValue(control, state) : null;
                default: return null;
            }
        }

        // A zero result means the native request was accepted. Completion still requires readback.
        private static int WriteBoostAction(string action, IntPtr handle, long value)
        {
#if PAVISE_SELFTEST
            if (BoostActionWriteForTest != null) return BoostActionWriteForTest(action, handle, value);
#endif
            switch (action)
            {
                case GamePriorityAction:
                    return Native.SetPriorityClass(handle, (uint)value) ? 0 : Marshal.GetLastWin32Error();
                case GameIoAction:
                    if (value == 3 && !Native.EnsureBoostPrivilege()) return 1314;
                    int status;
                    return Native.TrySetIoPriority(handle, (int)value, out status) ? 0 : status;
                case GameGpuAction:
                    return Native.D3DKMTSetProcessSchedulingPriorityClass(handle, (int)value);
                case GameEcoAction:
                    return Native.RestorePowerThrottling(handle, (int)(value >> 32), (int)value)
                        ? 0 : Marshal.GetLastWin32Error();
                default: return 50;
            }
        }

        private static int WriteHighQoSBoost(IntPtr handle)
        {
#if PAVISE_SELFTEST
            if (BoostActionWriteForTest != null)
                return BoostActionWriteForTest(GameEcoAction, handle, QosValue(Native.TimerExemptWanted ? 5 : 1, 0));
#endif
            bool timerExemptDropped;
            bool accepted = Native.ApplyHighQoS(handle, Native.TimerExemptWanted, out timerExemptDropped);
            if (timerExemptDropped) Logger.Log(Lang.T("log.gamemodeboost.59"));
            return accepted ? 0 : Marshal.GetLastWin32Error();
        }

        private static bool BoostActionSkipStatus(int status)
        {
            return status == 5 || status == 50 || status == 87 || status == 120 || status == 127 || status == 1314
                || status == unchecked((int)0xC0000022) || status == unchecked((int)0xC00000BB)
                || status == unchecked((int)0xC0000003) || status == unchecked((int)0xC000000D);
        }

        private bool TryOriginalBoostAction(int pid, string action, out long original)
        {
            original = 0;
            lock (sync)
            {
                Snap snapshot;
                if (!gameBoost.TryGetValue(pid, out snapshot)) return false;
                switch (action)
                {
                    case GamePriorityAction:
                        original = snapshot.Pri; return snapshot.Pri != 0 && snapshot.Pri != uint.MaxValue;
                    case GameIoAction:
                        original = snapshot.Io; return snapshot.Io >= 0;
                    case GameGpuAction:
                        int gpu;
                        if (!gameGpu.TryGetValue(pid, out gpu) || gpu < 0) return false;
                        original = gpu; return true;
                    case GameEcoAction:
                        original = QosValue(snapshot.QoSControl, snapshot.QoSState);
                        return snapshot.QoSControl >= 0 && snapshot.QoSState >= 0;
                    default: return false;
                }
            }
        }

        private void SetBoostActionRecovery(int pid, string action, bool pending)
        {
            lock (sync)
            {
                HashSet<string> actions;
                if (!boostActionRestorePending.TryGetValue(pid, out actions))
                {
                    if (!pending) return;
                    boostActionRestorePending[pid] = actions = new HashSet<string>(StringComparer.Ordinal);
                }
                if (pending) actions.Add(action);
                else { actions.Remove(action); if (actions.Count == 0) boostActionRestorePending.Remove(pid); }
            }
        }

        private bool RestoreBoostAction(IntPtr handle, int pid, long creation, string action)
        {
            try
            {
                int identity = BoostActionIdentity(handle, creation);
                if (identity == 1) { SetBoostActionRecovery(pid, action, false); return true; }
                if (identity != 0) return false;
                long original;
                if (!TryOriginalBoostAction(pid, action, out original)) return false;
                long? observed = ReadBoostAction(action, handle);
                if (!observed.HasValue) return false; // An unreadable current value never authorizes a write.
                bool accepted = observed.Value == original || WriteBoostAction(action, handle, original) == 0;
                long? restored = ReadBoostAction(action, handle);
                identity = BoostActionIdentity(handle, creation);
                bool ok = identity == 1 || identity == 0 && accepted && restored.HasValue && restored.Value == original;
                if (ok) SetBoostActionRecovery(pid, action, false);
                return ok;
            }
            catch { return false; }
        }

        private bool RestoreBoostActionTarget(IntPtr currentHandle, int currentPid, string target, string action)
        {
            var entries = new List<KeyValuePair<int, Snap>>();
            lock (sync)
                foreach (KeyValuePair<int, Snap> item in gameBoost)
                {
                    string path;
                    if (boostActionTargets.TryGetValue(item.Key, out path)
                        && string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) entries.Add(item);
                }
            foreach (KeyValuePair<int, Snap> item in entries)
            {
                IntPtr handle = item.Key == currentPid ? currentHandle : Native.OpenProcess(
                    Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION,
                    false, item.Key);
                if (handle == IntPtr.Zero)
                {
                    if (ProbeBoostTarget(item.Key, item.Value.Creation) == BoostTarget.Gone) continue;
                    return false;
                }
                try
                {
                    if (BoostActionIdentity(handle, item.Value.Creation) == 0
                        && !string.Equals(BoostActionTarget(handle), target, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!RestoreBoostAction(handle, item.Key, item.Value.Creation, action)) return false;
                }
                finally { if (item.Key != currentPid) Native.CloseHandle(handle); }
            }
            return true;
        }

        private bool ApplyIsolatedBoostAction(IntPtr handle, int pid, BoostPass pass, string action,
            Func<long, bool> desired, Func<int> write)
        {
            string target = pass.ActionTarget;
            if (string.IsNullOrEmpty(target) || pass.WriteDenied || stopping || panicReq
                || pass.IdentityGeneration != boostIdentityGeneration || BoostActionIdentity(handle, pass.RendererCreation) != 0
                || !string.Equals(BoostActionTarget(handle), target, StringComparison.OrdinalIgnoreCase)) return false;
            lock (sync) boostActionTargets[pid] = target;
            long original;
            if (!TryOriginalBoostAction(pid, action, out original)) return false;
            long? before = ReadBoostAction(action, handle);
            if (!before.HasValue) return false;
            bool recovery;
            lock (sync)
            {
                HashSet<string> pending;
                recovery = boostActionRestorePending.TryGetValue(pid, out pending) && pending.Contains(action);
            }
            bool retry = ActionFailureStore.IsRetryPending(action, target);
            if (recovery || retry)
            {
                if (!RestoreBoostActionTarget(handle, pid, target, action))
                {
                    if (retry) ActionFailureStore.Trip(action, target, "Recovery before explicit game-action retry failed");
                    return false;
                }
                // Only verified recovery permits removal of an unfinished prior intent.
                if (recovery) ActionFailureStore.CancelApply(action, target);
                before = ReadBoostAction(action, handle);
                if (!before.HasValue) return false;
            }
            if (ActionFailureStore.IsBlocked(action, target)) return false;
            if (desired(before.Value) && !retry) return true;
            string reason;
            if (!ActionFailureStore.TryBeginApply(action, target, out reason)) return false;
            bool issued = false;
            try
            {
                before = ReadBoostAction(action, handle);
                if (!before.HasValue || pass.WriteDenied || stopping || panicReq
                    || pass.IdentityGeneration != boostIdentityGeneration
                    || BoostActionIdentity(handle, pass.RendererCreation) != 0)
                { ActionFailureStore.CancelApply(action, target); return false; }
                int status = 0;
                if (!desired(before.Value))
                {
                    issued = true;
                    SetBoostActionRecovery(pid, action, true);
                    status = write();
                }
                int identity = BoostActionIdentity(handle, pass.RendererCreation);
                if (identity == 1)
                {
                    SetBoostActionRecovery(pid, action, false);
                    ActionFailureStore.CancelApply(action, target);
                    if (retry && issued) ActionFailureStore.WithdrawRetry(action, target);
                    return false;
                }
                if (identity != 0)
                {
                    if (!issued) ActionFailureStore.CancelApply(action, target);
                    else ActionFailureStore.Trip(action, target, "Game action completion identity unreadable; recovery pending");
                    return false;
                }
                if (stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration)
                {
                    if (!issued || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
                    {
                        ActionFailureStore.CancelApply(action, target);
                        if (retry && issued) ActionFailureStore.WithdrawRetry(action, target);
                    }
                    else ActionFailureStore.Trip(action, target, "Cancelled game action recovery remains incomplete");
                    return false;
                }
                long? after = ReadBoostAction(action, handle);
                if (status == 0 && after.HasValue && desired(after.Value))
                {
                    if (ActionFailureStore.CompleteApply(action, target))
                    { SetBoostActionRecovery(pid, action, false); return true; }
                    if (RestoreBoostAction(handle, pid, pass.RendererCreation, action))
                    {
                        ActionFailureStore.CancelApply(action, target);
                        if (retry) ActionFailureStore.WithdrawRetry(action, target);
                    }
                    return false;
                }
                bool restored = RestoreBoostAction(handle, pid, pass.RendererCreation, action);
                if (BoostActionSkipStatus(status))
                {
                    if (restored)
                    {
                        ActionFailureStore.CancelApply(action, target);
                        if (retry && issued) ActionFailureStore.WithdrawRetry(action, target);
                    }
                    else ActionFailureStore.Trip(action, target, "Skipped game action still requires verified recovery");
                    return false;
                }
                reason = "Game action apply/readback failed; native status " + status
                    + (restored ? "; original restored" : "; recovery pending");
                ActionFailureStore.Trip(action, target, reason);
                Logger.Warn(action + " | " + target + " | " + reason);
                return false;
            }
            catch (NotSupportedException)
            {
                if (!issued || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
                {
                    ActionFailureStore.CancelApply(action, target);
                    if (retry && issued) ActionFailureStore.WithdrawRetry(action, target);
                }
                else ActionFailureStore.Trip(action, target, "Unsupported game action recovery remains incomplete");
                return false;
            }
            catch (OperationCanceledException)
            {
                if (!issued || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
                {
                    ActionFailureStore.CancelApply(action, target);
                    if (retry && issued) ActionFailureStore.WithdrawRetry(action, target);
                }
                else ActionFailureStore.Trip(action, target, "Cancelled game action recovery remains incomplete");
                return false;
            }
            catch (Exception error)
            {
                if (!issued) { ActionFailureStore.CancelApply(action, target); return false; }
                if (issued) RestoreBoostAction(handle, pid, pass.RendererCreation, action);
                ActionFailureStore.Trip(action, target, error.GetType().Name + ": " + error.Message);
                return false;
            }
        }

        private bool HasBoostActionRetry(int pid)
        {
            if (!ActionFailureStore.HasRetryPermits) return false;
            string target;
            lock (sync) if (!boostActionTargets.TryGetValue(pid, out target)) return false;
            foreach (string action in BoostActions) if (ActionFailureStore.IsRetryPending(action, target)) return true;
            return false;
        }

        private void ForgetBoostActionTarget(int pid)
        { lock (sync) { boostActionTargets.Remove(pid); boostActionRestorePending.Remove(pid); } }

        private bool RestoreBoostSnapshot(IntPtr handle, int pid, Snap snapshot)
        {
            // Old receipts with unreadable originals must remain recoverable; never manufacture defaults.
            bool ok = snapshot.Pri != 0 && snapshot.Pri != uint.MaxValue && snapshot.Io >= 0
                && (!Native.PowerThrottlingSupported || snapshot.QoSControl >= 0 && snapshot.QoSState >= 0);
            foreach (string action in BoostActions)
            {
                long original;
                if (!TryOriginalBoostAction(pid, action, out original)) continue;
                bool restored = RestoreBoostAction(handle, pid, snapshot.Creation, action);
                ok &= restored;
                string target;
                lock (sync) boostActionTargets.TryGetValue(pid, out target);
                if (restored && !string.IsNullOrEmpty(target)) ActionFailureStore.CancelApply(action, target);
            }
            return ok;
        }
    }
}
