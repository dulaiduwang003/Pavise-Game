// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        internal const string LegacyCpuIdleLedgerKey = "CpuIdleStateV1";

        private enum CpuIdlePhase { Prepared, Owned, Restoring, Settled }

        private sealed class CpuIdleReceipt
        {
            internal Guid Scheme;
            internal CpuIdlePhase Phase;
            internal bool MayRestoreValue, OriginalObserved;

            internal string Text(CpuIdlePhase phase)
            {
                // 准备记录不能证明原生写入已经执行。
                string tag = phase == CpuIdlePhase.Prepared ? "P" : phase == CpuIdlePhase.Owned ? "O"
                    : phase == CpuIdlePhase.Restoring ? "R" : "S";
                return "2|" + Scheme.ToString("D") + "|0|" + tag;
            }
        }

        private static CpuIdleReceipt cpuIdleReceipt;

        internal static bool LegacyCpuIdleHasResidue
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

        internal static bool RestoreLegacyCpuIdle()
        {
            lock (lk)
            {
                try
                {
                    if (!LoadCpuIdleReceipt()) return false;
                    var receipt = cpuIdleReceipt;
                    if (receipt == null) return true;
                    if (receipt.Phase == CpuIdlePhase.Settled) return ClearCpuIdleReceipt();
                    uint value;
                    if (!CpuIdleReadAc(receipt.Scheme, out value)) return false;
                    if (value > 1 || (value == 1 && receipt.OriginalObserved))
                    {
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
                    if (!CpuIdleReadAc(receipt.Scheme, out value)) return false;
                    if (value > 1 || (value == 1 && receipt.OriginalObserved))
                    {
                        receipt.Phase = CpuIdlePhase.Settled;
                        return ClearCpuIdleReceipt();
                    }
                    if (value == 1)
                    {
                        if (!receipt.MayRestoreValue) return false;
                        // 恢复结果未确认时，不再次覆盖可能由外部写入的值。
                        receipt.MayRestoreValue = false;
                        if (!RestoreCpuIdleValue(receipt.Scheme)) return false;
                        if (!CpuIdleReadAc(receipt.Scheme, out value) || value != 0) return false;
                    }
                    receipt.OriginalObserved = true;
                    receipt.MayRestoreValue = false;
                    if (!ReapplyCpuIdle(receipt.Scheme)) return false;
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
            CpuIdlePhase phase = parts[3] == "R" ? CpuIdlePhase.Restoring
                : parts[3] == "S" ? CpuIdlePhase.Settled
                : parts[3] == "O" ? CpuIdlePhase.Owned : CpuIdlePhase.Prepared;
            cpuIdleReceipt = new CpuIdleReceipt
            {
                Scheme = scheme, Phase = phase,
                MayRestoreValue = phase == CpuIdlePhase.Owned
            };
            return true;
        }

        private static bool ClearCpuIdleReceipt()
        {
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

        private static bool ReapplyCpuIdle(Guid scheme)
        {
            Guid? current = CpuIdleCurrentScheme();
            if (!current.HasValue || current.Value == Guid.Empty) return false;
            if (current.Value != scheme) return true;
            uint value;
            if (!CpuIdleReadAc(scheme, out value)) return false;
            if (value != 0)
            {
                ObserveCpuIdleExternalValue(scheme, value);
                return false;
            }
            // 查询期间方案可能切换，不能激活过期目标。
            current = CpuIdleCurrentScheme();
            if (!current.HasValue || current.Value == Guid.Empty) return false;
            if (current.Value != scheme) return true;
            if (!CpuIdleSetActive(scheme)) return false;
            current = CpuIdleCurrentScheme();
            if (!current.HasValue || current.Value != scheme
                || !CpuIdleReadAc(scheme, out value)) return false;
            if (value != 0)
            {
                ObserveCpuIdleExternalValue(scheme, value);
                return false;
            }
            return true;
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

        private static Guid? CpuIdleCurrentScheme()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleCurrentSchemeForTest == null) throw CpuIdleMissingHook();
            return CpuIdleCurrentSchemeForTest();
#else
            return Current();
#endif
        }

        // AC、DC 作为整体判断，外部单侧修改不被认领。
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

        private static bool RestoreCpuIdleValue(Guid scheme)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleWriteAcForTest == null) throw CpuIdleMissingHook();
            return CpuIdleWriteAcForTest(scheme, 0);
#else
            return WriteAc(scheme, SubProcessor, IdleDisableSet, 0)
                && WriteDc(scheme, SubProcessor, IdleDisableSet, 0);
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
            return Settings.TryLoadStr(LegacyCpuIdleLedgerKey, out text);
        }

        private static bool CpuIdleWriteLedger(string text)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (CpuIdleWriteLedgerForTest != null) return CpuIdleWriteLedgerForTest(text);
#endif
            return Settings.SaveStr(LegacyCpuIdleLedgerKey, text);
        }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal delegate bool CpuIdleReadAcDelegate(Guid scheme, out uint value);
        internal delegate bool CpuIdleReadLedgerDelegate(out string text);
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
