// @author bdth 2074055628@qq.com
// Session-owned processor idle changes; never modify a user-selected power plan.
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        internal const string CpuIdleLedgerKey = "CpuIdleStateV1";

        private enum CpuIdlePhase { Prepared, Owned, Restoring, Settled }

        private sealed class CpuIdleReceipt
        {
            internal Guid Scheme;
            internal CpuIdlePhase Phase;
            internal bool Applied, ApplyAttempted;
            internal bool MayRestoreValue, OriginalObserved;

            internal string Text(CpuIdlePhase phase)
            {
                // A prepared write is not proof that the native call ran. Only
                // a successful write and its readback may be replayed as owned.
                string tag = phase == CpuIdlePhase.Prepared ? "P" : phase == CpuIdlePhase.Owned ? "O"
                    : phase == CpuIdlePhase.Restoring ? "R" : "S";
                return "2|" + Scheme.ToString("D") + "|0|" + tag;
            }
        }

        private static CpuIdleReceipt cpuIdleReceipt;

        internal static bool CpuIdleEligible
        {
            get
            {
                lock (lk)
                {
                    try
                    {
                        Guid scheme;
                        uint value;
                        return TryGetCpuIdleTarget(null, out scheme)
                            && CpuIdleReadAc(scheme, out value) && value <= 1;
                    }
                    catch { return false; }
                }
            }
        }

        internal static bool CpuIdleActive
        {
            get { lock (lk) return cpuIdleReceipt != null && cpuIdleReceipt.Applied; }
        }

        internal static bool CpuIdleHasResidue
        {
            get
            {
                lock (lk)
                {
                    if (cpuIdleReceipt != null) return true;
                    try
                    {
                        string text;
                        return !CpuIdleReadLedger(out text) || text == null || text.Length != 0;
                    }
                    catch { return true; }
                }
            }
        }

        internal static bool TryDisableCpuIdle(Func<bool> mayContinue)
        {
            lock (lk)
            {
                try
                {
                    if (!CpuIdleMayContinue(mayContinue) || !LoadCpuIdleReceipt()) return false;
                    Guid scheme;
                    uint value;
                    if (cpuIdleReceipt != null)
                    {
                        // Never replace an unresolved original with a second snapshot.
                        if (!cpuIdleReceipt.Applied)
                        {
                            RestoreCpuIdle();
                            return false;
                        }
                        if (!TryGetCpuIdleTarget(mayContinue, out scheme)
                            || scheme != cpuIdleReceipt.Scheme
                            || !CpuIdleReadAc(scheme, out value) || !CpuIdleMayContinue(mayContinue))
                            return FailCpuIdleApply();
                        if (value != 1)
                        {
                            cpuIdleReceipt.MayRestoreValue = false;
                            cpuIdleReceipt.OriginalObserved = value == 0;
                            if (value > 1) cpuIdleReceipt.Phase = CpuIdlePhase.Settled;
                            return FailCpuIdleApply();
                        }
                        return true;
                    }

                    if (!TryGetCpuIdleTarget(mayContinue, out scheme)
                        || !CpuIdleReadAc(scheme, out value) || !CpuIdleMayContinue(mayContinue)
                        || value > 1) return false;
                    // A pre-existing 1 is not ours, even when the user requested it.
                    if (value == 1) return true;

                    var receipt = new CpuIdleReceipt { Scheme = scheme, Phase = CpuIdlePhase.Prepared };
                    cpuIdleReceipt = receipt;
                    if (!WriteCpuIdleLedgerVerified(receipt.Text(CpuIdlePhase.Prepared))
                        || !CpuIdleMayContinue(mayContinue)) return FailCpuIdleApply();

                    // Saving the ledger can be slow. Recheck both the target and
                    // the original immediately before entering the native write.
                    Guid checkedScheme;
                    if (!TryGetCpuIdleTarget(mayContinue, out checkedScheme) || checkedScheme != scheme
                        || !CpuIdleReadAc(scheme, out value) || value != 0
                        || !CpuIdleMayContinue(mayContinue) || !CpuIdleOnAc()
                        || !CpuIdleMayContinue(mayContinue)) return FailCpuIdleApply();
                    receipt.ApplyAttempted = true;
                    if (!CpuIdleWriteAc(scheme, 1)) return FailCpuIdleApply();
                    // Even a cancellation after the write must first establish
                    // whether there is a confirmed value that needs rollback.
                    if (!CpuIdleReadAc(scheme, out value)) return FailCpuIdleApply();
                    if (value != 1)
                    {
                        receipt.Phase = CpuIdlePhase.Settled;
                        return FailCpuIdleApply();
                    }
                    receipt.MayRestoreValue = true;
                    receipt.Phase = CpuIdlePhase.Owned;
                    if (!WriteCpuIdleLedgerVerified(receipt.Text(CpuIdlePhase.Owned))
                        || !CpuIdleMayContinue(mayContinue)
                        || !TryGetCpuIdleTarget(mayContinue, out checkedScheme) || checkedScheme != scheme
                        || !ReapplyCpuIdle(scheme, 1, true, mayContinue)
                        || !CpuIdleMayContinue(mayContinue)) return FailCpuIdleApply();
                    if (!CpuIdleReadAc(scheme, out value)) return FailCpuIdleApply();
                    if (value != 1)
                    {
                        ObserveCpuIdleExternalValue(scheme, value);
                        return FailCpuIdleApply();
                    }
                    if (!CpuIdleMayContinue(mayContinue)) return FailCpuIdleApply();
                    receipt.Applied = true;
                    return true;
                }
                catch { return FailCpuIdleApply(); }
            }
        }

        private static bool FailCpuIdleApply()
        {
            if (cpuIdleReceipt != null && !cpuIdleReceipt.ApplyAttempted)
                cpuIdleReceipt.Phase = CpuIdlePhase.Settled;
            RestoreCpuIdle();
            return false;
        }

        internal static bool RestoreCpuIdle()
        {
            lock (lk)
            {
                try
                {
                    if (!LoadCpuIdleReceipt()) return false;
                    var receipt = cpuIdleReceipt;
                    if (receipt == null) return true;
                    receipt.Applied = false;
                    if (receipt.Phase == CpuIdlePhase.Settled) return ClearCpuIdleReceipt();
                    uint value;
                    if (!CpuIdleReadAc(receipt.Scheme, out value)) return false;
                    if (value > 1 || (value == 1 && receipt.OriginalObserved))
                    {
                        // A later external change is not ours to undo. Remember
                        // this in memory even when clearing the ledger fails.
                        receipt.Phase = CpuIdlePhase.Settled;
                        return ClearCpuIdleReceipt();
                    }
                    if (value == 1 && !receipt.MayRestoreValue) return false;
                    if (value == 0)
                    {
                        receipt.OriginalObserved = true;
                        receipt.MayRestoreValue = false;
                        if (receipt.Phase == CpuIdlePhase.Prepared)
                        {
                            receipt.Phase = CpuIdlePhase.Settled;
                            return ClearCpuIdleReceipt();
                        }
                    }
                    if (receipt.Phase != CpuIdlePhase.Restoring)
                    {
                        if (!WriteCpuIdleLedgerVerified(receipt.Text(CpuIdlePhase.Restoring))) return false;
                        receipt.Phase = CpuIdlePhase.Restoring;
                    }
                    // Recheck after the write-ahead restore record. Never infer
                    // ownership from a newer value after observing the original.
                    if (!CpuIdleReadAc(receipt.Scheme, out value)) return false;
                    if (value > 1 || (value == 1 && receipt.OriginalObserved))
                    {
                        receipt.Phase = CpuIdlePhase.Settled;
                        return ClearCpuIdleReceipt();
                    }
                    if (value == 1)
                    {
                        if (!receipt.MayRestoreValue) return false;
                        // An exception or an unverified successful write must
                        // not cause a second restore over a later external 1.
                        receipt.MayRestoreValue = false;
                        if (!CpuIdleWriteAc(receipt.Scheme, 0)) return false;
                        if (!CpuIdleReadAc(receipt.Scheme, out value) || value != 0) return false;
                    }
                    receipt.OriginalObserved = true;
                    receipt.MayRestoreValue = false;
                    // R + 0 after a crash still needs reactivation if this plan
                    // is active: a stored zero does not prove the kernel read it.
                    if (!ReapplyCpuIdle(receipt.Scheme, 0, false, null)) return false;
                    receipt.Phase = CpuIdlePhase.Settled;
                    return ClearCpuIdleReceipt();
                }
                catch { return false; }
            }
        }

        private static bool LoadCpuIdleReceipt()
        {
            if (cpuIdleReceipt != null) return true;
            string text;
            if (!CpuIdleReadLedger(out text) || text == null) return false;
            if (text.Length == 0) return true;
            if (text.Length > 80) return false;
            string[] parts = text.Split('|');
            Guid scheme;
            if (parts.Length != 4 || (parts[0] != "1" && parts[0] != "2") || parts[2] != "0"
                || (parts[0] == "1" && parts[3] != "A" && parts[3] != "R" && parts[3] != "S")
                || (parts[0] == "2" && parts[3] != "P" && parts[3] != "O" && parts[3] != "R" && parts[3] != "S")
                || !Guid.TryParseExact(parts[1], "D", out scheme) || scheme == Guid.Empty) return false;
            // Version 1 persisted A before the native write. Treat it as
            // prepared, never as ownership merely because the current value is 1.
            CpuIdlePhase phase = parts[3] == "R" ? CpuIdlePhase.Restoring
                : parts[3] == "S" ? CpuIdlePhase.Settled
                : parts[3] == "O" ? CpuIdlePhase.Owned : CpuIdlePhase.Prepared;
            cpuIdleReceipt = new CpuIdleReceipt
            {
                Scheme = scheme, ApplyAttempted = true, Phase = phase,
                MayRestoreValue = phase == CpuIdlePhase.Owned
            };
            return true;
        }

        private static bool ClearCpuIdleReceipt()
        {
            // Persist the terminal receipt first. A failed clear must not let
            // a later process replay O after the user changes this value again.
            if (cpuIdleReceipt == null || cpuIdleReceipt.Phase != CpuIdlePhase.Settled) return false;
            if (!WriteCpuIdleLedgerVerified(cpuIdleReceipt.Text(CpuIdlePhase.Settled))) return false;
            if (!WriteCpuIdleLedgerVerified("")) return false;
            cpuIdleReceipt = null;
            return true;
        }

        private static bool WriteCpuIdleLedgerVerified(string text)
        {
            string actual;
            return CpuIdleWriteLedger(text) && CpuIdleReadLedger(out actual)
                && string.Equals(actual, text, StringComparison.Ordinal);
        }

        private static bool TryGetCpuIdleTarget(Func<bool> mayContinue, out Guid scheme)
        {
            scheme = Guid.Empty;
            if (!CpuIdleMayContinue(mayContinue)) return false;
            string choice = CpuIdleChoice();
            if (!CpuIdleMayContinue(mayContinue) || choice == null
                || (choice.Length != 0 && choice != ManagedChoice)) return false;
            Guid managed = CpuIdleManagedScheme();
            if (!CpuIdleMayContinue(mayContinue) || managed == Guid.Empty) return false;
            bool onAc = CpuIdleOnAc();
            if (!CpuIdleMayContinue(mayContinue) || !onAc) return false;
            Guid? current = CpuIdleCurrentScheme();
            if (!CpuIdleMayContinue(mayContinue) || !current.HasValue || current.Value != managed) return false;
            scheme = managed;
            return true;
        }

        private static bool ReapplyCpuIdle(Guid scheme, uint expected, bool requireCurrent, Func<bool> mayContinue)
        {
            Guid? current = CpuIdleCurrentScheme();
            if (!CpuIdleMayContinue(mayContinue) || !current.HasValue || current.Value == Guid.Empty) return false;
            if (current.Value != scheme) return !requireCurrent;
            uint value;
            if (!CpuIdleReadAc(scheme, out value)) return false;
            if (value != expected)
            {
                ObserveCpuIdleExternalValue(scheme, value);
                return false;
            }
            if (!CpuIdleMayContinue(mayContinue)) return false;
            if (requireCurrent && (!CpuIdleOnAc() || !CpuIdleMayContinue(mayContinue))) return false;
            // Do not reactivate a stale target after the value read (or AC
            // check) blocked while another application switched the plan.
            current = CpuIdleCurrentScheme();
            if (!CpuIdleMayContinue(mayContinue) || !current.HasValue || current.Value == Guid.Empty) return false;
            if (current.Value != scheme) return !requireCurrent;
            if (!CpuIdleSetActive(scheme) || !CpuIdleMayContinue(mayContinue)) return false;
            current = CpuIdleCurrentScheme();
            if (!CpuIdleMayContinue(mayContinue) || !current.HasValue || current.Value != scheme
                || !CpuIdleReadAc(scheme, out value)) return false;
            if (value != expected)
            {
                ObserveCpuIdleExternalValue(scheme, value);
                return false;
            }
            return CpuIdleMayContinue(mayContinue);
        }

        private static void ObserveCpuIdleExternalValue(Guid scheme, uint value)
        {
            var receipt = cpuIdleReceipt;
            if (receipt == null || receipt.Scheme != scheme) return;
            if (value == 0)
            {
                receipt.OriginalObserved = true;
                receipt.MayRestoreValue = false;
            }
            else if (value > 1 || receipt.OriginalObserved)
            {
                receipt.Phase = CpuIdlePhase.Settled;
                receipt.MayRestoreValue = false;
            }
        }

        private static bool CpuIdleMayContinue(Func<bool> mayContinue)
        {
            if (mayContinue == null) return true;
            try { return mayContinue(); }
            catch { return false; }
        }

        private static Guid CpuIdleManagedScheme()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleManagedSchemeForTest == null) throw CpuIdleMissingHook();
            return CpuIdleManagedSchemeForTest();
