// @author bdth 2074055628@qq.com
// 文件用途 笔记本对局中把共享功耗预算从 CPU 让给 GPU 一局只决定一次 验不过就退回
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal enum YieldStage { Idle = 0, Observing = 1, Engaged = 2, Held = 3, Skipped = 4, Reverted = 5 }

    internal enum YieldAction { None = 0, Engage = 1, Keep = 2, Revert = 3 }

    // 笔记本上 CPU 和 GPU 吃同一份功耗与散热预算 GPU 瓶颈时 CPU 多烧的每一瓦都是 GPU 少拿的
    //   固件的 Dynamic Boost / DTT / SmartShift 已经在毫秒级做这件事 我们不跟它抢方向盘
    //   这里一局只做一次决定 做完验一次 验不过立刻退回并记住 跟"对局禁用处理器空闲"同一套路数
    //
    // 为什么动 EPP 不动 ProcThrottleMin
    //   ProcThrottleMin 只决定"能不能降" EPP 决定"愿不愿意降"
    //   地板放开了但 EPP 还是 0 照样强烈偏性能 省不出多少 所以 EPP 才是真正的杠杆
    //
    // 为什么必须能读到瓦数才参与
    //   验证要回答"预算真的让出来了吗" 只看 GPU 撞墙命中率会被两头骗
    //   GPU 拿到更高上限后可能照样撞墙 而把 CPU 压成瓶颈会让 GPU 利用率掉 命中率也跟着掉
    //   看起来像成功 实际是失败 所以没有 RAPL 读数就不参与 不猜
    internal sealed class PowerBudgetYield
    {
        // 本机台架结果 i7-9750H Coffee Lake-H 笔记本 6 线程 40% 占空比负载
        //   EPP 取值范围 0~100 逐档写入读回全部吻合 说明写是写进去了
        //   EPP        0      32     48     84     100
        //   封装功耗   47.57  47.02  49.01  48.54  47.40 W   无趋势 正负 1.5W 是噪声
        //   实际频率   153.8  153.8  153.8  153.9  153.8 %   跨全量程纹丝不动
        //   也就是说这台机器上 OS 侧的 EPP 根本没作用到硬件 这个杠杆是死的
        // 所以本功能默认关闭 而且在这类机器上验证必然不通过 会自动退回并熔断 那是对的行为
        //   2019 年的 Coffee Lake-H 不代表全部 Alder Lake 之后的 HWP 平台要重新量过才知道
        //   另外这份负载是占空比脉冲 对 EPP 未必是好探针 换稳态部分负载值得再测一次
        internal const double MinGpuUtilToYield = 90.0;
        internal const double MaxCpuUtilToYield = 65.0;
        internal const long ObserveTicks = TimeSpan.TicksPerSecond * 20;
        internal const long VerifyTicks = TimeSpan.TicksPerSecond * 15;
        internal const uint YieldEpp = 48;          // 0 最偏性能 255 最偏能效 48 仍然偏性能
        internal const double MinWattsFreed = 3.0;
        internal const double MaxGpuUtilDrop = 3.0;
        internal const int MinSamples = 4;

        private const string FuseKey = "PowerYieldFuse";

        // 参与的硬门槛 少一条都不参与 判错的代价落在对局里 宁可不做
        internal static bool Eligible(bool laptop, bool onAc, bool competitive,
            bool managedPlanActive, bool wattsReadable, bool fused)
        {
            if (fused) return false;               // 这台机器验过不吃这套
            if (!laptop) return false;             // 台式机没有共享预算可让
            if (!onAc) return false;               // 电池上用户要的是明确的续航或性能 不掺和
            if (!competitive) return false;        // 只有专注档写的是最激进的 CPU 侧设置
            if (!managedPlanActive) return false;  // 不碰用户自己选的电源方案
            if (!wattsReadable) return false;      // 验不了就不做
            return true;
        }

        // GPU 吃满而 CPU 有余量 才说明预算花错了地方
        internal static bool WorthYielding(double gpuUtil, double cpuUtil)
        {
            return gpuUtil >= MinGpuUtilToYield && cpuUtil <= MaxCpuUtilToYield;
        }

        // 让路之后必须两条同时成立 封装功耗真降了 而且 GPU 没被拖下水
        internal static bool VerifyHold(double pkgBefore, double pkgAfter,
            double gpuBefore, double gpuAfter)
        {
            if (pkgBefore <= 0 || pkgAfter <= 0) return false;
            if (pkgBefore - pkgAfter < MinWattsFreed) return false;
            if (gpuBefore - gpuAfter > MaxGpuUtilDrop) return false;
            return true;
        }

        public static bool Fused
        {
            get { return Settings.Load(FuseKey, false); }
        }

        public static void ClearFuse() { if (Fused) Settings.Save(FuseKey, false); }

        // 读不到瓦数的那几次不能计进分母 否则封装功耗均值被稀释成偏低
        //   基线偏低 后面 VerifyHold 看到的降幅就偏小 会把本来有收益的机器判成没收益并熔断
        internal const int GiveUpSampleMultiple = 3;

        private YieldStage stage = YieldStage.Idle;
        private long stageAt;
        private int samples;
        private int pkgSamples;
        private double gpuSum, cpuSum, pkgSum;
        private double baseGpu, basePkg;

        public YieldStage Stage { get { return stage; } }
        public double BaselineWatts { get { return basePkg; } }
        public double BaselineGpuUtil { get { return baseGpu; } }

        public void Begin(long now, bool eligible)
        {
            Reset();
            stage = eligible ? YieldStage.Observing : YieldStage.Skipped;
            stageAt = now;
        }

        public void End() { Reset(); }

        private void Reset()
        {
            stage = YieldStage.Idle; stageAt = 0; samples = 0; pkgSamples = 0;
            gpuSum = cpuSum = pkgSum = 0; baseGpu = basePkg = 0;
        }

        // 喂一次采样 返回这一刻该做什么 负数的瓦数表示这次没读到 只是不计入均值
        public YieldAction Advance(long now, double gpuUtil, double cpuUtil, double pkgWatts)
        {
            if (stage != YieldStage.Observing && stage != YieldStage.Engaged) return YieldAction.None;
            samples++;
            gpuSum += gpuUtil; cpuSum += cpuUtil;
            if (pkgWatts > 0) { pkgSum += pkgWatts; pkgSamples++; }

            long span = now - stageAt;
            if (stage == YieldStage.Observing)
            {
                if (span < ObserveTicks || samples < MinSamples) return YieldAction.None;
                // 瓦数读得太少就别下结论 一直读不到也别干等 攒够三倍样本还不够就放弃这局
                if (pkgSamples < MinSamples)
                {
                    if (samples < MinSamples * GiveUpSampleMultiple) return YieldAction.None;
                    stage = YieldStage.Skipped;
                    return YieldAction.None;
                }
                double gpu = gpuSum / samples, cpu = cpuSum / samples;
                double pkg = pkgSum / pkgSamples;
                if (!WorthYielding(gpu, cpu) || pkg <= 0)
                {
                    stage = YieldStage.Skipped;
                    return YieldAction.None;
                }
                baseGpu = gpu; basePkg = pkg;
                stage = YieldStage.Engaged; stageAt = now;
                samples = 0; pkgSamples = 0; gpuSum = cpuSum = pkgSum = 0;
                return YieldAction.Engage;
            }

            if (span < VerifyTicks || samples < MinSamples) return YieldAction.None;
            // 验证期同理 读不到瓦数就不能判 但这里已经改过 EPP 攒不够样本必须退回而不是干等
            if (pkgSamples < MinSamples)
            {
                if (samples < MinSamples * GiveUpSampleMultiple) return YieldAction.None;
                stage = YieldStage.Reverted;
                return YieldAction.Revert;
            }
            double gpuNow = gpuSum / samples, pkgNow = pkgSum / pkgSamples;
            bool keep = VerifyHold(basePkg, pkgNow, baseGpu, gpuNow);
            stage = keep ? YieldStage.Held : YieldStage.Reverted;
            if (!keep) Settings.Save(FuseKey, true);
            return keep ? YieldAction.Keep : YieldAction.Revert;
        }
    }

    // 运行时 自带采样线程 只在对局期间活着 退场必还原 EPP
    //   采样放独立线程 不占扫描循环 每 2 秒一次 一局最多做一次决定
    internal static class PowerBudgetYieldRunner
    {
        internal const string EnabledKey = "GmPowerYield";
        internal const int SampleIntervalMs = 2000;
        internal const int GpuWindowMs = 500;

        private static readonly object gate = new object();
        private static readonly object operationGate = new object();
        private static Action mutationBegin;
        private static Action mutationEnd;
        private static Thread worker;
        private static volatile bool running;
        private static PowerBudgetYield state;
        private static int generation;
        private static bool stopInProgress;
        private static bool shutdownClosed;

        public static bool EnabledSetting { get { return Settings.Load(EnabledKey, false); } }

        public static void ConfigureMutationBoundary(Action begin, Action end)
        {
            lock (gate)
            {
                mutationBegin = begin;
                mutationEnd = end;
            }
        }

        private static void BeginMutation()
        {
            Action callback;
            lock (gate) callback = mutationBegin;
            if (callback != null) try { callback(); } catch { }
        }

        private static void EndMutation()
        {
            Action callback;
            lock (gate) callback = mutationEnd;
            if (callback != null) try { callback(); } catch { }
        }

        private static bool RunMutation(Func<bool> action)
        {
            BeginMutation();
            try { return action != null && action(); }
            catch { return false; }
            finally { EndMutation(); }
        }

        public static YieldStage Stage
        {
            get { lock (gate) return state == null ? YieldStage.Idle : state.Stage; }
        }

        // 开关取值由调用方给 对局中走冻结快照 逐游戏配置能覆盖全局
        public static void Start(bool enabled, bool competitive)
        {
            lock (gate)
            {
                if (shutdownClosed || stopInProgress || running
                    || worker != null && worker.IsAlive) return;
                if (!enabled) return;
                bool eligible = PowerBudgetYield.Eligible(
                    Native.HasSystemBattery(), Native.OnAcPower(), competitive,
                    PowerPlan.ManagedPlanIsActive, EnergyMeter.Available, PowerBudgetYield.Fused);
                if (!eligible) return;
                state = new PowerBudgetYield();
                state.Begin(DateTime.UtcNow.Ticks, true);
                running = true;
                int mine = ++generation;
                worker = new Thread(delegate () { Loop(mine); });
                worker.IsBackground = true;
                worker.Name = "Pavise.PowerYield";
                worker.Priority = ThreadPriority.BelowNormal;
                worker.Start();
                Logger.Log(Lang.T("log.poweryield.1"));
            }
        }

        public static bool Stop()
        {
            return StopCore(3000, false);
        }

        internal static bool CloseForShutdown(int timeoutMs)
        {
            return StopCore(timeoutMs, true);
        }

        private static bool StopCore(int timeoutMs, bool terminal)
        {
            // 不管这一局有没有起过采样线程 都要查一次 EPP 有没有还原
            //   上一局还原失败留下的残值 不能因为这一局没参与就漏掉
            if (timeoutMs < 0) return false;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Thread t;
            lock (gate)
            {
                if (terminal) shutdownClosed = true;
                stopInProgress = true;
                running = false;
                generation++;
                t = worker;
                if (state != null) { state.End(); state = null; }
            }
            // A failed join must retain the thread reference. Clearing it would
            // make a subsequent final stop falsely report that no worker exists.
            if (t != null)
            {
                try
                {
                    if (t == Thread.CurrentThread || !t.Join(RemainingStopMs(elapsed, timeoutMs))) return false;
                }
                catch { return false; }
            }
            if (Monitor.IsEntered(operationGate)
                || !Monitor.TryEnter(operationGate, RemainingStopMs(elapsed, timeoutMs))) return false;
            try
            {
                bool ok = true;
                if (PowerPlan.EppYielded)
                {
                    ok = RunMutation(PowerPlan.RestoreEpp);
                    Logger.Log(Lang.T(ok ? "log.poweryield.5" : "log.poweryield.6"));
                }
                lock (gate)
                {
                    if (object.ReferenceEquals(worker, t)) worker = null;
                    stopInProgress = false;
                }
                return ok;
            }
            catch { return false; }
            finally { Monitor.Exit(operationGate); }
        }

        private static int RemainingStopMs(System.Diagnostics.Stopwatch elapsed, int timeoutMs)
        {
            return (int)Math.Max(0L, timeoutMs - elapsed.ElapsedMilliseconds);
        }

        private static bool GenerationRunning(int mine)
        {
            lock (gate) return running && !shutdownClosed && mine == generation;
        }

        private static bool RunCurrentMutation(int mine, Func<bool> mutation)
        {
            lock (operationGate)
            {
                if (!GenerationRunning(mine)) return false;
                return RunMutation(mutation);
            }
        }

        private static void Loop(int mine)
        {
            var cpu = new CpuSaturation();
            EnergyMeter.Sample prev = EnergyMeter.Take();
            while (GenerationRunning(mine))
            {
                Thread.Sleep(SampleIntervalMs);
                if (!GenerationRunning(mine)) break;
                double gpu = SampleGpuUtil();
                double cpuPct = cpu.Sample() * 100.0;
                EnergyMeter.Sample now = EnergyMeter.Take();
                double watts = EnergyMeter.Watts(prev, now, EnergyRail.Package);
                if (now != null) prev = now;
                if (gpu < 0) continue;

                YieldAction action;
                lock (gate)
                {
                    if (!running || mine != generation || shutdownClosed || state == null) break;
                    action = state.Advance(DateTime.UtcNow.Ticks, gpu, cpuPct, watts);
                }
                if (action == YieldAction.Engage)
                {
                    bool ok = RunCurrentMutation(mine, delegate
                    {
                        return PowerPlan.TryYieldEpp(PowerBudgetYield.YieldEpp);
                    });
                    Logger.Log(Lang.F(ok ? "log.poweryield.2" : "log.poweryield.3",
                        PowerBudgetYield.YieldEpp.ToString(),
                        gpu.ToString("F0"), cpuPct.ToString("F0"), watts.ToString("F1")));
                    if (!ok) break;
                }
                else if (action == YieldAction.Revert)
                {
                    RunCurrentMutation(mine, PowerPlan.RestoreEpp);
                    Logger.Log(Lang.T("log.poweryield.4"));
                    break;
                }
                else if (action == YieldAction.Keep)
                {
                    Logger.Log(Lang.T("log.poweryield.7"));
                    break;
                }
            }
        }

#if PAVISE_SELFTEST
        internal static int StartShutdownWorkerForTest(Action<int> body)
        {
            lock (gate)
            {
                if (shutdownClosed || stopInProgress || running
                    || worker != null && worker.IsAlive) return -1;
                running = true;
                int mine = ++generation;
                worker = new Thread(delegate () { body(mine); });
                worker.IsBackground = true;
                worker.Start();
                return mine;
            }
        }

        internal static bool RunShutdownMutationForTest(int mine, Func<bool> action)
        {
            return RunCurrentMutation(mine, action);
        }

        internal static void ResetShutdownForTest()
        {
            lock (operationGate)
            lock (gate)
            {
                if (worker != null && worker.IsAlive)
                    throw new InvalidOperationException("Cannot reset a live isolated power-yield worker");
                worker = null;
                state = null;
                running = stopInProgress = shutdownClosed = false;
                generation++;
            }
        }
#endif

        private static double SampleGpuUtil()
        {
            try
            {
                Dictionary<int, double> byPid = GpuEvidence.Sample3D(1, GpuWindowMs, null);
                if (byPid == null) return -1;
                double sum = 0;
                foreach (KeyValuePair<int, double> kv in byPid) sum += kv.Value;
                return sum > 100.0 ? 100.0 : sum;
            }
            catch { return -1; }
        }
    }
}
