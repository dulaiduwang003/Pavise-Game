// @author bdth 2074055628@qq.com
// 文件用途 笔记本对局中把共享功耗预算从 CPU 让给 GPU 一局只决定一次 验不过就退回
using System;
using System.Threading;

namespace PaviseApp
{
    internal enum YieldStage { Idle = 0, Observing = 1, Engaged = 2, Held = 3, Skipped = 4, Reverted = 5 }

    internal enum YieldAction { None = 0, Engage = 1, Keep = 2, Revert = 3, Release = 4 }

    // 验证结论 只用于日志分流 Inconclusive 不熔断 下局可以再试
    internal enum YieldVerdict { None = 0, Kept = 1, NoGain = 2, GpuHarm = 3, Inconclusive = 4 }

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
    //
    // 频率代理 读不到瓦数的机器的降级验证
    //   EPP 释放功耗的机制就是频率 EPP 抬高→部分负载下 CPU 愿意跑更低频→功耗跟着频率立方走
    //   所以频率降没降是"预算让没让"的次一级证据 频率纹丝不动 = EPP 是死杠杆 熔断是对的
    //   降级验证多一个坑 频率同时受负载影响 验证窗和观察窗的 CPU 占用差太多就没法比
    //   这种情况退回但不熔断 那是场景变了 不是机器的错 下局再试
    //   频率代理的熔断单独记账 换了台有 EMI 的机器或修好了 EMI 驱动 瓦数路径不受牵连
    internal sealed class PowerBudgetYield
    {
        // 本机台架结果 i7-9750H Coffee Lake-H 笔记本 6 线程 40% 占空比负载
        //   EPP 取值范围 0~100 逐档写入读回全部吻合 说明写是写进去了
        //   EPP 档位 0 32 48 84 100
        //   封装功耗 47.57 47.02 49.01 48.54 47.40 W 无趋势 正负 1.5W 是噪声
        //   实际频率 153.8 153.8 153.8 153.9 153.8 % 跨全量程纹丝不动
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
        // 频率代理判据 相对降幅 3% 起判 负载漂移超过 10 个百分点就不下结论
        internal const double MinFreqDropShare = 0.03;
        internal const double MaxCpuUtilShift = 10.0;

        // 方向盘判据 验收通过后持续盯着 瓶颈移回 CPU 就把预算还回去
        //   释放阈值与参与阈值拉开迟滞带 90 让 80 收 防止在边界上来回打摆
        //   还回去之后 GPU 再吃满可以重新让 重新让要走完整的观察加验收 那就是天然冷却
        internal const double ReleaseGpuUtil = 80.0;
        internal const double ReleaseCpuUtil = 75.0;
        internal const long HoldWindowTicks = TimeSpan.TicksPerSecond * 30;
        internal const int MaxReengage = 3;

        private const string FuseKey = "PowerYieldFuse";
        private const string FreqFuseKey = "PowerYieldFreqFuse";

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
            return ValidUtil(gpuUtil) && ValidUtil(cpuUtil)
                && gpuUtil >= MinGpuUtilToYield && cpuUtil <= MaxCpuUtilToYield;
        }

        // 让路之后必须两条同时成立 封装功耗真降了 而且 GPU 没被拖下水
        internal static bool VerifyHold(double pkgBefore, double pkgAfter,
            double gpuBefore, double gpuAfter)
        {
            if (!ValidPositive(pkgBefore) || !ValidPositive(pkgAfter)
                || !ValidUtil(gpuBefore) || !ValidUtil(gpuAfter)) return false;
            if (pkgBefore - pkgAfter < MinWattsFreed) return false;
            if (gpuBefore - gpuAfter > MaxGpuUtilDrop) return false;
            return true;
        }

        public static bool Fused
        {
            get { return Settings.Load(FuseKey, false); }
        }

        public static bool FreqFused
        {
            get { return Settings.Load(FreqFuseKey, false); }
        }

