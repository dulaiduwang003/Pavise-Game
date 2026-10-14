// 作者：bdth 2074055628@qq.com
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

    internal interface IOptionalServiceStopClassification
    {
        bool LastStopWasNormalSkip { get; }
    }

    internal sealed class OptionalServicePauseEngine
    {
        // 先停依赖服务，恢复时反序执行。
        internal static readonly string[] Names =
            { "PrintNotify", "Spooler", "WSearch", "WMPNetworkSvc", "MapsBroker", "DiagTrack", "RetailDemo" };
        internal const string PcaPreferenceKey = "MatchPausePcaV1";
        private const long StopVerificationSeconds = 30;
        private static readonly long SettledAuditTicks = TimeSpan.FromSeconds(30).Ticks;
        // 单调时钟不受系统校时影响。
        private static readonly System.Diagnostics.Stopwatch stopClock = System.Diagnostics.Stopwatch.StartNew();

        private sealed class Entry
        {
            internal string Name, Configuration;
            internal bool Owned, StopObserved, Restoring;
            internal long AcceptedAt, NextAuditAt;
        }

        private readonly IOptionalServiceControl control;
        private readonly IOptionalServiceLedger ledger;
        private readonly object gate = new object();
        // 准备记录不能证明停止请求成功，进程内回执另行保留。
        private readonly Dictionary<string, Entry> receipts =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> startNotIssued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool active;
        private int activeSelection;
        private bool stopObservationDirty;
        private string receiptBoot;
        private string lastWarning;
        private string lastFailure;
        private readonly Func<long> nowTicks;

        internal OptionalServicePauseEngine(IOptionalServiceControl control, IOptionalServiceLedger ledger)
            : this(control, ledger, delegate { return stopClock.Elapsed.Ticks; }) { }

        internal OptionalServicePauseEngine(IOptionalServiceControl control, IOptionalServiceLedger ledger, Func<long> nowTicks)
        {
            if (control == null || ledger == null || nowTicks == null) throw new ArgumentNullException();
            this.control = control;
            this.ledger = ledger;
            this.nowTicks = nowTicks;
        }

        internal static bool IsAllowed(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            switch (name.ToLowerInvariant())
            {
                case "printnotify": case "spooler": case "wsearch":
                case "wmpnetworksvc": case "mapsbroker": case "diagtrack": case "retaildemo": case "pcasvc":
                    return true;
                default: return false;
            }
        }

        public bool Active { get { lock (gate) return active; } }
        internal bool MatchesSelection(bool includePca, bool includeBase)
        { lock (gate) return active && activeSelection == ((includeBase ? 1 : 0) | (includePca ? 2 : 0)); }
        public string LastFailure { get { lock (gate) return lastFailure; } }
        public int PendingStopCount { get { lock (gate) { int count = 0; foreach (Entry entry in receipts.Values) if (!entry.StopObserved) count++; return count; } } }

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

        public bool Activate(Func<bool> mayContinue) { return Activate(mayContinue, false); }

        public bool Activate(Func<bool> mayContinue, bool includePca)
        { return Activate(mayContinue, includePca, true); }

        public bool Activate(Func<bool> mayContinue, bool includePca, bool includeBase)
        {
            lock (gate)
            {
                int selection = (includeBase ? 1 : 0) | (includePca ? 2 : 0);
                if (active && activeSelection == selection) return AuditStops(mayContinue);
                if (!RestoreCore()) return false;
                if (!MayContinue(mayContinue)) return true;
                string boot;
                if (!ReadBoot(out boot)) return false;
                lastFailure = null;
                receiptBoot = boot;
                var entries = new List<Entry>();
                var stoppedNames = new List<string>();
                Action flush = delegate
                {
                    if (stoppedNames.Count == 0) return;
                    Logger.Log(Lang.F("log.pausesvc.stop", string.Join(", ", stoppedNames.ToArray())));
                    stoppedNames.Clear();
                };
                var selectedNames = includeBase ? new List<string>(Names) : new List<string>();
                if (includePca) selectedNames.Add("PcaSvc");
                foreach (string name in selectedNames)
                {
                    if (!MayContinue(mayContinue)) { flush(); return RestoreCore(); }
                    OptionalServiceSnapshot before;
                    if (!Query(name, out before))
                    {
                        lastFailure = name + ": service state could not be read";
                        flush(); RestoreCore(); return false;
                    }
                    if (!before.Exists || before.State != 4
                        || before.StartType == 4 || !before.AcceptsStop || before.HasActiveDependents
                        || string.IsNullOrEmpty(before.Configuration) || before.Configuration.Length > 8192) continue;
                    if (IsPrinting(name) && !PrintingIdle()) continue;
                    // 打印查询返回后重新检查策略，退出后的结果不执行。
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
                        lastFailure = name + ": stop request failed or its result is unknown";
                        flush();
                        Warn("log.pausesvc.unowned", name);
                        RestoreCore();
                        return false;
                    }
                    if (!accepted)
                    {
                        entries.Remove(entry);
                        if (!Save(entries, boot)) { flush(); return AbortAfterLedgerFailure(); }
                        // The adapter may decline a normal ownership/cancellation race. An
                        // unchanged, still eligible service instead proves a failed request.
                        OptionalServiceSnapshot declined;
                        var classification = control as IOptionalServiceStopClassification;
                        if ((classification == null || !classification.LastStopWasNormalSkip)
                            && MayContinue(mayContinue) && (!Query(name, out declined)
                            || (declined.Exists && declined.State == 4 && declined.StartType != 4
                                && declined.AcceptsStop && !declined.HasActiveDependents
                                && declined.Configuration == before.Configuration && (!IsPrinting(name) || PrintingIdle()))))
                        {
                            lastFailure = name + ": service stop was rejected";
                            flush(); RestoreCore(); return false;
                        }
                        continue;
                    }

                    entry.Owned = true;
                    entry.AcceptedAt = nowTicks();
                    receipts[name] = entry;
                    OptionalServiceSnapshot stopped;
                    bool readback = Query(name, out stopped);
                    entry.StopObserved = readback && stopped.Exists && stopped.State == 1;
                    if (entry.StopObserved)
                    {
                        entry.NextAuditAt = nowTicks() + SettledAuditTicks;
                        stoppedNames.Add(name);
                    }
                    if (!Save(entries, boot)) { flush(); return AbortAfterLedgerFailure(); }
                    if (!readback)
                    {
                        lastFailure = name + ": accepted stop could not be read back";
                        flush(); RestoreCore(); return false;
                    }
                }
                flush();
                if (!MayContinue(mayContinue)) return RestoreCore();
                active = true;
                activeSelection = selection;
                lastWarning = null;
                return true;
            }
        }

        public bool Restore() { lock (gate) return RestoreCore(); }

        // 停稳后降低复查频率，待确认服务仍随对局循环检查。
        public bool AuditStops(Func<bool> mayContinue)
        {
            lock (gate)
            {
                if (!active || !MayContinue(mayContinue)) return true;
                long now = nowTicks();
                foreach (Entry receipt in receipts.Values)
                {
                    if (!MayContinue(mayContinue)) return true;
                    if (receipt.Restoring || settled.Contains(receipt.Name)) continue;
                    if (receipt.StopObserved && now < receipt.NextAuditAt
                        && now >= receipt.NextAuditAt - SettledAuditTicks) continue;
                    OptionalServiceSnapshot current;
                    if (!Query(receipt.Name, out current))
                    { lastFailure = receipt.Name + ": paused state could not be verified"; return false; }
                    if (!current.Exists || current.StartType == 4 || current.Configuration != receipt.Configuration)
                    { lastFailure = receipt.Name + ": service ownership changed during the match"; return false; }
                    if (current.State == 1)
                    {
                        if (!receipt.StopObserved) { receipt.StopObserved = true; stopObservationDirty = true; }
                        receipt.NextAuditAt = now + SettledAuditTicks;
                    }
                    else if (receipt.StopObserved)
                    { lastFailure = receipt.Name + ": service resumed after its confirmed stop"; return false; }
                    else if (now - receipt.AcceptedAt >= TimeSpan.FromSeconds(StopVerificationSeconds).Ticks)
                    { lastFailure = receipt.Name + ": stop confirmation timed out"; return false; }
                }
                bool ok = SaveStopObservations();
                if (!ok) lastFailure = "Service stop confirmation could not be persisted";
                return ok;
            }
        }

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
                        receipt.NextAuditAt = nowTicks() + SettledAuditTicks;
                        stopObservationDirty = true;
                    }
                }
                return SaveStopObservations();
            }
        }

        private bool SaveStopObservations()
        {
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

        private bool AbortAfterLedgerFailure()
        {
            Warn("log.pausesvc.ledger", null);
            RestoreCore();
            return false;
        }

        private bool RestoreCore()
        {
            active = false;
            activeSelection = 0;
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
            // 新系统启动不重放上次手动运行服务的恢复记录。
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
                    Warn("log.pausesvc.ledger", null);
                    return false;
                }
            }

            // 服务仍在转换状态时保留记录，不阻塞界面或强杀宿主。
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
                    Logger.Log(Lang.F("log.pausesvc.changed", entry.Name));
                    remove = true;
                }
                else if (entry.Owned && !entry.Restoring
                    && ((entry.StopObserved && current.State >= 2 && current.State <= 7)
                        || current.State == 2 || current.State == 5 || current.State == 6 || current.State == 7))
                    remove = true;
                else if (entry.Owned && !entry.StopObserved && !entry.Restoring && current.State != 1)
                    pending.Add(entry.Name);
                else if (current.State == 4 || current.State == 7)
                    remove = true;
                else if (!entry.Owned || (entry.Restoring && current.State == 1))
                    uncertain.Add(entry.Name);
                else if (current.State == 1)
                {
                    entry.StopObserved = true;
                    entry.Restoring = true;
                    receipts[entry.Name] = entry;
                    startNotIssued.Remove(entry.Name);
                    // 启动前持久化恢复意图，避免崩溃后重复启动。
                    if (!Save(entries, boot))
                    {
                        entry.Restoring = false;
                        startNotIssued.Add(entry.Name);
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
                        startNotIssued.Add(entry.Name);
                    }
                    if (!remove)
                    {
                        OptionalServiceSnapshot after;
                        bool queried = Query(entry.Name, out after);
                        if (queried && startResult == OptionalServiceStartResult.Accepted && after.State == 2)
                            queried = WaitStartSettled(entry.Name, out after);
                        if (queried)
                        {
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
                    || id == Guid.Empty || lines.Length > 9) return false;
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

        public static bool Activate(Func<bool> mayContinue) { return Activate(mayContinue, true); }
        public static bool Activate(Func<bool> mayContinue, bool includeBase)
        { lock (SystemConfigurationGate.Sync) return engine.Activate(mayContinue, Settings.Load(OptionalServicePauseEngine.PcaPreferenceKey, false), includeBase); }
        public static bool Active { get { return engine.Active; } }
        internal static bool MatchesSelection(bool includeBase)
        { return engine.MatchesSelection(Settings.Load(OptionalServicePauseEngine.PcaPreferenceKey, false), includeBase); }
        public static string LastFailure { get { return engine.LastFailure; } }
        public static int PendingStopCount { get { return engine.PendingStopCount; } }
        public static bool ObserveStops() { return engine.ObserveStops(); }
        public static bool AuditStops(Func<bool> mayContinue) { lock (SystemConfigurationGate.Sync) return engine.AuditStops(mayContinue); }
        public static bool Restore() { lock (SystemConfigurationGate.Sync) return engine.Restore(); }
        public static bool HasResidue { get { return engine.HasResidue; } }
        public static void HealFromCrash() { if (HasResidue) Restore(); }
    }
}
