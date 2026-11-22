using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static class InputDeviceHolders
    {
        private const int ScanDebounceMs = 400;
        private const int MinWakeScanGapMs = 3000;
        private const int ConfirmDelayMs = 2000;
        private const int RescanMs = 20000;
        private const int CandidateTtlMs = 10000;
        private const int IdleExitMs = 30000;
        private const int MaxUnreadableAttempts = 2;
        // 还没核对出结果的进程先不压 最多等这么久 探测一直出不了结果就按普通后台处理
        internal const int PendingLimitMs = 10000;
        // 确认过的驱动程序一直放行 这么久没再出现在后台候选里才清掉记录
        private const int HolderTtlMs = 30 * 60 * 1000;

        private sealed class Candidate
        {
            internal long Creation;
            internal string Name;
            internal int FirstTick;
            internal int SeenTick;
            internal bool Holder;
            internal bool Scanned;
            internal bool Suspect;
            internal bool Skipped;
            internal int UnreadableAttempts;
        }

        private static readonly object sync = new object();
        private static readonly Dictionary<int, Candidate> candidates = new Dictionary<int, Candidate>();
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static Thread scanner;
        private static int lastCallTick;
        private static HidProbeResult lastBatchFailure;

        // 后台扫描每轮对准备压制的进程调用 返回 true 表示这一轮不要压它
        // 确认开着键鼠私有接口的进程一直返回 true 还没核对完的也先返回 true 核对出不是驱动才返回 false
        // 激进档会把后台进程压到几乎不运行 先压再核对会让驱动停摆几秒 所以核对在前
        internal static bool IsHolderOrPending(int pid, long creation, string name)
        {
            if (pid <= 4 || creation <= 0 || !Environment.Is64BitProcess) return false;
            lock (sync)
            {
                int now = Environment.TickCount;
                lastCallTick = now;
                Candidate candidate;
                if (candidates.TryGetValue(pid, out candidate) && candidate.Creation == creation)
                {
                    // 隔了一阵才又回到后台的普通进程可能已经打开了设备 重新核对 核对期间同样先不压
                    if (!candidate.Holder && !candidate.Skipped && now - candidate.SeenTick > CandidateTtlMs)
                    {
                        candidate.Scanned = false;
                        candidate.Suspect = false;
                        candidate.FirstTick = now;
                        KickScanner();
                    }
                    candidate.SeenTick = now;
                    return candidate.Holder || Pending(candidate, now);
                }
                candidates[pid] = new Candidate { Creation = creation, Name = name, FirstTick = now, SeenTick = now };
                KickScanner();
                return true;
            }
        }

        // 第一次核对还没出结果 或者刚看到一次持有还在等第二次确认
        private static bool Pending(Candidate candidate, int now)
        {
            return !candidate.Skipped && (!candidate.Scanned || candidate.Suspect) && now - candidate.FirstTick <= PendingLimitMs;
        }

        // 还有刚见到的进程在等核对结果 开局汇总据此推迟到核对完
        internal static bool AnyPending()
        {
            lock (sync)
            {
                int now = Environment.TickCount;
                foreach (Candidate candidate in candidates.Values)
                {
                    if (!candidate.Holder && now - candidate.SeenTick <= CandidateTtlMs && Pending(candidate, now)) return true;
                }
                return false;
            }
        }

        private static void KickScanner()
        {
            if (scanner == null)
            {
                scanner = new Thread(ScanLoop)
                {
                    IsBackground = true,
                    Name = "Pavise input device holders",
                    Priority = ThreadPriority.BelowNormal
                };
                scanner.Start();
            }
            wake.Set();
        }

        internal static string[] HolderNamesForDisplay()
        {
            lock (sync)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int now = Environment.TickCount;
                foreach (Candidate candidate in candidates.Values)
                {
                    if (candidate.Holder && now - candidate.SeenTick <= CandidateTtlMs
                        && !string.IsNullOrEmpty(candidate.Name)) names.Add(candidate.Name);
                }
                var result = new string[names.Count];
                names.CopyTo(result);
                Array.Sort(result, StringComparer.OrdinalIgnoreCase);
                return result;
            }
        }

        private static void ScanLoop()
        {
            int lastFullScan = Environment.TickCount - RescanMs;
            int lastScan = Environment.TickCount - MinWakeScanGapMs;
            int waitMs = RescanMs;
            using (var worker = new NativeHid.Worker())
            {
                while (true)
                {
                    if (wake.WaitOne(waitMs))
                    {
                        int elapsed = Environment.TickCount - lastScan;
                        Thread.Sleep(Math.Min(MinWakeScanGapMs, Math.Max(ScanDebounceMs, MinWakeScanGapMs - elapsed)));
                    }
                    var batch = new Dictionary<int, long>();
                    lock (sync)
                    {
                        int now = Environment.TickCount;
                        bool idle = now - lastCallTick > IdleExitMs;
                        bool full = now - lastFullScan >= RescanMs;
                        var expired = new List<int>();
                        foreach (KeyValuePair<int, Candidate> entry in candidates)
                        {
                            Candidate candidate = entry.Value;
                            // 确认过的驱动程序不再重查 设备休眠或重新插拔时它会暂时关掉句柄 这时撤销放行它就没法及时连回设备
                            if (candidate.Holder)
                            {
                                if (now - candidate.SeenTick > HolderTtlMs) expired.Add(entry.Key);
                                continue;
                            }
                            // 对局结束后只留下确认过的驱动程序 其余的下一局重新核对
                            if (idle)
                            {
                                expired.Add(entry.Key);
                                continue;
                            }
                            // 同一进程偶尔离开后台候选列表不能重置失败次数
                            if (candidate.Skipped) continue;
                            if (now - candidate.SeenTick > CandidateTtlMs && candidate.UnreadableAttempts == 0)
                                expired.Add(entry.Key);
                            else if (now - candidate.SeenTick <= CandidateTtlMs && (full || !candidate.Scanned || candidate.Suspect))
                                batch.Add(entry.Key, candidate.Creation);
                        }
                        foreach (int pid in expired) candidates.Remove(pid);
                        if (idle)
                        {
                            scanner = null;
                            lastBatchFailure = HidProbeResult.Absent;
                            return;
                        }
                        lastScan = now;
                        if (full) lastFullScan = now;
                    }
                    if (batch.Count > 0 && ScanBatch(worker, batch))
                    {
                        waitMs = ConfirmDelayMs;
                    }
                    else
                    {
                        int untilFull = RescanMs - (Environment.TickCount - lastFullScan);
                        waitMs = Math.Min(RescanMs, Math.Max(1000, untilFull));
                    }
                }
            }
        }

        internal static bool ScanBatch(IHidProbeWorker worker, Dictionary<int, long> batch)
        {
            HidProbeResult status = worker.Prepare();
            var results = new Dictionary<int, HidProbeResult>();
            if (status == HidProbeResult.Absent)
            {
                foreach (KeyValuePair<int, long> entry in batch)
                {
                    if (!NeedsProbe(entry.Key, entry.Value)) continue;
                    HidProbeResult result = worker.Probe(entry.Key, entry.Value);
                    if (result == HidProbeResult.InterfaceUnreadable)
                    {
                        status = result;
                        continue;
                    }
                    if (result == HidProbeResult.NoInterface || result == HidProbeResult.WorkerUnavailable)
                    {
                        status = result;
                        break;
                    }
                    results.Add(entry.Key, result);
                }
            }
            ReportBatch(status);
            return Publish(batch, results, status == HidProbeResult.NoInterface);
        }

        private static bool NeedsProbe(int pid, long creation)
        {
            lock (sync)
            {
                Candidate current;
                return candidates.TryGetValue(pid, out current) && current.Creation == creation && !current.Skipped && !current.Holder;
            }
        }

        private static void ReportBatch(HidProbeResult status)
        {
            if (lastBatchFailure == status) return;
            lastBatchFailure = status;
            if (status == HidProbeResult.NoInterface) Logger.Log(() => Lang.T("log.inputdeviceholders.nointerface"));
            else if (status == HidProbeResult.InterfaceUnreadable) Logger.Warn(Lang.T("log.inputdeviceholders.interfacefailure"));
            else if (status == HidProbeResult.WorkerUnavailable) Logger.Warn(Lang.T("log.inputdeviceholders.workerfailure"));
        }

        private static bool Publish(Dictionary<int, long> batch, Dictionary<int, HidProbeResult> results, bool noInterface)
        {
            var added = new List<string>();
            var unreadable = new List<string>();
            var timedOut = new List<string>();
            bool pendingConfirm = false;
            lock (sync)
            {
                foreach (KeyValuePair<int, long> identity in batch)
                {
                    Candidate candidate;
                    HidProbeResult result;
                    // 确认过的驱动程序保持放行 后来的任何结果都不再改它
                    if (!candidates.TryGetValue(identity.Key, out candidate) || candidate.Creation != identity.Value
                        || candidate.Skipped || candidate.Holder) continue;
                    if (!results.TryGetValue(identity.Key, out result))
                    {
                        if (!noInterface) continue;
                        result = HidProbeResult.Absent;
                    }
                    else if (noInterface && result == HidProbeResult.Holding)
                        result = HidProbeResult.Absent;
                    string name = candidate.Name + " (" + identity.Key + ")";
                    candidate.Scanned = true;
                    switch (result)
                    {
                        case HidProbeResult.Holding:
                            // 两次观测都持有私有接口才放行 避免把短暂枚举识别成驻留程序
                            if (candidate.Suspect)
                            {
                                candidate.Holder = true;
                                candidate.Suspect = false;
                                added.Add(name);
                            }
                            else
                            {
                                candidate.Suspect = true;
                                pendingConfirm = true;
                            }
                            break;
                        case HidProbeResult.Absent:
                            candidate.Suspect = false;
                            break;
                        case HidProbeResult.ProcessUnreadable:
                            if (candidate.UnreadableAttempts == 0) unreadable.Add(name);
                            candidate.UnreadableAttempts++;
                            candidate.Suspect = false;
                            if (candidate.UnreadableAttempts >= MaxUnreadableAttempts) candidate.Skipped = true;
                            break;
                        case HidProbeResult.Timeout:
                            candidate.Skipped = true;
                            candidate.Suspect = false;
                            timedOut.Add(name);
                            break;
                    }
                }
            }
            if (added.Count > 0) Logger.Log(() => Lang.T("log.inputdeviceholders.1") + string.Join(" ", added));
            if (unreadable.Count > 0) Logger.Warn(Lang.F("log.inputdeviceholders.unreadable", string.Join(" ", unreadable)));
            if (timedOut.Count > 0) Logger.Warn(Lang.F("log.inputdeviceholders.timeout", string.Join(" ", timedOut)));
            return pendingConfirm;
        }
    }
}