        public static void ClearFuse()
        {
            if (Fused) Settings.Save(FuseKey, false);
            if (FreqFused) Settings.Save(FreqFuseKey, false);
        }

        // 读不到瓦数的那几次不能计进分母 否则封装功耗均值被稀释成偏低
        //   基线偏低 后面 VerifyHold 看到的降幅就偏小 会把本来有收益的机器判成没收益并熔断
        internal const int GiveUpSampleMultiple = 3;
        internal const long EvidenceMaxAgeTicks = 10 * TimeSpan.TicksPerSecond;
        private long lastEvidenceAt;

        private YieldStage stage = YieldStage.Idle;
        private long stageAt;
        private bool proxyMode;
        private int samples;
        private int pkgSamples;
        private int freqSamples;
        private double gpuSum, cpuSum, pkgSum, freqSum;
        private double baseGpu, basePkg, baseCpu, baseFreq;
        private YieldVerdict verdict;
        private int engagements;

        public YieldStage Stage { get { return stage; } }
        public YieldVerdict Verdict { get { return verdict; } }
        public double BaselineWatts { get { return basePkg; } }
        public double BaselineGpuUtil { get { return baseGpu; } }

        public void Begin(long now, bool eligible)
        {
            Begin(now, eligible, false);
        }

        public void Begin(long now, bool eligible, bool proxy)
        {
            Reset();
            proxyMode = proxy;
            stage = eligible ? YieldStage.Observing : YieldStage.Skipped;
            stageAt = now;
        }

        public void End() { Reset(); }

        private void Reset()
        {
            lastEvidenceAt = 0;
            stage = YieldStage.Idle; stageAt = 0; samples = 0; pkgSamples = 0; freqSamples = 0;
            gpuSum = cpuSum = pkgSum = freqSum = 0; baseGpu = basePkg = baseCpu = baseFreq = 0;
            proxyMode = false; verdict = YieldVerdict.None; engagements = 0;
        }

        private void EnterHold(long now)
        {
            stage = YieldStage.Held;
            verdict = YieldVerdict.Kept;
            stageAt = now;
            samples = 0; gpuSum = cpuSum = 0;
        }

        // 喂一次采样 返回这一刻该做什么 负数的瓦数或频率表示这次没读到 只是不计入均值
        public YieldAction Advance(long now, double gpuUtil, double cpuUtil, double pkgWatts)
        {
            return Advance(now, gpuUtil, cpuUtil, pkgWatts, -1);
        }

