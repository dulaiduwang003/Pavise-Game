using System;
using System.Diagnostics;

namespace PaviseApp
{
    // 页面销毁只关闭句柄；真实 API 失败账按运行会话保留，成功和预热均不补次数。
    internal sealed class OverviewGpuSamplingBudget
    {
        internal static readonly OverviewGpuSamplingBudget Shared = new OverviewGpuSamplingBudget(
            delegate { return PerformanceTuning.RecoveryCycleSerial; },
            delegate { return Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency); }, Logger.Warn);

        internal readonly object Sync = new object();
        private readonly Func<long> readCycle;
        private readonly Func<double> clockMilliseconds;
        private readonly Action<string> log;
        private long cycle = -1;
        private int initializationFailures, samplingFailures;
        private double nextAttempt;
        private string lastError;

        internal OverviewGpuSamplingBudget(Func<long> readCycle, Func<double> clockMilliseconds, Action<string> log)
        { this.readCycle = readCycle; this.clockMilliseconds = clockMilliseconds; this.log = log; }

        internal bool CanSample(bool initializing, out string detail, out bool stopped)
        {
            lock (Sync)
            {
                long current = readCycle();
                if (current > cycle)
                {
                    cycle = current;
                    initializationFailures = samplingFailures = 0;
                    nextAttempt = 0;
                    lastError = null;
                }
                stopped = samplingFailures >= 3 || (initializing && initializationFailures >= 3);
                detail = lastError;
                if (stopped)
                {
                    detail = PerformancePolicy.Text("本次运行会话 GPU 占用读取已暂停：", "GPU usage sampling paused for this session: ") + lastError;
                    return false;
                }
                return clockMilliseconds() >= nextAttempt;
            }
        }

        internal void Failed(bool initializing, string stage, string error)
        {
            lock (Sync)
            {
                int failures = initializing ? ++initializationFailures : ++samplingFailures;
                lastError = stage + " " + error + " (" + failures + "/3)";
                nextAttempt = clockMilliseconds() + (failures == 1 ? 2000 : 4000);
                if (log != null) log("概览 GPU 占用读取失败，阶段=" + stage + " 错误=" + error
                    + " 次数=" + failures + "/3 会话=" + cycle
                    + (failures >= 3 ? "；本次运行会话暂停该阶段读取" : "；等待后续采样重试"));
            }
        }
    }
}
