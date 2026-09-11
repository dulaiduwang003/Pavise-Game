// @author bdth 2074055628@qq.com
// 文件用途 对局期间临时暂停非必要服务 只恢复本次成功请求停止的服务
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
        // 先停依赖方再停宿主 还原时反着来
        // 和已下架的 SysMain/WSearch 策略以及它的台账分开 别混用
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
        // STOP 发成功了这台服务就归我们管 哪怕收据没写进去
        // 这个进程内的凭证不能从磁盘上的 Prepared 记录反推
        private readonly Dictionary<string, Entry> receipts =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        // 还原成功或者外部改过配置以后 别再拿旧收据重放
        // 台账没清干净也不行
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
            // 授权不能来自那个可变数组 也不能来自落盘的服务名
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
                // 先把上一局收尾 再去认领新服务
                if (!RestoreCore()) return false;
                if (!MayContinue(mayContinue)) return true;
                string boot;
                if (!ReadBoot(out boot)) return false;
                receiptBoot = boot;
                var entries = new List<Entry>();
                // 停了几个服务合成一行记 中途放弃也先把已停的记上再还原
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
                    // 查本地打印提供程序会阻塞 用户已经关掉策略或者退出之后
                    // 迟到的结果一律不处理
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
                        // 适配器抛异常时 STOP 可能已经发出去了 所以它的 Prepared
                        // 记录在拿到收据之前故意不升级
                        flush();
                        Warn("log.pausesvc.unowned", name);
                        RestoreCore();
                        return false;
                    }
                    if (!accepted)
                    {
                        // 别人先把它停了 不算我们的所有权
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

        // 只确认已被接受的停止 不反复强制 一旦观察到 STOPPED
        // 之后再变回 RUNNING 就是别人干的
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
            // 进程内收据能回滚那种停成功但收据没落盘的情况
            // 新起的进程没有这份凭证
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
            // 新开机时 Windows 已经按用户配置的启动类型跑过一遍
            // 别再去启动上次开机时手动运行着的服务
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
                    // 收据被替换或者篡改过就直接拒绝 不要覆盖写
                    Warn("log.pausesvc.ledger", null);
                    return false;
                }
            }

            // 启动的服务可能一直停在 START_PENDING 停掉的可能停在 STOP_PENDING
            // 留着记录交给正常重试轮询以后再看
            // 不要卡住界面 也不要强杀宿主进程
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
                    // 我们停完之后用户或者安装程序改了配置
                    // 永久放弃所有权 不去推翻这个选择
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
                    // START 之前先把恢复意图落盘 崩溃之后
                    // R 记录不会对一台后来被停掉的服务重放 START
                    // 因为之前那次还原可能已经成功了
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
                        // 适配器既没发出 START 也没观察到会让我们放弃所有权的用户或服务动作
                        // 会让我们放弃所有权的用户或服务动作
                        startNotIssued.Add(entry.Name);
                    }
                    if (!remove)
                    {
                        OptionalServiceSnapshot after;
                        bool queried = Query(entry.Name, out after);
                        // START 受理后服务先停在 START_PENDING 几百毫秒 等它一小会再核对
                        //   免得每局都留一条待重试 再靠残留重试补一轮
                        if (queried && startResult == OptionalServiceStartResult.Accepted && after.State == 2)
                            queried = WaitStartSettled(entry.Name, out after);
                        if (queried)
                        {
                            // 外部的决定要保留 哪怕下次重试之前它又变了
                            // 最后一次查询不能把所有权已经交出去这个证据
                            // 给忘掉
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

        // 最多等一秒半 每 100ms 查一次 离开 START_PENDING 就返回 查不到或超时按最后一次结果算
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