        public YieldAction Advance(long now, double gpuUtil, double cpuUtil,
            double pkgWatts, double freqPct)
        {
            if (stage != YieldStage.Observing && stage != YieldStage.Engaged
                && stage != YieldStage.Held) return YieldAction.None;
            bool validLoad = ValidUtil(gpuUtil) && ValidUtil(cpuUtil);
            bool validMeter = ValidPositive(proxyMode ? freqPct : pkgWatts);
            // 修改过 EPP 后，无证据不能继续持有。先检查间隙，再接纳新样本，
            // 避免长时间失联后的一个好读数洗掉失联记录。缺测不熔断硬件。
            if ((stage == YieldStage.Engaged || stage == YieldStage.Held)
                && (now < lastEvidenceAt || now - lastEvidenceAt >= EvidenceMaxAgeTicks))
            {
                stage = YieldStage.Reverted;
                verdict = YieldVerdict.Inconclusive;
                return YieldAction.Revert;
            }
            if (validLoad && (stage == YieldStage.Held || validMeter)) lastEvidenceAt = now;
            if (stage == YieldStage.Observing && now - stageAt >= ObserveTicks * GiveUpSampleMultiple)
            {
                stage = YieldStage.Skipped;
                verdict = YieldVerdict.Inconclusive;
                return YieldAction.None;
            }
            if (!validLoad) return YieldAction.None;
            // 方向盘的维持段 让出去之后持续盯 30 秒滚动窗口
            //   GPU 仍吃满且 CPU 有余量就按兵不动 瓶颈移回 CPU 就把预算还回去
            if (stage == YieldStage.Held)
            {
                samples++;
                gpuSum += gpuUtil; cpuSum += cpuUtil;
                if (now - stageAt < HoldWindowTicks || samples < MinSamples) return YieldAction.None;
                double gpuAvg = gpuSum / samples, cpuAvg = cpuSum / samples;
                samples = 0; gpuSum = cpuSum = 0; stageAt = now;
                if (gpuAvg >= ReleaseGpuUtil && cpuAvg <= ReleaseCpuUtil) return YieldAction.None;
                // 还回去 名额没用完就回到观察期 GPU 再吃满可以重新让
                //   重新让必须重走完整观察和验收 那就是天然的振荡冷却
                if (engagements >= MaxReengage)
                {
                    stage = YieldStage.Skipped;
                    return YieldAction.Release;
                }
                stage = YieldStage.Observing;
                pkgSamples = freqSamples = 0; pkgSum = freqSum = 0;
                return YieldAction.Release;
            }
            if (stage != YieldStage.Observing && stage != YieldStage.Engaged) return YieldAction.None;
            samples++;
            gpuSum += gpuUtil; cpuSum += cpuUtil;
            if (ValidPositive(pkgWatts)) { pkgSum += pkgWatts; pkgSamples++; }
            if (ValidPositive(freqPct)) { freqSum += freqPct; freqSamples++; }
            int meterSamples = proxyMode ? freqSamples : pkgSamples;

            long span = now - stageAt;
            if (stage == YieldStage.Observing)
            {
                if (span < ObserveTicks || samples < MinSamples) return YieldAction.None;
                // 证据读得太少就别下结论 一直读不到也别干等 攒够三倍样本还不够就放弃这局
                if (meterSamples < MinSamples)
                {
                    if (samples < MinSamples * GiveUpSampleMultiple) return YieldAction.None;
                    stage = YieldStage.Skipped;
                    return YieldAction.None;
                }
                if (now - lastEvidenceAt >= EvidenceMaxAgeTicks)
                {
                    stage = YieldStage.Skipped;
                    verdict = YieldVerdict.Inconclusive;
                    return YieldAction.None;
                }
                double gpu = gpuSum / samples, cpu = cpuSum / samples;
                double meter = proxyMode ? freqSum / freqSamples : pkgSum / pkgSamples;
                if (!WorthYielding(gpu, cpu) || meter <= 0)
                {
                    stage = YieldStage.Skipped;
                    return YieldAction.None;
                }
                baseGpu = gpu; baseCpu = cpu;
                if (proxyMode) baseFreq = meter; else basePkg = meter;
                stage = YieldStage.Engaged; stageAt = now;
                lastEvidenceAt = now;
                engagements++;
                samples = 0; pkgSamples = 0; freqSamples = 0;
                gpuSum = cpuSum = pkgSum = freqSum = 0;
                return YieldAction.Engage;
            }

            if (span < VerifyTicks || samples < MinSamples) return YieldAction.None;
            // 验证期同理 读不到证据就不能判 但这里已经改过 EPP 攒不够样本必须退回而不是干等
            if (meterSamples < MinSamples)
            {
                if (samples < MinSamples * GiveUpSampleMultiple) return YieldAction.None;
                stage = YieldStage.Reverted;
                verdict = YieldVerdict.Inconclusive;
                return YieldAction.Revert;
            }
            double gpuNow = gpuSum / samples;
            if (!proxyMode)
            {
                // 瓦数路径同样吃负载漂移守卫 验证窗撞上过场或加载屏时
                //   功耗自然回落会被误判成"没让出来" 方向盘一局最多三个验证窗
                //   误熔断的暴露面是老行为的三倍 不能再靠运气
                double cpuShift = Math.Abs(cpuSum / samples - baseCpu);
                if (cpuShift > MaxCpuUtilShift)
                {
                    stage = YieldStage.Reverted;
                    verdict = YieldVerdict.Inconclusive;
                    return YieldAction.Revert;
                }
            }
            if (proxyMode)
            {
                double cpuNow = cpuSum / samples, freqNow = freqSum / freqSamples;
                // 负载漂移大就没法比 退回但不熔断 那是场景变了不是机器的错
                if (Math.Abs(cpuNow - baseCpu) > MaxCpuUtilShift)
                {
                    stage = YieldStage.Reverted;
                    verdict = YieldVerdict.Inconclusive;
                    return YieldAction.Revert;
                }
                if (baseGpu - gpuNow > MaxGpuUtilDrop)
                {
                    stage = YieldStage.Reverted;
                    verdict = YieldVerdict.GpuHarm;
                    Settings.Save(FreqFuseKey, true);
                    return YieldAction.Revert;
                }
                if (baseFreq - freqNow < baseFreq * MinFreqDropShare)
                {
                    // 频率纹丝不动 = 这台机器上 EPP 是死杠杆 与作者台架的 i7-9750H 同款结局
                    stage = YieldStage.Reverted;
                    verdict = YieldVerdict.NoGain;
                    Settings.Save(FreqFuseKey, true);
                    return YieldAction.Revert;
                }
                EnterHold(now);
                return YieldAction.Keep;
            }
            double pkgNow = pkgSum / pkgSamples;
            bool keep = VerifyHold(basePkg, pkgNow, baseGpu, gpuNow);
            if (keep) EnterHold(now);
            else
            {
                stage = YieldStage.Reverted;
                verdict = YieldVerdict.NoGain;
                Settings.Save(FuseKey, true);
            }
            return keep ? YieldAction.Keep : YieldAction.Revert;
        }

