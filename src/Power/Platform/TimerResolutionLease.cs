using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class TimerResolutionLease
    {
        internal delegate int Request(uint desired, bool acquire, out uint actual);

        internal delegate int Read(out uint coarsest, out uint finest, out uint current);

        // 0.5 ms 正好是个坑 两跳刚好等于 1 ms Sleep(1) 要多等一跳 约 1.5 ms
        // 系统按硬件档位往下取整 从 0.504 往上逐档请求 取第一个读回高于 0.5 ms 的档
        internal const uint Boundary = 5000u;

        internal const uint Ceiling = 5300u;

        internal static readonly uint[] Steps = { 5040u, 5070u, 5100u, 5150u, 5210u, 5300u };

        // 有的机器 0.5 ms 往上没有细档 细档全读回 0.5 ms 就先实测 0.5 ms 本身 不理想再往粗里找 到 1 ms 为止
        // 游戏一般自己就请求 1 ms 选到 1 ms 也就等于不额外加码
        internal static readonly uint[] CoarseSteps = { 5500u, 6000u, 7000u, 8000u, 9000u, 10000u };

        internal const uint CoarseCeiling = 10000u;

        // 读回合格后实测本进程 Sleep(1) 的中位数 超过上限说明这一档没起作用 换下一档
        internal delegate double SleepProbe(int samples);

        internal const int EffectSamples = 40;

        internal const double EffectLimitMs = 1.25;

        // 测试注入用 产品运行时为空 走原生实测
        internal SleepProbe Measure = null;

        private uint requested = Steps[0];

        // 本次运行记住选中的档和实测结果 下次进局先请求它 读回不变就不再实测
        private uint chosenStep;

        private uint chosenActual;

        private double chosenMedian;

        internal const string FailureAction = "GlobalTimerResolutionLeaseV1";

        internal const int AuditSeconds = 5;

        internal const int AcquireReadbackSamples = 3;

        internal const int AcquireReadbackDelayMs = 20;

        internal Request Set;

        internal Read Query;

        private readonly object gate = new object();

        private long nextAudit;

        private long releaseRetryAt;

        private int releaseFailures;

        private readonly AutomaticRecoveryBudget recoveryBudget = new AutomaticRecoveryBudget();

        internal void BeginAutomaticCycle(string identity)
        {
            lock (gate)
            {
                ActionFailureStore.BeginAutomaticCycle(FailureAction, "system", identity);
                if (recoveryBudget.BeginCycle(identity))
                {
                    releaseRetryAt = 0L;
                    releaseFailures = recoveryBudget.FailureCount;
                    nextAudit = 0L;
                }
            }
        }

        private bool exemptionOwned;

        private int originalControl;

        private int originalState;

        private string nativeFailure;

        private string failureDetail;

        private StringBuilder acquisitionTrace;

        private string acquisitionDetail;

        private bool nativeAcquisition;

        internal string AcquisitionDetail
        {
            get { lock (gate) { return acquisitionDetail ?? ""; } }
        }

        private void TraceAcquisition(string detail)
        {
            if (acquisitionTrace == null) return;
            if (acquisitionTrace.Length > 0) acquisitionTrace.Append("; ");
            acquisitionTrace.Append(detail);
        }

        internal string FailureDetail
        {
            get
            {
                lock (gate)
                {
                    string original = failureDetail ?? ActionFailureStore.FailureReason(FailureAction, "system");
                    if (HasResidue && !string.IsNullOrEmpty(nativeFailure) && nativeFailure != original)
                        return nativeFailure + (string.IsNullOrEmpty(original) ? "" : " cause={" + original + "}");
                    return original ?? "";
                }
            }
        }

        internal bool Active { get; private set; }

        internal bool HasResidue
        {
            get
            {
                if (!Active)
                {
                    return exemptionOwned;
                }
                return true;
            }
        }

        internal uint Actual { get; private set; }

        internal uint Finest { get; private set; }

        internal uint Coarsest { get; private set; }

        internal bool Verified { get; private set; }

        internal uint Requested
        {
            get { lock (gate) { return requested; } }
        }

        // 读回不超过上限即达标 低于等于 0.5 ms 多半是别的程序持有更细的精度 本方无法改善 不算失败
        // 实测后选中的粗档读回没变也算达标 复核时不再反复重选
        internal bool TargetReached
        {
            get
            {
                if (Active && Verified && Actual != 0)
                {
                    return Actual <= Ceiling || (chosenStep != 0 && requested == chosenStep && Actual == chosenActual);
                }
                return false;
            }
        }

        // 当前持有的正是实测选中的那一档时给出那次的 Sleep(1) 中位数 没实测过或读回已变返回 0
        internal double ChosenMedianMs
        {
            get
            {
                lock (gate)
                {
                    return Active && Verified && chosenStep != 0 && requested == chosenStep && Actual == chosenActual ? chosenMedian : 0;
                }
            }
        }

        internal bool AvoidsBoundary
        {
            get { return TargetReached && Actual > Boundary; }
        }

        internal bool Failed { get; private set; }

        internal string State
        {
            get
            {
                lock (gate)
                {
                    return (!Failed) ? ((!Active) ? "" : ((!Verified) ? "timer-unverified" : (TargetReached ? "timer-locked" : "timer-limited"))) : (HasResidue ? "restore-failed" : "stopped");
                }
            }
        }

        internal TimerResolutionLease()
        {
            Set = NativeSet;
            Query = NativeQuery;
        }

        internal string ReadState(out bool hasResidue)
        {
            lock (gate)
            {
                hasResidue = HasResidue;
                return State;
            }
        }

        internal bool Readback(out bool verified, out uint actual, out uint finest)
        {
            lock (gate)
            {
                verified = Verified;
                actual = Actual;
                finest = Finest;
                return Active;
            }
        }

        internal void ResetAcquireAttempts()
        {
            lock (gate)
            {
                nextAudit = 0L;
            }
        }

        private int NativeSet(uint desired, bool acquire, out uint actual)
        {
            actual = 0;
            if (acquire) nativeAcquisition = true;
            if (acquire && Native.OsBuild() >= 22000)
            {
                int controlMask;
                int stateMask;
                int win32, ntStatus;
                if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out controlMask, out stateMask, out win32, out ntStatus))
                {
                    nativeFailure = PowerReadFailure("exemption-query", win32, ntStatus);
                    TraceAcquisition("exemptionBefore=unreadable " + nativeFailure);
                    return -1;
                }
                TraceAcquisition("exemptionBefore=" + controlMask + "/" + stateMask);
                if (!exemptionOwned)
                {
                    originalControl = controlMask;
                    originalState = stateMask;
                }
                if (!Native.TimerExemptOk(controlMask, stateMask))
                {
                    exemptionOwned = true;
                    if (!Native.RestorePowerThrottling(new IntPtr(-1), controlMask | 4, stateMask & -5, out win32))
                    {
                        nativeFailure = "stage=exemption-write api=SetProcessInformation win32=" + win32;
                        return -1;
                    }
                    int controlMask2;
                    int stateMask2;
                    if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out controlMask2, out stateMask2, out win32, out ntStatus))
                    {
                        nativeFailure = PowerReadFailure("exemption-readback", win32, ntStatus);
                        TraceAcquisition("exemptionAfter=unreadable " + nativeFailure);
                        return -1;
                    }
                    TraceAcquisition("exemptionAfter=" + controlMask2 + "/" + stateMask2);
                    if (!Native.TimerExemptOk(controlMask2, stateMask2))
                    {
                        nativeFailure = "stage=exemption-readback result=mismatch control=" + controlMask2 + " state=" + stateMask2;
                        return -1;
                    }
                }
                else TraceAcquisition("exemptionAfter=" + controlMask + "/" + stateMask + " already-confirmed");
            }
            else if (acquire) TraceAcquisition("exemption=not-required");
            int status = Native.NtSetTimerResolution(desired, acquire, out actual);
            if (acquire) TraceAcquisition("ntSetStatus=0x" + status.ToString("X8") + " ntSetActual100ns=" + actual);
            if (status < 0) nativeFailure = TimerStatus(acquire ? "request" : "release", "NtSetTimerResolution", status);
            return status;
        }

        private static string TimerStatus(string stage, string api, int status)
        {
            return "stage=" + stage + " api=" + api + " ntstatus=0x" + status.ToString("X8");
        }

        private static string PowerReadFailure(string stage, int win32, int status)
        {
            return TimerStatus(stage, "GetProcessInformation/NtQueryInformationProcess", status) + " win32=" + win32;
        }

        private string CurrentFailure(string stage)
        {
            return string.IsNullOrEmpty(nativeFailure) ? "stage=" + stage : nativeFailure;
        }

        private void RecordFailure(string reason)
        {
            if (acquisitionTrace != null && acquisitionTrace.Length > 0)
                reason += " request={" + acquisitionTrace + "}";
            failureDetail = reason;
            ActionFailureStore.Trip(FailureAction, "system", reason);
        }

        private static int NativeQuery(out uint coarsest, out uint finest, out uint current)
        {
            return Native.NtQueryTimerResolution(out coarsest, out finest, out current);
        }

        private bool CleanupExemption()
        {
            if (!exemptionOwned)
            {
                return true;
            }
            try
            {
                nativeFailure = null;
                int controlMask;
                int stateMask;
                int win32, ntStatus;
                if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out controlMask, out stateMask, out win32, out ntStatus))
                {
                    nativeFailure = PowerReadFailure("exemption-restore-query", win32, ntStatus);
                    return false;
                }
                if (Native.TimerExemptOk(controlMask, stateMask))
                {
                    int desiredControl = (controlMask & -5) | (originalControl & 4);
                    int desiredState = (stateMask & -5) | (originalState & 4);
                    if (!Native.RestorePowerThrottling(new IntPtr(-1), desiredControl, desiredState, out win32))
                    {
                        nativeFailure = "stage=exemption-restore-write api=SetProcessInformation win32=" + win32;
                        return false;
                    }
                    int verifiedControl, verifiedState;
                    if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out verifiedControl, out verifiedState, out win32, out ntStatus))
                    {
                        nativeFailure = PowerReadFailure("exemption-restore-readback", win32, ntStatus);
                        return false;
                    }
                    if ((verifiedControl & 4) != (originalControl & 4) || (verifiedState & 4) != (originalState & 4))
                    {
                        nativeFailure = "stage=exemption-restore-readback result=mismatch control=" + verifiedControl + " state=" + verifiedState;
                        return false;
                    }
                }
                exemptionOwned = false;
                return true;
            }
            catch (Exception error)
            {
                nativeFailure = "stage=exemption-restore exception=" + error.GetType().Name;
                return false;
            }
        }

        internal bool Update(bool want, long now)
        {
            lock (gate)
            {
                long previousDeadline = releaseRetryAt;
                Stopwatch elapsed = Stopwatch.StartNew();
                bool result = UpdateLocked(want, now);
                if (releaseRetryAt != previousDeadline && releaseRetryAt > now) releaseRetryAt += elapsed.Elapsed.Ticks;
                if (!result && HasResidue)
                    RecoveryAttemptTrace.FailureSchedule(recoveryBudget.FailureCount, AutomaticRecoveryBudget.MaximumFailures, releaseRetryAt);
                return result;
            }
        }

        private bool UpdateLocked(bool want, long now)
        {
            if (!want)
            {
                nextAudit = 0L;
                if (now < releaseRetryAt || (HasResidue && !recoveryBudget.CanAttempt))
                {
                    return !HasResidue;
                }
                if (!Active)
                {
                    bool hadResidue = HasResidue;
                    Verified = false;
                    Failed = !CleanupExemption();
                    recoveryBudget.Record(!Failed);
                    if (hadResidue && !Failed && !HasResidue) recoveryBudget.CompleteRecovery();
                    if (Failed)
                    {
                        RecordFailure(CurrentFailure("exemption-restore"));
                        DelayRelease(now);
                    }
                    else
                    {
                        ActionFailureStore.CancelApply(FailureAction, "system");
                        releaseFailures = 0;
                        releaseRetryAt = 0L;
                    }
                    return !Failed;
                }
                bool releaseSucceeded = Release();
                if (!releaseSucceeded)
                {
                    RecordFailure(CurrentFailure("release"));
                    DelayRelease(now);
                }
                else
                {
                    ActionFailureStore.CancelApply(FailureAction, "system");
                    releaseFailures = 0;
                    releaseRetryAt = 0L;
                }
                return releaseSucceeded;
            }
            if (ActionFailureStore.IsBlocked("GlobalTimerResolutionLeaseV1", "system"))
            {
                failureDetail = ActionFailureStore.FailureReason(FailureAction, "system");
                if (HasResidue && recoveryBudget.CanAttempt && now >= releaseRetryAt)
                {
                    if (!Release())
                    {
                        DelayRelease(now);
                    }
                    else
                    {
                        ActionFailureStore.CancelApply(FailureAction, "system");
                        releaseFailures = 0;
                        releaseRetryAt = 0L;
                    }
                }
                Failed = true;
                return false;
            }
            if (ActionFailureStore.IsIsolated("GlobalTimerResolutionLeaseV1", "system"))
            {
                if (HasResidue && (!recoveryBudget.CanAttempt || now < releaseRetryAt)) return false;
                if (HasResidue && !Release())
                {
                    RecordFailure(CurrentFailure("recovery-before-apply"));
                    DelayRelease(now);
                    return false;
                }
                nextAudit = 0L;
            }
            if (Active)
            {
                if (now < nextAudit)
                {
                    return !Failed;
                }
                if (!ReadCurrent(now))
                {
                    return Isolate(CurrentFailure("query"), now);
                }
                // 复核只确认请求还在 不重新选档 避免反复改动系统时钟
                if (TargetReached)
                {
                    Failed = false;
                    return true;
                }
                if (Finest > Ceiling)
                {
                    Failed = false;
                    return true;
                }
                if (!Release(false))
                {
                    RecordFailure(CurrentFailure("release-before-maintenance"));
                    DelayRelease(now);
                    return false;
                }
                return Acquire(now);
            }
            return Acquire(now);
        }

        private bool Acquire(long now)
        {
            string reason;
            if (!ActionFailureStore.TryBeginApply("GlobalTimerResolutionLeaseV1", "system", out reason))
            {
                Failed = true;
                failureDetail = string.IsNullOrEmpty(reason) ? "stage=admission result=denied" : reason;
                return false;
            }
            acquisitionTrace = new StringBuilder();
            nativeAcquisition = false;
            try
            {
                nativeFailure = null;
                int sample = 0;
                uint bestStep = 0, bestActual = 0;
                double bestMedian = double.MaxValue;
                bool decided = false;
                // 每档只请求一次 档数有上限 整个过程不退还失败预算
                foreach (uint step in StepOrder())
                {
                    string failure;
                    if (!RequestStep(step, now, ref sample, out failure)) return Isolate(failure, now);
                    // 本次运行已选过这一档且读回没变 直接沿用 不重测 也不重新比较
                    if (step == chosenStep && Actual == chosenActual)
                    {
                        TraceAcquisition("step=" + step + " reused sleep1MedianMs=" + chosenMedian.ToString("F3", CultureInfo.InvariantCulture));
                        decided = true;
                        break;
                    }
                    // 上次选的粗档读回变了 回到细档从头比
                    if (step > Ceiling) continue;
                    if (Actual > Ceiling)
                    {
                        // 系统最细一档就高于上限 硬件做不到 按受限处理 能做到却没给就是请求没生效
                        if (Finest > Ceiling) { TraceAcquisition("limited finest100ns=" + Finest); decided = true; break; }
                        return Isolate("stage=target-readback requested100ns=" + step + " actual100ns=" + Actual + " finest100ns=" + Finest, now);
                    }
                    if (Actual <= Boundary)
                    {
                        TraceAcquisition("step=" + step + " boundary actual100ns=" + Actual);
                        continue;
                    }
                    double median = Effect(step);
                    if (median <= EffectLimitMs)
                    {
                        Choose(step, median);
                        decided = true;
                        break;
                    }
                    if (median < bestMedian) { bestMedian = median; bestStep = step; bestActual = Actual; }
                }
                if (!decided && bestStep == 0)
                {
                    // 细档全读回 0.5 ms 要么别的程序持有 0.5 ms 要么这台机器 0.5 ms 往上没有细档
                    string failure;
                    if (!SurveyAboveBoundary(now, ref sample, ref bestStep, ref bestMedian, ref bestActual, out decided, out failure)) return Isolate(failure, now);
                }
                if (!decided)
                {
                    // 实测都不理想时取中位数最低的一档 什么都没测到就持有最低一档 等持有 0.5 ms 的程序退出后自动落到好档
                    uint fallback = bestStep != 0 ? bestStep : Steps[0];
                    uint ceiling = fallback > Ceiling ? CoarseCeiling : Ceiling;
                    TraceAcquisition("fallback=" + fallback);
                    string failure;
                    if (!RequestStep(fallback, now, ref sample, out failure)) return Isolate(failure, now);
                    if (Actual > ceiling && Finest <= ceiling)
                        return Isolate("stage=target-readback requested100ns=" + fallback + " actual100ns=" + Actual + " finest100ns=" + Finest, now);
                    // 读回和实测那次一致才记成选中 否则下次进局还要重新比
                    if (bestStep != 0 && Actual == bestActual) Choose(fallback, bestMedian);
                }
                if (!ActionFailureStore.CompleteApply("GlobalTimerResolutionLeaseV1", "system"))
                {
                    return Isolate("stage=completion-record result=persistence-failed", now);
                }
                failureDetail = null;
                nativeFailure = null;
                return true;
            }
            catch (Exception error)
            {
                nativeFailure = "stage=request exception=" + error.GetType().Name;
                Verified = false;
                return Isolate(CurrentFailure("request"), now);
            }
            finally
            {
                acquisitionDetail = acquisitionTrace.ToString();
                acquisitionTrace = null;
                nativeAcquisition = false;
            }
        }

        // 细档全落在 0.5 ms 时先实测 0.5 ms 本身 够好就持有最低一档
        // 不够好就往粗里找 到 1 ms 为止 每个新的读回实测一次 取第一个达标的 都不达标就记下中位数最低的一档
        private bool SurveyAboveBoundary(long now, ref int sample, ref uint bestStep, ref double bestMedian, ref uint bestActual, out bool decided, out string failure)
        {
            decided = false;
            if (!RequestStep(Steps[0], now, ref sample, out failure)) return false;
            // 读回又不在 0.5 ms 了 交给兜底按最低一档处理
            if (Actual > Boundary) return true;
            double atBoundary = Effect(Steps[0]);
            if (atBoundary <= EffectLimitMs) { Choose(Steps[0], atBoundary); decided = true; return true; }
            bestStep = Steps[0]; bestMedian = atBoundary; bestActual = Actual;
            uint seen = Actual;
            foreach (uint step in CoarseSteps)
            {
                if (!RequestStep(step, now, ref sample, out failure)) return false;
                if (Actual > CoarseCeiling)
                {
                    failure = "stage=target-readback requested100ns=" + step + " actual100ns=" + Actual + " finest100ns=" + Finest;
                    return false;
                }
                if (Actual == seen) { TraceAcquisition("step=" + step + " same actual100ns=" + Actual); continue; }
                seen = Actual;
                double coarseMedian = Effect(step);
                if (coarseMedian <= EffectLimitMs) { Choose(step, coarseMedian); decided = true; return true; }
                if (coarseMedian < bestMedian) { bestMedian = coarseMedian; bestStep = step; bestActual = Actual; }
            }
            return true;
        }

        // 请求一档后读回 读回可能滞后一拍 每次先等 20 ms 再读 最多读三次
        // 读回在上限内且与上一次相同才算稳定 第三次读到上限内也采用 一直高于上限就按最后一次判断
        // 粗档的上限是 1 ms
        private bool RequestStep(uint step, long now, ref int sample, out string failure)
        {
            failure = null;
            uint ceiling = step > Ceiling ? CoarseCeiling : Ceiling;
            uint actual;
            int status = Set(step, true, out actual);
            TraceAcquisition("step=" + step + " setStatus=0x" + status.ToString("X8") + " setActual100ns=" + actual);
            if (status < 0)
            {
                if (string.IsNullOrEmpty(nativeFailure)) nativeFailure = TimerStatus("request", "NtSetTimerResolution", status);
                Verified = false;
                failure = CurrentFailure("request");
                return false;
            }
            Active = true;
            requested = step;
            Failed = false;
            uint previous = 0;
            for (int read = 0; read < AcquireReadbackSamples; read++)
            {
                Thread.Sleep(AcquireReadbackDelayMs);
                sample++;
                // 豁免刚在请求里核验过 同一档的后续读回前再确认一次 防止中途被外部撤销
                if (read > 0 && !ConfirmAcquisitionExemption(sample)) { failure = CurrentFailure("exemption-during-acquire"); return false; }
                if (!ReadCurrent(now, sample)) { failure = CurrentFailure("query-after-acquire"); return false; }
                if (Actual <= ceiling && (read == AcquireReadbackSamples - 1 || (read > 0 && Actual == previous))) return true;
                previous = Actual;
            }
            TraceAcquisition("step=" + step + " readback-unsettled actual100ns=" + Actual);
            return true;
        }

        private IEnumerable<uint> StepOrder()
        {
            if (chosenStep != 0) yield return chosenStep;
            foreach (uint step in Steps)
                if (step != chosenStep) yield return step;
        }

        // 测的是本进程自己的 Sleep(1) 本进程持有请求 结果就是自己请求精度的游戏实际拿到的效果
        private double Effect(uint step)
        {
            SleepProbe probe = Measure;
            if (probe == null && nativeAcquisition) probe = NativeMeasure;
            if (probe == null) return 0;
            double median = probe(EffectSamples);
            TraceAcquisition("step=" + step + " sleep1MedianMs=" + median.ToString("F3", CultureInfo.InvariantCulture));
            return median;
        }

        private void Choose(uint step, double median)
        {
            chosenStep = step;
            chosenActual = Actual;
            chosenMedian = median;
            TraceAcquisition("chosen=" + step + " actual100ns=" + Actual);
        }

        private static double NativeMeasure(int samples)
        {
            var values = new double[samples];
            double scale = 1000.0 / Stopwatch.Frequency;
            Thread.Sleep(1);
            for (int i = 0; i < samples; i++)
            {
                long start = Stopwatch.GetTimestamp();
                Thread.Sleep(1);
                values[i] = (Stopwatch.GetTimestamp() - start) * scale;
            }
            Array.Sort(values);
            return values[samples / 2];
        }

        private bool ConfirmAcquisitionExemption(int sample)
        {
            // 同步原生路径固定操作当前进程 PID 复用不能把申请重定向到其他进程
            if (!nativeAcquisition || Native.OsBuild() < 22000) return true;
            int control, state, win32, status;
            if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out control, out state, out win32, out status))
            {
                nativeFailure = PowerReadFailure("exemption-during-acquire", win32, status);
                TraceAcquisition("exemptionSample" + sample + "=unreadable " + nativeFailure);
                return false;
            }
            TraceAcquisition("exemptionSample" + sample + "=" + control + "/" + state);
            if (Native.TimerExemptOk(control, state)) return true;
            nativeFailure = "stage=exemption-during-acquire result=mismatch control=" + control + " state=" + state;
            return false;
        }

        private bool Isolate(string reason, long now)
        {
            RecordFailure(reason);
            if (HasResidue && !Release())
            {
                DelayRelease(now);
            }
            Failed = true;
            return false;
        }

        private void DelayRelease(long now)
        {
            int upperBound = Math.Min(300, 30 * (1 << releaseFailures));
            releaseFailures = Math.Min(4, releaseFailures + 1);
            releaseRetryAt = now + TimeSpan.FromSeconds(upperBound).Ticks;
        }

        private bool Release(bool cleanup = true)
        {
            if (HasResidue && !recoveryBudget.CanAttempt) return false;
            bool hadResidue = HasResidue;
            bool restored = ReleaseCore(cleanup);
            recoveryBudget.Record(restored);
            if (hadResidue && restored && !HasResidue) recoveryBudget.CompleteRecovery();
            return restored;
        }

        private bool ReleaseCore(bool cleanup)
        {
            nativeFailure = null;
            if (!Active)
            {
                Failed = cleanup && !CleanupExemption();
                return !Failed;
            }
            try
            {
                uint actual;
                int status = Set(requested, false, out actual);
                if (status >= 0)
                {
                    Active = false;
                    Actual = actual;
                    Verified = false;
                    Failed = cleanup && !CleanupExemption();
                    return !Failed;
                }
                if (string.IsNullOrEmpty(nativeFailure)) nativeFailure = TimerStatus("release", "NtSetTimerResolution", status);
            }
            catch (Exception error)
            {
                nativeFailure = "stage=release exception=" + error.GetType().Name;
            }
            Failed = true;
            return false;
        }

        private bool ReadCurrent(long now, int acquisitionSample = 0)
        {
            nextAudit = now + TimeSpan.FromSeconds(5.0).Ticks;
            Verified = false;
            try
            {
                uint coarsest;
                uint finest;
                uint current;
                int status = Query(out coarsest, out finest, out current);
                if (acquisitionSample > 0)
                    TraceAcquisition("query" + acquisitionSample + "Status=0x" + status.ToString("X8")
                        + " actual100ns=" + current + " finest100ns=" + finest + " coarsest100ns=" + coarsest);
                if (status >= 0 && finest != 0 && coarsest >= finest && current != 0)
                {
                    Coarsest = coarsest;
                    Finest = finest;
                    Actual = current;
                    Verified = true;
                }
                else
                {
                    nativeFailure = TimerStatus("query", "NtQueryTimerResolution", status)
                        + " coarsest100ns=" + coarsest + " finest100ns=" + finest + " current100ns=" + current;
                }
            }
            catch (Exception error)
            {
                if (acquisitionSample > 0)
                    TraceAcquisition("query" + acquisitionSample + "Exception=" + error.GetType().Name);
                nativeFailure = "stage=query exception=" + error.GetType().Name;
            }
            return Verified;
        }
    }
}
