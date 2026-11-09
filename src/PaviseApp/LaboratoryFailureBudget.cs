using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 调用方需持有所属控制器的锁；只有执行失败才消耗预算。
    internal sealed class LaboratoryFailureBudget
    {
        private sealed class Entry { internal int Failures; internal long Due; }
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private long session;
        internal bool BeginSession(long value)
        {
            if (value <= session) return false;
            session = value; entries.Clear(); return true;
        }
        internal bool CanAttempt(string identity, long now, int maximum)
        {
            Entry entry;
            return !entries.TryGetValue(identity, out entry) || entry.Failures < maximum && now >= entry.Due;
        }
        internal bool Exhausted(string identity, int maximum)
        { Entry entry; return entries.TryGetValue(identity, out entry) && entry.Failures >= maximum; }
        internal void ReportRecovery(string identity, int maximum)
        {
            Entry entry;
            if (entries.TryGetValue(identity, out entry))
                RecoveryAttemptTrace.FailureSchedule(entry.Failures, maximum, entry.Due);
        }
        internal void Failed(string identity, long completedAt, int maximum = 3, string owner = "session")
        {
            Entry entry;
            if (!entries.TryGetValue(identity, out entry)) entries[identity] = entry = new Entry();
            int previous = entry.Failures;
            entry.Failures = Math.Min(maximum, entry.Failures + 1);
            entry.Due = completedAt + TimeSpan.FromSeconds(entry.Failures == 1 ? 30 : 60).Ticks;
            if (entry.Failures != previous && (entry.Failures == 1 || entry.Failures == maximum))
                Logger.Warn("实验室 " + owner + " 动作=" + identity + " 会话=" + session
                    + " 失败详情=" + entry.Failures + "/" + maximum
                    + (entry.Failures == maximum ? "；预算已耗尽，保留恢复所有权" : "；等待退避"));
        }
        internal void Neutral(string identity) { entries.Remove(identity); }
    }
}
