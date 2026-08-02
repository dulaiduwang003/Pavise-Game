// @author bdth 2074055628@qq.com
// 文件用途 冻结资格判定 只放行长时间零占用且无可见窗口的后台进程

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 挂起是压制链条里唯一不可逆的动作，最凶险的失败不是冻错了谁，
    // 而是冻住一个正持有锁的进程——卡死的会是游戏本身，且表现为随机
    // 卡顿几乎无法归因。一个在 Idle 优先级下仍持续零占用的进程，几乎
    // 必然停在消息循环或等待态，不在临界区里，这就是本闸的全部依据。
    internal sealed class FreezeDwellTracker
    {
        // 低于此 CPU 占用（核数）视为静默
        internal const double QuietCores = 0.005;
        // 需要连续静默这么久才放行
        internal const int DwellSeconds = 30;
        private const long MinSampleTicks = TimeSpan.TicksPerSecond;

        private sealed class Sample
        {
            public string Name;
            public long Creation;
            public long Cpu;
            public long At;
            public long QuietSince;
        }

        private readonly Dictionary<int, Sample> samples = new Dictionary<int, Sample>();

        public bool Observe(int pid, string name, long creation, long cpu, long now)
        {
            Sample previous;
            if (!samples.TryGetValue(pid, out previous)
                || previous.Creation != creation
                || !string.Equals(previous.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                samples[pid] = new Sample
                {
                    Name = name,
                    Creation = creation,
                    Cpu = cpu,
                    At = now
                };
                return false;
            }

            long dt = now - previous.At;
            if (dt < MinSampleTicks) return Settled(previous, now);

            long dcpu = cpu - previous.Cpu;
            previous.Cpu = cpu;
            previous.At = now;
            // 计数器回退说明采样不可信，按"有活动"处理，宁可不冻
            if (dcpu < 0 || (double)dcpu / dt >= QuietCores)
            {
                previous.QuietSince = 0;
                return false;
            }
            if (previous.QuietSince == 0) previous.QuietSince = now;
            return Settled(previous, now);
        }

        private static bool Settled(Sample sample, long now)
        {
            return sample.QuietSince != 0
                && now - sample.QuietSince >= DwellSeconds * TimeSpan.TicksPerSecond;
        }

        public void Forget(int pid) { samples.Remove(pid); }

        public void Prune(HashSet<int> live)
        {
            var dead = new List<int>();
            foreach (int pid in samples.Keys) if (!live.Contains(pid)) dead.Add(pid);
            foreach (int pid in dead) samples.Remove(pid);
        }

        public void Clear() { samples.Clear(); }
    }
}