#else
            return ManagedPlanGuid();
#endif
        }

        private static Guid? CpuIdleCurrentScheme()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleCurrentSchemeForTest == null) throw CpuIdleMissingHook();
            return CpuIdleCurrentSchemeForTest();
#else
            return Current();
#endif
        }

        private static bool CpuIdleReadAc(Guid scheme, out uint value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleReadAcForTest == null) throw CpuIdleMissingHook();
            return CpuIdleReadAcForTest(scheme, out value);
#else
            return ReadAc(scheme, SubProcessor, IdleDisableSet, out value);
#endif
        }

        private static bool CpuIdleWriteAc(Guid scheme, uint value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleWriteAcForTest == null) throw CpuIdleMissingHook();
            return CpuIdleWriteAcForTest(scheme, value);
#else
            return WriteAc(scheme, SubProcessor, IdleDisableSet, value);
#endif
        }

        private static bool CpuIdleSetActive(Guid scheme)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleSetActiveForTest == null) throw CpuIdleMissingHook();
            return CpuIdleSetActiveForTest(scheme);
#else
            return Set(scheme);
#endif
        }

        private static bool CpuIdleOnAc()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleOnAcForTest == null) throw CpuIdleMissingHook();
            return CpuIdleOnAcForTest();
#else
            CpuIdlePowerStatus status;
            return CpuIdleGetSystemPowerStatus(out status) && status.AcLineStatus == 1;
