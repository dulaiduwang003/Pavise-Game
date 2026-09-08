// @author bdth 2074055628@qq.com
// 文件用途 笔记本对局中把共享功耗预算从 CPU 让给 GPU 每次让出后验收 一局最多三次
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
    //   每次让出都重新观察和验收 证据不充分就退回 只有连续证据确认负结果才持久停用
    //
    // 为什么动 EPP 不动 ProcThrottleMin
    //   ProcThrottleMin 只决定"能不能降" EPP 决定"愿不愿意降"
    //   地板放开了但 EPP 还是 0 照样强烈偏性能 省不出多少 所以 EPP 才是真正的杠杆
    //
    // 为什么优先使用瓦数验证
    //   验证要回答"预算真的让出来了吗" 只看 GPU 撞墙命中率会被两头骗
    //   GPU 拿到更高上限后可能照样撞墙 而把 CPU 压成瓶颈会让 GPU 利用率掉 命中率也跟着掉
    //   看起来像成功 实际是失败 所以缺少功耗读数时必须另有下述频率代理证据
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
        // 驱动计数器可能在加载/切屏时短暂缺测 不再十秒就结束本局。
        // 已经改过 EPP 的状态最多等一分钟 仍没恢复才归还预算。
        internal const long EvidenceMaxAgeTicks = 60 * TimeSpan.TicksPerSecond;
        // 正常两秒采样允许调度抖动；更长的回调间隔不能用于持久的硬件负收益结论。
        internal const long EvidenceContinuityMaxGapTicks = 4 * TimeSpan.TicksPerSecond;
        private long lastEvidenceAt;
        private long lastAdvanceAt;
        private bool awaitingObservationData;
        private bool observationInterrupted;
        private bool verificationInterrupted;

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
            lastEvidenceAt = now;
            lastAdvanceAt = now;
        }

        public void End() { Reset(); }

        private void Reset()
        {
            lastEvidenceAt = 0;
            lastAdvanceAt = 0;
            awaitingObservationData = false;
            observationInterrupted = false;
            verificationInterrupted = false;
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

        private void RestartObservationWindow(long now)
        {
            stageAt = lastEvidenceAt = now;
            awaitingObservationData = true;
            observationInterrupted = false;
            samples = pkgSamples = freqSamples = 0;
            gpuSum = cpuSum = pkgSum = freqSum = 0;
        }

        private YieldAction RevertInconclusive()
        {
            stage = YieldStage.Reverted;
            verdict = YieldVerdict.Inconclusive;
            return YieldAction.Revert;
        }

        private YieldAction RevertNegativeVerdict(YieldVerdict negativeVerdict, string fuseKey)
        {
            // 缺测前后的负载可能属于不同场景。退回 EPP，但不能据此说这台机器无效。
            if (verificationInterrupted) return RevertInconclusive();
            stage = YieldStage.Reverted;
            verdict = negativeVerdict;
            Settings.Save(fuseKey, true);
            return YieldAction.Revert;
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
            bool validEvidence = validLoad && (stage == YieldStage.Held || validMeter);
            bool timeReversed = now < lastAdvanceAt;
            long sampleGap = now - lastAdvanceAt;
            lastAdvanceAt = now;
            // 先查缺测间隙 再收新样本 避免一个迟到的读数掩盖长时间失联。
            // 观察期还没改电源 可以重新等完整窗口 不因游戏晚加载而放弃整局。
            if (timeReversed || now < lastEvidenceAt || now - lastEvidenceAt >= EvidenceMaxAgeTicks)
            {
                if (stage == YieldStage.Observing) RestartObservationWindow(now);
                else return RevertInconclusive();
            }
            // 等计数器恢复可以等一分钟，但中断了整段验证时间的旧样本不能用于硬件熔断。
            // 零星读数也不能不断延长已改过 EPP 的未验收状态。
            if (stage == YieldStage.Engaged
                && (now - stageAt >= EvidenceMaxAgeTicks
                    || validEvidence && now - lastEvidenceAt >= VerifyTicks))
                return RevertInconclusive();
            if (stage == YieldStage.Engaged
                && (!validEvidence || sampleGap > EvidenceContinuityMaxGapTicks))
                verificationInterrupted = true;
            // 观察期不改电源。证据断档超过一个观察窗口时丢掉旧场景，继续等完整新窗口。
            if (stage == YieldStage.Observing && now - lastEvidenceAt >= ObserveTicks)
                RestartObservationWindow(now);
            if (stage == YieldStage.Observing && validEvidence
                && (awaitingObservationData || samples == 0 && now - stageAt >= ObserveTicks))
            {
                RestartObservationWindow(now);
                awaitingObservationData = false;
            }
            // 基线中已有证据以后出现缺测，负结论也不能当成硬件结论。
            // 稀疏基线仍可试让路；重新收集完整观察窗才清除这份不连续标记。
            if (stage == YieldStage.Observing && samples > 0
                && (!validEvidence || sampleGap > EvidenceContinuityMaxGapTicks))
                observationInterrupted = true;
            // 维持段不再验硬件收益；恢复采样后另开滚动窗口，不能混入断档前的负载。
            if (stage == YieldStage.Held && validLoad && now - lastEvidenceAt >= HoldWindowTicks)
            {
                stageAt = now;
                samples = 0; gpuSum = cpuSum = 0;
            }
            if (validEvidence) lastEvidenceAt = now;
            if (!validLoad) return YieldAction.None;
            // 基线只用完整配对的负载与 meter；零星有效 meter 可以继续积累，缺值不作零值。
            if (stage == YieldStage.Observing && !validMeter)
                return YieldAction.None;
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
                observationInterrupted = false;
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
                verificationInterrupted = observationInterrupted;
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
                    return RevertNegativeVerdict(YieldVerdict.GpuHarm, FreqFuseKey);
                if (baseFreq - freqNow < baseFreq * MinFreqDropShare)
                {
                    // 频率纹丝不动 = 这台机器上 EPP 是死杠杆 与作者台架的 i7-9750H 同款结局
                    return RevertNegativeVerdict(YieldVerdict.NoGain, FreqFuseKey);
                }
                EnterHold(now);
                return YieldAction.Keep;
            }
            double pkgNow = pkgSum / pkgSamples;
            bool keep = VerifyHold(basePkg, pkgNow, baseGpu, gpuNow);
            if (keep) EnterHold(now);
            else return RevertNegativeVerdict(YieldVerdict.NoGain, FuseKey);
            return YieldAction.Keep;
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
        private static bool policyEnabled, policyCompetitive;
        private static Func<bool> policyAdmission;

        public static bool EnabledSetting { get { return Settings.LoadCached(EnabledKey, false); } }

        // 频率代理可用性 探一次记一辈子 计数器在不在不会中途变
#if PAVISE_SELFTEST
        // 隔离测试不真探 PDH 默认按不可用 既有用例的语义分毫不变
        internal static bool FreqProxyForTest;
        internal static Func<bool> RuntimeEnvironmentForTest;

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

        private static bool RuntimeEnvironmentEligible()
        {
            try
            {
#if PAVISE_SELFTEST
                // 隔离 runner 用例只走显式模拟值，不读取本机电源配置。
                Func<bool> test = RuntimeEnvironmentForTest;
                return test == null || test();
#else
                return Native.HasSystemBattery() && Native.OnAcPower() && PowerPlan.ManagedPlanIsActive;
#endif
            }
            catch { return false; }
        }

        private static bool RuntimeAdmissionEligible()
        {
            Func<bool> admission;
            lock (gate)
            {
                if (!policyEnabled || !policyCompetitive) return false;
                admission = policyAdmission;
            }
            try { return (admission == null || admission()) && RuntimeEnvironmentEligible(); }
            catch { return false; }
        }

        // livePolicyAdmission 不得取得 GameMode.sync，运行时会在原生写入闸内重查它。
        public static void Start(bool enabled, bool competitive, int rendererPid, long rendererCreation,
            Func<bool> livePolicyAdmission = null)
        {
            bool retarget, hadWorker, wasRunning, pendingStop;
            int observedGeneration;
            lock (gate)
            {
                if (shutdownClosed) return;
                observedGeneration = generation;
                wasRunning = running;
                hadWorker = running || worker != null;
                pendingStop = stopInProgress;
                retarget = running && (targetPid != rendererPid || targetCreation != rendererCreation);
                // 新目标的准入闭包不能借给仍在运行的旧目标。先撤销旧资格，再等停止。
                if (retarget || !enabled || !competitive || rendererPid <= 0 || rendererCreation <= 0)
                    policyEnabled = false;
            }
            // 同 PID 的配置令牌也可能失效。保持旧闭包直到旧代完整停止，不能用新 true 续旧基线。
            bool oldAdmitted = !wasRunning || RuntimeAdmissionEligible();
            bool nextAdmitted = false;
            try
            {
                nextAdmitted = enabled && competitive && rendererPid > 0 && rendererCreation > 0
                    && (livePolicyAdmission == null || livePolicyAdmission()) && RuntimeEnvironmentEligible();
            }
            catch { }
            if (hadWorker && (pendingStop || retarget || !oldAdmitted || !nextAdmitted))
            {
                lock (gate)
                {
                    if (running && generation != observedGeneration) return;
                    policyEnabled = false;
                }
                if (!StopCore(3000, false)) return;
            }
            if (!nextAdmitted)
            {
                if (PowerPlan.EppYielded) StopCore(3000, false);
                return;
            }
            // 上一代资格撤销后的还原失败回执不能被新一轮观察/写入覆盖。
            if (!GenerationIsRunning() && PowerPlan.EppYielded && !StopCore(3000, false)) return;

            lock (gate)
            {
                if (shutdownClosed || stopInProgress || running
                    || worker != null && worker.IsAlive) return;
                if (!enabled || rendererPid <= 0 || rendererCreation <= 0) return;
                // 每轮新建的委托不算策略变化；活着且仍获准的 worker 保留原闭包与原代。
                policyEnabled = enabled;
                policyCompetitive = competitive;
                policyAdmission = livePolicyAdmission;
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

        private static bool GenerationIsRunning()
        { lock (gate) return running; }

        private static bool RevokeCurrentAdmission(int mine, Func<bool> restore)
        {
            lock (operationGate)
            {
                if (!GenerationRunning(mine)) return false;
                // 即使失败也保留底层 receipt，由 Start/StopCore 重试；旧代不能再写入。
                RunMutation(restore);
                lock (gate)
                    if (mine == generation)
                    {
                        running = false;
                        generation++;
                        if (state != null) { state.End(); state = null; }
                    }
                return false;
            }
        }

        private static bool EnsureRuntimeAdmission(int mine, Func<bool> restore)
        {
            if (!GenerationRunning(mine)) return false;
            return RuntimeAdmissionEligible() || RevokeCurrentAdmission(mine, restore);
        }

        private static bool RunCurrentEngagement(int mine, Func<bool> engage, Func<bool> restore)
        {
            lock (operationGate)
            {
                if (!GenerationRunning(mine)) return false;
                bool revoked = false;
                bool applied = RunMutation(delegate
                {
                    // BeginMutation 可能等待其它工作；资格复核要贴着实际 EPP 写入。
                    if (!GenerationRunning(mine) || !RuntimeAdmissionEligible()) { revoked = true; return false; }
                    return engage != null && engage();
                });
                return revoked ? RevokeCurrentAdmission(mine, restore) : applied;
            }
        }

        private static bool RunCurrentMutation(int mine, Func<bool> mutation)
        {
            lock (operationGate)
            {
                if (!GenerationRunning(mine)) return false;
                return RunMutation(mutation);
            }
        }

        // 和隔离测试共用整条样本到决策到写入的分发 无效 GPU 也得从这走
        internal static bool ProcessSample(int mine, long now, double gpu, double cpu,
            double watts, double freq, Func<bool> engage, Func<bool> restore,
            out YieldAction action, out YieldVerdict verdict)
        {
            action = YieldAction.None; verdict = YieldVerdict.None;
            if (!EnsureRuntimeAdmission(mine, restore)) return false;
            lock (gate)
            {
                if (!running || mine != generation || shutdownClosed || state == null) return false;
                action = state.Advance(now, gpu, cpu, watts, freq);
                verdict = state.Verdict;
                if (state.Stage == YieldStage.Skipped && action == YieldAction.None) return false;
            }
            if (action == YieldAction.Engage) return RunCurrentEngagement(mine, engage, restore);
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
                    if (!EnsureRuntimeAdmission(mine, PowerPlan.RestoreEpp)) break;
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
                        // PID 复用 renderer 退出 或者渲染挪到另一块卡 旧基线都不能再用
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
        internal static void SetRuntimeAdmissionForTest(int mine, Func<bool> admission,
            int rendererPid = 123, long rendererCreation = 456)
        {
            lock (gate)
            {
                if (mine != generation || !running) throw new InvalidOperationException("No test generation");
                policyAdmission = admission;
                targetPid = rendererPid;
                targetCreation = rendererCreation;
            }
        }

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
                policyEnabled = policyCompetitive = true;
                policyAdmission = null;
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
                policyEnabled = policyCompetitive = false;
                policyAdmission = null;
                RuntimeEnvironmentForTest = null;
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
