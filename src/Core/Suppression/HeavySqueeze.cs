// @author bdth 2074055628@qq.com
// 文件用途 重压后台绑核的热度判定与落点校验 纯函数 不读硬件不碰进程
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 收益来自忙进程 代价来自闲进程 这是 2026-08-20 两组台架同一天给出的结论
    //   合成带宽杀手负载下把它挤到一个物理核 +75% 到 +126% 掩码越窄越好
    //   同一天真机无差别绑 128 个进程 -3.1% 最长帧翻 2.6 倍 只绑 6 个真忙的 +3.8%
    //   1.8.1.3 台架三场景按热度门控后全部转正 2.0 连门控版一起下架时没有重跑
    //   所以热度判定不是细节 是这条路成立的条件 门槛宁严勿宽
    internal static class HeavySqueezePolicy
    {
        // 连续 10 秒平均占用超过半个逻辑核才算热 08-20 真机 292 个进程里只有 6 个过这条线
        public const int HotUtilPercent = 50;
        public const int HotHoldSeconds = 10;
        // 降到一成以下并持续 30 秒才解绑 防止阈值附近横跳
        public const int ColdUtilPercent = 10;
        public const int ColdHoldSeconds = 30;
        // 两次快照间隔不足这个数不计算 复用的快照时间戳相同 直接跳过
        public const int MinSampleMs = 250;

        // 只在这几种情况下给出落点 其余一律 0 表示不绑
        //   多处理器组没有单掩码可写 进程自己设过 CPU Sets 或自己收过亲和 不替它做主
        //   落点必须完整落在进程原本允许的范围内 与原范围相同则绑了等于没绑
        public static ulong SqueezeTarget(ulong squeezeMask, ulong originalAffinity,
            uint[] originalCpuSets, ulong allMask, bool multiGroup)
        {
            if (multiGroup || squeezeMask == 0 || allMask == 0) return 0;
            if (originalCpuSets != null && originalCpuSets.Length > 0) return 0;
            ulong allowed = originalAffinity != 0 ? originalAffinity : allMask;
            if ((squeezeMask & allowed) != squeezeMask) return 0;
            if (squeezeMask == allowed) return 0;
            return squeezeMask;
        }

        // 后台条目的落点由热度给 反作弊条目的落点由 Tamer 在压制落地后直接给 两条路互不越界
        //   反作弊绑核限的是并行度不是时间片 扫描线程在落点上仍按自己的优先级满速跑
        //   这和把它压到 Idle 饿死扫描不是一回事 见 Apply 侧扫描安全构成的说明
        public static ulong DesiredAffinity(SuppressReason reasons, ulong squeezeAffinity,
            ulong originalAffinity, ulong allMask)
        {
            ulong original = originalAffinity != 0 ? originalAffinity : allMask;
            if (squeezeAffinity == 0) return original;
            return (reasons & (SuppressReason.AntiCheat | SuppressReason.Background)) != 0
                ? squeezeAffinity : original;
        }
    }

    // 逐进程的热度状态机 按 PID 加创建时间认身份 PID 复用即重新计时
    internal sealed class HeavySqueezeTracker
    {
        private sealed class Sample
        {
            public long Creation;
            public long LastTicks;
            public long LastCpu;
            public long HotSince;
            public long ColdSince;
            public bool Hot;
            public long RefusedUntil;
        }

        private readonly Dictionary<int, Sample> samples = new Dictionary<int, Sample>();
        private readonly object gate = new object();

        public int Count { get { lock (gate) return samples.Count; } }

        public bool IsHot(int pid)
        {
            lock (gate)
            {
                Sample s;
                return samples.TryGetValue(pid, out s) && s.Hot;
            }
        }

        // cpuTicks 是进程累计 CPU 时间 nowTicks 是这份快照的拍摄时刻
        //   第一次见到只记基线 同一份快照喂两次或间隔太短都不推进状态
        public bool Observe(int pid, long creation, long cpuTicks, long nowTicks)
        {
            lock (gate)
            {
                Sample s;
                if (!samples.TryGetValue(pid, out s) || s.Creation != creation)
                {
                    s = new Sample { Creation = creation, LastTicks = nowTicks, LastCpu = cpuTicks };
                    samples[pid] = s;
                    return false;
                }
                long wall = nowTicks - s.LastTicks;
                if (wall < HeavySqueezePolicy.MinSampleMs * TimeSpan.TicksPerMillisecond) return s.Hot;
                long cpu = cpuTicks - s.LastCpu;
                if (cpu < 0) cpu = 0;
                s.LastTicks = nowTicks;
                s.LastCpu = cpuTicks;
                double util = cpu * 100.0 / wall;
                if (!s.Hot)
                {
                    if (util >= HeavySqueezePolicy.HotUtilPercent)
                    {
                        if (s.HotSince == 0) s.HotSince = nowTicks;
                        if (nowTicks - s.HotSince >= HeavySqueezePolicy.HotHoldSeconds * TimeSpan.TicksPerSecond)
                        {
                            s.Hot = true;
                            s.ColdSince = 0;
                        }
                    }
                    else s.HotSince = 0;
                }
                else
                {
                    if (util < HeavySqueezePolicy.ColdUtilPercent)
                    {
                        if (s.ColdSince == 0) s.ColdSince = nowTicks;
                        if (nowTicks - s.ColdSince >= HeavySqueezePolicy.ColdHoldSeconds * TimeSpan.TicksPerSecond)
                        {
                            s.Hot = false;
                            s.HotSince = 0;
                        }
                    }
                    else s.ColdSince = 0;
                }
                return s.Hot;
            }
        }

        public void Forget(int pid)
        {
            lock (gate) samples.Remove(pid);
        }

        // 绑核被系统拒绝或条目还没落账 这个 pid 先歇一会 别每轮开句柄重试
        public void Refuse(int pid, long untilTicks)
        {
            lock (gate)
            {
                Sample s;
                if (samples.TryGetValue(pid, out s)) s.RefusedUntil = untilTicks;
            }
        }

        public bool IsRefused(int pid, long nowTicks)
        {
            lock (gate)
            {
                Sample s;
                return samples.TryGetValue(pid, out s) && s.RefusedUntil != 0 && nowTicks < s.RefusedUntil;
            }
        }

        // 本轮快照里已经不在的进程一并忘掉 PID 回收后由 Observe 的创建时间兜底
        public void Prune(HashSet<int> live)
        {
            if (live == null) return;
            lock (gate)
            {
                var gone = new List<int>();
                foreach (int pid in samples.Keys) if (!live.Contains(pid)) gone.Add(pid);
                foreach (int pid in gone) samples.Remove(pid);
            }
        }

        public void Clear()
        {
            lock (gate) samples.Clear();
        }
    }
}
