using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class CoreThreadPlacement
    {
        private static readonly object recoveryGate = new object();
        private static readonly AutomaticRecoveryBudget recoveryBudget = new AutomaticRecoveryBudget();
        private static long recoveryDue;
        private sealed class Receipt
        {
            internal int Pid;

            internal int Tid;

            internal long ProcessCreation;

            internal long ThreadCreation;

            internal uint[] Original;

            internal uint[] Applied;

            internal IntPtr Handle;
        }

        internal const string JournalKey = "AutoCoreThreadPlacementV1";

        private static int journalGeneration = -1;

        private static bool unownedPending;

        internal static bool UnownedPending
        {
            get
            {
                int mutationGeneration = Settings.MutationGeneration;
                if (journalGeneration != mutationGeneration)
                {
                    unownedPending = HasResidue;
                    journalGeneration = mutationGeneration;
                }
                return unownedPending;
            }
        }

        internal static bool HasResidue
        {
            get
            {
                string text;
                if (Settings.TryLoadStr("AutoCoreThreadPlacementV1", out text))
                {
                    return text.Length != 0;
                }
                return true;
            }
        }

        internal static bool Same(uint[] a, uint[] b)
        {
            if (a != null && b != null && a.Length == b.Length)
            {
                return new HashSet<uint>(a).SetEquals(b);
            }
            return false;
        }

        private static bool RestoreReceipt(Receipt r)
        {
            if (r.Tid != 0 && Native.WaitForSingleObject(r.Handle, 0u) == 0)
            {
                return true;
            }
            if (r.Tid == 0 && !Native.StillActive(r.Handle))
            {
                return true;
            }
            uint[] cpuSets = ((r.Tid == 0) ? Native.QueryCpuSets(r.Handle) : Native.QueryThreadCpuSets(r.Handle));
            if (cpuSets == null)
            {
                return false;
            }
            if (!Same(cpuSets, r.Applied))
            {
                return true;
            }
            if ((r.Tid == 0) ? Native.RestoreCpuSets(r.Handle, r.Original) : Native.WriteThreadCpuSets(r.Handle, r.Original))
            {
                return Same((r.Tid == 0) ? Native.QueryCpuSets(r.Handle) : Native.QueryThreadCpuSets(r.Handle), r.Original);
            }
            return false;
        }

        internal static bool Recover()
        {
            lock (recoveryGate)
            {
                if (recoveryBudget.BeginCycle(PerformanceTuning.RecoveryCycleSerial + "\nlegacy-core-thread")) recoveryDue = 0;
                if (!recoveryBudget.CanAttempt) { RecoveryAttemptTrace.Exhausted(); return false; }
                if (DateTime.UtcNow.Ticks < recoveryDue) { RecoveryAttemptTrace.DeferredUntil(recoveryDue); return false; }
                bool restored = false;
                try { restored = RecoverCore(); return restored; }
                catch { return false; }
                finally
                {
                    if (restored) { recoveryBudget.CompleteRecovery(); recoveryDue = 0; }
                    else
                    {
                        recoveryBudget.Record(false);
                        recoveryDue = DateTime.UtcNow.AddSeconds(30).Ticks;
                        RecoveryAttemptTrace.FailureSchedule(recoveryBudget.FailureCount, 3, recoveryDue);
                        Logger.Warn("旧核心放置恢复失败=" + recoveryBudget.FailureCount + "/3；保留恢复日志");
                    }
                }
            }
        }

        private static bool RecoverCore()
        {
            string text;
            if (!Settings.TryLoadStr("AutoCoreThreadPlacementV1", out text))
            {
                return false;
            }
            if (text.Length == 0)
            {
                return true;
            }
            if (text.Length > 65536)
            {
                return false;
            }
            List<string> entryTexts = new List<string>();
            string[] valueParts = text.Split(';');
            Array.Reverse(valueParts);
            foreach (string entryText in valueParts)
            {
                IntPtr processHandle = IntPtr.Zero;
                IntPtr threadHandle = IntPtr.Zero;
                try
                {
                    string[] entryTextParts = entryText.Split('|');
                    if (entryTextParts.Length != 6)
                    {
                        entryTexts.Add(entryText);
                        continue;
                    }
                    Receipt currentReceipt = new Receipt();
                    currentReceipt.Pid = int.Parse(entryTextParts[0]);
                    currentReceipt.ProcessCreation = long.Parse(entryTextParts[1]);
                    currentReceipt.Tid = int.Parse(entryTextParts[2]);
                    currentReceipt.ThreadCreation = long.Parse(entryTextParts[3]);
                    currentReceipt.Original = ParseIds(entryTextParts[4]);
                    currentReceipt.Applied = ParseIds(entryTextParts[5]);
                    if (currentReceipt.Pid <= 0 || currentReceipt.ProcessCreation <= 0 || currentReceipt.Tid < 0 || (currentReceipt.Tid > 0 && currentReceipt.ThreadCreation <= 0) || currentReceipt.Applied.Length == 0)
                    {
                        entryTexts.Add(entryText);
                        continue;
                    }
                    processHandle = Native.OpenProcess(0x1000 | ((currentReceipt.Tid == 0) ? 8704 : 0), false, currentReceipt.Pid);
                    long creation;
                    long cpu;
                    ulong io;
                    if (processHandle == IntPtr.Zero)
                    {
                        if (Marshal.GetLastWin32Error() != 87)
                        {
                            entryTexts.Add(entryText);
                        }
                    }
                    else if (!Native.QueryProcessSample(processHandle, out creation, out cpu, out io))
                    {
                        entryTexts.Add(entryText);
                    }
                    else
                    {
                        if (creation != currentReceipt.ProcessCreation || !Native.StillActive(processHandle))
                        {
                            continue;
                        }
                        if (currentReceipt.Tid == 0)
                        {
                            currentReceipt.Handle = processHandle;
                            if (!RestoreReceipt(currentReceipt)) entryTexts.Add(entryText);
                            continue;
                        }
                        threadHandle = Native.OpenThread(1051648, false, currentReceipt.Tid);
                        long exit;
                        long kernel;
                        long user;
                        if (threadHandle == IntPtr.Zero)
                        {
                            if (Marshal.GetLastWin32Error() != 87)
                            {
                                entryTexts.Add(entryText);
                            }
                        }
                        else if (!Native.GetThreadTimes(threadHandle, out creation, out exit, out kernel, out user))
                        {
                            entryTexts.Add(entryText);
                        }
                        else if (exit == 0 && creation == currentReceipt.ThreadCreation && Native.QueryThreadOwnerPid(threadHandle) == currentReceipt.Pid)
                        {
                            currentReceipt.Handle = threadHandle;
                            if (!RestoreReceipt(currentReceipt)) entryTexts.Add(entryText);
                        }
                        continue;
                    }
                }
                catch
                {
                    entryTexts.Add(entryText);
                }
                finally
                {
                    if (threadHandle != IntPtr.Zero)
                    {
                        Native.CloseHandle(threadHandle);
                    }
                    if (processHandle != IntPtr.Zero)
                    {
                        Native.CloseHandle(processHandle);
                    }
                }
            }
            entryTexts.Reverse();
            if (Settings.SaveStrDurable("AutoCoreThreadPlacementV1", string.Join(";", entryTexts)))
            {
                return entryTexts.Count == 0;
            }
            return false;
        }

        private static uint[] ParseIds(string value)
        {
            if (value.Length != 0)
            {
                return Array.ConvertAll(value.Split(','), uint.Parse);
            }
            return new uint[0];
        }
    }
}
