// @author bdth 2074055628@qq.com
// File purpose Temporarily pause optional services during the match, restoring only services whose stop request this run issued successfully
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class OptionalServiceSnapshot
    {
        public bool Exists, AcceptsStop, HasActiveDependents;
        public int State, StartType;
        public string Configuration;
    }

    internal enum OptionalServiceStartResult
    {
        NotIssued,
        Accepted,
        OwnershipChanged
    }

    internal interface IOptionalServiceControl
    {
        bool TryGetBootIdentity(out string identity);
        bool TryQuery(string name, out OptionalServiceSnapshot snapshot);
        bool IsPrintingIdle();
        bool TryStop(string name, OptionalServiceSnapshot expected, Func<bool> mayContinue);
        OptionalServiceStartResult TryStart(string name, string expectedConfiguration);
    }

    internal interface IOptionalServiceLedger
    {
        bool TryRead(out string value);
        bool TryWrite(string value);
    }

    internal sealed class OptionalServicePauseEngine
    {
        // Stop dependents before the host, restore in reverse order
        // Kept separate from the retired SysMain/WSearch policy and its ledger, do not mix them
        internal static readonly string[] Names =
            { "PrintNotify", "Spooler", "WSearch", "WMPNetworkSvc", "MapsBroker", "DiagTrack", "RetailDemo" };

        private sealed class Entry
        {
            internal string Name, Configuration;
            internal bool Owned, StopObserved, Restoring;
        }

        private readonly IOptionalServiceControl control;
        private readonly IOptionalServiceLedger ledger;
        private readonly object gate = new object();
        // Once STOP is issued successfully the service is ours, even if the receipt never got written
        // This in-process credential cannot be inferred back from the Prepared record on disk
        private readonly Dictionary<string, Entry> receipts =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        // After a successful restore or an external config change, never replay the stale receipt
        // Not even when the ledger failed to clear
        private readonly HashSet<string> settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> startNotIssued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool active;
        private bool stopObservationDirty;
        private string receiptBoot;
        private string lastWarning;

        internal OptionalServicePauseEngine(IOptionalServiceControl control, IOptionalServiceLedger ledger)
        {
            if (control == null || ledger == null) throw new ArgumentNullException();
            this.control = control;
            this.ledger = ledger;
        }

        internal static bool IsAllowed(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // Authorization must not come from that mutable array nor from service names persisted on disk
            switch (name.ToLowerInvariant())
            {
                case "printnotify": case "spooler": case "wsearch":
                case "wmpnetworksvc": case "mapsbroker": case "diagtrack": case "retaildemo":
                    return true;
                default: return false;
            }
        }

        public bool Active { get { lock (gate) return active; } }

        public bool HasResidue
        {
            get
            {
                lock (gate)
                {
                    string raw;
                    return receipts.Count != 0 || settled.Count != 0 || !ReadRaw(out raw) || raw.Length != 0;
                }
            }
        }

        public bool Activate() { return Activate(null); }

        public bool Activate(Func<bool> mayContinue)
        {
            lock (gate)
            {
                if (active) return true;
                // Settle the previous match first, then claim new services
                if (!RestoreCore()) return false;
                if (!MayContinue(mayContinue)) return true;
                string boot;
                if (!ReadBoot(out boot)) return false;
                receiptBoot = boot;
                var entries = new List<Entry>();
                // Stopped services are logged as one combined line; on abort, log the already-stopped ones first, then restore
                var stoppedNames = new List<string>();
                Action flush = delegate
                {
                    if (stoppedNames.Count == 0) return;
                    Logger.Log(Lang.F("log.pausesvc.stop", string.Join(", ", stoppedNames.ToArray())));
                    stoppedNames.Clear();
                };
                foreach (string name in Names)
                {
                    if (!MayContinue(mayContinue)) { flush(); return RestoreCore(); }
                    OptionalServiceSnapshot before;
                    if (!Query(name, out before) || !before.Exists || before.State != 4
                        || before.StartType == 4 || !before.AcceptsStop || before.HasActiveDependents
                        || string.IsNullOrEmpty(before.Configuration) || before.Configuration.Length > 8192) continue;
                    if (IsPrinting(name) && !PrintingIdle()) continue;
                    // Querying the local print provider can block; once the user has disabled the policy or exited,
                    // late results are ignored entirely
                    if (!MayContinue(mayContinue)) { flush(); return RestoreCore(); }

                    var entry = new Entry { Name = name, Configuration = before.Configuration };
                    entries.Add(entry);
                    if (!Save(entries, boot)) { flush(); return AbortAfterLedgerFailure(); }
                    if (!MayContinue(mayContinue))
                    {
                        entries.Remove(entry);
                        flush();
                        if (!Save(entries, boot)) return AbortAfterLedgerFailure();
                        return RestoreCore();
                    }

                    bool accepted;
                    try { accepted = control.TryStop(name, before, mayContinue); }
                    catch
                    {
                        // When the adapter throws, STOP may already have been issued, so its Prepared
                        // record is deliberately not promoted until a receipt is obtained
                        flush();
                        Warn("log.pausesvc.unowned", name);
                        RestoreCore();
                        return false;
                    }
                    if (!accepted)
                    {
                        // Someone else stopped it first, that does not count as our ownership
                        entries.Remove(entry);
                        if (!Save(entries, boot)) { flush(); return AbortAfterLedgerFailure(); }
                        continue;
                    }

                    entry.Owned = true;
                    receipts[name] = entry;
                    OptionalServiceSnapshot stopped;
                    entry.StopObserved = Query(name, out stopped) && stopped.Exists && stopped.State == 1;
                    stoppedNames.Add(name);
                    if (!Save(entries, boot)) { flush(); return AbortAfterLedgerFailure(); }
                }
                flush();
                if (!MayContinue(mayContinue)) return RestoreCore();
                active = true;
                lastWarning = null;
                return true;
            }
        }

        public bool Restore() { lock (gate) return RestoreCore(); }

        // Only confirm stops that were accepted, never force repeatedly; once STOPPED is observed,
        // any later return to RUNNING is someone else's doing
        public bool ObserveStops()
        {
            lock (gate)
            {
                if (!active) return true;
                foreach (Entry receipt in receipts.Values)
                {
                    if (receipt.StopObserved || receipt.Restoring || settled.Contains(receipt.Name)) continue;
                    OptionalServiceSnapshot state;
                    if (Query(receipt.Name, out state) && state.Exists && state.State == 1)
                    {
                        receipt.StopObserved = true;
                        stopObservationDirty = true;
                    }
                }
                if (!stopObservationDirty) return true;
                string raw, boot;
                List<Entry> entries;
                if (!ReadRaw(out raw) || !Parse(raw, out boot, out entries) || boot != receiptBoot)
                {
                    Warn("log.pausesvc.ledger", null);
                    return false;
                }
                foreach (Entry entry in entries)
                {
                    Entry receipt;
                    if (receipts.TryGetValue(entry.Name, out receipt)
                        && entry.Configuration == receipt.Configuration)
                        entry.StopObserved |= receipt.StopObserved;
                }
                bool saved = Save(entries, boot);
                if (saved) stopObservationDirty = false;
                if (!saved) Warn("log.pausesvc.ledger", null);
                return saved;
            }
        }

        private bool AbortAfterLedgerFailure()
        {
            Warn("log.pausesvc.ledger", null);
            // The in-process receipt can roll back the case where the stop succeeded but the receipt never landed on disk
            // A freshly started process has no such credential
            RestoreCore();
            return false;
        }

        private bool RestoreCore()
        {
            active = false;
            string raw, recordedBoot;
            List<Entry> entries;
            if (!ReadRaw(out raw) || !Parse(raw, out recordedBoot, out entries))
            {
                Warn("log.pausesvc.ledger", null);
                return false;
            }
            if (entries.Count == 0 && receipts.Count == 0)
            {
                lastWarning = null;
                bool cleared = raw.Length == 0 || Save(entries, recordedBoot);
                if (cleared) { settled.Clear(); startNotIssued.Clear(); stopObservationDirty = false; }
                return cleared;
            }
            string boot;
            if (!ReadBoot(out boot)) return false;
            // On a fresh boot Windows has already run each service per the user's configured start type
            // Do not restart services that were manually running during the previous boot
            if (recordedBoot != null && recordedBoot != boot)
            {
                if (!Save(new List<Entry>(), boot)) return false;
                entries.Clear();
                settled.Clear();
                startNotIssued.Clear();
                stopObservationDirty = false;
                Logger.Log(Lang.T("log.pausesvc.boot"));
            }
            if (receiptBoot != boot) { receipts.Clear(); startNotIssued.Clear(); }
            receiptBoot = boot;
            foreach (Entry receipt in receipts.Values)
            {
                Entry found = entries.Find(delegate(Entry e)
                    { return string.Equals(e.Name, receipt.Name, StringComparison.OrdinalIgnoreCase); });
                if (found == null) entries.Add(receipt);
                else if (string.Equals(found.Configuration, receipt.Configuration, StringComparison.Ordinal))
                {
                    found.Owned = true;
                    found.StopObserved |= receipt.StopObserved;
                    if (startNotIssued.Contains(receipt.Name)) found.Restoring = false;
                    else found.Restoring |= receipt.Restoring;
                }
                else
                {
                    // Reject outright if the receipt was replaced or tampered with, never overwrite it
                    Warn("log.pausesvc.ledger", null);
                    return false;
                }
            }

            // Started services may linger in START_PENDING, stopped ones in STOP_PENDING
            // Keep the record and leave it to the normal retry poll
            // Do not block the UI, do not kill the host process
            var pending = new List<string>();
            var uncertain = new List<string>();
            var restored = new List<string>();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry entry = entries[i];
                if (settled.Contains(entry.Name)) { entries.RemoveAt(i); continue; }
                OptionalServiceSnapshot current;
                if (!Query(entry.Name, out current)) { pending.Add(entry.Name); continue; }
                bool remove = false;
                if (!current.Exists || current.StartType == 4
                    || !string.Equals(current.Configuration, entry.Configuration, StringComparison.Ordinal))
                {
                    // The user or an installer changed the config after we stopped it
                    // Give up ownership permanently, do not override that choice
                    Logger.Log(Lang.F("log.pausesvc.changed", entry.Name));
                    remove = true;
                }
                else if (entry.Owned && !entry.Restoring
                    && ((entry.StopObserved && current.State >= 2 && current.State <= 7)
                        || current.State == 2 || current.State == 5 || current.State == 6 || current.State == 7))
                    remove = true; // A known completed STOP cannot cause a later state transition
                else if (entry.Owned && !entry.StopObserved && !entry.Restoring && current.State != 1)
                    pending.Add(entry.Name); // Accepted STOP may still report RUNNING briefly
                else if (current.State == 4 || current.State == 7)
                    remove = true; // Already running or externally paused no START/CONTINUE
                else if (!entry.Owned || (entry.Restoring && current.State == 1))
                    uncertain.Add(entry.Name);
                else if (current.State == 1)
                {
                    entry.StopObserved = true;
                    entry.Restoring = true;
                    receipts[entry.Name] = entry;
                    startNotIssued.Remove(entry.Name);
                    // Persist the restore intent before START; after a crash
                    // the R record will not replay START on a service that was stopped later
                    // because the earlier restore may already have succeeded
                    if (!Save(entries, boot))
                    {
                        entry.Restoring = false;
                        startNotIssued.Add(entry.Name); // No START was called
                        Warn("log.pausesvc.ledger", null);
                        return false;
                    }
                    OptionalServiceStartResult startResult;
                    try { startResult = control.TryStart(entry.Name, entry.Configuration); }
                    catch
                    {
                        uncertain.Add(entry.Name);
                        continue;
                    }
                    if (startResult == OptionalServiceStartResult.OwnershipChanged)
                        remove = true;
                    else if (startResult == OptionalServiceStartResult.NotIssued)
                    {
                        entry.Restoring = false;
                        // Adapter neither issued START nor observed a user or service action that would make us give up ownership
                        startNotIssued.Add(entry.Name);
                    }
                    if (!remove)
                    {
                        OptionalServiceSnapshot after;
                        bool queried = Query(entry.Name, out after);
                        // After START is accepted the service sits in START_PENDING for a few hundred ms, wait briefly before checking
                        //   avoids leaving a pending retry every match and then needing a leftover-retry pass to catch up
                        if (queried && startResult == OptionalServiceStartResult.Accepted && after.State == 2)
                            queried = WaitStartSettled(entry.Name, out after);
                        if (queried)
                        {
                            // External decisions are preserved even if the service changes again before the next retry
                            // The last query must not throw away the evidence that ownership was already
                            // handed over
                            bool changed = !after.Exists || after.StartType == 4
                                || !string.Equals(after.Configuration, entry.Configuration, StringComparison.Ordinal);
                            bool externalTransition = startResult == OptionalServiceStartResult.NotIssued
                                && after.State >= 2 && after.State <= 7;
                            if (changed || externalTransition || after.State == 4 || after.State == 7)
                            {
                                if (changed) Logger.Log(Lang.F("log.pausesvc.changed", entry.Name));
                                else if (after.State == 4 && startResult == OptionalServiceStartResult.Accepted)
                                    restored.Add(entry.Name);
                                remove = true;
                            }
                        }
                        if (!remove) pending.Add(entry.Name);
                    }
                }
                else pending.Add(entry.Name);

                if (remove)
                {
                    settled.Add(entry.Name);
                    startNotIssued.Remove(entry.Name);
                    receipts.Remove(entry.Name);
                    entries.RemoveAt(i);
                }
            }
            if (restored.Count != 0)
                Logger.Log(Lang.F("log.pausesvc.restore", string.Join(", ", restored.ToArray())));
            bool saved = Save(entries, boot);
            if (saved) { settled.Clear(); stopObservationDirty = false; }
            if (!saved) Warn("log.pausesvc.ledger", null);
            else if (uncertain.Count != 0) Warn("log.pausesvc.unowned", string.Join(", ", uncertain.ToArray()));
            else if (pending.Count != 0) Warn("log.pausesvc.pending", string.Join(", ", pending.ToArray()));
            else lastWarning = null;
            return saved && entries.Count == 0;
        }

        // Wait at most 1.5s, polling every 100ms; return as soon as it leaves START_PENDING; on query failure or timeout use the last result
        private const int StartSettleWaitMs = 1500;
        private const int StartSettleStepMs = 100;

        private bool WaitStartSettled(string name, out OptionalServiceSnapshot snapshot)
        {
            snapshot = null;
            bool queried = false;
            for (int waited = 0; waited < StartSettleWaitMs; waited += StartSettleStepMs)
            {
                Thread.Sleep(StartSettleStepMs);
                queried = Query(name, out snapshot);
                if (!queried || snapshot.State != 2) return queried;
            }
            return queried;
        }

        private bool Query(string name, out OptionalServiceSnapshot snapshot)
        {
            snapshot = null;
            try { return IsAllowed(name) && control.TryQuery(name, out snapshot) && snapshot != null; }
            catch { return false; }
        }

        private bool PrintingIdle()
        {
            try { return control.IsPrintingIdle(); } catch { return false; }
        }

        private static bool IsPrinting(string name)
        {
            return string.Equals(name, "PrintNotify", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Spooler", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MayContinue(Func<bool> mayContinue)
        {
            try { return mayContinue == null || mayContinue(); } catch { return false; }
        }

        private bool ReadBoot(out string boot)
        {
            boot = null;
            try
            {
                string value;
                Guid id;
                if (!control.TryGetBootIdentity(out value) || !Guid.TryParse(value, out id) || id == Guid.Empty)
                    return false;
                boot = id.ToString("D");
                return true;
            }
            catch { return false; }
        }

        private bool ReadRaw(out string raw)
        {
            raw = null;
            try { return ledger.TryRead(out raw) && raw != null; } catch { return false; }
        }

        private bool Save(List<Entry> entries, string boot)
        {
            string raw = "";
            if (entries.Count != 0)
            {
                var text = new StringBuilder("v1\t" + boot);
                foreach (Entry entry in entries)
                    text.Append('\n').Append(!entry.Owned ? 'P' : entry.Restoring ? 'R' : entry.StopObserved ? 'O' : 'A')
                        .Append('\t').Append(entry.Name)
                        .Append('\t').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Configuration)));
                raw = text.ToString();
            }
            try
            {
                string readBack;
                return ledger.TryWrite(raw) && ReadRaw(out readBack)
                    && string.Equals(raw, readBack, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool Parse(string raw, out string boot, out List<Entry> entries)
        {
            boot = null;
            entries = new List<Entry>();
            if (raw.Length == 0) return true;
            if (raw.Length > 262144) return false;
            try
            {
                string[] lines = raw.Split('\n');
                string[] header = lines[0].Split('\t');
                Guid id;
                if (header.Length != 2 || header[0] != "v1" || !Guid.TryParse(header[1], out id)
                    || id == Guid.Empty || lines.Length > 8) return false;
                boot = id.ToString("D");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length != 3 || (parts[0] != "O" && parts[0] != "P" && parts[0] != "A" && parts[0] != "R")
                        || !IsAllowed(parts[1]) || !seen.Add(parts[1])) return false;
                    string config = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(parts[2]));
                    if (config.Length == 0 || config.Length > 8192) return false;
                    entries.Add(new Entry { Name = parts[1], Configuration = config,
                        Owned = parts[0] != "P", StopObserved = parts[0] == "O" || parts[0] == "R",
                        Restoring = parts[0] == "R" });
                }
                return true;
            }
            catch { return false; }
        }

        private void Warn(string key, string names)
        {
            string message = names == null ? Lang.T(key) : Lang.F(key, names);
            if (message == lastWarning) return;
            lastWarning = message;
            Logger.Log(message);
        }
    }

    internal static class OptionalServicePause
    {
        internal const string LedgerKey = "PrevOptionalServicesPausedV1";

        private sealed class SettingsLedger : IOptionalServiceLedger
        {
            public bool TryRead(out string value) { return Settings.TryLoadStr(LedgerKey, out value); }
            public bool TryWrite(string value) { return Settings.SaveStr(LedgerKey, value); }
        }

        private static readonly OptionalServicePauseEngine engine =
            new OptionalServicePauseEngine(new OptionalServiceControl(), new SettingsLedger());

        public static bool Activate(Func<bool> mayContinue) { return engine.Activate(mayContinue); }
        public static bool Active { get { return engine.Active; } }
        public static bool ObserveStops() { return engine.ObserveStops(); }
        public static bool Restore() { return engine.Restore(); }
        public static bool HasResidue { get { return engine.HasResidue; } }
        public static void HealFromCrash() { if (HasResidue) Restore(); }
    }
}
