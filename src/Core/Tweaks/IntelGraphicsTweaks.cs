// 文件用途 临时开启 Intel 全局低延迟 每次动驱动之前先记所有权流水
using System;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal interface IIntelGraphicsLedger
    {
        bool TryRead(out string value);
        bool TryWrite(string value);
    }

    internal sealed class IntelGraphicsSettingsLedger : IIntelGraphicsLedger
    {
        internal const string Key = "IntelLowLatencyLedgerV1";
        public bool TryRead(out string value) { return Settings.TryLoadStr(Key, out value); }
        public bool TryWrite(string value) { return Settings.SaveStr(Key, value); }
    }

    internal sealed class IntelLowLatencyEngine
    {
        private sealed class Receipt
        {
            internal string AdapterId;
            internal char Phase;
            internal bool CanRestore, Active, Settled;
        }

        private readonly IIntelGraphicsControl control;
        private readonly IIntelGraphicsLedger ledger;
        private readonly object gate = new object();
        private Receipt receipt;
        private bool suppressed, failedApplication, busy;
        // 本引擎是账本唯一写入方 确认为空后不必逐轮重读注册表
        private bool ledgerKnownEmpty;

        internal IntelLowLatencyEngine(IIntelGraphicsControl control, IIntelGraphicsLedger ledger)
        {
            if (control == null || ledger == null) throw new ArgumentNullException();
            this.control = control;
            this.ledger = ledger;
        }

        internal bool Active { get { lock (gate) return receipt != null && receipt.Active; } }
        internal bool HasResidue
        {
            get
            {
                lock (gate)
                {
                    if (receipt != null) return true;
                    if (ledgerKnownEmpty) return false;
                    string value;
                    if (!ReadLedger(out value)) return true;
                    ledgerKnownEmpty = string.IsNullOrEmpty(value);
                    return !ledgerKnownEmpty;
                }
            }
        }

        // 恢复被"无法证明当前值归属"挡住时为真 仅用于提示 不改变任何状态
        internal bool RestoreBlockedByOwnership
        {
            get { lock (gate) return receipt != null && !receipt.Settled && !receipt.CanRestore; }
        }

        // 支持性查询来自 UI 线程 Apply/Restore 持锁做驱动调用可能长达数秒
        //   查询等不到锁时返回上次结果 别把界面冻在驱动调用上 空闲时照常实查
        private bool lastHasAvailable, lastLowLatencySupported;

        internal bool HasAvailable
        {
            get
            {
                bool taken = false;
                try
                {
                    Monitor.TryEnter(gate, 100, ref taken);
                    if (!taken) return lastHasAvailable;
                    bool found = false;
                    IntelGraphicsAdapter[] adapters;
                    if (ReadAdapters(out adapters))
                        foreach (IntelGraphicsAdapter adapter in adapters)
                            if (adapter != null && adapter.VendorId == 0x8086) { found = true; break; }
                    lastHasAvailable = found;
                    return found;
                }
                finally { if (taken) Monitor.Exit(gate); }
            }
        }

        internal bool LowLatencySupported
        {
            get
            {
                bool taken = false;
                try
                {
                    Monitor.TryEnter(gate, 100, ref taken);
                    if (!taken) return lastLowLatencySupported;
                    IntelGraphicsAdapter[] adapters;
                    bool ok = ReadAdapters(out adapters) && SelectAdapter(adapters) != null;
                    lastLowLatencySupported = ok;
                    return ok;
                }
                finally { if (taken) Monitor.Exit(gate); }
            }
        }

        internal bool Apply(Func<bool> mayContinue)
        {
            lock (gate)
            {
                if (busy) return false;
                busy = true;
                try { return ApplyCore(mayContinue); }
                catch { return false; }
                finally { busy = false; }
            }
        }

        private bool ApplyCore(Func<bool> mayContinue)
        {
            if (!IntelGraphicsApi.Continue(mayContinue) || !LoadReceipt()) return false;
            if (receipt != null && receipt.Active)
            {
                uint value;
                if (!ReadMode(receipt.AdapterId, out value)) return false;
                if (!IntelGraphicsApi.Continue(mayContinue)) { RestoreCore(); return false; }
                if (value == 1) return true;
                // 外部改动在整局之内都优先 用户在驱动界面关掉之后
                // 不要反复再把这个选项打开
                suppressed = true;
                return FinishReceipt();
            }
            if (receipt != null && !RestoreCore()) return false;
            if (failedApplication) return false;
            if (suppressed) return true;
            IntelGraphicsAdapter[] adapters;
            if (!ReadAdapters(out adapters)) return false;
            if (!IntelGraphicsApi.Continue(mayContinue)) return false;
            IntelGraphicsAdapter target = SelectAdapter(adapters);
            // 硬件不支持或者混合显卡说不清时算跳过 绝不能伪造成
            // Active=true 的成功 也不代表可以去改另一块适配器
            if (target == null) return true;
            uint original;
            if (!ReadMode(target.Id, out original)) return false;
            if (!IntelGraphicsApi.Continue(mayContinue)) return false;
            if (original != 0) { suppressed = true; return true; }

            receipt = new Receipt { AdapterId = target.Id, Phase = 'P' };
            if (!SavePhase('P')) { FinishReceipt(); return false; }
            if (!IntelGraphicsApi.Continue(mayContinue)) { FinishReceipt(); return false; }
            IntelGraphicsWriteResult result;
            try { result = control.TryWriteLowLatency(target.Id, 0, 1, mayContinue); }
            catch { result = IntelGraphicsWriteResult.Uncertain; }
            if (result == IntelGraphicsWriteResult.NotIssued || result == IntelGraphicsWriteResult.Cancelled
                || result == IntelGraphicsWriteResult.Conflict)
            {
                if (result == IntelGraphicsWriteResult.Conflict) suppressed = true;
                FinishReceipt();
                return false;
            }
            // 进了 setter 不等于拥有所有权 返回值不确定之后看到 On
            // 可能是别的调用方改的 保持 P 不要去还原那个值
            // 只有确认写成功 才给这个进程还原的所有权
            receipt.CanRestore = result == IntelGraphicsWriteResult.Written;
            uint observed;
            if (!ReadMode(target.Id, out observed)) return false;
            if (observed != 1) { failedApplication = true; FinishReceipt(); return false; }
            if (!receipt.CanRestore) return false;
            if (!IntelGraphicsApi.Continue(mayContinue)
                || !SavePhase('A') || !IntelGraphicsApi.Continue(mayContinue))
            {
                RestoreCore();
                return false;
            }
            receipt.Active = true;
            return true;
        }

        internal bool Restore()
        {
            lock (gate)
            {
                if (busy) return false;
                busy = true;
                try
                {
                    bool ok = RestoreCore();
                    if (ok) { suppressed = false; failedApplication = false; }
                    return ok;
                }
                catch { return false; }
                finally { busy = false; }
            }
        }

        private bool RestoreCore()
        {
            if (!LoadReceipt()) return false;
            if (receipt == null) return true;
            receipt.Active = false;
            if (receipt.Settled) return FinishReceipt();
            uint current;
            if (!ReadMode(receipt.AdapterId, out current)) return false;
            if (current != 1) return FinishReceipt();
            if (!receipt.CanRestore) return false;
            if (receipt.Phase != 'R' && !SavePhase('R')) return false;
            // 台账写入可能阻塞 发那唯一一次还原之前 重新读一遍驱动实际设置
            // 进了适配层里面再比一次
            if (!ReadMode(receipt.AdapterId, out current)) return false;
            if (current != 1) return FinishReceipt();
            IntelGraphicsWriteResult result;
            try { result = control.TryWriteLowLatency(receipt.AdapterId, 1, 0, null); }
            catch { result = IntelGraphicsWriteResult.Uncertain; }
            if (result == IntelGraphicsWriteResult.Conflict) return FinishReceipt();
            if (result == IntelGraphicsWriteResult.NotIssued || result == IntelGraphicsWriteResult.Cancelled)
                return false;
            // 只要还原有可能已经跑过 后面出现的 On 就可能是用户新选的
            // R + On 无论是崩溃后还是回读失败后 都不能安全重试
            receipt.CanRestore = false;
            if (!ReadMode(receipt.AdapterId, out current)) return false;
            return current != 1 && FinishReceipt();
        }

        // 仅供整体重置 发过写入后无法证明驱动当前 On 归属的收据(CanRestore=false)
        //   RestoreCore 对它永远失败 唯一自愈路径是外部把值改掉 清除全部配置会被无限期拦住
        //   用户已明确要求清空全部数据时 放弃这类恢复责任并留日志
        //   残值只是全局低延迟停在当前值 在 Intel 显卡控制中心关闭一次即结清
        //   可认领的收据(CanRestore=true)与瞬时失败不放弃
        internal bool AbandonUnprovableForReset()
        {
            lock (gate)
            {
                if (busy) return false;
                busy = true;
                try
                {
                    if (!LoadReceipt()) return false;
                    if (receipt == null) return true;
                    if (receipt.Settled) return FinishReceipt();
                    if (receipt.CanRestore) return false;
                    Logger.Warn(Lang.T("log.intelabandon.1"));
                    return FinishReceipt();
                }
                catch { return false; }
                finally { busy = false; }
            }
        }

        private bool FinishReceipt()
        {
            if (receipt == null) return true;
            receipt.Active = false;
            receipt.Settled = true;
            receipt.CanRestore = false;
            // 清理失败时 S 会留下来 应用重启后只是重试清理
            // 此时用户新设的 On 不能变成又一个还原目标
            if (receipt.Phase != 'S' && !SavePhase('S')) return false;
            if (!WriteVerified("")) return false;
            receipt = null;
            return true;
        }

        private bool SavePhase(char phase)
        {
            if (receipt == null || !ValidAdapterId(receipt.AdapterId)) return false;
            if (!WriteVerified("1|" + phase + "|" + receipt.AdapterId)) return false;
            receipt.Phase = phase;
            return true;
        }

        private bool LoadReceipt()
        {
            if (receipt != null) return true;
            string value;
            if (!ReadLedger(out value)) return false;
            if (value.Length == 0) return true;
            if (value.Length > 256) return false;
            string[] parts = value.Split('|');
            if (parts.Length != 3 || parts[0] != "1" || parts[1].Length != 1
                || "PARS".IndexOf(parts[1][0]) < 0 || !ValidAdapterId(parts[2])) return false;
            char phase = parts[1][0];
            receipt = new Receipt
            {
                AdapterId = parts[2], Phase = phase,
                CanRestore = phase == 'A', Settled = phase == 'S'
            };
            return true;
        }

        private bool WriteVerified(string value)
        {
            ledgerKnownEmpty = false;
            try
            {
                string readback;
                bool ok = ledger.TryWrite(value) && ledger.TryRead(out readback) && readback == value;
                if (ok) ledgerKnownEmpty = value.Length == 0;
                return ok;
            }
            catch { return false; }
        }

        private bool ReadLedger(out string value)
        {
            value = null;
            try { return ledger.TryRead(out value) && value != null; }
            catch { return false; }
        }

        private bool ReadAdapters(out IntelGraphicsAdapter[] adapters)
        {
            adapters = null;
            try { return control.TryGetAdapters(out adapters) && adapters != null && adapters.Length <= 32; }
            catch { return false; }
        }

        private bool ReadMode(string id, out uint value)
        {
            value = 0;
            try { return control.TryReadLowLatency(id, out value) && value <= 2; }
            catch { return false; }
        }

        internal static IntelGraphicsAdapter SelectAdapter(IntelGraphicsAdapter[] adapters)
        {
            if (adapters == null || adapters.Length > 32) return null;
            IntelGraphicsAdapter discrete = null, integrated = null;
            int intelDiscrete = 0, intelIntegrated = 0;
            bool otherDiscrete = false;
            foreach (IntelGraphicsAdapter adapter in adapters)
            {
                if (adapter == null) return null;
                if (adapter.VendorId != 0x8086) { if (!adapter.Integrated) otherDiscrete = true; continue; }
                if (!ValidAdapterId(adapter.Id)) return null;
                if (adapter.Integrated) { integrated = adapter; intelIntegrated++; }
                else { discrete = adapter; intelDiscrete++; }
            }
            IntelGraphicsAdapter selected = intelDiscrete == 1 ? discrete
                : intelDiscrete == 0 && !otherDiscrete && intelIntegrated == 1 ? integrated : null;
            return selected != null && selected.LowLatencySupported ? selected : null;
        }

        internal static bool ValidAdapterId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 45) return false;
            string[] parts = value.Split(':');
            int[] lengths = { 4, 4, 4, 4, 2, 2, 2, 16 };
            if (parts.Length != lengths.Length || parts[0] != "8086") return false;
            ulong[] fields = new ulong[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length != lengths[i]) return false;
                foreach (char c in parts[i]) if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F')) return false;
                if (!ulong.TryParse(parts[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                    out fields[i])) return false;
            }
            return fields[1] != 0 && fields[5] <= 31 && fields[6] <= 7 && fields[7] != 0;
        }
    }

    internal static class IntelGraphicsTweaks
    {
        private static IntelLowLatencyEngine engine =
            new IntelLowLatencyEngine(new IntelGraphicsApi(), new IntelGraphicsSettingsLedger());
        public static bool HasAvailable { get { return engine.HasAvailable; } }
        public static bool LowLatencySupported { get { return engine.LowLatencySupported; } }
        public static bool Active { get { return engine.Active; } }
        public static bool HasResidue { get { return engine.HasResidue; } }
        public static bool RestoreBlockedByOwnership { get { return engine.RestoreBlockedByOwnership; } }
        public static bool TryApply(Func<bool> mayContinue) { return engine.Apply(mayContinue); }
        public static bool Restore() { return engine.Restore(); }
        public static bool AbandonUnprovableForReset() { return engine.AbandonUnprovableForReset(); }
        public static bool HealFromCrash() { return engine.Restore(); }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static IntelLowLatencyEngine ReplaceEngineForTest(IntelLowLatencyEngine replacement)
        {
            if (replacement == null) throw new ArgumentNullException("replacement");
            IntelLowLatencyEngine previous = engine;
            engine = replacement;
            return previous;
        }
#endif
    }
}