#endif
        }

        private static string CpuIdleChoice()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleChoiceForTest != null) return CpuIdleChoiceForTest();
#endif
            string choice;
            if (!Settings.TryLoadStr(ChoiceKey, out choice)) throw new InvalidOperationException("Cannot read the power plan choice.");
            return choice;
        }

        private static bool CpuIdleReadLedger(out string text)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleReadLedgerForTest != null) return CpuIdleReadLedgerForTest(out text);
#endif
            return Settings.TryLoadStr(CpuIdleLedgerKey, out text);
        }

        private static bool CpuIdleWriteLedger(string text)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleWriteLedgerForTest != null) return CpuIdleWriteLedgerForTest(text);
#endif
            return Settings.SaveStr(CpuIdleLedgerKey, text);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CpuIdlePowerStatus
        {
            internal byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            internal uint BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CpuIdleGetSystemPowerStatus(out CpuIdlePowerStatus status);

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal delegate bool CpuIdleReadAcDelegate(Guid scheme, out uint value);
        internal delegate bool CpuIdleReadLedgerDelegate(out string text);
        internal static Func<Guid> CpuIdleManagedSchemeForTest;
        internal static Func<Guid?> CpuIdleCurrentSchemeForTest;
        internal static CpuIdleReadAcDelegate CpuIdleReadAcForTest;
        internal static Func<Guid, uint, bool> CpuIdleWriteAcForTest;
        internal static Func<Guid, bool> CpuIdleSetActiveForTest;
        internal static Func<bool> CpuIdleOnAcForTest;
        internal static Func<string> CpuIdleChoiceForTest;
        internal static CpuIdleReadLedgerDelegate CpuIdleReadLedgerForTest;
        internal static Func<string, bool> CpuIdleWriteLedgerForTest;

        private static Exception CpuIdleMissingHook()
        {
            return new InvalidOperationException("CPU idle tests must mock every native power boundary.");
        }

        internal static void ResetCpuIdleForTest()
        {
            lock (lk)
            {
                cpuIdleReceipt = null;
                CpuIdleManagedSchemeForTest = null;
                CpuIdleCurrentSchemeForTest = null;
                CpuIdleReadAcForTest = null;
                CpuIdleWriteAcForTest = null;
                CpuIdleSetActiveForTest = null;
                CpuIdleOnAcForTest = null;
                CpuIdleChoiceForTest = null;
                CpuIdleReadLedgerForTest = null;
                CpuIdleWriteLedgerForTest = null;
            }
        }
#endif
    }
}