        internal static bool ValidUtil(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 100; }

        private static bool ValidPositive(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0; }
    }

    // 运行时 自带采样线程 只在对局期间活着 退场必还原 EPP
    //   采样放独立线程 不占扫描循环 每 2 秒一次 一局最多做一次决定
    internal static class PowerBudgetYieldRunner
    {
        internal const string EnabledKey = "GmPowerYield";
        internal const int SampleIntervalMs = 2000;
        internal const int GpuWindowMs = 500;
        internal const int SamplerReopenAfterDry = 15;

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
        private static volatile bool proxyRun;
        private static int targetPid;
        private static long targetCreation;

        public static bool EnabledSetting { get { return Settings.LoadCached(EnabledKey, false); } }

        // 频率代理可用性 探一次记一辈子 计数器在不在不会中途变
#if PAVISE_SELFTEST
        // 隔离测试不真探 PDH 默认按不可用 既有用例的语义分毫不变
        internal static bool FreqProxyForTest;

        public static bool FreqProxyAvailable { get { return FreqProxyForTest; } }
#else
        private static int freqCounterState;

        public static bool FreqProxyAvailable
        {
            get
            {
                if (freqCounterState == 0)
                {
                    var sampler = new FreqSampler();
                    freqCounterState = sampler.Open() ? 1 : -1;
                    sampler.Close();
                }
                return freqCounterState > 0;
            }
        }
#endif

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
        public static void Start(bool enabled, bool competitive, int rendererPid, long rendererCreation)
        {
            bool retarget;
            lock (gate)
                retarget = running && (targetPid != rendererPid || targetCreation != rendererCreation);
            // 启动器交接给真实 renderer 时旧窗口的 GPU/CPU 基线已经失效
            // 先完整停掉并还原 EPP 再为新身份开一轮 不能把两进程的数据拼起来
            if (retarget && !StopCore(3000, false)) return;

            lock (gate)
            {
                if (shutdownClosed || stopInProgress || running
                    || worker != null && worker.IsAlive) return;
                if (!enabled || rendererPid <= 0 || rendererCreation <= 0) return;
                // 有瓦数走瓦数 没瓦数但有频率计数器走降级验证 熔断各记各的账
                bool watts = EnergyMeter.Available;
                bool proxy = !watts && FreqProxyAvailable;
                bool eligible = PowerBudgetYield.Eligible(
                    Native.HasSystemBattery(), Native.OnAcPower(), competitive,
                    PowerPlan.ManagedPlanIsActive, watts || proxy,
                    watts ? PowerBudgetYield.Fused : PowerBudgetYield.FreqFused);
                if (!eligible) return;
                proxyRun = proxy;
                targetPid = rendererPid;
                targetCreation = rendererCreation;
                state = new PowerBudgetYield();
                state.Begin(DateTime.UtcNow.Ticks, true, proxy);
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
            // join 失败时线程引用要留着 清掉它会让后面最终那次停止
            // 误报成根本没有工作线程
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
                    targetPid = 0;
                    targetCreation = 0;
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

        // 与隔离测试共享完整的“样本 -> 决策 -> 写入”分发；无效 GPU 也必须经过这里。
        internal static bool ProcessSample(int mine, long now, double gpu, double cpu,
            double watts, double freq, Func<bool> engage, Func<bool> restore,
            out YieldAction action, out YieldVerdict verdict)
        {
            action = YieldAction.None; verdict = YieldVerdict.None;
            lock (gate)
            {
                if (!running || mine != generation || shutdownClosed || state == null) return false;
                action = state.Advance(now, gpu, cpu, watts, freq);
                verdict = state.Verdict;
                if (state.Stage == YieldStage.Skipped && action == YieldAction.None) return false;
            }
            if (action == YieldAction.Engage) return RunCurrentMutation(mine, engage);
            if (action == YieldAction.Revert)
            {
                RunCurrentMutation(mine, restore);
                return false; // 失败时底层 receipt 保留，StopCore 仍负责最终恢复。
            }
            if (action == YieldAction.Release) return RunCurrentMutation(mine, restore);
            return GenerationRunning(mine);
        }

        private static void Loop(int mine)
        {
            var cpu = new CpuSaturation();
            cpu.Sample(); // Prime GetSystemTimes before the first measurement window.
            bool proxy = proxyRun;
            int rendererPid;
            long rendererCreation;
            lock (gate)
            {
                rendererPid = targetPid;
                rendererCreation = targetCreation;
            }
            bool adapterKnown = false;
            int adapterLuidHigh = 0;
            uint adapterLuidLow = 0;
            var freq = new FreqSampler();
            // GPU 占用走持久查询 整局只开一次 每轮一次采集 没有睡眠 打不开时退回一次性解析
            var gpuSampler = new GpuEvidence.GpuEngineSampler();
            gpuSampler.Open();
            if (proxy && !freq.Open())
            {
                // 进程级探测成功不代表本局也打得开 静默夭折要留话也要收状态
                freq.Close();
                gpuSampler.Close();
                Logger.Warn(Lang.T("log.poweryield.12"));
                lock (gate)
                    if (mine == generation)
                    {
                        running = false;
                        if (state != null) { state.End(); state = null; }
                    }
                return;
            }
            EnergyMeter.Sample prev = proxy ? null : EnergyMeter.Take();
            int drySamples = 0;
            try
            {
                while (GenerationRunning(mine))
                {
                    Thread.Sleep(SampleIntervalMs);
                    if (!GenerationRunning(mine)) break;
                    bool targetChanged;
                    double gpu = SampleGpuUtil(gpuSampler, rendererPid, rendererCreation,
                        ref adapterKnown, ref adapterLuidHigh, ref adapterLuidLow,
                        out targetChanged);
                    // 持久查询长期看不到这个进程的 3D 实例时重开一次 防止通配实例表没跟上晚起的渲染设备
                    if (gpu < 0 && !targetChanged)
                    {
                        if (++drySamples >= SamplerReopenAfterDry) { drySamples = 0; gpuSampler.Open(); }
                    }
                    else drySamples = 0;
                    if (targetChanged)
                    {
                        // PID 复用、renderer 退出或渲染迁到另一块卡后旧基线都不可继续用
                        // 若已经让过 EPP 立即尝试还原 退局 StopCore 仍是失败兜底
                        if (PowerPlan.EppYielded) RunCurrentMutation(mine, PowerPlan.RestoreEpp);
                        Logger.Warn(Lang.T("log.poweryield.13"));
                        break;
                    }
                    double cpuPct = cpu.Sample() * 100.0;
                    double watts = -1, freqPct = -1;
                    if (proxy) freqPct = freq.Read();
                    else
                    {
                        EnergyMeter.Sample now = EnergyMeter.Take();
                        watts = EnergyMeter.Watts(prev, now, EnergyRail.Package);
                        if (now != null) prev = now;
                    }
                    YieldAction action;
                    YieldVerdict verdict;
                    bool keepRunning = ProcessSample(mine, DateTime.UtcNow.Ticks, gpu, cpuPct,
                        watts, freqPct, delegate { return PowerPlan.TryYieldEpp(PowerBudgetYield.YieldEpp); },
                        PowerPlan.RestoreEpp, out action, out verdict);
                    if (action == YieldAction.Engage)
                    {
                        bool ok = keepRunning;
                        Logger.Log(proxy
                            ? Lang.F(ok ? "log.poweryield.8" : "log.poweryield.3",
                                PowerBudgetYield.YieldEpp.ToString(),
                                gpu.ToString("F0"), cpuPct.ToString("F0"), freqPct.ToString("F0"))
                            : Lang.F(ok ? "log.poweryield.2" : "log.poweryield.3",
                                PowerBudgetYield.YieldEpp.ToString(),
                                gpu.ToString("F0"), cpuPct.ToString("F0"), watts.ToString("F1")));
                        if (!ok) break;
                    }
                    else if (action == YieldAction.Revert)
                    {
                        // 这里还原失败也不重试 EppYielded 仍为真 退局 StopCore 兜底还原
                        Logger.Warn(Lang.T(verdict == YieldVerdict.Inconclusive
                            ? "log.poweryield.9" : "log.poweryield.4"));
                        break;
                    }
                    else if (action == YieldAction.Keep)
                    {
                        // 验收通过不收工 方向盘上路 瓶颈移回 CPU 时把预算还回去
                        Logger.Log(Lang.T(proxy ? "log.poweryield.10" : "log.poweryield.7"));
                    }
                    else if (action == YieldAction.Release)
                    {
                        // 还原失败不能当没事 状态已经认为还回去了 让退局的兜底还原来收尾
                        if (!keepRunning) break;
                        Logger.Log(Lang.T("log.poweryield.11"));
                    }
                    if (!keepRunning) break;
                }
            }
            finally { freq.Close(); gpuSampler.Close(); }
        }

        // 平台频率百分比 与作者台架记录用的是同一个计数器 两次采集之间的均值
        private sealed class FreqSampler
        {
            private IntPtr query;
            private IntPtr counter;

            public bool Open()
            {
                try
                {
                    if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
                        return false;
                    if (PdhAddEnglishCounterW(query,
                            @"\Processor Information(_Total)\% Processor Performance",
                            IntPtr.Zero, out counter) != 0) return false;
                    return PdhCollectQueryData(query) == 0;
                }
                catch { return false; }
            }

            public double Read()
            {
                try
                {
                    if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return -1;
                    var fmt = new PdhFmtCounterValue();
                    uint type;
                    if (PdhGetFormattedCounterValue(counter, PdhFmtDouble, out type, out fmt) != 0
                        || fmt.CStatus != 0) return -1;
                    return fmt.DoubleValue > 0 ? fmt.DoubleValue : -1;
                }
                catch { return -1; }
            }

            public void Close()
            {
                if (query == IntPtr.Zero) return;
                try { PdhCloseQuery(query); } catch { }
                query = IntPtr.Zero;
            }

            private const uint PdhFmtDouble = 0x00000200;

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential)]
            private struct PdhFmtCounterValue
            {
                public uint CStatus;
                public double DoubleValue;
            }

            [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
            [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
            [System.Runtime.InteropServices.DllImport("pdh.dll")]
            private static extern uint PdhCollectQueryData(IntPtr query);
            [System.Runtime.InteropServices.DllImport("pdh.dll")]
            private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);
            [System.Runtime.InteropServices.DllImport("pdh.dll")]
            private static extern uint PdhCloseQuery(IntPtr query);
        }

#if PAVISE_SELFTEST
        internal static void SetSampleStateForTest(int mine, PowerBudgetYield value)
        {
            lock (gate)
            {
                if (mine != generation || !running) throw new InvalidOperationException("No test generation");
                state = value;
            }
        }

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
                targetPid = 0;
                targetCreation = 0;
                generation++;
            }
        }
