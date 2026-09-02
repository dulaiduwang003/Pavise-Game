// @author bdth 2074055628@qq.com
// 文件用途 疑似恶意进程判定 按连续快照的 CPU 增量识别长时间吃满核心的无窗口后台进程
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    // 只认两类证据 一是被压到后台核的进程跑出了超过后台核数的 CPU 量 说明它自己改回了亲和
    //   二是没有可见窗口 不在系统目录和 Program Files 里的进程 连续两分钟占用四分之一以上的逻辑核
    //   随机名进程第二类门槛减半 每个进程名一生只报一次 报的是"可能" 由用户核对文件位置和杀毒结果
    //   这里不开句柄不碰注册表 输入全来自主循环已有的进程快照 纯判定 便于回归
    internal sealed class RogueProcessWatch
    {
        internal sealed class Sample
        {
            public int Pid;
            public long Creation;
            public long Cpu;
            public string Name;
            public string Path;
            public bool Confined;
            public bool Visible;
            public bool Trusted;
        }

        internal sealed class Verdict
        {
            public int Pid;
            public string Name;
            public string Path;
            public double Cores;
            public bool Escaped;
            public bool RandomName;
        }

        internal const long HogHoldTicks = TimeSpan.TicksPerSecond * 120;
        internal const long EscapeHoldTicks = TimeSpan.TicksPerSecond * 60;
        internal const long MinIntervalTicks = TimeSpan.TicksPerSecond * 2;
        internal const long StaleTicks = TimeSpan.TicksPerMinute * 5;
        internal const double EscapeSlackCores = 0.75;

        private sealed class Track
        {
            public long Cpu;
            public long Stamp;
            public long Creation;
            public long HotSince;
            public long EscapeSince;
        }

        private readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        private readonly HashSet<string> reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static double HogCores(int logicalCpus)
        {
            return Math.Max(2.0, Math.Ceiling(logicalCpus / 4.0));
        }

        internal static double RandomNameCores(int logicalCpus)
        {
            return Math.Max(1.5, HogCores(logicalCpus) / 2.0);
        }

        // 随机名 只含字母数字 且要么数字大小写三样齐 要么大小写来回切换四次以上
        //   yIgaJZfC 1TNnr Hnvw40 这类命中 OneDrive WerFault SDXHelper 这类不命中
        internal static bool LooksRandom(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (name.Length < 4 || name.Length > 12) return false;
            bool digit = false, upper = false, lower = false;
            int switches = 0;
            int lastCase = 0;
            foreach (char c in name)
            {
                if (c >= '0' && c <= '9') { digit = true; continue; }
                bool isUpper = c >= 'A' && c <= 'Z';
                bool isLower = c >= 'a' && c <= 'z';
                if (!isUpper && !isLower) return false;
                if (isUpper) upper = true; else lower = true;
                int thisCase = isUpper ? 1 : 2;
                if (lastCase != 0 && lastCase != thisCase) switches++;
                lastCase = thisCase;
            }
            return (digit && upper && lower) || switches >= 4;
        }

        internal List<Verdict> Step(IList<Sample> samples, long now, int logicalCpus, int confinedCpus)
        {
            var verdicts = new List<Verdict>();
            var seen = new HashSet<int>();
            foreach (Sample s in samples)
            {
                seen.Add(s.Pid);
                Track t;
                if (!tracks.TryGetValue(s.Pid, out t) || t.Creation != s.Creation)
                {
                    tracks[s.Pid] = new Track { Cpu = s.Cpu, Stamp = now, Creation = s.Creation };
                    continue;
                }
                long wall = now - t.Stamp;
                if (wall < MinIntervalTicks) continue;
                double cores = s.Cpu > t.Cpu ? (double)(s.Cpu - t.Cpu) / wall : 0.0;
                t.Cpu = s.Cpu;
                t.Stamp = now;

                bool partitioned = confinedCpus > 0 && confinedCpus < logicalCpus;
                bool escaping = s.Confined && partitioned && cores >= confinedCpus + EscapeSlackCores;
                if (!escaping) t.EscapeSince = 0;
                else if (t.EscapeSince == 0) t.EscapeSince = now;

                bool random = LooksRandom(s.Name);
                double floor = random ? RandomNameCores(logicalCpus) : HogCores(logicalCpus);
                bool hot = !s.Visible && !s.Trusted && cores >= floor;
                if (!hot) t.HotSince = 0;
                else if (t.HotSince == 0) t.HotSince = now;

                bool escaped = t.EscapeSince != 0 && now - t.EscapeSince >= EscapeHoldTicks;
                bool hog = t.HotSince != 0 && now - t.HotSince >= HogHoldTicks;
                if ((escaped || hog) && !string.IsNullOrEmpty(s.Name) && reported.Add(s.Name))
                    verdicts.Add(new Verdict
                    {
                        Pid = s.Pid, Name = s.Name, Path = s.Path, Cores = cores,
                        Escaped = escaped, RandomName = random
                    });
            }
            var stale = new List<int>();
            foreach (KeyValuePair<int, Track> kv in tracks)
                if (!seen.Contains(kv.Key) && now - kv.Value.Stamp >= StaleTicks) stale.Add(kv.Key);
            foreach (int pid in stale) tracks.Remove(pid);
            return verdicts;
        }

        internal void Reset()
        {
            tracks.Clear();
        }

        // 提醒台账 name|utcTicks 分号分隔 一天内同名不再提醒 写回时顺手把过期条目丢掉
        internal static bool LedgerRecent(string ledger, string name, long now, long repeatTicks)
        {
            if (string.IsNullOrEmpty(ledger) || string.IsNullOrEmpty(name)) return false;
            foreach (string rec in ledger.Split(';'))
            {
                int bar = rec.LastIndexOf('|');
                if (bar <= 0) continue;
                long stamp;
                if (!long.TryParse(rec.Substring(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out stamp)) continue;
                if (string.Equals(rec.Substring(0, bar), name, StringComparison.OrdinalIgnoreCase)
                    && now - stamp < repeatTicks) return true;
            }
            return false;
        }

        internal static string LedgerAppend(string ledger, string name, long now, long repeatTicks)
        {
            var kept = new List<string>();
            if (!string.IsNullOrEmpty(ledger))
                foreach (string rec in ledger.Split(';'))
                {
                    int bar = rec.LastIndexOf('|');
                    if (bar <= 0) continue;
                    long stamp;
                    if (!long.TryParse(rec.Substring(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out stamp)) continue;
                    if (now - stamp >= repeatTicks) continue;
                    if (string.Equals(rec.Substring(0, bar), name, StringComparison.OrdinalIgnoreCase)) continue;
                    kept.Add(rec);
                }
            kept.Add(name.Replace(';', '_').Replace('|', '_') + "|" + now.ToString(CultureInfo.InvariantCulture));
            return string.Join(";", kept.ToArray());
        }
    }
}
