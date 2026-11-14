using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class ServicePauser
    {
        private enum Phase
        {
            Prepared,
            Owned,
            Restoring,
            Settled
        }

        private sealed class Receipt
        {
            internal string Name;

            internal Phase State;

            internal bool CanStart;

            internal bool StopObserved;

            internal int RunningSeen;

            // 诊断字段仅保存在内存，不参与恢复状态判断。
            internal string HoldStep = "";

            internal int HoldState;

            internal bool HoldWait;

            internal string StartStep = "";

            internal int StartWin32;
        }

        // 启停等待按有限间隔复核；耗尽后仅状态变化可唤醒。
        internal const int MaximumWaitObservations = 6;

        private static readonly int[] WaitSeconds = new int[MaximumWaitObservations] { 5, 10, 20, 30, 30, 30 };

        private int waitObservations;

        // 仅恢复完整执行并落盘、且剩余服务均在启停等待时成立。
        private bool waitOnlyRound;

#if PAVISE_SELFTEST
        internal delegate bool StopHook(string name, out bool confirmed);
        internal delegate bool StartHook(string name, out string step, out int win32);
        // 测试入口注入服务查询与启停实现。
        internal static Func<string, int> QueryForTest;
        internal static StopHook StopForTest;
        internal static StartHook StartForTest;
        internal int WaitObservationsForTest { get { lock (lk) return waitObservations; } }
#endif

        private readonly string[] names;

        private readonly HashSet<string> allowed;

        private readonly string ledgerSettingKey;

        private readonly object lk = new object();

        private readonly Dictionary<string, Receipt> receipts = new Dictionary<string, Receipt>(StringComparer.OrdinalIgnoreCase);

        private bool active;

        private bool loaded;

        private bool busy;

        private bool observationDirty;

        private string lastRead = "";

        public bool HasResidue
        {
            get
            {
                lock (lk)
                {
                    if (receipts.Count != 0)
                    {
                        return true;
                    }
                    string value;
                    return !Read(out value) || value == null || value.Length != 0;
                }
            }
        }

        public bool HadLedger
        {
            get
            {
                return HasResidue;
            }
        }

        public ServicePauser(string[] serviceNames, string flagKey)
        {
            if (serviceNames == null)
            {
                throw new ArgumentNullException("serviceNames");
            }
            names = (string[])serviceNames.Clone();
            allowed = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            ledgerSettingKey = flagKey;
        }

        internal string[] CaptureConfirmedStops()
        {
            lock (lk)
            {
                var result = new List<string>();
                if (!active) return result.ToArray();
                foreach (Receipt receipt in receipts.Values)
                    if (receipt.State == Phase.Owned && receipt.CanStart && receipt.StopObserved && Query(receipt.Name) == 1)
                        result.Add(receipt.Name);
                return result.ToArray();
            }
        }

        public bool Activate(out List<string> justStopped, out List<string> confirmedStopped, out bool ledgerLost)
        {
            justStopped = new List<string>();
            confirmedStopped = new List<string>();
            ledgerLost = false;
            lock (lk)
            {
                if (busy)
                {
                    return false;
                }
                busy = true;
                try
                {
                    if (active)
                    {
                        return ObserveStops();
                    }
                    if (!Load())
                    {
                        return false;
                    }
                    List<string> remain;
                    if (receipts.Count != 0 && !RestoreCore(out remain))
                    {
                        return false;
                    }
                    string[] namesSnapshot = names;
                    foreach (string entryText in namesSnapshot)
                    {
                        if (Query(entryText) != 4)
                        {
                            continue;
                        }
                        string lastReadSnapshot = lastRead;
                        Receipt receipt = new Receipt();
                        receipt.Name = entryText;
                        receipt.State = Phase.Prepared;
                        Receipt receipt2 = receipt;
                        receipts.Add(entryText, receipt2);
                        if (!Save())
                        {
                            string value;
                            if (Read(out value) && value == lastReadSnapshot)
                            {
                                receipts.Remove(entryText);
                            }
                            else
                            {
                                receipt2.State = Phase.Settled;
                            }
                            ledgerLost = true;
                            RestoreCore(out remain);
                            return false;
                        }
                        bool confirmed;
                        if (Query(entryText) != 4)
                        {
                            Settle(receipt2);
                            if (!FlushSettled())
                            {
                                ledgerLost = true;
                                RestoreCore(out remain);
                                return false;
                            }
                        }
                        else if (Stop(entryText, out confirmed))
                        {
                            receipt2.State = Phase.Owned;
                            receipt2.CanStart = true;
                            receipt2.StopObserved = confirmed || Query(entryText) == 1;
                            justStopped.Add(entryText);
                            if (receipt2.StopObserved)
                            {
                                confirmedStopped.Add(entryText);
                            }
                            if (!Save())
                            {
                                ledgerLost = true;
                                RestoreCore(out remain);
                                return false;
                            }
                        }
                        else
                        {
                            if (Query(entryText) == 4)
                            {
                                receipt2.State = Phase.Settled;
                            }
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
                catch
                {
                    return false;
                }
                finally
                {
                    busy = false;
                }
            }
        }

        public bool Restore(out List<string> remain)
        {
            lock (lk)
            {
                remain = PendingNames();
                waitOnlyRound = false;
                if (busy)
                {
                    return false;
                }
                busy = true;
                try
                {
                    return RestoreCore(out remain);
                }
                catch
                {
                    remain = PendingNames();
                    return false;
                }
                finally
                {
                    busy = false;
                }
            }
        }

        private bool RestoreCore(out List<string> remain)
        {
            active = false;
            waitOnlyRound = false;
            // 预置读取失败，避免中途退出后沿用上次诊断。
            foreach (Receipt stale in receipts.Values) Hold(stale, "ledger-read", 0, false);
            remain = PendingNames();
            if (!Load())
            {
                return false;
            }
            bool allRestored = true;
            string[] namesSnapshot = names;
            foreach (string entryText in namesSnapshot)
            {
                Receipt value;
                if (!receipts.TryGetValue(entryText, out value) || value.State == Phase.Settled)
                {
                    continue;
                }
                int queryResult = Query(entryText);
                switch (queryResult)
                {
                case 0:
                    allRestored = false;
                    Hold(value, "query", 0, false);
                    continue;
                default:
                    value.RunningSeen = 0;
                    break;
                case 4:
                    break;
                }
                switch (queryResult)
                {
                case 4:
                    if (value.State == Phase.Owned && !value.StopObserved && ++value.RunningSeen < 2)
                    {
                        allRestored = false;
                        Hold(value, "running-unconfirmed", queryResult, true);
                    }
                    else
                    {
                        Settle(value);
                    }
                    break;
                default:
                    if ((value.State == Phase.Owned && !value.StopObserved && queryResult == 3) || (value.State == Phase.Prepared && (queryResult == 2 || queryResult == 3)) || (value.State == Phase.Restoring && (queryResult == 2 || queryResult == 3)))
                    {
                        allRestored = false;
                        Hold(value, queryResult == 3 ? "stop-pending" : "start-pending", queryResult, true);
                    }
                    else
                    {
                        Settle(value);
                    }
                    break;
                case 1:
                    if (!value.CanStart)
                    {
                        allRestored = false;
                        Hold(value, "start-used", queryResult, false);
                        break;
                    }
                    value.StopObserved = true;
                    value.State = Phase.Restoring;
                    if (!Save())
                    {
                        allRestored = false;
                        Hold(value, "ledger-write", queryResult, false);
                        break;
                    }
                    switch (Query(entryText))
                    {
                    case 0:
                        allRestored = false;
                        Hold(value, "query", 0, false);
                        break;
                    default:
                        Settle(value);
                        break;
                    case 1:
                    {
                        value.CanStart = false;
                        Start(entryText, value);
                        int queryResult2 = Query(entryText);
                        if (queryResult2 == 4 || queryResult2 == 7)
                        {
                            Settle(value);
                        }
                        else
                        {
                            allRestored = false;
                    // 已受理的启动请求继续等待，其他结果按启动失败处理。
                            Hold(value, "start", queryResult2, queryResult2 == 2);
                        }
                        break;
                    }
                    }
                    break;
                }
            }
            bool flushed = FlushSettled();
            if (!flushed)
            {
                allRestored = false;
            }
            remain = PendingNames();
            if (allRestored)
            {
            // 完整结清后，下一次暂停使用新的观察阶段。
                if (receipts.Count == 0) waitObservations = 0;
                return receipts.Count == 0;
            }
            if (flushed && receipts.Count != 0)
            {
                waitOnlyRound = true;
                foreach (Receipt receipt in receipts.Values)
                    if (receipt.State != Phase.Settled && !receipt.HoldWait) { waitOnlyRound = false; break; }
            }
            return false;
        }

        private static void Hold(Receipt receipt, string step, int state, bool wait)
        {
            receipt.HoldStep = step;
            receipt.HoldState = state;
            receipt.HoldWait = wait;
        }

        // 剩余项均在启停等待时，本轮仅观察状态，不执行写入。
        internal bool PendingIsWaitOnly
        {
            get { lock (lk) return waitOnlyRound; }
        }

        internal static string StateName(int state)
        {
            switch (state)
            {
            case 1: return PerformancePolicy.Text("已停止", "stopped");
            case 2: return PerformancePolicy.Text("启动中(START_PENDING)", "start pending");
            case 3: return PerformancePolicy.Text("停止中(STOP_PENDING)", "stop pending");
            case 4: return PerformancePolicy.Text("运行中", "running");
            case 5: return PerformancePolicy.Text("继续中(CONTINUE_PENDING)", "continue pending");
            case 6: return PerformancePolicy.Text("暂停中(PAUSE_PENDING)", "pause pending");
            case 7: return PerformancePolicy.Text("已暂停", "paused");
            default: return PerformancePolicy.Text("读不到", "unreadable");
            }
        }

        // 未结清服务的失败阶段，仅供日志使用。
        internal string DescribePending()
        {
            lock (lk)
            {
                var parts = new List<string>();
                string[] namesSnapshot = names;
                foreach (string name in namesSnapshot)
                {
                    Receipt receipt;
                    if (!receipts.TryGetValue(name, out receipt) || receipt.State == Phase.Settled || receipt.HoldStep.Length == 0) continue;
                    string started = receipt.StartStep.Length == 0 ? "" : PerformancePolicy.Text(" 启动步骤=", " start step=") + receipt.StartStep
                        + PerformancePolicy.Text(" 系统错误码=", " system error=") + receipt.StartWin32;
                    switch (receipt.HoldStep)
                    {
                    case "stop-pending":
                        parts.Add(name + PerformancePolicy.Text(" 仍在停止中，停完后再启动", " is still stopping; it will be started once stopped")); break;
                    case "start-pending":
                        parts.Add(name + PerformancePolicy.Text(" 正在启动，等待完成", " is starting; waiting for it to finish")); break;
                    case "running-unconfirmed":
                        parts.Add(name + PerformancePolicy.Text(" 已在运行，下一次复核后结清", " is already running; it will be settled after one more check")); break;
                    case "query":
                        parts.Add(name + PerformancePolicy.Text(" 状态读取失败", " state could not be read")); break;
                    case "ledger-write":
                        parts.Add(name + PerformancePolicy.Text(" 恢复记录写入失败，未发起启动", " recovery record could not be written; no start was issued")); break;
                    case "start-used":
                        parts.Add(name + PerformancePolicy.Text(" 已停止，唯一一次启动已经用过，需要手动启动", " is stopped and the single start attempt was already used; start it manually") + started); break;
                    default:
                        parts.Add(name + (receipt.HoldWait ? PerformancePolicy.Text(" 启动请求已受理，等待完成", " start request accepted; waiting for it to finish")
                            : PerformancePolicy.Text(" 启动未完成", " did not start")) + PerformancePolicy.Text(" 当前状态=", " state=") + StateName(receipt.HoldState) + started); break;
                    }
                }
                return string.Join(PerformancePolicy.Text("；", "; "), parts.ToArray());
            }
        }

                // 启停等待不占恢复失败预算，交还调度方后续复核。
                // 观察额度耗尽后，仅真实状态变化可再次唤醒。
        internal void ReportWait()
        {
            string[] watchedNames;
            int[] watchedStates;
            lock (lk)
            {
                if (!waitOnlyRound || !RecoveryAttemptTrace.Active) return;
                var pending = new List<Receipt>();
                foreach (Receipt receipt in receipts.Values)
                    if (receipt.State != Phase.Settled) pending.Add(receipt);
                if (pending.Count == 0) return;
                // 已运行服务的最终复核不占等待观察额度。
                foreach (Receipt receipt in pending)
                    if (receipt.HoldStep == "running-unconfirmed") { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(WaitSeconds[0])); return; }
                if (waitObservations < MaximumWaitObservations)
                {
                    RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(WaitSeconds[waitObservations++]));
                    return;
                }
                watchedNames = new string[pending.Count];
                watchedStates = new int[pending.Count];
                for (int i = 0; i < pending.Count; i++) { watchedNames[i] = pending[i].Name; watchedStates[i] = pending[i].HoldState; }
            }
            RecoveryAttemptTrace.Exhausted(delegate
            {
                for (int i = 0; i < watchedNames.Length; i++)
                    if (Query(watchedNames[i]) != watchedStates[i]) return true;
                return false;
            });
        }

        private bool ObserveStops()
        {
            foreach (Receipt value in receipts.Values)
            {
                if (value.State == Phase.Owned && !value.StopObserved && Query(value.Name) == 1)
                {
                    value.StopObserved = true;
                    observationDirty = true;
                }
            }
            if (observationDirty)
            {
                return Save();
            }
            return true;
        }

        private static void Settle(Receipt receipt)
        {
            receipt.State = Phase.Settled;
            receipt.CanStart = false;
            receipt.HoldStep = "";
            receipt.HoldWait = false;
        }

        private List<string> PendingNames()
        {
            List<string> list = new List<string>();
            string[] namesSnapshot = names;
            foreach (string entryText in namesSnapshot)
            {
                if (receipts.ContainsKey(entryText))
                {
                    list.Add(entryText);
                }
            }
            return list;
        }

        private bool Load()
        {
            string value;
            if (!Read(out value) || value == null)
            {
                return false;
            }
            Dictionary<string, Receipt> result;
            if (!Parse(value, out result))
            {
                return false;
            }
            if (!loaded)
            {
                foreach (KeyValuePair<string, Receipt> item in result)
                {
                    receipts.Add(item.Key, item.Value);
                }
                loaded = true;
            }
            else
            {
                foreach (KeyValuePair<string, Receipt> item2 in result)
                {
                    Receipt value2;
                    if (!receipts.TryGetValue(item2.Key, out value2))
                    {
                        receipts.Add(item2.Key, item2.Value);
                    }
                    else if (item2.Value.State == Phase.Settled)
                    {
                        Settle(value2);
                    }
                }
            }
            lastRead = value;
            return true;
        }

        private bool Parse(string raw, out Dictionary<string, Receipt> result)
        {
            result = new Dictionary<string, Receipt>(StringComparer.OrdinalIgnoreCase);
            if (raw.Length == 0)
            {
                return true;
            }
            if (raw.Length > 4096)
            {
                return false;
            }
            if (!raw.StartsWith("2\n", StringComparison.Ordinal))
            {
                string[] textEntries = ((raw == "1" && names.Length == 1) ? new string[1] { names[0] } : raw.Split('|'));
                string[] textEntries2 = textEntries;
                foreach (string entryText in textEntries2)
                {
                    if (!allowed.Contains(entryText) || result.ContainsKey(entryText))
                    {
                        return false;
                    }
                    result.Add(entryText, new Receipt
                    {
                        Name = entryText,
                        State = Phase.Prepared,
                        CanStart = true
                    });
                }
                return result.Count > 0;
            }
            string[] rawParts = raw.Split('\n');
            if (rawParts.Length < 2 || rawParts.Length - 1 > allowed.Count)
            {
                return false;
            }
            for (int j = 1; j < rawParts.Length; j++)
            {
                string[] parts = rawParts[j].Split('\t');
                if (parts.Length != 3 || !allowed.Contains(parts[1]) || result.ContainsKey(parts[1]) || (parts[2] != "0" && parts[2] != "1"))
                {
                    return false;
                }
                Phase phase;
                if (parts[0] == "P")
                {
                    phase = Phase.Prepared;
                }
                else if (parts[0] == "O")
                {
                    phase = Phase.Owned;
                }
                else if (parts[0] == "R")
                {
                    phase = Phase.Restoring;
                }
                else
                {
                    if (parts[0] != "S")
                    {
                        return false;
                    }
                    phase = Phase.Settled;
                }
                if (phase == Phase.Prepared && parts[2] != "0")
                {
                    return false;
                }
                result.Add(parts[1], new Receipt
                {
                    Name = parts[1],
                    State = phase,
                    CanStart = (phase == Phase.Owned),
                    StopObserved = (parts[2] == "1")
                });
            }
            return true;
        }

        private string Encode()
        {
            if (receipts.Count == 0)
            {
                return "";
            }
            List<string> list = new List<string>();
            list.Add("2");
            List<string> list2 = list;
            string[] namesSnapshot = names;
            foreach (string entryText in namesSnapshot)
            {
                Receipt value;
                if (receipts.TryGetValue(entryText, out value))
                {
                    string message = ((value.State == Phase.Prepared) ? "P" : ((value.State == Phase.Owned) ? "O" : ((value.State == Phase.Restoring) ? "R" : "S")));
                    list2.Add(message + "\t" + entryText + "\t" + (value.StopObserved ? "1" : "0"));
                }
            }
            return string.Join("\n", list2.ToArray());
        }

        private bool Save()
        {
            string encodeResult = Encode();
            string value;
            if (!Write(encodeResult) || !Read(out value) || value != encodeResult)
            {
                return false;
            }
            lastRead = value;
            observationDirty = false;
            return true;
        }

        private bool FlushSettled()
        {
            if (receipts.Count == 0)
            {
                return true;
            }
            if (!Save())
            {
                return false;
            }
            List<Receipt> list = new List<Receipt>();
            foreach (Receipt value in receipts.Values)
            {
                if (value.State == Phase.Settled)
                {
                    list.Add(value);
                }
            }
            if (list.Count == 0)
            {
                return true;
            }
            foreach (Receipt item in list)
            {
                receipts.Remove(item.Name);
            }
            if (Save())
            {
                return true;
            }
            foreach (Receipt item2 in list)
            {
                receipts.Add(item2.Name, item2);
            }
            return false;
        }

        private int Query(string name)
        {
            try
            {
#if PAVISE_SELFTEST
                if (QueryForTest != null) return QueryForTest(name);
#endif
                return SvcState.Query(name);
            }
            catch
            {
                return 0;
            }
        }

        private bool Stop(string name, out bool confirmed)
        {
            confirmed = false;
            try
            {
#if PAVISE_SELFTEST
                if (StopForTest != null) return StopForTest(name, out confirmed);
#endif
                return SvcCtl.StopIfRunning(name, out confirmed);
            }
            catch
            {
                return false;
            }
        }

                // 每次尝试只发起一次启动请求，结果写入诊断回执。
        private void Start(string name, Receipt receipt)
        {
            string step = "exception";
            int win32 = 0;
            try
            {
#if PAVISE_SELFTEST
                if (StartForTest != null) { StartForTest(name, out step, out win32); receipt.StartStep = step ?? ""; receipt.StartWin32 = win32; return; }
#endif
                SvcCtl.EnsureStarted(name, out step, out win32);
            }
            catch
            {
            }
            receipt.StartStep = step ?? "";
            receipt.StartWin32 = win32;
        }

        private bool Read(out string value)
        {
            value = null;
            try
            {
                return Settings.TryLoadStr(ledgerSettingKey, out value);
            }
            catch
            {
                return false;
            }
        }

        private bool Write(string value)
        {
            try
            {
                return Settings.SaveStr(ledgerSettingKey, value);
            }
            catch
            {
                return false;
            }
        }
    }
}