#endif

        private static bool SameProcessIdentity(int pid, long creation)
        {
            if (pid <= 0 || creation <= 0) return false;
            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                long actual, cpu;
                ulong io;
                return Native.QueryProcessSample(process, out actual, out cpu, out io)
                    && actual == creation;
            }
            catch { return false; }
            finally { Native.CloseHandle(process); }
        }

        private static double SampleGpuUtil(GpuEvidence.GpuEngineSampler sampler, int pid, long creation,
            ref bool adapterKnown, ref int adapterLuidHigh, ref uint adapterLuidLow,
            out bool targetChanged)
        {
            targetChanged = false;
            if (!SameProcessIdentity(pid, creation))
            {
                targetChanged = true;
                return -1;
            }
            try
            {
                RenderAdapter adapter = sampler != null && sampler.IsOpen
                    ? sampler.Resolve(pid)
                    : GpuEvidence.ResolveRenderAdapter(pid, GpuWindowMs);
                return AcceptTargetAdapterSample(adapter, ref adapterKnown,
                    ref adapterLuidHigh, ref adapterLuidLow, out targetChanged);
            }
            catch { return -1; }
        }

        // 只接受这个 renderer 唯一且稳定的渲染适配器
        // ResolveRenderAdapter 已经按 PID 过滤 这里再锁 LUID 防止 Optimus/多卡迁移后串用旧基线
        internal static double AcceptTargetAdapterSample(RenderAdapter adapter,
            ref bool adapterKnown, ref int adapterLuidHigh, ref uint adapterLuidLow,
            out bool targetChanged)
        {
            targetChanged = false;
            if (adapter == null || adapter.Ambiguous) return -1;
            if (double.IsNaN(adapter.Util) || double.IsInfinity(adapter.Util) || adapter.Util < 0) return -1;
            if (adapterKnown
                && (adapter.LuidHigh != adapterLuidHigh || adapter.LuidLow != adapterLuidLow))
            {
                // 各卡都是 0% 时 PickAdapter 只是随手挑了第一块 那不是迁移 是这一轮没数据
                if (adapter.Util < GpuEvidence.MinElectUtilization) return -1;
                targetChanged = true;
                return -1;
            }
            if (!adapterKnown)
            {
                // 0% 的 PDH 残留实例不能证明渲染卡 等 renderer 真正在 3D 上出力再绑定
                if (adapter.Util < GpuEvidence.MinElectUtilization) return -1;
                adapterKnown = true;
                adapterLuidHigh = adapter.LuidHigh;
                adapterLuidLow = adapter.LuidLow;
            }
            return adapter.Util > 100.0 ? 100.0 : adapter.Util;
        }
    }
}
