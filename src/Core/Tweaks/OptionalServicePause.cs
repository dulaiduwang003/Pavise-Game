// @author bdth 2074055628@qq.com
// 文件用途 对局期间临时暂停非必要服务；只恢复本次成功请求停止的服务。
using System;
using System.Collections.Generic;
using System.Text;

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
        // Stop dependents before their host; restoration uses the reverse order.
        // Keep this separate from the retired SysMain/WSearch policy and its ledger.
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
        // A successful STOP remains ours even if writing the receipt fails.
        // This in-process proof must never be inferred from a Prepared disk record.
        private readonly Dictionary<string, Entry> receipts =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        // Do not replay a stale disk receipt after a successful restore or an
        // external configuration change, even when clearing the ledger failed.
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
            // Do not derive authorization from the mutable array or a persisted name.
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
                // Finish an earlier session before claiming any new services.
                if (!RestoreCore()) return false;
                if (!MayContinue(mayContinue)) return true;
                string boot;
                if (!ReadBoot(out boot)) return false;
                receiptBoot = boot;
                var entries = new List<Entry>();
                foreach (string name in Names)
                {
                    if (!MayContinue(mayContinue)) return RestoreCore();
                    OptionalServiceSnapshot before;
                    if (!Query(name, out before) || !before.Exists || before.State != 4
                        || before.StartType == 4 || !before.AcceptsStop || before.HasActiveDependents
                        || string.IsNullOrEmpty(before.Configuration) || before.Configuration.Length > 8192) continue;
                    if (IsPrinting(name) && !PrintingIdle()) continue;
                    // Querying a local print provider can block. Never act on its
                    // late result after the user has disabled the policy or exited.
                    if (!MayContinue(mayContinue)) return RestoreCore();

                    var entry = new Entry { Name = name, Configuration = before.Configuration };
                    entries.Add(entry);
                    if (!Save(entries, boot)) return AbortAfterLedgerFailure();
                    if (!MayContinue(mayContinue))
                    {
                        entries.Remove(entry);
                        if (!Save(entries, boot)) return AbortAfterLedgerFailure();
                        return RestoreCore();
                    }

                    bool accepted;
                    try { accepted = control.TryStop(name, before, mayContinue); }
                    catch
                    {
                        // A throwing adapter may have issued STOP. Its Prepared
                        // record is deliberately not promoted without a receipt.
                        Warn("log.pausesvc.unowned", name);
                        RestoreCore();
                        return false;
                    }
                    if (!accepted)
                    {
                        // Another actor stopping it first is not our ownership.
                        entries.Remove(entry);
                        if (!Save(entries, boot)) return AbortAfterLedgerFailure();
                        continue;
                    }

                    entry.Owned = true;
                    receipts[name] = entry;
                    OptionalServiceSnapshot stopped;
                    entry.StopObserved = Query(name, out stopped) && stopped.Exists && stopped.State == 1;
                    Logger.Log(Lang.F("log.pausesvc.stop", name));
                    if (!Save(entries, boot)) return AbortAfterLedgerFailure();
                }
                if (!MayContinue(mayContinue)) return RestoreCore();
                active = true;
                lastWarning = null;
                return true;
            }
        }

        public bool Restore() { lock (gate) return RestoreCore(); }

        // Only acknowledge accepted stops, never enforce them repeatedly. Once
        // STOPPED is observed, a later RUNNING state belongs to another actor.
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
            // In-process receipts can roll back a successful STOP whose receipt
            // could not be persisted. A fresh process has no such proof.
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
            // Windows has already applied the user's configured startup policy
            // on a new boot. Do not start last boot's manually running services.
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
                    // Refuse a replaced/tampered receipt instead of overwriting it.
                    Warn("log.pausesvc.ledger", null);
                    return false;
                }
            }

            // A started service may remain START_PENDING; a stopped one may
            // remain STOP_PENDING. Keep its record and let the normal retry loop
            // check it later instead of blocking the UI or force-killing a host.
            var pending = new List<string>();
            var uncertain = new List<string>();
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
                    // A user/service installer changed the configuration after
                    // our stop. Drop ownership permanently; never undo that choice.
                    Logger.Log(Lang.F("log.pausesvc.changed", entry.Name));
                    remove = true;
                }
                else if (entry.Owned && !entry.Restoring
                    && ((entry.StopObserved && current.State >= 2 && current.State <= 7)
                        || current.State == 2 || current.State == 5 || current.State == 6 || current.State == 7))
                    remove = true; // A known completed STOP cannot cause a later state transition.
                else if (entry.Owned && !entry.StopObserved && !entry.Restoring && current.State != 1)
                    pending.Add(entry.Name); // Accepted STOP may still report RUNNING briefly.
                else if (current.State == 4 || current.State == 7)
                    remove = true; // Already running, or externally paused: no START/CONTINUE.
                else if (!entry.Owned || (entry.Restoring && current.State == 1))
                    uncertain.Add(entry.Name);
                else if (current.State == 1)
                {
                    entry.StopObserved = true;
                    entry.Restoring = true;
                    receipts[entry.Name] = entry;
                    startNotIssued.Remove(entry.Name);
                    // Persist recovery intent before START. After a crash, an R
                    // record never replays START against a subsequently stopped
                    // service: the earlier restore may already have succeeded.
                    if (!Save(entries, boot))
                    {
                        entry.Restoring = false;
                        startNotIssued.Add(entry.Name); // No START was called.
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
                        // The adapter has neither issued START nor observed a
                        // user/service action that would relinquish ownership.
                        startNotIssued.Add(entry.Name);
                    }
                    if (!remove)
                    {
                        OptionalServiceSnapshot after;
                        if (Query(entry.Name, out after))
                        {
                            // Preserve an external decision even if it is changed
                            // again before the next retry. A final query must not
                            // forget evidence that our ownership was relinquished.
                            bool changed = !after.Exists || after.StartType == 4
                                || !string.Equals(after.Configuration, entry.Configuration, StringComparison.Ordinal);
                            bool externalTransition = startResult == OptionalServiceStartResult.NotIssued
                                && after.State >= 2 && after.State <= 7;
                            if (changed || externalTransition || after.State == 4 || after.State == 7)
                            {
                                if (changed) Logger.Log(Lang.F("log.pausesvc.changed", entry.Name));
                                else if (after.State == 4 && startResult == OptionalServiceStartResult.Accepted)
                                    Logger.Log(Lang.F("log.pausesvc.restore", entry.Name));
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
            bool saved = Save(entries, boot);
            if (saved) { settled.Clear(); stopObservationDirty = false; }
            if (!saved) Warn("log.pausesvc.ledger", null);
            else if (uncertain.Count != 0) Warn("log.pausesvc.unowned", string.Join(", ", uncertain.ToArray()));
            else if (pending.Count != 0) Warn("log.pausesvc.pending", string.Join(", ", pending.ToArray()));
            else lastWarning = null;
            return saved && entries.Count == 0;
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
