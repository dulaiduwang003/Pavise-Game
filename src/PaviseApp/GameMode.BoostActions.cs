using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private static int BoostActionIdentity(IntPtr handle, long creation)
        {
            return Native.QueryProcessIdentityState(handle, creation);
        }
        private static string BoostActionTarget(IntPtr handle)
        {
            string imagePath = Native.ImagePath(handle);
            try
            {
                return (string.IsNullOrEmpty(imagePath) || !Path.IsPathRooted(imagePath)) ? null : Path.GetFullPath(imagePath);
            }
            catch
            {
                return null;
            }
        }
        private static long QosValue(int control, int state)
        {
            return (long)(((ulong)(uint)control << 32) | (uint)state);
        }
        private static long? ReadBoostAction(string action, IntPtr handle)
        {
            switch (action)
            {
            case "GamePriority":
            {
                uint priorityClass = Native.GetPriorityClass(handle);
                if (priorityClass != 0)
                {
                    return priorityClass;
                }
                return null;
            }
            case "GameIoPriority":
            {
                int ioPriority = Native.QueryIoPriority(handle);
                if (ioPriority >= 0)
                {
                    return ioPriority;
                }
                return null;
            }
            case "GameMemoryPriority":
            {
                int pagePriority = Native.QueryPagePriority(handle);
                if (pagePriority >= 1)
                {
                    return pagePriority;
                }
                return null;
            }
            case "GameGpuPriority":
            {
                int cls;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(handle, out cls) != 0)
                {
                    return null;
                }
                return cls;
            }
            case "GameEcoQoS":
            {
                int controlMask;
                int stateMask;
                if (!Native.PowerThrottlingSupported || !Native.TryQueryPowerThrottling(handle, out controlMask, out stateMask))
                {
                    return null;
                }
                return QosValue(controlMask, stateMask);
            }
            default:
                return null;
            }
        }
        private static int WriteBoostAction(string action, IntPtr handle, long value)
        {
            switch (action)
            {
            case "GamePriority":
                if (!Native.SetPriorityClass(handle, (uint)value))
                {
                    return Marshal.GetLastWin32Error();
                }
                return 0;
            case "GameIoPriority":
            {
                if (value == 3 && !Native.EnsureBoostPrivilege())
                {
                    return 1314;
                }
                int status2;
                if (!Native.TrySetIoPriority(handle, (int)value, out status2))
                {
                    return status2;
                }
                return 0;
            }
            case "GameGpuPriority":
                return Native.D3DKMTSetProcessSchedulingPriorityClass(handle, (int)value);
            case "GameMemoryPriority":
            {
                int status;
                if (!Native.TrySetPagePriority(handle, (int)value, out status))
                {
                    return status;
                }
                return 0;
            }
            case "GameEcoQoS":
                if (!Native.RestorePowerThrottling(handle, (int)(value >> 32), (int)value))
                {
                    return Marshal.GetLastWin32Error();
                }
                return 0;
            default:
                return 50;
            }
        }
        private static int WriteHighQoSBoost(IntPtr handle)
        {
            bool timerExemptDropped;
            bool applyHighQoSSucceeded = Native.ApplyHighQoS(handle, Native.TimerExemptWanted, out timerExemptDropped);
            if (timerExemptDropped)
            {
                Logger.Log(Lang.T("log.gamemodeboost.59", 0));
            }
            if (!applyHighQoSSucceeded)
            {
                return Marshal.GetLastWin32Error();
            }
            return 0;
        }
        private static bool BoostActionSkipStatus(int status)
        {
            if (status != 5 && status != 50 && status != 87 && status != 120 && status != 127 && status != 1314 && status != -1073741790 && status != -1073741637 && status != -1073741821)
            {
                return status == -1073741811;
            }
            return true;
        }
        private bool TryOriginalBoostAction(int pid, string action, out long original)
        {
            original = 0L;
            lock (sync)
            {
                Snap value;
                if (!gameBoost.TryGetValue(pid, out value))
                {
                    return false;
                }
                switch (action)
                {
                case "GamePriority":
                    original = value.Pri;
                    return value.Pri != 0 && value.Pri != uint.MaxValue;
                case "GameIoPriority":
                    original = value.Io;
                    return value.Io >= 0;
                case "GameMemoryPriority":
                    original = value.Pg;
                    return value.Pg >= 1;
                case "GameGpuPriority":
                {
                    int value2;
                    if (!gameGpu.TryGetValue(pid, out value2) || value2 < 0)
                    {
                        return false;
                    }
                    original = value2;
                    return true;
                }
                case "GameEcoQoS":
                    original = QosValue(value.QoSControl, value.QoSState);
                    return value.QoSControl >= 0 && value.QoSState >= 0;
                default:
                    return false;
                }
            }
        }
        private bool ApplyIsolatedBoostAction(IntPtr handle, int pid, BoostPass pass, string action, Func<long, bool> desired, Func<int> write)
        {
            if (action == "GamePriority")
            {
                Func<int> requestedWrite = write;
                write = () => WriteOwnedGamePriority(handle, pid, pass, requestedWrite);
            }
            string actionTarget = pass.ActionTarget;
            if (string.IsNullOrEmpty(actionTarget) || pass.WriteDenied || stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration || BoostActionIdentity(handle, pass.RendererCreation) != 0 || !string.Equals(BoostActionTarget(handle), actionTarget, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            ActionFailureStore.BeginAutomaticCycle(action, actionTarget, AutomaticPhaseKey(action));
            lock (sync)
            {
                boostActionTargets[pid] = actionTarget;
            }
            long original;
            if (!TryOriginalBoostAction(pid, action, out original))
            {
                return false;
            }
            long? boostAction = ReadBoostAction(action, handle);
            if (!boostAction.HasValue)
            {
                return false;
            }
            bool restorePending;
            lock (sync)
            {
                HashSet<string> value;
                restorePending = boostActionRestorePending.TryGetValue(pid, out value) && value.Contains(action);
            }
            if (restorePending)
            {
                if (!RestoreBoostActionTarget(handle, pid, actionTarget, action))
                {
                    return false;
                }
                if (restorePending)
                {
                    ActionFailureStore.CancelApply(action, actionTarget);
                }
                boostAction = ReadBoostAction(action, handle);
                if (!boostAction.HasValue)
                {
                    return false;
                }
            }
            if (ActionFailureStore.IsBlocked(action, actionTarget))
            {
                return false;
            }
            if (desired(boostAction.Value))
            {
                return true;
            }
            string reason;
            if (!ActionFailureStore.TryBeginApply(action, actionTarget, out reason))
            {
                return false;
            }
            bool writeAttempted = false;
            try
            {
                boostAction = ReadBoostAction(action, handle);
                if (!boostAction.HasValue || pass.WriteDenied || stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration || BoostActionIdentity(handle, pass.RendererCreation) != 0)
                {
                    ActionFailureStore.CancelApply(action, actionTarget);
                    return false;
                }
                int applyIsolatedBoostActionValue = 0;
                if (!desired(boostAction.Value))
                {
                    writeAttempted = true;
                    SetBoostActionRecovery(pid, action, true);
                    applyIsolatedBoostActionValue = write();
                }
                switch (BoostActionIdentity(handle, pass.RendererCreation))
                {
                case 1:
                    SetBoostActionRecovery(pid, action, false);
                    ActionFailureStore.CancelApply(action, actionTarget);
                    return false;
                default:
                    if (!writeAttempted)
                    {
                        ActionFailureStore.CancelApply(action, actionTarget);
                    }
                    else
                    {
                        ActionFailureStore.Trip(action, actionTarget, "Game action completion identity unreadable; recovery pending");
                    }
                    return false;
                case 0:
                {
                    if (stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration)
                    {
                        if (!writeAttempted || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
                        {
                            ActionFailureStore.CancelApply(action, actionTarget);
                        }
                        else
                        {
                            ActionFailureStore.Trip(action, actionTarget, "Cancelled game action recovery remains incomplete");
                        }
                        return false;
                    }
                    long? boostAction2 = ReadBoostAction(action, handle);
                    if (applyIsolatedBoostActionValue == 0 && boostAction2.HasValue && desired(boostAction2.Value))
                    {
                        if (ActionFailureStore.CompleteApply(action, actionTarget))
                        {
                            SetBoostActionRecovery(pid, action, false);
                            return true;
                        }
                        if (RestoreBoostAction(handle, pid, pass.RendererCreation, action))
                        {
                            ActionFailureStore.CancelApply(action, actionTarget);
                        }
                        return false;
                    }
                    bool restoreBoostActionSucceeded = RestoreBoostAction(handle, pid, pass.RendererCreation, action);
                    if (BoostActionSkipStatus(applyIsolatedBoostActionValue) || (action == "GamePriority" && applyIsolatedBoostActionValue == 1237))
                    {
                        if (restoreBoostActionSucceeded)
                        {
                            ActionFailureStore.CancelApply(action, actionTarget);
                        }
                        else
                        {
                            ActionFailureStore.Trip(action, actionTarget, "Skipped game action still requires verified recovery");
                        }
                        return false;
                    }
                    reason = "Game action apply/readback failed; native status " + applyIsolatedBoostActionValue + (restoreBoostActionSucceeded ? "; original restored" : "；等待恢复");
                    ActionFailureStore.Trip(action, actionTarget, reason);
                    Logger.Warn(action + " | " + actionTarget + " | " + reason);
                    return false;
                }
                }
            }
            catch (NotSupportedException)
            {
                if (!writeAttempted || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
                {
                    ActionFailureStore.CancelApply(action, actionTarget);
                }
                else
                {
                    ActionFailureStore.Trip(action, actionTarget, "Unsupported game action recovery remains incomplete");
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                if (!writeAttempted || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
                {
                    ActionFailureStore.CancelApply(action, actionTarget);
                }
                else
                {
                    ActionFailureStore.Trip(action, actionTarget, "Cancelled game action recovery remains incomplete");
                }
                return false;
            }
            catch (Exception ex3)
            {
                if (!writeAttempted)
                {
                    ActionFailureStore.CancelApply(action, actionTarget);
                    return false;
                }
                if (writeAttempted)
                {
                    RestoreBoostAction(handle, pid, pass.RendererCreation, action);
                }
                ActionFailureStore.Trip(action, actionTarget, ex3.GetType().Name + ": " + ex3.Message);
                return false;
            }
        }
        private void ForgetBoostActionTarget(int pid)
        {
            lock (sync)
            {
                boostActionTargets.Remove(pid);
                boostActionRestorePending.Remove(pid);
                boostResourceSelections.Remove(pid);
            }
        }
        private static string ResourcePriorityKey(string action)
        {
            switch (action)
            {
            case "GamePriority":
                return "GmBoostCpuV1";
            case "GameMemoryPriority":
                return "GmBoostMemoryV1";
            case "GameIoPriority":
                return "GmBoostIoV1";
            case "GameGpuPriority":
                return "GmBoostGpuV1";
            default:
                throw new ArgumentException("Unknown resource priority", "action");
            }
        }
        private bool ApplySelectedBoostAction(IntPtr handle, int pid, BoostPass pass, string action, bool enabled, Func<long, bool> desired, Func<int> write)
        {
            string key = ResourcePriorityKey(action);
            HashSet<string> value;
            if (!enabled)
            {
                bool hasOwnedAction;
                lock (sync)
                {
                    HashSet<string> value2;
                    hasOwnedAction = (boostResourceSelections.TryGetValue(pid, out value) && value.Contains(action)) || (boostActionRestorePending.TryGetValue(pid, out value2) && value2.Contains(action));
                }
                bool actionRestored = !hasOwnedAction || RestoreBoostAction(handle, pid, pass.RendererCreation, action);
                if (actionRestored)
                {
                    lock (sync)
                    {
                        if (boostResourceSelections.TryGetValue(pid, out value))
                        {
                            value.Remove(action);
                        }
                    }
                    if (!string.IsNullOrEmpty(pass.ActionTarget))
                    {
                        ActionFailureStore.CancelApply(action, pass.ActionTarget);
                    }
                }
                RecordAutomation(key, (!actionRestored) ? "restore-failed" : (hasOwnedAction ? "restored" : "unchanged"));
                return actionRestored;
            }
            bool applyIsolatedBoostActionSucceeded = ApplyIsolatedBoostAction(handle, pid, pass, action, desired, write);
            if (applyIsolatedBoostActionSucceeded)
            {
                lock (sync)
                {
                    if (!boostResourceSelections.TryGetValue(pid, out value))
                    {
                        value = (boostResourceSelections[pid] = new HashSet<string>(StringComparer.Ordinal));
                    }
                    value.Add(action);
                }
            }
            RecordAutomation(key, applyIsolatedBoostActionSucceeded ? "applied" : (pass.WriteDenied ? "unsupported" : "unverified"));
            return applyIsolatedBoostActionSucceeded;
        }

    }
}
