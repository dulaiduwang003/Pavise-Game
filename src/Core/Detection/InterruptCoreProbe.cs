// @author bdth 2074055628@qq.com
// 文件用途 会话内测量一次中断分布 找出值得让游戏躲开的核心

using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed class InterruptCoreProbe
    {
        public const int DefaultWindowMs = 30000;

        private readonly object gate = new object();
        private readonly int windowMs;
        private Thread worker;
        private long avoidMask;
        private bool started;

        public InterruptCoreProbe() : this(DefaultWindowMs) { }

        internal InterruptCoreProbe(int measureWindowMs)
        {
            windowMs = measureWindowMs > 0 ? measureWindowMs : DefaultWindowMs;
        }

        public ulong AvoidMask { get { return (ulong)Interlocked.Read(ref avoidMask); } }

        internal bool WaitForCompletion(int timeoutMs)
        {
            Thread t;
            lock (gate) t = worker;
            if (t == null) return true;
            return t.Join(timeoutMs);
        }

        public void Begin()
        {
            lock (gate)
            {
                if (started) return;
                started = true;
                if (CpuTopology.PhysicalCoreCount < CpuPartitionPolicy.InterruptAvoidMinPhysicalCores)
                {
                    Logger.Log("中断核规避：本机物理核 " + CpuTopology.PhysicalCoreCount
                        + " 个，少于 " + CpuPartitionPolicy.InterruptAvoidMinPhysicalCores
                        + " 个，让出一核的代价超过收益，跳过");
                    return;
                }
                worker = new Thread(Measure);
                worker.IsBackground = true;
                worker.Name = "PaviseIrqProbe";
                worker.Start();
            }
        }

        public void Reset()
        {
            lock (gate)
            {
                started = false;
                worker = null;
            }
            Interlocked.Exchange(ref avoidMask, 0);
        }

        private void Measure()
        {
            try
            {
                double[] rates = DpcSampler.MeasureInterruptRates(windowMs);
                if (rates == null)
                {
                    Logger.Log("中断核规避：处理器性能接口不可用，跳过");
                    return;
                }
                ulong[] cores = CpuTopology.PhysicalCoreMasks();
                ulong found = CpuPartitionPolicy.FindInterruptCore(
                    rates, cores, CpuTopology.PhysicalCoreCount);
                if (found == 0)
                {
                    Logger.Log("中断核规避：本机没有明显离群的中断核，游戏分区保持完整");
                    return;
                }
                ulong partition = CpuTopology.StrictBoostMask;
                if (partition != 0 && (partition & found) == 0)
                {
                    Logger.Log("中断核规避：最脏的核 0x" + found.ToString("X")
                        + " 本就不在游戏分区内，无需处理");
                    return;
                }
                Interlocked.Exchange(ref avoidMask, (long)found);
                Logger.Log("中断核规避：实测中断集中在核 0x" + found.ToString("X")
                    + "（占用 " + (CpuPartitionPolicy.CoreInterruptRate(rates, found) * 100.0).ToString("F2")
                    + "%），已将其移出游戏分区");
            }
            catch (Exception ex) { Logger.Log("中断核规避：测量失败 " + ex.Message); }
        }
    }
}
