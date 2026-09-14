// @author bdth 2074055628@qq.com
// File purpose Change processor idle state only within the match, writing the AC value of the active scheme, receipt-tracked and restored at match end
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
                // A Prepared record written before the write cannot prove the native call actually ran
                // Only a successful write with a verified read-back may be replayed as Owned
                string tag = phase == CpuIdlePhase.Prepared ? "P" : phase == CpuIdlePhase.Owned ? "O"
                    : phase == CpuIdlePhase.Restoring ? "R" : "S";
                return "2|" + Scheme.ToString("D") + "|0|" + tag;
            }
        }

        private static CpuIdleReceipt cpuIdleReceipt;

        // Ryzen boost relies on idle cores entering CC6 to free power and thermal headroom; disabling idle throttles our own single-core boost
        //   Not offered on AMD processors; machines with an existing receipt still restore per receipt
        private static bool? cpuIdleVendorBlocked;

        internal static bool CpuIdleVendorBlocked
        {
            get
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (CpuIdleAmdForTest.HasValue) return CpuIdleAmdForTest.Value;
#endif
                // Asked on every environment orchestration round; the processor never changes mid-run, so read the registry once
                bool? cached = cpuIdleVendorBlocked;
                if (cached.HasValue) return cached.Value;
                bool blocked = CpuIdleReadVendorBlocked();
                cpuIdleVendorBlocked = blocked;
                return blocked;
            }
        }

        private static bool CpuIdleReadVendorBlocked()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k == null) return false;
                    string name = (k.GetValue("ProcessorNameString") as string) ?? "";
                    string vendor = (k.GetValue("VendorIdentifier") as string) ?? "";
                    return name.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0
                        || vendor.IndexOf("AuthenticAMD", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        internal static bool CpuIdleEligible
        {
            get
            {
                lock (lk)
                {
                    try
                    {
                        if (CpuIdleVendorBlocked) return false;
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

        // When the scheme is switched away our value still hangs on the old scheme; callers treat it as not in effect
        //   The next activation round finds the scheme mismatch in the ownership check, restores the old scheme per receipt, then pins the new one
        internal static bool CpuIdleSchemeDrifted
        {
            get
            {
                lock (lk)
                {
                    var receipt = cpuIdleReceipt;
                    if (receipt == null || !receipt.Applied) return false;
                    try
                    {
                        Guid? current = CpuIdleCurrentScheme();
                        return current.HasValue && current.Value != Guid.Empty
                            && current.Value != receipt.Scheme;
                    }
                    catch { return false; }
                }
            }
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
                        // While the original value is still unresolved, do not overwrite it with a second snapshot
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
                    // A value that is already 1 is not ours, even if the user wanted that value anyway
                    if (value == 1) return true;

                    var receipt = new CpuIdleReceipt { Scheme = scheme, Phase = CpuIdlePhase.Prepared };
                    cpuIdleReceipt = receipt;
                    if (!WriteCpuIdleLedgerVerified(receipt.Text(CpuIdlePhase.Prepared))
                        || !CpuIdleMayContinue(mayContinue)) return FailCpuIdleApply();

                    // Ledger persistence can be slow; before entering the native write both the target and original values
                    // must be re-read immediately
                    Guid checkedScheme;
                    if (!TryGetCpuIdleTarget(mayContinue, out checkedScheme) || checkedScheme != scheme
                        || !CpuIdleReadAc(scheme, out value) || value != 0
                        || !CpuIdleMayContinue(mayContinue)) return FailCpuIdleApply();
                    receipt.ApplyAttempted = true;
                    if (!CpuIdleWriteAc(scheme, 1)) return FailCpuIdleApply();
                    // Even if cancelled after the write, first determine whether there is a confirmed value
                    // that needs rolling back
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
                        // A value changed externally afterwards is not ours to revert; when the ledger cannot be cleared
                        // remember that in memory too
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
                    // Re-check after the pre-write restore record; once the original value has been observed
                    // do not infer ownership from a newer value again
                    if (!CpuIdleReadAc(receipt.Scheme, out value)) return false;
                    if (value > 1 || (value == 1 && receipt.OriginalObserved))
                    {
                        receipt.Phase = CpuIdlePhase.Settled;
                        return ClearCpuIdleReceipt();
                    }
                    if (value == 1)
                    {
                        if (!receipt.MayRestoreValue) return false;
                        // Neither an exception nor an unverified successful write may trigger a second restore
                        // that would clobber a 1 written externally later
                        receipt.MayRestoreValue = false;
                        if (!CpuIdleWriteAc(receipt.Scheme, 0)) return false;
                        if (!CpuIdleReadAc(receipt.Scheme, out value) || value != 0) return false;
                    }
                    receipt.OriginalObserved = true;
                    receipt.MayRestoreValue = false;
                    // Seeing R + 0 after a crash still needs a re-apply if this scheme is still the active one
                    // A stored 0 does not prove the kernel ever read it
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
            // Version 1 wrote A before the native write, so it can only count as Prepared
            // Do not assume ownership just because the current value is 1
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
            // Persist the settled receipt first; if cleanup fails, a later process must not
            // replay O after the user has changed this value again
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

        // Target is the current active scheme, no longer requires the Pavise managed scheme; receipts keyed by scheme GUID
        //   If the scheme is switched away mid-match, the ownership check finds the mismatch, restores the old scheme per receipt, next round pins the new one
        //   No gate on power source, the toggle is the user's choice; applies on battery too, both sides written together
        private static bool TryGetCpuIdleTarget(Func<bool> mayContinue, out Guid scheme)
        {
            scheme = Guid.Empty;
            if (!CpuIdleMayContinue(mayContinue)) return false;
            Guid? current = CpuIdleCurrentScheme();
            if (!CpuIdleMayContinue(mayContinue) || !current.HasValue || current.Value == Guid.Empty) return false;
            scheme = current.Value;
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
            if (requireCurrent && !CpuIdleMayContinue(mayContinue)) return false;
            // Blocked while reading the value or checking AC power, another program switched the scheme meanwhile
            // In that case do not activate a stale target
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

        private static Guid? CpuIdleCurrentScheme()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleCurrentSchemeForTest == null) throw CpuIdleMissingHook();
            return CpuIdleCurrentSchemeForTest();
#else
            return Current();
#endif
        }

        // AC/DC treated as one unit; on battery the kernel reads the DC value, writing one side only means no effect on battery
        //   Composite value 0=both 0, 1=both 1, anything else including mismatched sides folds to 2 and takes the existing external-value branch
        //   Mismatched sides mean someone hand-edited one of them; the pair is neither taken over nor ours to revert
        private static bool CpuIdleReadAc(Guid scheme, out uint value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleReadAcForTest == null) throw CpuIdleMissingHook();
            return CpuIdleReadAcForTest(scheme, out value);
#else
            value = 0;
            uint ac, dc;
            if (!ReadAc(scheme, SubProcessor, IdleDisableSet, out ac)
                || !ReadDc(scheme, SubProcessor, IdleDisableSet, out dc)) return false;
            value = ac == dc && ac <= 1 ? ac : 2;
            return true;
#endif
        }

        private static bool CpuIdleWriteAc(Guid scheme, uint value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleWriteAcForTest == null) throw CpuIdleMissingHook();
            return CpuIdleWriteAcForTest(scheme, value);
#else
            return WriteAc(scheme, SubProcessor, IdleDisableSet, value)
                && WriteDc(scheme, SubProcessor, IdleDisableSet, value);
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
        internal static bool? CpuIdleAmdForTest;
        internal static Func<Guid?> CpuIdleCurrentSchemeForTest;
        internal static CpuIdleReadAcDelegate CpuIdleReadAcForTest;
        internal static Func<Guid, uint, bool> CpuIdleWriteAcForTest;
        internal static Func<Guid, bool> CpuIdleSetActiveForTest;
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
                CpuIdleAmdForTest = null;
                CpuIdleCurrentSchemeForTest = null;
                CpuIdleReadAcForTest = null;
                CpuIdleWriteAcForTest = null;
                CpuIdleSetActiveForTest = null;
                CpuIdleReadLedgerForTest = null;
                CpuIdleWriteLedgerForTest = null;
            }
        }
#endif
    }
}
