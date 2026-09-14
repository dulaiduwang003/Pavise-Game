// @author bdth 2074055628@qq.com
// File purpose Per-match service pause, keeping four receipt phases: Prepared, Owned, Restoring, Settled
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class ServicePauser
    {
        private enum Phase { Prepared, Owned, Restoring, Settled }

        private sealed class Receipt
        {
            internal string Name;
            internal Phase State;
            internal bool CanStart, StopObserved;
            // In-memory only, count of consecutive Running reads during restore checks, see the settle rule in RestoreCore
            internal int RunningSeen;
        }

        private readonly string[] names;
        private readonly HashSet<string> allowed;
        private readonly string flag;
        private readonly object lk = new object();
        private readonly Dictionary<string, Receipt> receipts =
            new Dictionary<string, Receipt>(StringComparer.OrdinalIgnoreCase);
        private bool active, loaded, busy, observationDirty;
        private string lastRead = "";

        // Every group must actually observe the Running state before stopping
        // Query failures and services already stopped are not ours
        public ServicePauser(string[] serviceNames, string flagKey)
        {
            if (serviceNames == null) throw new ArgumentNullException("serviceNames");
            names = (string[])serviceNames.Clone();
            allowed = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            flag = flagKey;
        }

        public bool HasResidue
        {
            get
            {
                lock (lk)
                {
                    if (receipts.Count != 0) return true;
                    string raw;
                    return !Read(out raw) || raw == null || raw.Length != 0;
                }
            }
        }

        public bool HadLedger { get { return HasResidue; } }

        public bool Activate(out List<string> justStopped, out List<string> confirmedStopped, out bool ledgerLost)
        {
            justStopped = new List<string>();
            confirmedStopped = new List<string>();
            ledgerLost = false;
            lock (lk)
            {
                if (busy) return false;
                busy = true;
                try
                {
                    if (active) return ObserveStops();
                    if (!Load()) return false;
                    List<string> remain;
                    if (receipts.Count != 0 && !RestoreCore(out remain)) return false;
                    foreach (string name in names)
                    {
                        if (Query(name) != 4) continue;
                        string previous = lastRead;
                        var receipt = new Receipt { Name = name, State = Phase.Prepared };
                        receipts.Add(name, receipt);
                        if (!Save())
                        {
                            // If a rejected write truly left the original bytes untouched
                            // then there is no receipt and nothing owed to the system
                            string actual;
                            if (Read(out actual) && actual == previous) receipts.Remove(name);
                            else receipt.State = Phase.Settled;
                            ledgerLost = true;
                            RestoreCore(out remain);
                            return false;
                        }

                        // Persistence can be slow; a service that changed while the intent was being recorded
                        // must no longer be stopped now
                        if (Query(name) != 4)
                        {
                            Settle(receipt);
                            if (!FlushSettled())
                            {
                                ledgerLost = true;
                                RestoreCore(out remain);
                                return false;
                            }
                            continue;
                        }

                        bool confirmed;
                        bool issued = Stop(name, out confirmed);
                        if (issued)
                        {
                            receipt.State = Phase.Owned;
                            receipt.CanStart = true;
                            receipt.StopObserved = confirmed || Query(name) == 1;
                            justStopped.Add(name);
                            if (receipt.StopObserved) confirmedStopped.Add(name);
                            if (!Save())
                            {
                                ledgerLost = true;
                                RestoreCore(out remain);
                                return false;
                            }
                        }
                        else
                        {
                            // Observing it stopped does not prove our failed STOP request
                            // caused it; stay Prepared and unowned
                            if (Query(name) == 4) receipt.State = Phase.Settled;
                            if (!FlushSettled())
                            {
                                ledgerLost = true;
                                RestoreCore(out remain);
                                return false;
                            }
                        }
                    }
                    active = true;
                    return true;
                }
                catch { return false; }
                finally { busy = false; }
            }
        }

        public bool Restore(out List<string> remain)
        {
            lock (lk)
            {
                remain = PendingNames();
                if (busy) return false;
                busy = true;
                try { return RestoreCore(out remain); }
                catch { remain = PendingNames(); return false; }
                finally { busy = false; }
            }
        }

        private bool RestoreCore(out List<string> remain)
        {
            active = false;
            remain = PendingNames();
            if (!Load()) return false;
            bool ok = true;
            foreach (string name in names)
            {
                Receipt receipt;
                if (!receipts.TryGetValue(name, out receipt) || receipt.State == Phase.Settled) continue;
                int state = Query(name);
                if (state == 0) { ok = false; continue; }
                if (state != 4) receipt.RunningSeen = 0;
                if (state == 4)
                {
                    // For an accepted STOP whose stop was never observed, a single Running read may precede
                    //   the STOP_PENDING transition, so keep the debt first; two independent restore checks both reading
                    //   Running means STOP did not take effect or the service was restarted, and the expected end state, running,
                    //   already holds, nothing to restore, settle; otherwise a service that triggers a restart would
                    //   leave this group's pause feature stuck forever
                    if (receipt.State == Phase.Owned && !receipt.StopObserved
                        && ++receipt.RunningSeen < 2) { ok = false; continue; }
                    Settle(receipt);
                    continue;
                }
                if (state != 1)
                {
                    if ((receipt.State == Phase.Owned && !receipt.StopObserved && state == 3)
                        || (receipt.State == Phase.Prepared && (state == 2 || state == 3))
                        || (receipt.State == Phase.Restoring && (state == 2 || state == 3)))
                    { ok = false; continue; }
                    Settle(receipt);
                    continue;
                }
                if (!receipt.CanStart) { ok = false; continue; }

                receipt.StopObserved = true;
                receipt.State = Phase.Restoring;
                // CanStart stays true only when it is certain START was never issued
                // A fresh process loading R cannot infer this in-memory credential
                if (!Save()) { ok = false; continue; }
                int fresh = Query(name);
                if (fresh == 0) { ok = false; continue; }
                if (fresh != 1) { Settle(receipt); continue; }
                receipt.CanStart = false;
                Start(name);
                int after = Query(name);
                if (after == 4 || after == 7) Settle(receipt);
                else ok = false;
            }
            if (!FlushSettled()) ok = false;
            remain = PendingNames();
            return ok && receipts.Count == 0;
        }

        private bool ObserveStops()
        {
            foreach (Receipt receipt in receipts.Values)
                if (receipt.State == Phase.Owned && !receipt.StopObserved && Query(receipt.Name) == 1)
                { receipt.StopObserved = true; observationDirty = true; }
            return !observationDirty || Save();
        }

        private static void Settle(Receipt receipt)
        {
            receipt.State = Phase.Settled;
            receipt.CanStart = false;
        }

        private List<string> PendingNames()
        {
            var result = new List<string>();
            foreach (string name in names) if (receipts.ContainsKey(name)) result.Add(name);
            return result;
        }

        private bool Load()
        {
            string raw;
            if (!Read(out raw) || raw == null) return false;
            Dictionary<string, Receipt> disk;
            if (!Parse(raw, out disk)) return false;
            if (!loaded)
            {
                foreach (var item in disk) receipts.Add(item.Key, item.Value);
                loaded = true;
            }
            else
            {
                foreach (var item in disk)
                {
                    Receipt current;
                    if (!receipts.TryGetValue(item.Key, out current)) receipts.Add(item.Key, item.Value);
                    else if (item.Value.State == Phase.Settled) Settle(current);
                    // When a confirmed write or a later ledger update fails to persist
                    // the definitive in-process receipts must be kept
                }
            }
            lastRead = raw;
            return true;
        }

        private bool Parse(string raw, out Dictionary<string, Receipt> result)
        {
            result = new Dictionary<string, Receipt>(StringComparer.OrdinalIgnoreCase);
            if (raw.Length == 0) return true;
            if (raw.Length > 4096) return false;
            if (!raw.StartsWith("2\n", StringComparison.Ordinal))
            {
                string[] oldNames = raw == "1" && names.Length == 1 ? new[] { names[0] } : raw.Split('|');
                foreach (string name in oldNames)
                {
                    if (!allowed.Contains(name) || result.ContainsKey(name)) return false;
                    // The old format was written by older versions, whose restore contract was to unconditionally restart every service in the ledger
                    //   Inherit that contract, stopped services may be started; otherwise services the old version really stopped
                    //   would never be restarted after upgrade; still handled as Prepared, settled once Running is observed
                    result.Add(name, new Receipt { Name = name, State = Phase.Prepared, CanStart = true });
                }
                return result.Count > 0;
            }

            string[] lines = raw.Split('\n');
            if (lines.Length < 2 || lines.Length - 1 > allowed.Count) return false;
            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length != 3 || !allowed.Contains(parts[1]) || result.ContainsKey(parts[1])
                    || (parts[2] != "0" && parts[2] != "1")) return false;
                Phase phase;
                if (parts[0] == "P") phase = Phase.Prepared;
                else if (parts[0] == "O") phase = Phase.Owned;
                else if (parts[0] == "R") phase = Phase.Restoring;
                else if (parts[0] == "S") phase = Phase.Settled;
                else return false;
                if (phase == Phase.Prepared && parts[2] != "0") return false;
                result.Add(parts[1], new Receipt
                {
                    Name = parts[1], State = phase, CanStart = phase == Phase.Owned,
                    StopObserved = parts[2] == "1"
                });
            }
            return true;
        }

        private string Encode()
        {
            if (receipts.Count == 0) return "";
            var lines = new List<string> { "2" };
            foreach (string name in names)
            {
                Receipt receipt;
                if (!receipts.TryGetValue(name, out receipt)) continue;
                string phase = receipt.State == Phase.Prepared ? "P" : receipt.State == Phase.Owned ? "O"
                    : receipt.State == Phase.Restoring ? "R" : "S";
                lines.Add(phase + "\t" + name + "\t" + (receipt.StopObserved ? "1" : "0"));
            }
            return string.Join("\n", lines.ToArray());
        }

        private bool Save()
        {
            string expected = Encode(), actual;
            if (!Write(expected) || !Read(out actual) || actual != expected) return false;
            lastRead = actual;
            observationDirty = false;
            return true;
        }

        private bool FlushSettled()
        {
            if (receipts.Count == 0) return true;
            if (!Save()) return false;
            var settled = new List<Receipt>();
            foreach (Receipt receipt in receipts.Values)
                if (receipt.State == Phase.Settled) settled.Add(receipt);
            if (settled.Count == 0) return true;
            foreach (Receipt receipt in settled) receipts.Remove(receipt.Name);
            if (Save()) return true;
            foreach (Receipt receipt in settled) receipts.Add(receipt.Name, receipt);
            return false;
        }

        private int Query(string name)
        {
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return QueryForTest == null ? 0 : QueryForTest(name);
#else
                return SvcState.Query(name);
#endif
            }
            catch { return 0; }
        }

        private bool Stop(string name, out bool confirmed)
        {
            confirmed = false;
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return StopForTest != null && StopForTest(name, out confirmed);
#else
                return SvcCtl.StopIfRunning(name, out confirmed);
#endif
            }
            catch { return false; }
        }

        private void Start(string name)
        {
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (StartForTest != null) StartForTest(name);
#else
                SvcCtl.EnsureStarted(name);
#endif
            }
            catch { }
        }

        private bool Read(out string value)
        {
            value = null;
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (ReadForTest != null) return ReadForTest(out value);
#endif
                return Settings.TryLoadStr(flag, out value);
            }
            catch { return false; }
        }

        private bool Write(string value)
        {
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (WriteForTest != null) return WriteForTest(value);
#endif
                return Settings.SaveStr(flag, value);
            }
            catch { return false; }
        }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal delegate bool StopDelegate(string name, out bool confirmed);
        internal delegate bool ReadDelegate(out string value);
        internal Func<string, int> QueryForTest;
        internal StopDelegate StopForTest;
        internal Func<string, bool> StartForTest;
        internal ReadDelegate ReadForTest;
        internal Func<string, bool> WriteForTest;

        internal void ResetForTest()
        {
            lock (lk)
            {
                receipts.Clear(); active = false; busy = false; loaded = false; observationDirty = false; lastRead = "";
                QueryForTest = null; StopForTest = null; StartForTest = null;
                ReadForTest = null; WriteForTest = null;
            }
        }
#endif
    }
}
