// @author bdth 2074055628@qq.com
// 文件用途 重压后台绑核 对局中只把持续吃 CPU 的已隔离后台限定到最窄落点 空闲后台不碰
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class SqueezeCandidate
        {
            public int Pid;
            public long Creation;
            public string Name;
            public long Cpu;
        }

        // 默认关 标实验性 键名带 V2 不继承 1.x 的 GmSqueezeBg 那份默认开的旧值
        private const long SqueezeRefuseTicks = 30 * TimeSpan.TicksPerSecond;
        private volatile bool heavySqueezeOn;
        private readonly HeavySqueezeTracker squeezeTracker = new HeavySqueezeTracker();
        private bool squeezeUnsupportedLogged;

        public bool HeavySqueezeOn
        {
            get { return heavySqueezeOn; }
            set
            {
                heavySqueezeOn = value;
                Settings.Save(PolicyCatalog.KeyHeavySqueeze, value);
                RequestPolicyApply();
            }
        }

        private bool EffHeavySqueeze
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.HeavySqueeze : heavySqueezeOn;
            }
        }

        // 落点算法复用 CpuPartitionPolicy.SqueezeMask 五个物理核以下和多处理器组没有落点
        private static long squeezeSupportStamp;
        private static volatile bool squeezeSupportValue;

        // 策略页每 1.2 秒问一次 落点算法是纯算术但不必每次重算 5 秒内复用
        internal static bool HeavySqueezeSupported()
        {
            long now = DateTime.UtcNow.Ticks;
            long stamp = Interlocked.Read(ref squeezeSupportStamp);
            if (stamp != 0 && now >= stamp && now - stamp < 5 * TimeSpan.TicksPerSecond)
                return squeezeSupportValue;
            bool value;
            try { value = !CpuTopology.MultiGroup && CpuTopology.BackgroundSqueezeMask() != 0; }
            catch { value = false; }
            squeezeSupportValue = value;
            Interlocked.Exchange(ref squeezeSupportStamp, now);
            return value;
        }

        private void ResetHeavySqueeze()
        {
            squeezeTracker.Clear();
            squeezeUnsupportedLogged = false;
        }

        // 每轮 Sweep 之后调 候选只来自本轮通过保护边界且要求隔离的后台
        //   热度按快照时间戳推进 复用的快照不会把同一段 CPU 时间算两遍
        //   开关关着时只负责把已绑的放回去 不再采样
        private void ApplyHeavySqueeze(List<SqueezeCandidate> candidates, HashSet<int> live, long snapshotTicks)
        {
            if (!EffHeavySqueeze)
            {
                if (core.SqueezedCount(SuppressReason.Background) > 0)
                {
                    int released = core.ClearSqueezes(SuppressReason.Background);
                    if (released > 0)
                        Logger.Log(Lang.T("log.squeeze.5") + released + Lang.T("log.squeeze.6"));
                }
                if (squeezeTracker.Count > 0) squeezeTracker.Clear();
                return;
            }
            ulong mask = CpuTopology.MultiGroup ? 0 : CpuTopology.BackgroundSqueezeMask();
            if (mask == 0)
            {
                if (!squeezeUnsupportedLogged)
                {
                    squeezeUnsupportedLogged = true;
                    Logger.Log(Lang.T("log.squeeze.4"));
                }
                return;
            }
            foreach (SqueezeCandidate c in candidates)
            {
                bool hot = squeezeTracker.Observe(c.Pid, c.Creation, c.Cpu, snapshotTicks);
                if (hot && squeezeTracker.IsRefused(c.Pid, snapshotTicks)) continue;
                bool changed;
                bool ok = core.SetSqueeze(c.Pid, c.Creation, c.Name, hot ? mask : 0, out changed);
                if (!ok)
                {
                    // 亲和被拒或条目尚未落账 这个 pid 退避一段 不许每轮开句柄重试
                    if (hot) squeezeTracker.Refuse(c.Pid, snapshotTicks + SqueezeRefuseTicks);
                    continue;
                }
                if (!changed) continue;
                Logger.Log(Lang.T("log.squeeze.1") + c.Name + "(pid " + c.Pid + ") "
                    + (hot ? Lang.T("log.squeeze.2") + CpuTopology.DescribeMask(mask) : Lang.T("log.squeeze.3")));
            }
            squeezeTracker.Prune(live);
        }

#if PAVISE_SELFTEST
        internal bool ProbeEffHeavySqueeze { get { return EffHeavySqueeze; } }
#endif
    }
}
