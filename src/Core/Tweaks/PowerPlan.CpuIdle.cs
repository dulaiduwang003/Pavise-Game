// @author bdth 2074055628@qq.com
// 文件用途 只在本局内改处理器空闲状态 写当前活动方案的 AC 值 收据记账退局还原
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
                // 写之前的准备记录 不能证明原生调用真的跑过
                // 只有写成功并且回读通过 才允许当成已拥有重放
                string tag = phase == CpuIdlePhase.Prepared ? "P" : phase == CpuIdlePhase.Owned ? "O"
                    : phase == CpuIdlePhase.Restoring ? "R" : "S";
                return "2|" + Scheme.ToString("D") + "|0|" + tag;
            }
        }

        private static CpuIdleReceipt cpuIdleReceipt;

        // Ryzen 的睿频靠闲核进 CC6 让出功耗和热余量 不让空闲等于自己压自己的单核睿频
        //   AMD 处理器不提供 已有收据的机器照常按收据还原
        private static bool? cpuIdleVendorBlocked;

        internal static bool CpuIdleVendorBlocked
        {
            get
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (CpuIdleAmdForTest.HasValue) return CpuIdleAmdForTest.Value;
#endif
                // 每轮环境编排都会问 处理器不会中途换 只读一次注册表
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

        // 方案被切走时旧方案上还挂着我们的值 调用方把它当未生效处理
        //   下一轮激活会在所有权校验里发现方案不符 先按收据还原旧方案 再钉新方案
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
                        // 原始值还没解决的时候 不要用第二次快照去覆盖它
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
                    // 本来就是 1 的不算我们的 哪怕用户自己也想要这个值
                    if (value == 1) return true;

                    var receipt = new CpuIdleReceipt { Scheme = scheme, Phase = CpuIdlePhase.Prepared };
                    cpuIdleReceipt = receipt;
                    if (!WriteCpuIdleLedgerVerified(receipt.Text(CpuIdlePhase.Prepared))
                        || !CpuIdleMayContinue(mayContinue)) return FailCpuIdleApply();

                    // 台账落盘可能很慢 进原生写入之前 目标值和原始值
                    // 都要立刻重新读一遍
                    Guid checkedScheme;
                    if (!TryGetCpuIdleTarget(mayContinue, out checkedScheme) || checkedScheme != scheme
                        || !CpuIdleReadAc(scheme, out value) || value != 0
                        || !CpuIdleMayContinue(mayContinue)) return FailCpuIdleApply();
                    receipt.ApplyAttempted = true;
                    if (!CpuIdleWriteAc(scheme, 1)) return FailCpuIdleApply();
                    // 就算写完之后被取消 也得先弄清楚有没有一个已确认的值
                    // 需要回滚
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
                        // 后来外部改的值不归我们撤 台账清不掉的时候
                        // 也要在内存里记住这件事
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
                    // 写前恢复记录之后再检查一次 观察到原始值以后
                    // 不要再从更新的值反推所有权
                    if (!CpuIdleReadAc(receipt.Scheme, out value)) return false;
                    if (value > 1 || (value == 1 && receipt.OriginalObserved))
                    {
                        receipt.Phase = CpuIdlePhase.Settled;
                        return ClearCpuIdleReceipt();
                    }
                    if (value == 1)
                    {
                        if (!receipt.MayRestoreValue) return false;
                        // 抛异常 或者写成功但没验证过 都不能触发第二次还原
                        // 把后来外部写的 1 给盖掉
                        receipt.MayRestoreValue = false;
                        if (!CpuIdleWriteAc(receipt.Scheme, 0)) return false;
                        if (!CpuIdleReadAc(receipt.Scheme, out value) || value != 0) return false;
                    }
                    receipt.OriginalObserved = true;
                    receipt.MayRestoreValue = false;
                    // 崩溃后看到 R + 0 如果这套方案还是活动方案就仍需重新生效
                    // 存着的 0 不能证明内核读到过它
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
            // 版本 1 是在原生写入之前就落了 A 只能当成准备状态
            // 不能因为当前值是 1 就认成所有权
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
            // 先落终态收据 清理失败的话 不能让后面的进程在用户又改过
            // 这个值之后还去重放 O
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

        // 目标就是当前活动方案 不再要求 Pavise 托管方案 收据按方案 GUID 记账
        //   对局中方案被切走时 所有权校验发现方案不符 先按收据还原旧方案 下轮再钉新方案
        //   电源来源不设门 开关是用户的选择 电池上照样生效 两侧值一起写
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
            // 读值或者查交流供电的时候被阻塞 期间别的程序切了方案
            // 这种情况下不要再去激活一个过期的目标
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

        // AC/DC 两侧当一个整体 电池供电时内核读的是 DC 值 只写一侧等于电池上没生效
        //   复合值 0=两侧都 0  1=两侧都 1  其余含两侧不一致折叠成 2 走既有的外部值分支
        //   两侧不一致说明有人手改过其中一侧 整对不接管也不归我们撤
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
