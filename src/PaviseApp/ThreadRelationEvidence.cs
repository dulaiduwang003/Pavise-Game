using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
    internal sealed class ThreadRelationMetrics
    {
        internal readonly int Tid, RunningBursts, ReadyIntervals, StateConflicts;
        // 切出前唤醒属于正常内核时序，单独计数，不计为状态歧义。
        internal readonly int EarlyWakes;
        internal readonly long Creation;
        internal readonly double RunningMs, ReadyMs, KnownMs, RunBurstP95Ms, MaxRunBurstMs, ReadyP95Ms, MaxReadyMs;
        internal readonly double ReadyFirstMs, ReadySecondMs, KnownFirstMs, KnownSecondMs;
        internal readonly double RunningFirstMs, RunningSecondMs;
        internal readonly int ProcessorMigrations;
        internal readonly int ReadyFirstIntervals, ReadySecondIntervals;
        internal readonly string CpuDistribution;
        internal readonly KeyValuePair<int, double>[] CpuRunningMs;
        internal readonly bool CpuZeroGrowthVerified;

        internal ThreadRelationMetrics(int tid, long creation, double running, double ready, double known,
            int bursts, double burstP95, double maximumBurst, int readyIntervals, double readyP95,
            double maximumReady, int conflicts, string cpus, double readyFirst = -1, double readySecond = -1,
            double knownFirst = -1, double knownSecond = -1, int readyFirstIntervals = -1, int readySecondIntervals = -1,
            int earlyWakes = 0, double runningFirst = -1, double runningSecond = -1, int migrations = 0,
            KeyValuePair<int, double>[] cpuRunningMs = null, bool cpuZeroGrowthVerified = false)
        {
            EarlyWakes = earlyWakes;
            Tid = tid; Creation = creation; RunningMs = running; ReadyMs = ready; KnownMs = known;
            RunningBursts = bursts; RunBurstP95Ms = burstP95; MaxRunBurstMs = maximumBurst;
            ReadyIntervals = readyIntervals; ReadyP95Ms = readyP95; MaxReadyMs = maximumReady;
            StateConflicts = conflicts; CpuDistribution = cpus;
            ReadyFirstMs = readyFirst; ReadySecondMs = readySecond;
            KnownFirstMs = knownFirst; KnownSecondMs = knownSecond;
            ReadyFirstIntervals = readyFirstIntervals; ReadySecondIntervals = readySecondIntervals;
            RunningFirstMs = runningFirst; RunningSecondMs = runningSecond; ProcessorMigrations = migrations;
            CpuRunningMs = cpuRunningMs == null ? null : (KeyValuePair<int, double>[])cpuRunningMs.Clone();
            CpuZeroGrowthVerified = cpuZeroGrowthVerified;
        }

        internal bool KnownZeroRunning(double duration, double firstHalf, double secondHalf)
        {
            return StateConflicts == 0 && RunningMs == 0 && duration > 0 && firstHalf > 0 && secondHalf > 0
                && (CpuZeroGrowthVerified || RunningFirstMs == 0 && RunningSecondMs == 0
                    && KnownMs >= duration - .000001 && KnownMs <= duration + .000001
                    && KnownFirstMs >= firstHalf - .000001 && KnownFirstMs <= firstHalf + .000001
                    && KnownSecondMs >= secondHalf - .000001 && KnownSecondMs <= secondHalf + .000001);
        }
    }

    // 仅保存固定窗口调度摘要，唤醒关系不能证明共享数据、锁归属或完整帧依赖。
    internal sealed class ThreadRelationSnapshot
    {
        internal readonly int Pid, ForwardFirst, ForwardSecond, ReverseFirst, ReverseSecond, PresentIntervals;
        internal readonly long Birth, Begin, End;
        internal readonly bool Valid;
        internal readonly uint LostEvents;
        internal readonly string Reason;
        internal readonly double DurationMs, DemandOverlapMs, RunningOverlapMs, PresentP50Ms, PresentP95Ms, PresentP99Ms;
        internal readonly double FirstHalfDurationMs, SecondHalfDurationMs;
        internal readonly int TrackedThreads, RecordedTransitions, RecordedPresents;
        internal readonly int AllocatedBlocks, OverflowedThreads;
        internal readonly double RecordedThroughMs;
        internal readonly string StateConflictDetails;
        internal readonly ThreadRelationMetrics Presenter, Worker;
        internal readonly ThreadRelationMetrics[] WorkloadThreads;
        internal readonly ThreadRelationMetrics[] WorkloadLedger;
        internal readonly bool WorkloadLedgerComplete;
        internal readonly bool WorkloadLedgerCapacityComplete;
        internal readonly int WorkloadCapacityGapThreads;
        internal readonly double WorkloadCapacityGapRunningMs;
        internal readonly UnifiedWorkloadEdge[] WorkloadEdges;
        internal readonly bool WorkloadValid, WorkloadTruncated;
        internal readonly string WorkloadReason;
        internal readonly double WorkloadTotalRunningMs;
        internal readonly int WorkloadThreadCount;

        internal bool HasStablePair { get { return Worker != null && Worker.Tid > 0; } }

        internal static ThreadRelationSnapshot Unavailable(int pid, long birth, long begin, long end, long frequency, uint lost, string reason)
        { return new ThreadRelationSnapshot(pid, birth, begin, end, frequency, false, reason, lost, null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); }

        internal ThreadRelationSnapshot(int pid, long birth, long begin, long end, long frequency, bool valid, string reason,
            uint lost, ThreadRelationMetrics presenter, ThreadRelationMetrics worker, int forwardFirst, int forwardSecond,
            int reverseFirst, int reverseSecond, double demandOverlap, double runningOverlap,
            int presentIntervals, double p50, double p95, double p99,
            int trackedThreads = -1, int recordedTransitions = -1, int recordedPresents = -1, long recordedThrough = 0,
            int allocatedBlocks = -1, int overflowedThreads = -1, string conflictDetails = null, long midpoint = -1,
            ThreadRelationMetrics[] workloadThreads = null, UnifiedWorkloadEdge[] workloadEdges = null,
            bool workloadValid = false, string workloadReason = null, double workloadTotalRunningMs = 0,
            bool workloadTruncated = false, int workloadThreadCount = 0,
            ThreadRelationMetrics[] workloadLedger = null, bool? workloadLedgerComplete = null, bool? workloadLedgerCapacityComplete = null,
            int workloadCapacityGapThreads = 0, double workloadCapacityGapRunningMs = 0)
        {
            Pid = pid; Birth = birth; Begin = begin; End = end; Valid = valid; Reason = reason ?? "";
            LostEvents = lost; Presenter = presenter; Worker = worker;
            DurationMs = frequency > 0 ? Math.Max(0, end - begin) * 1000.0 / frequency : 0;
            bool halvesKnown = frequency > 0 && midpoint >= 0 && begin < midpoint && midpoint < end;
            FirstHalfDurationMs = halvesKnown ? (midpoint - begin) * 1000.0 / frequency : -1;
            SecondHalfDurationMs = halvesKnown ? (end - midpoint) * 1000.0 / frequency : -1;
            ForwardFirst = forwardFirst; ForwardSecond = forwardSecond; ReverseFirst = reverseFirst; ReverseSecond = reverseSecond;
            DemandOverlapMs = demandOverlap; RunningOverlapMs = runningOverlap;
            PresentIntervals = presentIntervals; PresentP50Ms = p50; PresentP95Ms = p95; PresentP99Ms = p99;
            TrackedThreads = trackedThreads; RecordedTransitions = recordedTransitions; RecordedPresents = recordedPresents;
            RecordedThroughMs = (recordedTransitions > 0 || recordedPresents > 0) && frequency > 0 && recordedThrough >= begin
                ? (recordedThrough - begin) * 1000.0 / frequency : -1;
            AllocatedBlocks = allocatedBlocks; OverflowedThreads = overflowedThreads;
            StateConflictDetails = conflictDetails ?? "";
            WorkloadThreads = workloadThreads == null ? new ThreadRelationMetrics[0] : (ThreadRelationMetrics[])workloadThreads.Clone();
            WorkloadEdges = workloadEdges == null ? new UnifiedWorkloadEdge[0] : (UnifiedWorkloadEdge[])workloadEdges.Clone();
            WorkloadValid = workloadValid; WorkloadReason = workloadReason ?? "尚未导出全线程工作图";
            WorkloadTotalRunningMs = workloadTotalRunningMs; WorkloadTruncated = workloadTruncated;
            WorkloadThreadCount = workloadThreadCount;
            WorkloadLedger = workloadLedger == null ? WorkloadThreads : (ThreadRelationMetrics[])workloadLedger.Clone();
            WorkloadLedgerComplete = workloadLedgerComplete ?? !workloadTruncated;
            WorkloadLedgerCapacityComplete = workloadLedgerCapacityComplete ?? WorkloadLedgerComplete;
            WorkloadCapacityGapThreads = workloadCapacityGapThreads; WorkloadCapacityGapRunningMs = workloadCapacityGapRunningMs;
        }

        internal string Describe() { return Describe("未核验"); }

        internal string Describe(string placementContext)
        {
            var text = new StringBuilder("线程关系采样=").Append(Valid ? "有效" : "无效")
                .Append("；进程编号=").Append(Pid).Append("；进程创建时间=").Append(Birth)
                .Append(Valid ? "；采样毫秒=" : "；请求窗口毫秒=").Append(Number(DurationMs)).Append("；基线约束=").Append(placementContext ?? "未核验")
                .Append("；丢失事件=").Append(LostEvents).Append("；说明=").Append(Reason);
            AppendBudget(text);
            if (!Valid)
            {
                // 截断后的最后状态可能延伸到请求窗口末尾，不能把这些数值当作完整窗口结论。
                AppendIdentity(text, "呈现", Presenter);
                AppendIdentity(text, "伴随", Worker);
                if (StateConflictDetails.Length != 0) text.Append("；前4处状态冲突=").Append(StateConflictDetails);
                return text.Append("；判据=无效摘要仅用于排查，不提供运行、Ready、需求重叠或Present间隔结论").ToString();
            }
            text.Append("；状态核验范围=仅选中角色；其他线程状态未核验")
                .Append("；前后半窗口毫秒=").Append(KnownNumber(FirstHalfDurationMs)).Append('/').Append(KnownNumber(SecondHalfDurationMs));
            AppendThread(text, "呈现", Presenter);
            AppendThread(text, "伴随", Worker);
            if (HasStablePair)
            {
                text.Append("；呈现到伴随唤醒=").Append(ForwardFirst).Append('/').Append(ForwardSecond)
                    .Append("；伴随到呈现唤醒=").Append(ReverseFirst).Append('/').Append(ReverseSecond)
                    .Append("；同时运行毫秒=").Append(Number(RunningOverlapMs))
                    .Append("；运行或Ready需求重叠毫秒=").Append(Number(DemandOverlapMs));
            }
            text.Append("；Present成功提交间隔样本=").Append(PresentIntervals)
                .Append("；Present间隔P50毫秒=").Append(PresentIntervals == 0 ? "未知" : Number(PresentP50Ms))
                .Append("；Present间隔P95毫秒=").Append(PresentIntervals == 0 ? "未知" : Number(PresentP95Ms))
                .Append("；Present间隔P99毫秒=").Append(PresentIntervals == 0 ? "未知" : Number(PresentP99Ms))
                .Append("；判据=仅调度线索，需求重叠可能包含忙等，Present间隔不等于显示帧时间，不自动同核");
            return text.ToString();
        }

        private void AppendBudget(StringBuilder text)
        {
            text.Append("；固定身份线程数=").Append(Count(TrackedThreads))
                .Append("；已记录状态转换=").Append(Count(RecordedTransitions)).Append("；转换软水位=").Append(ThreadRelationCollector.MaximumTransitions)
                .Append("；线程容量=共享块池按需扩展")
                .Append("；溢出线程桶数=").Append(Count(OverflowedThreads))
                .Append("；已分配状态块=").Append(Count(AllocatedBlocks)).Append("；状态块软水位=").Append(ThreadRelationCollector.MaximumBlocks)
                .Append("；事件载荷参考字节=").Append(ThreadRelationCollector.MaximumEventBytes)
                .Append("；已记录成功提交=").Append(Count(RecordedPresents)).Append("；提交软水位=").Append(ThreadRelationCollector.MaximumPresents)
                .Append("；已记录最晚事件毫秒=").Append(RecordedThroughMs < 0 ? "未知" : Number(RecordedThroughMs));
            if (WorkloadThreadCount > 0)
                text.Append("；全线程图有效=").Append(WorkloadValid).Append("；深入候选=").Append(WorkloadThreads.Length)
                    .Append("；全量账本线程=").Append(WorkloadLedger.Length).Append("；账本身份完整=").Append(WorkloadLedgerComplete)
                    .Append("；关系边=").Append(WorkloadEdges.Length).Append("；全线程已观测运行毫秒=").Append(Number(WorkloadTotalRunningMs))
                    .Append("；工作图截断=").Append(WorkloadTruncated).Append("；工作图说明=").Append(WorkloadReason);
        }

        private static void AppendIdentity(StringBuilder text, string name, ThreadRelationMetrics value)
        {
            text.Append('；').Append(name).Append("线程=");
            if (value == null) { text.Append("未知"); return; }
            text.Append(value.Tid).Append('/').Append(value.Creation)
                .Append('；').Append(name).Append("状态歧义=").Append(value.StateConflicts)
                .Append('；').Append(name).Append("切出前唤醒=").Append(value.EarlyWakes);
        }

        private static void AppendThread(StringBuilder text, string name, ThreadRelationMetrics value)
        {
            text.Append('；').Append(name).Append("线程=");
            if (value == null) { text.Append("未知"); return; }
            text.Append(value.Tid).Append('/').Append(value.Creation)
                .Append('；').Append(name).Append("运行毫秒=").Append(Number(value.RunningMs))
                .Append('；').Append(name).Append("Ready毫秒=").Append(Number(value.ReadyMs))
                .Append('；').Append(name).Append("已知状态毫秒=").Append(Number(value.KnownMs))
                .Append('；').Append(name).Append("前后半Ready毫秒=").Append(KnownNumber(value.ReadyFirstMs)).Append('/').Append(KnownNumber(value.ReadySecondMs))
                .Append('；').Append(name).Append("前后半已知状态毫秒=").Append(KnownNumber(value.KnownFirstMs)).Append('/').Append(KnownNumber(value.KnownSecondMs))
                .Append('；').Append(name).Append("前后半Ready起始片段=").Append(Count(value.ReadyFirstIntervals)).Append('/').Append(Count(value.ReadySecondIntervals))
                .Append('；').Append(name).Append("运行片段=").Append(value.RunningBursts)
                .Append('；').Append(name).Append("运行片段P95毫秒=").Append(value.RunningBursts == 0 ? "未知" : Number(value.RunBurstP95Ms))
                .Append('；').Append(name).Append("最长运行片段毫秒=").Append(Number(value.MaxRunBurstMs))
                .Append('；').Append(name).Append("Ready片段=").Append(value.ReadyIntervals)
                .Append('；').Append(name).Append("ReadyP95毫秒=").Append(value.ReadyIntervals == 0 ? "未知" : Number(value.ReadyP95Ms))
                .Append('；').Append(name).Append("最长Ready毫秒=").Append(Number(value.MaxReadyMs))
                .Append('；').Append(name).Append("状态歧义=").Append(value.StateConflicts)
                .Append('；').Append(name).Append("切出前唤醒=").Append(value.EarlyWakes)
                .Append('；').Append(name).Append("实际CPU=").Append(value.CpuDistribution);
        }

        private static string Number(double value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
        private static string KnownNumber(double value) { return value < 0 ? "未知" : Number(value); }
        private static string Count(int value) { return value < 0 ? "未知" : value.ToString(CultureInfo.InvariantCulture); }
    }

    internal sealed class ThreadRelationCollector
    {
        internal const int TransitionsPerBlock = 256, MaximumBlocks = 1024;
        internal const int MaximumTransitions = MaximumBlocks * TransitionsPerBlock, MaximumPresents = 8192;
        // 旧常量名供诊断与既有台架引用，仅表示软水位；固定窗口内按实际事件量扩展，不据此弃样。
        internal const int MaximumThreadTransitions = MaximumTransitions;
        internal const int MaximumEventBytes = MaximumTransitions * 16;
        private struct Transition
        {
            internal long Time;
            internal int Sequence;
            private ushort processorIndex;
            private byte kindAndUnknownCpu;
            internal byte State;

            // 真实 CPU 索引占满 ushort；来源未知的兼容 Ready 记录借用种类保留位，仍保持每条 16 字节。
            internal int Cpu
            {
                get { return (kindAndUnknownCpu & 0x80) == 0 ? processorIndex : -1; }
                set
                {
                    if (value < -1 || value > ushort.MaxValue) throw new ArgumentOutOfRangeException("value");
                    processorIndex = value < 0 ? (ushort)0 : (ushort)value;
                    kindAndUnknownCpu = (byte)((kindAndUnknownCpu & 0x7F) | (value < 0 ? 0x80 : 0));
                }
            }
            internal byte Kind
            {
                get { return (byte)(kindAndUnknownCpu & 0x7F); }
                set { kindAndUnknownCpu = (byte)((kindAndUnknownCpu & 0x80) | value); }
            }
        }
        private struct SelectedTransition
        {
            internal int Tid;
            internal Transition Value;
        }
        private sealed class Bucket
        {
            internal readonly long Creation;
            internal readonly List<Transition[]> Blocks = new List<Transition[]>();
            internal int Count;
            internal bool Overflowed;
            internal Bucket(long creation) { Creation = creation; }
        }
        private struct Present
        {
            internal long Time;
            internal int Tid;
        }
        private sealed class Track
        {
            internal readonly int Tid;
            internal readonly long Creation;
            internal readonly long Begin, Half, End;
            internal int State, Cpu = -1, Conflicts, EarlyWakes, LastRunCpu = -1, Migrations;
            // 运行中收到唤醒时暂存，待线程下次转换确认是否为切出前唤醒。
            internal bool WakePending;
            internal long WakeAt;
            internal string WakeDetail;
            internal long At, Known, Running, Ready;
            internal long ReadyFirst, ReadySecond, KnownFirst, KnownSecond, RunningFirst, RunningSecond;
            internal int ReadyFirstIntervals, ReadySecondIntervals;
            internal readonly List<double> Runs, Readies;
            internal readonly Dictionary<int, long> Cpus;
            internal readonly int IntervalCapacity;
            internal int RunningBursts, ReadyIntervals;
            internal Track(int tid, long creation, long begin, long half, long end, bool detailed, int transitionCount)
            {
                Tid = tid; Creation = creation; At = Begin = begin; Half = half; End = end;
                IntervalCapacity = (int)Math.Min(int.MaxValue, (long)transitionCount + 1);
                Cpus = new Dictionary<int, long>();
                if (detailed)
                { Runs = new List<double>(); Readies = new List<double>(); }
            }
        }

        private readonly Dictionary<int, Bucket> buckets = new Dictionary<int, Bucket>();
        private readonly List<Present> presents = new List<Present>(512);
        private readonly long begin, half, end, frequency;
        private readonly int trackedThreads;
        private int recordedTransitions, allocatedBlocks, overflowedThreads, sequence;
        private long recordedThrough;
        private string invalid;
        // 键只接收原生 16 位 CPU 索引；已出现的零线程也保留，区分空闲与尚无上下文。
        private readonly Dictionary<int, int> workloadRunning = new Dictionary<int, int>();
        private bool workloadCpuConsistent = true;

        internal ThreadRelationCollector(Dictionary<int, long> threads, long start, long midpoint, long finish, long clockFrequency)
        {
            begin = start; half = midpoint; end = finish; frequency = clockFrequency;
            trackedThreads = threads.Count;
            if (frequency <= 0 || begin >= half || half >= end)
            { invalid = "采样范围不可核验"; return; }
            foreach (KeyValuePair<int, long> identity in threads)
            {
                buckets.Add(identity.Key, new Bucket(identity.Value));
                if (identity.Key <= 0 || identity.Value <= 0) invalid = "采样线程身份不可核验";
            }
        }

        internal void Invalidate(string reason) { if (invalid == null) invalid = reason; }

        internal void Switch(long time, int cpu, int incoming, int outgoing, byte oldState)
        {
            if (invalid != null || time < begin || time > end) return;
            if (cpu < 0 || cpu > ushort.MaxValue) { Invalidate("处理器编号超过关系摘要支持范围"); return; }
            int previous;
            if (workloadRunning.TryGetValue(cpu, out previous) && previous != outgoing) workloadCpuConsistent = false;
            workloadRunning[cpu] = incoming;
            Add(time, outgoing, cpu, 0, oldState);
            Add(time, incoming, cpu, 2, 2);
        }

        internal void Ready(long time, int target, byte flags)
        {
            // 旧调用没有来源 CPU 证据，保留未知，不与真实的 CPU65535 混同。
            if ((flags & 6) == 0) Add(time, target, -1, 1, flags);
        }

        internal void Ready(long time, int target, byte flags, int sourceCpu)
        {
            if (invalid != null || time < begin || time > end) return;
            if (sourceCpu < 0 || sourceCpu > ushort.MaxValue) { Invalidate("处理器编号超过关系摘要支持范围"); return; }
            // 栈或地址空间尚未换入时还不能派发；后续真正 Ready 的事件才开始计时。
            // Kind=1 不读取 State 重建状态，复用该字节保留原始标志，不增加事件数组大小。
            if ((flags & 6) == 0) Add(time, target, sourceCpu, 1, flags);
        }

        private void Add(long time, int tid, int cpu, byte kind, byte state)
        {
            Bucket bucket;
            if (invalid != null || time < begin || time > end || !buckets.TryGetValue(tid, out bucket) || bucket.Overflowed) return;
            // 仅整数寻址或真实分配失败使采集无效，软水位不丢弃高频线程或竞争者。
            if (recordedTransitions == int.MaxValue) { Invalidate("状态转换数量超过集合可寻址范围"); return; }
            int offset = bucket.Count % TransitionsPerBlock;
            if (offset == 0)
            {
                try { bucket.Blocks.Add(new Transition[TransitionsPerBlock]); }
                catch (OutOfMemoryException) { Invalidate("调度状态块分配失败，实际内存不足"); return; }
                allocatedBlocks++;
            }
            bucket.Blocks[bucket.Count / TransitionsPerBlock][offset] = new Transition {
                Time = time, Sequence = sequence++, Cpu = cpu, Kind = kind, State = state };
            bucket.Count++;
            recordedTransitions++;
            recordedThrough = Math.Max(recordedThrough, time);
        }

        internal void SuccessfulPresent(int tid, long time)
        {
            if (invalid != null || time < begin || time > end || !buckets.ContainsKey(tid)) return;
            try { presents.Add(new Present { Tid = tid, Time = time }); }
            catch (OutOfMemoryException) { Invalidate("画面提交记录分配失败，实际内存不足"); return; }
            recordedThrough = Math.Max(recordedThrough, time);
        }

        internal ThreadRelationSnapshot Build(FrameCoordinationSample sample, int presenter,
            Dictionary<long, int>[] edges, FrameCaptureIssue captureIssue, Func<int, long, bool> verifyIdentity, bool exportWorkload = true,
            Func<bool> continueBuild = null)
        {
            try { return BuildCore(sample, presenter, edges, captureIssue, verifyIdentity, exportWorkload, continueBuild); }
            catch (BuildStoppedException)
            {
                return new ThreadRelationSnapshot(sample.Pid, sample.Birth, begin, end, frequency, false,
                    "摘要处理继续资格已撤销，未发布部分工作图", sample.LostEvents, null, null,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, trackedThreads, recordedTransitions, presents.Count,
                    recordedThrough, allocatedBlocks, overflowedThreads, null, half);
            }
            catch (OutOfMemoryException)
            {
                return new ThreadRelationSnapshot(sample.Pid, sample.Birth, begin, end, frequency, false,
                    "摘要分配失败，实际内存不足，未发布部分工作图", sample.LostEvents, null, null,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, trackedThreads, recordedTransitions, presents.Count,
                    recordedThrough, allocatedBlocks, overflowedThreads, null, half);
            }
        }

        private sealed class BuildStoppedException : Exception { }

        private static void CheckBuild(Func<bool> continueBuild)
        { if (continueBuild != null && !continueBuild()) throw new BuildStoppedException(); }

        private ThreadRelationSnapshot BuildCore(FrameCoordinationSample sample, int presenter,
            Dictionary<long, int>[] edges, FrameCaptureIssue captureIssue, Func<int, long, bool> verifyIdentity,
            bool exportWorkload, Func<bool> continueBuild)
        {
            CheckBuild(continueBuild);
            string reason = invalid;
            if (captureIssue != FrameCaptureIssue.None) reason = "采集未通过核验：" + captureIssue;
            if (sample.LostEvents != 0) reason = "采样事件丢失";
            Bucket presenterBucket = null;
            bool knownPresenter = presenter > 0 && buckets.TryGetValue(presenter, out presenterBucket);
            long presenterBirth = knownPresenter ? presenterBucket.Creation : 0;
            if (!knownPresenter) reason = reason ?? "未取得稳定呈现线程";
            int worker = knownPresenter ? ChooseWorker(presenter, edges) : 0;
            Bucket workerBucket = worker > 0 ? buckets[worker] : null;
            long workerBirth = workerBucket != null ? workerBucket.Creation : 0;
            if (presenterBucket != null && presenterBucket.Overflowed) reason = reason ?? "呈现线程状态转换超过单线程诊断预算";
            // 伴随序列溢出仅排除该线程对；呈现序列完整时仍可独立使用，不改变其他无效原因。
            int truncatedWorker = 0;
            if (workerBucket != null && workerBucket.Overflowed && reason == null)
            { truncatedWorker = worker; worker = 0; workerBucket = null; workerBirth = 0; }
            if (verifyIdentity != null && knownPresenter && !verifyIdentity(presenter, presenterBirth)) reason = "呈现线程身份已变化或不可读";
            if (verifyIdentity != null && worker > 0 && !verifyIdentity(worker, workerBirth)) reason = "伴随线程身份已变化或不可读";
            // 全线程和角色重建复用按实际事件量分配的暂存区，避免为每个线程保留全量副本。
            var selected = new List<SelectedTransition>(recordedTransitions);
            SelectTransitions(selected, presenter, presenterBucket);
            SelectTransitions(selected, worker, workerBucket);
            CheckBuild(continueBuild);
            selected.Sort(CompareTransitions);
            CheckBuild(continueBuild);
            Track a = knownPresenter ? new Track(presenter, presenterBirth, begin, half, end, true, presenterBucket.Count) : null;
            Track b = worker > 0 ? new Track(worker, workerBirth, begin, half, end, true, workerBucket.Count) : null;
            long overlap = 0, runningOverlap = 0, at = begin;
            bool sameCpuOverlap = false;
            var conflictDetails = new List<string>(4);
            for (int selectedIndex = 0; selectedIndex < selected.Count; selectedIndex++)
            {
                if ((selectedIndex & 1023) == 0) CheckBuild(continueBuild);
                SelectedTransition entry = selected[selectedIndex];
                ObserveOverlap(a, b, at, Math.Max(0, entry.Value.Time - at), ref overlap, ref runningOverlap, ref sameCpuOverlap, conflictDetails);
                at = entry.Value.Time;
                Track current = entry.Tid == presenter ? a : b;
                if (current != null) Advance(current, entry.Value, begin, frequency, conflictDetails, selected, selectedIndex);
            }
            ObserveOverlap(a, b, at, Math.Max(0, end - at), ref overlap, ref runningOverlap, ref sameCpuOverlap, conflictDetails);
            SettleTail(a, end, frequency, conflictDetails);
            SettleTail(b, end, frequency, conflictDetails);
            if (a != null) Finish(a, end, frequency);
            if (b != null) Finish(b, end, frequency);
            if (sameCpuOverlap) reason = reason ?? "不同线程在同一CPU出现重叠运行区间";
            if ((a != null && a.Conflicts != 0) || (b != null && b.Conflicts != 0)) reason = reason ?? "线程状态重建存在歧义";
            if (a != null && a.Known == 0) reason = reason ?? "未取得可重建的呈现线程调度区间";
            if (b != null && b.Known == 0) reason = reason ?? "未取得可重建的伴随线程调度区间";
            var intervals = PresentIntervals(presenter);
            bool valid = reason == null;
            CheckBuild(continueBuild);
            ThreadRelationMetrics presenterMetrics = Metrics(a, valid), workerMetrics = Metrics(b, valid);
            ReleaseIntervals(a); ReleaseIntervals(b);
            CheckBuild(continueBuild);
            // 渲染策略只消费原呈现/伙伴关系；完整工作图只在工作组用途明确请求时重建。
            WorkloadExport workload = exportWorkload ? ExportWorkload(sample, edges, captureIssue, presenter, worker, verifyIdentity, selected, continueBuild)
                : new WorkloadExport { Reason = "本次仅识别渲染角色，未重建全线程工作图" };
            CheckBuild(continueBuild);
            return new ThreadRelationSnapshot(sample.Pid, sample.Birth, begin, end, frequency, valid,
                reason ?? (truncatedWorker != 0 ? TruncatedWorkerText(truncatedWorker)
                    : worker == 0 ? "未取得稳定双向唤醒线程" : "固定线程身份、双向唤醒与调度区间已记录"), sample.LostEvents,
                presenterMetrics, workerMetrics, Edge(edges, 0, worker, presenter), Edge(edges, 1, worker, presenter),
                Edge(edges, 0, presenter, worker), Edge(edges, 1, presenter, worker), Milliseconds(overlap), Milliseconds(runningOverlap),
                intervals.Count, Percentile(intervals, .50), Percentile(intervals, .95), Percentile(intervals, .99),
                trackedThreads, recordedTransitions, presents.Count, recordedThrough, allocatedBlocks, overflowedThreads,
                string.Join(" | ", conflictDetails.ToArray()), half, workload.Threads, workload.Edges,
                workload.Valid, workload.Reason, workload.TotalRunning, workload.Truncated, trackedThreads,
                workload.Ledger, workload.LedgerComplete, workload.LedgerCapacityComplete,
                workload.CapacityGapThreads, workload.CapacityGapRunning);
        }

        private sealed class WorkloadExport
        {
            internal ThreadRelationMetrics[] Threads = new ThreadRelationMetrics[0];
            internal ThreadRelationMetrics[] Ledger = new ThreadRelationMetrics[0];
            internal UnifiedWorkloadEdge[] Edges = new UnifiedWorkloadEdge[0];
            internal bool Valid, Truncated, LedgerComplete, LedgerCapacityComplete;
            internal double TotalRunning, CapacityGapRunning;
            internal int CapacityGapThreads;
            internal string Reason;
        }

        private WorkloadExport ExportWorkload(FrameCoordinationSample sample, Dictionary<long, int>[] edges,
            FrameCaptureIssue issue, int presenter, int worker, Func<int, long, bool> verifyIdentity,
            List<SelectedTransition> transitions, Func<bool> continueBuild)
        {
            var result = new WorkloadExport();
            var watch = Stopwatch.StartNew();
            if (invalid != null || issue != FrameCaptureIssue.None || sample.LostEvents != 0 || frequency <= 0)
            { result.Reason = invalid ?? "全线程窗口未通过采集完整性核验"; return result; }
            if (sample.ThreadIdentityReadFailures > 0)
            {
                result.Truncated = true;
                result.Reason = "初始线程身份未完整核验；未核验线程数=" + sample.ThreadIdentityReadFailures
                    + "；首个线程=" + sample.FirstUnverifiedThreadId + "；错误码=" + sample.FirstThreadIdentityError;
                return result;
            }
            if (!workloadCpuConsistent)
            { result.Reason = "同一CPU的切入切出链不连续，不能把重叠运行算入全线程图"; return result; }
            // 全量轻账本保留每CPU运行时间；精确分位数仍只对有限深入角色重建，避免扩大首次接管规模。
            var metrics = new List<Track>(buckets.Count);
            var ledger = new Dictionary<int, ThreadRelationMetrics>();
            var capacityKnown = new HashSet<int>();
            foreach (KeyValuePair<int, Bucket> entry in buckets)
            {
                CheckBuild(continueBuild);
                if (entry.Value.Overflowed) { result.Truncated = true; continue; }
                Track track = Reconstruct(entry.Key, entry.Value, false, transitions, continueBuild);
                result.TotalRunning += Milliseconds(track.Running);
                metrics.Add(track);
                bool identityVerified = verifyIdentity == null || verifyIdentity(track.Tid, track.Creation);
                // 已退出身份不能再写，但同创建时间、包围采集窗的计数零增长仍可证明历史零负载。
                ThreadRelationMetrics summary = Metrics(track, true, sample.CpuZeroGrowthVerified(track.Tid, track.Creation, begin, end));
                if (identityVerified)
                { ledger.Add(track.Tid, summary); capacityKnown.Add(track.Tid); }
                else
                {
                    result.Truncated = true;
                    if (summary.KnownZeroRunning(Milliseconds(end - begin), Milliseconds(half - begin), Milliseconds(end - half)))
                        capacityKnown.Add(track.Tid);
                    // 随账本导出短命线程缺口数量，供容量诊断。
                    else { result.CapacityGapThreads++; result.CapacityGapRunning += summary.RunningMs; }
                }
            }
            metrics.Sort(delegate(Track left, Track right)
            {
                int order = (Milliseconds(right.Running) + Milliseconds(right.Ready))
                    .CompareTo(Milliseconds(left.Running) + Milliseconds(left.Ready));
                return order == 0 ? left.Tid.CompareTo(right.Tid) : order;
            });
            var selected = new List<Track>(32);
            // 保留呈现身份，不占用计算候选名额，也不默认选为主候选。
            foreach (Track value in metrics)
                if ((value.Tid == presenter || value.Tid == worker) && selected.Count < 32) selected.Add(value);
            foreach (Track value in metrics)
            {
                if (selected.Count >= 32) break;
                if (value.Running + value.Ready > 0 && !selected.Exists(item => item.Tid == value.Tid)) selected.Add(value);
            }
            var confirmed = new List<ThreadRelationMetrics>(selected.Count);
            foreach (Track value in selected)
            {
                CheckBuild(continueBuild);
                if (!ledger.ContainsKey(value.Tid)) continue;
                if (verifyIdentity != null && !verifyIdentity(value.Tid, value.Creation))
                {
                    result.Truncated = true;
                    if (!ledger[value.Tid].KnownZeroRunning(Milliseconds(end - begin), Milliseconds(half - begin), Milliseconds(end - half)))
                    { capacityKnown.Remove(value.Tid); result.CapacityGapThreads++; result.CapacityGapRunning += ledger[value.Tid].RunningMs; }
                    ledger.Remove(value.Tid); continue;
                }
                Track detail = Reconstruct(value.Tid, buckets[value.Tid], true, transitions, continueBuild);
                confirmed.Add(Metrics(detail, true));
                ReleaseIntervals(detail);
                CheckBuild(continueBuild);
            }
            var selectedIds = new HashSet<int>();
            foreach (ThreadRelationMetrics value in confirmed) selectedIds.Add(value.Tid);
            var graph = new List<UnifiedWorkloadEdge>();
            if (edges != null && edges.Length == 2 && edges[0] != null && edges[1] != null)
            {
                var keys = new HashSet<long>(edges[0].Keys); keys.UnionWith(edges[1].Keys);
                foreach (long key in keys)
                {
                    if ((graph.Count & 1023) == 0) CheckBuild(continueBuild);
                    int target = (int)(key >> 32), source = (int)(uint)key;
                    if (source == target || !selectedIds.Contains(source) || !selectedIds.Contains(target)) continue;
                    int first, second; edges[0].TryGetValue(key, out first); edges[1].TryGetValue(key, out second);
                    if (first != 0 || second != 0) graph.Add(new UnifiedWorkloadEdge(source, target, first, second));
                }
            }
            graph.Sort(delegate(UnifiedWorkloadEdge left, UnifiedWorkloadEdge right)
            {
                int order = Math.Min(right.First, right.Second).CompareTo(Math.Min(left.First, left.Second));
                if (order != 0) return order;
                order = left.Source.CompareTo(right.Source);
                return order != 0 ? order : left.Target.CompareTo(right.Target);
            });
            if (graph.Count > 128) { graph.RemoveRange(128, graph.Count - 128); result.Truncated = true; }
            result.Threads = confirmed.ToArray(); result.Edges = graph.ToArray();
            result.Ledger = new List<ThreadRelationMetrics>(ledger.Values).ToArray();
            result.LedgerComplete = ledger.Count == buckets.Count && overflowedThreads == 0;
            result.LedgerCapacityComplete = capacityKnown.Count == buckets.Count && overflowedThreads == 0;
            result.Valid = confirmed.Count != 0 && overflowedThreads == 0;
            result.Reason = result.Valid ? "全量身份与CPU运行账本独立核验；深入候选上限32、关系边上限128，候选范围不代表账本覆盖"
                : "全线程账本存在单线程溢出或没有可用身份";
            result.Reason += "；摘要处理毫秒=" + watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
            return result;
        }

        private Track Reconstruct(int tid, Bucket bucket, bool detailed, List<SelectedTransition> transitions,
            Func<bool> continueBuild)
        {
            transitions.Clear();
            SelectTransitions(transitions, tid, bucket);
            CheckBuild(continueBuild);
            transitions.Sort(CompareTransitions);
            CheckBuild(continueBuild);
            var track = new Track(tid, bucket.Creation, begin, half, end, detailed, bucket.Count);
            var details = new List<string>(4);
            for (int index = 0; index < transitions.Count; index++)
            {
                if ((index & 1023) == 0) CheckBuild(continueBuild);
                Advance(track, transitions[index].Value, begin, frequency, details, transitions, index);
            }
            SettleTail(track, end, frequency, details); Finish(track, end, frequency);
            return track;
        }

        private static int CompareTransitions(SelectedTransition left, SelectedTransition right)
        {
            int order = left.Value.Time.CompareTo(right.Value.Time);
            // 等时转换保留接收顺序，不按事件类型猜测跨CPU因果。
            return order == 0 ? left.Value.Sequence.CompareTo(right.Value.Sequence) : order;
        }

        internal static string TruncatedWorkerText(int tid)
        { return "伴随线程 " + tid.ToString(CultureInfo.InvariantCulture) + " 的状态转换超过单线程诊断预算，未纳入成对指标；本次只按呈现线程成立"; }

        private static void SelectTransitions(List<SelectedTransition> selected, int tid, Bucket bucket)
        {
            if (bucket == null) return;
            for (int index = 0; index < bucket.Count; index++)
                selected.Add(new SelectedTransition { Tid = tid,
                    Value = bucket.Blocks[index / TransitionsPerBlock][index % TransitionsPerBlock] });
        }

        private int ChooseWorker(int presenter, Dictionary<long, int>[] edges)
        {
            int best = 0, maximum = 0, next = 0;
            foreach (int tid in buckets.Keys)
            {
                if (tid == presenter) continue;
                int a = Edge(edges, 0, tid, presenter), b = Edge(edges, 1, tid, presenter);
                int c = Edge(edges, 0, presenter, tid), d = Edge(edges, 1, presenter, tid);
                if (Math.Min(Math.Min(a, b), Math.Min(c, d)) < 3) continue;
                int score = a + b + c + d;
                if (score > maximum) { next = maximum; maximum = score; best = tid; }
                else next = Math.Max(next, score);
            }
            return maximum > 0 && (next == 0 || maximum >= next * 1.25) ? best : 0;
        }

        private static int Edge(Dictionary<long, int>[] edges, int half, int target, int source)
        {
            int value;
            return target > 0 && source > 0 && edges != null && edges.Length == 2 && edges[half] != null
                && edges[half].TryGetValue(((long)target << 32) | (uint)source, out value) ? value : 0;
        }

        private static bool AddOverlap(Track a, Track b, long elapsed, ref long demand, ref long running)
        {
            if (a == null || b == null || elapsed <= 0) return false;
            if ((a.State == 1 || a.State == 2) && (b.State == 1 || b.State == 2)) demand += elapsed;
            if (a.State == 2 && b.State == 2) running += elapsed;
            return a.State == 2 && b.State == 2 && a.Cpu == b.Cpu;
        }

        private void ObserveOverlap(Track a, Track b, long time, long elapsed, ref long demand, ref long running,
            ref bool sameCpuOverlap, List<string> details)
        {
            if (!AddOverlap(a, b, elapsed, ref demand, ref running)) return;
            if (!sameCpuOverlap && details.Count < 4)
                details.Add("同CPU双运行：线程=" + a.Tid + "/" + b.Tid + "；CPU=" + a.Cpu
                    + "；时刻毫秒=" + Milliseconds(time - begin).ToString("F3", CultureInfo.InvariantCulture)
                    + "；持续毫秒=" + Milliseconds(elapsed).ToString("F3", CultureInfo.InvariantCulture));
            sameCpuOverlap = true;
        }

        // 切出前唤醒最多相隔 100 微秒，容纳中断引起的送达差异。
        internal const int EarlyWakeToleranceMicroseconds = 100;

        private static long EarlyWakeTicks(long frequency)
        { return Math.Max(1, frequency * EarlyWakeToleranceMicroseconds / 1000000); }

        private static string ConflictText(Track track, Transition next, long begin, long frequency,
            List<SelectedTransition> selected, int selectedIndex)
        {
            var text = new StringBuilder("线程=").Append(track.Tid).Append("；时刻毫秒=")
                .Append((frequency > 0 ? (next.Time - begin) * 1000.0 / frequency : 0).ToString("F3", CultureInfo.InvariantCulture))
                .Append("；事件=").Append(next.Kind == 1 ? "Ready" : "切入").Append("；原状态=").Append(track.State)
                .Append("；原CPU=").Append(track.Cpu).Append("；事件CPU=").Append(next.Cpu);
            text.Append("；QPC=").Append(next.Time).Append("；接收序号=").Append(next.Sequence);
            if (next.Kind == 1) text.Append("；ReadyFlags=0x").Append(next.State.ToString("X2", CultureInfo.InvariantCulture));
            text.Append("；序号范围=本次已记录转换");
            AppendAdjacentSwitch(text, selected, selectedIndex, track.Tid, -1);
            AppendAdjacentSwitch(text, selected, selectedIndex, track.Tid, 1);
            return text.ToString();
        }

        // 挂起的唤醒没有等到切出：按原判据记一次歧义，状态从唤醒那一刻起未知。
        private static void RejectPendingWake(Track track, long frequency, List<string> details)
        {
            Finish(track, track.WakeAt, frequency);
            track.Conflicts++; track.State = 0; track.At = track.WakeAt;
            if (track.WakeDetail != null && details.Count < 4) details.Add(track.WakeDetail);
            track.WakePending = false; track.WakeDetail = null;
        }

        // 关窗时仍未切出：容许间隔内保留运行尾段，超时记为歧义。
        private static void SettleTail(Track track, long end, long frequency, List<string> details)
        {
            if (track == null || !track.WakePending) return;
            if (end - track.WakeAt <= EarlyWakeTicks(frequency)) { track.WakePending = false; track.WakeDetail = null; return; }
            RejectPendingWake(track, frequency, details);
        }

        private static void Advance(Track track, Transition next, long begin, long frequency, List<string> details,
            List<SelectedTransition> selected, int selectedIndex)
        {
            if (next.Kind == 1 && track.State == 1) return;
            if (track.WakePending)
            {
                // 等切出期间重复到达的唤醒不另算，间隔仍从第一次算起。
                if (next.Kind == 1) return;
                if (next.Kind == 0 && next.Cpu == track.Cpu && next.Time - track.WakeAt <= EarlyWakeTicks(frequency))
                {
                // 唤醒可能早于切出送达；非终止线程在切出后直接进入就绪状态。
                    Finish(track, next.Time, frequency);
                    track.EarlyWakes++;
                    track.WakePending = false; track.WakeDetail = null;
                    track.State = next.State == 4 ? 0 : 1; track.Cpu = -1; track.At = next.Time;
                    return;
                }
                RejectPendingWake(track, frequency, details);
            }
            if (track.State == 2 && next.Kind == 1)
            {
                // 运行中收到唤醒时保持计时，暂存事件以待后续确认。
                track.WakePending = true; track.WakeAt = next.Time;
                track.WakeDetail = track.Runs != null && details.Count < 4 ? ConflictText(track, next, begin, frequency, selected, selectedIndex) : null;
                return;
            }
            if (track.Runs != null && track.State == 2 && next.Kind == 2 && details.Count < 4)
                details.Add(ConflictText(track, next, begin, frequency, selected, selectedIndex));
            Finish(track, next.Time, frequency);
            if (next.Kind == 2)
            {
                if (track.State == 2) track.Conflicts++;
                if (track.LastRunCpu >= 0 && track.LastRunCpu != next.Cpu) track.Migrations++;
                track.LastRunCpu = next.Cpu;
                track.State = 2; track.Cpu = next.Cpu;
            }
            else if (next.Kind == 1)
            {
                track.State = 1;
            }
            else
            {
                // Ready、Standby、DeferredReady 都表示仍有运行需求；Waiting 才是阻塞。
                track.State = next.State == 1 || next.State == 3 || next.State == 7 ? 1 : next.State == 5 ? 5 : 0;
                track.Cpu = -1;
            }
            track.At = next.Time;
        }

        private static void AppendAdjacentSwitch(StringBuilder text, List<SelectedTransition> selected, int selectedIndex, int tid, int direction)
        {
            text.Append(direction < 0 ? "；前CSwitch={" : "；后CSwitch={");
            // 仅在已选角色的现有序列内查找；最多四处冲突复用同一序列，不另存全量事件。
            for (int index = selectedIndex + direction; index >= 0 && index < selected.Count; index += direction)
            {
                SelectedTransition entry = selected[index];
                if (entry.Tid != tid || entry.Value.Kind == 1) continue;
                text.Append("QPC=").Append(entry.Value.Time).Append(" 接收序号=").Append(entry.Value.Sequence)
                    .Append(" CPU=").Append(entry.Value.Cpu).Append(" 类型=").Append(entry.Value.Kind == 0 ? "切出" : "切入");
                text.Append('}');
                return;
            }
            text.Append("无}");
        }

        private static void Finish(Track track, long time, long frequency)
        {
            long ticks = Math.Max(0, time - track.At);
            // 在已重建的原区间上裁剪半窗，不增加转换或把跨界等待拆成两个分位数样本。
            long first = Math.Max(0, Math.Min(time, track.Half) - Math.Max(track.At, track.Begin));
            long second = Math.Max(0, Math.Min(time, track.End) - Math.Max(track.At, track.Half));
            if (track.State != 0)
            {
                track.Known += ticks;
                track.KnownFirst += first;
                track.KnownSecond += second;
            }
            if (track.State == 2)
            {
                track.Running += ticks;
                track.RunningFirst += first; track.RunningSecond += second;
                if (ticks > 0)
                {
                    track.RunningBursts++;
                    if (track.Runs != null) AddInterval(track.Runs, ticks * 1000.0 / frequency, track.IntervalCapacity);
                }
                if (track.Cpus != null)
                {
                    long old;
                    track.Cpus.TryGetValue(track.Cpu, out old); track.Cpus[track.Cpu] = old + ticks;
                }
            }
            else if (track.State == 1)
            {
                track.Ready += ticks;
                track.ReadyFirst += first;
                track.ReadySecond += second;
                if (ticks > 0)
                {
                    track.ReadyIntervals++;
                    if (track.Readies != null) AddInterval(track.Readies, ticks * 1000.0 / frequency, track.IntervalCapacity);
                    // 同一区间只归属其起点半窗，后半可有等待时长而没有新起始片段。
                    if (track.At < track.Half) track.ReadyFirstIntervals++;
                    else track.ReadySecondIntervals++;
                }
            }
        }

        private static void AddInterval(List<double> intervals, double value, int capacity)
        {
            if (intervals.Count >= capacity) throw new InvalidOperationException("线程区间数量超过转换预算");
            if (intervals.Count == intervals.Capacity)
                intervals.Capacity = (int)Math.Min(capacity, Math.Max(4L, (long)intervals.Capacity * 2));
            intervals.Add(value);
        }

        private static void ReleaseIntervals(Track track)
        {
            if (track == null || track.Runs == null) return;
            track.Runs.Clear(); track.Runs.Capacity = 0;
            track.Readies.Clear(); track.Readies.Capacity = 0;
        }

        private ThreadRelationMetrics Metrics(Track track, bool complete, bool cpuZeroGrowthVerified = false)
        {
            if (track == null) return null;
            if (track.Runs != null) { track.Runs.Sort(); track.Readies.Sort(); }
            var keys = new List<int>(track.Cpus.Keys); keys.Sort();
            var cpus = new StringBuilder();
            var cpuRunning = new List<KeyValuePair<int, double>>(keys.Count);
            foreach (int cpu in keys)
            {
                cpuRunning.Add(new KeyValuePair<int, double>(cpu, Milliseconds(track.Cpus[cpu])));
                if (cpus.Length > 0) cpus.Append(',');
                cpus.Append("CPU").Append(cpu).Append(':').Append(Milliseconds(track.Cpus[cpu]).ToString("F3", CultureInfo.InvariantCulture)).Append("ms");
            }
            return new ThreadRelationMetrics(track.Tid, track.Creation, Milliseconds(track.Running), Milliseconds(track.Ready), Milliseconds(track.Known),
                track.RunningBursts, Percentile(track.Runs, .95), Percentile(track.Runs, 1), track.ReadyIntervals,
                Percentile(track.Readies, .95), Percentile(track.Readies, 1), track.Conflicts, cpus.Length == 0 ? "未知" : cpus.ToString(),
                complete && track.KnownFirst > 0 ? Milliseconds(track.ReadyFirst) : -1,
                complete && track.KnownSecond > 0 ? Milliseconds(track.ReadySecond) : -1,
                complete && track.KnownFirst > 0 ? Milliseconds(track.KnownFirst) : -1,
                complete && track.KnownSecond > 0 ? Milliseconds(track.KnownSecond) : -1,
                complete && track.KnownFirst > 0 ? track.ReadyFirstIntervals : -1,
                complete && track.KnownSecond > 0 ? track.ReadySecondIntervals : -1, track.EarlyWakes,
                complete && track.KnownFirst > 0 ? Milliseconds(track.RunningFirst) : -1,
                complete && track.KnownSecond > 0 ? Milliseconds(track.RunningSecond) : -1, track.Migrations, cpuRunning.ToArray(), cpuZeroGrowthVerified);
        }

        private List<double> PresentIntervals(int presenter)
        {
            var stamps = new List<long>();
            foreach (Present value in presents) if (value.Tid == presenter) stamps.Add(value.Time);
            stamps.Sort();
            var intervals = new List<double>();
            for (int index = 1; index < stamps.Count; index++) if (stamps[index] > stamps[index - 1]) intervals.Add(Milliseconds(stamps[index] - stamps[index - 1]));
            intervals.Sort(); return intervals;
        }

        private double Milliseconds(long ticks) { return frequency > 0 ? ticks * 1000.0 / frequency : 0; }
        private static double Percentile(List<double> sorted, double fraction)
        { return sorted == null ? -1 : sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(sorted.Count * fraction) - 1))]; }
    }
}
