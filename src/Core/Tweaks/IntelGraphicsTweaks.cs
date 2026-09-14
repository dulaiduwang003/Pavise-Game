// File purpose Temporarily enable Intel global low latency; record the ownership trail before every driver change
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
        // This engine is the ledger's sole writer; once confirmed empty there's no need to re-read the registry every round
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

        // True when restore is blocked by 'can't prove who owns the current value'; for display only, changes no state
        internal bool RestoreBlockedByOwnership
        {
            get { lock (gate) return receipt != null && !receipt.Settled && !receipt.CanRestore; }
        }

        // Support queries come from the UI thread; Apply/Restore hold the lock while making driver calls that can take seconds
        //   When a query can't get the lock, return the last result; don't freeze the UI on a driver call; query for real when idle
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
                // External changes win for the whole match; after the user turns it off in the driver UI
                // don't keep switching the option back on
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
            // Unsupported hardware or ambiguous hybrid graphics counts as skipped; never fake it as
            // an Active=true success, nor does it mean we may go change another adapter
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
            // Entering the setter doesn't mean ownership; with an uncertain return value, a later On
            // may have been set by another caller; stay at P and don't restore that value
            // Only a confirmed successful write gives this process ownership of the restore
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
            // The ledger write may block; before issuing the single restore, re-read the driver's actual setting
            // and compare once more inside the adapter layer
            if (!ReadMode(receipt.AdapterId, out current)) return false;
            if (current != 1) return FinishReceipt();
            IntelGraphicsWriteResult result;
            try { result = control.TryWriteLowLatency(receipt.AdapterId, 1, 0, null); }
            catch { result = IntelGraphicsWriteResult.Uncertain; }
            if (result == IntelGraphicsWriteResult.Conflict) return FinishReceipt();
            if (result == IntelGraphicsWriteResult.NotIssued || result == IntelGraphicsWriteResult.Cancelled)
                return false;
            // Once the restore may already have run, any On seen afterwards may be the user's new choice
            // R + On, whether after a crash or a read-back failure, can't be retried safely
            receipt.CanRestore = false;
            if (!ReadMode(receipt.AdapterId, out current)) return false;
            return current != 1 && FinishReceipt();
        }

        // For full reset only: receipts where a write was issued but the driver's current On can't be proven ours (CanRestore=false)
        //   RestoreCore fails on them forever; the only self-heal is an external change of the value, so wipe all settings would be blocked indefinitely
        //   Once the user has explicitly asked to wipe all data, give up that restore duty and log it
        //   The residue is just global low latency staying at its current value; turning it off once in Intel Graphics Command Center settles it
        //   Claimable receipts (CanRestore=true) and transient failures are not abandoned
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
            // If cleanup fails, S stays behind; after an app restart it just retries the cleanup
            // An On the user sets in the meantime must not become yet another restore target
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
