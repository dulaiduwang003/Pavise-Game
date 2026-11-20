using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PaviseApp
{
    // 只合并诊断输出 不参与调度状态 写入资格 失败预算或恢复判定
    internal sealed class SchedulingDiagnosticLog
    {
        private sealed class Entry
        {
            internal string State;
            internal long Next, Seen, Suppressed;
        }

        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private string scope;
        private string latestState;
        private long stableSince;
        private bool stableTransitionPending;
        private static readonly Regex ChangingFacts = new Regex(
            @"(至少等待)\d+(秒)|(线程=)\d+(?::\d+)?|(下次间隔毫秒=)\d+|(连续身份确认=)\d+"
            + @"|(已核验线程属性（[^）]*）=)\d+|(本轮已确认退出工作成员=)\d+"
            + @"|((?:耗时|等待|间隔)(?:毫秒|秒)?=)\d+(?:\.\d+)?"
            + @"|((?:tid|birth|mask|captured|applied|expected|group|expectedGroup)=)(?:0x[0-9A-Fa-f]+|\d+)"
            // 英文日志里同一批字段的写法 和上面的中文分支逐项对应 第 10 组是英文的至少等待 后缀在第 11 组
            + @"|(in at least )\d+( s)|(thread=)\d+(?::\d+)?|(consecutiveIdentityConfirmations=)\d+|(exitedWorkers=)\d+",
            RegexOptions.CultureInvariant);

        internal static string StableReason(string reason)
        {
            // 只归一化已知观测字段 保留错误码 异常类型和失败阶段 不能把不同失败并成成功
            return ChangingFacts.Replace(reason ?? "", delegate(Match match)
            {
                for (int index = 1; index < match.Groups.Count; index++)
                    if (match.Groups[index].Success) return match.Groups[index].Value + "#" + (index == 1 ? "秒" : index == 10 ? " s" : "");
                return match.Value;
            });
        }

        internal bool Admit(string owner, string state, string reason, long now, long interval, out string summary)
        {
            lock (gate)
            {
                summary = "";
                if (!string.Equals(scope, owner, StringComparison.Ordinal))
                {
                    summary = DrainLocked();
                    entries.Clear(); scope = owner;
                    latestState = null; stableTransitionPending = false;
                }
                if (latestState != state)
                {
                    stableTransitionPending = latestState != null;
                    latestState = state; stableSince = now;
                }
                // 短暂抖动合并 但重新持续核验的健康状态不能被十分钟心跳遮住
                bool stableRecovery = stableTransitionPending && state == "active"
                    && now - stableSince >= TimeSpan.FromSeconds(30).Ticks;
                string key = state + "|" + StableReason(reason);
                Entry entry;
                if (entries.TryGetValue(key, out entry))
                {
                    entry.Seen = now;
                    if (now < entry.Next && !stableRecovery)
                    {
                        entry.Suppressed++;
                        return false;
                    }
                    if (entry.Suppressed != 0)
                        summary += Logger.Text("；此前同类状态合并=", "; mergedEarlierSameState=") + entry.Suppressed + Logger.Text("次（不代表写入或恢复成功）", " times (does not mean writes or restores succeeded)");
                    entry.Suppressed = 0;
                }
                else
                {
                    // 极端多原因时只淘汰最久未观察的窗口 不整体清空后重新刷出全部事件
                    if (entries.Count >= 128)
                    {
                        string oldest = entries.OrderBy(pair => pair.Value.Seen).First().Key;
                        Entry retired = entries[oldest];
                        if (retired.Suppressed != 0) summary += Logger.Text("；旧诊断窗口合并=", "; mergedRetiredWindow=") + retired.Suppressed + Logger.Text("次", " times");
                        entries.Remove(oldest);
                    }
                    entries.Add(key, entry = new Entry { State = state, Seen = now });
                }
                if (stableRecovery) summary += Logger.Text("；状态变化30秒后再次核验，记录当前结果", "; re-verified 30 s after the state change; current result recorded");
                stableTransitionPending = false;
                entry.Next = now + interval;
                return true;
            }
        }

        internal string Finish()
        {
            lock (gate)
            {
                string summary = DrainLocked();
                entries.Clear(); scope = null; latestState = null; stableTransitionPending = false;
                return summary;
            }
        }

        private string DrainLocked()
        {
            var counts = entries.Values.Where(entry => entry.Suppressed != 0)
                .GroupBy(entry => entry.State).Select(group => group.Key + "=" + group.Sum(entry => entry.Suppressed)).ToArray();
            return counts.Length == 0 ? "" : Logger.Text("；前一诊断阶段重复状态合并次数：", "; repeated states merged in the previous diagnostic phase: ") + string.Join(Logger.Text("，", ", "), counts)
                + Logger.Text("（仅统计日志，非成功次数）", " (log counts only, not success counts)");
        }
    }
}
