using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
#if PAVISE_SELFTEST
    internal sealed class ThreadRelationMetrics
    {
        internal readonly int Tid, RunningBursts, ReadyIntervals, StateConflicts;
        // 切出前唤醒属于正常内核时序 单独计数 不计为状态歧义
        internal readonly int EarlyWakes;
        internal readonly long Creation;
        internal readonly double RunningMs, ReadyMs, KnownMs, RunBurstP95Ms, MaxRunBurstMs, ReadyP95Ms, MaxReadyMs;
        internal readonly double ReadyFirstMs, ReadySecondMs, KnownFirstMs, KnownSecondMs;
        internal readonly int ReadyFirstIntervals, ReadySecondIntervals;
        internal readonly string CpuDistribution;

        internal ThreadRelationMetrics(int tid, long creation, double running, double ready, double known,
            int bursts, double burstP95, double maximumBurst, int readyIntervals, double readyP95,
            double maximumReady, int conflicts, string cpus, double readyFirst = -1, double readySecond = -1,
            double knownFirst = -1, double knownSecond = -1, int readyFirstIntervals = -1, int readySecondIntervals = -1,
            int earlyWakes = 0)
        {
            EarlyWakes = earlyWakes;
            Tid = tid; Creation = creation; RunningMs = running; ReadyMs = ready; KnownMs = known;
            RunningBursts = bursts; RunBurstP95Ms = burstP95; MaxRunBurstMs = maximumBurst;
            ReadyIntervals = readyIntervals; ReadyP95Ms = readyP95; MaxReadyMs = maximumReady;
            StateConflicts = conflicts; CpuDistribution = cpus;
            ReadyFirstMs = readyFirst; ReadySecondMs = readySecond;
            KnownFirstMs = knownFirst; KnownSecondMs = knownSecond;
            ReadyFirstIntervals = readyFirstIntervals; ReadySecondIntervals = readySecondIntervals;
        }

    }

    // 仅保存固定窗口调度摘要 唤醒关系不能证明共享数据 锁归属或完整帧依赖
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
        internal bool HasStablePair { get { return Worker != null && Worker.Tid > 0; } }

        internal static ThreadRelationSnapshot Unavailable(int pid, long birth, long begin, long end, long frequency, uint lost, string reason)
        { return new ThreadRelationSnapshot(pid, birth, begin, end, frequency, false, reason, lost, null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); }

        internal ThreadRelationSnapshot(int pid, long birth, long begin, long end, long frequency, bool valid, string reason,
            uint lost, ThreadRelationMetrics presenter, ThreadRelationMetrics worker, int forwardFirst, int forwardSecond,
            int reverseFirst, int reverseSecond, double demandOverlap, double runningOverlap,
            int presentIntervals, double p50, double p95, double p99,
            int trackedThreads = -1, int recordedTransitions = -1, int recordedPresents = -1, long recordedThrough = 0,
            int allocatedBlocks = -1, int overflowedThreads = -1, string conflictDetails = null, long midpoint = -1)
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
                // 截断后的最后状态可能延伸到请求窗口末尾 不能把这些数值当作完整窗口结论
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
#endif

    internal sealed class ThreadRelationCollector
    {
        internal const int TransitionsPerBlock = 256, EmergencyMaximumBlocks = 16384;
#if PAVISE_SELFTEST
        internal const int MaximumBlocks = 1024;
        internal const int MaximumTransitions = MaximumBlocks * TransitionsPerBlock, MaximumPresents = 8192;
        // 这个别名供诊断和现有台架引用 只表示软水位 固定窗口内按实际事件量扩展 不据此弃样
        internal const int MaximumThreadTransitions = MaximumTransitions;
        internal const int MaximumEventBytes = MaximumTransitions * 16;
        // 应急边界保留高负载完整窗口 触及后不发布部分图 仍由原请求结算本次采集
        internal const int EmergencyMaximumPresents = 65536;
#endif
        private struct Transition
        {
            internal long Time;
            internal int Sequence;
            private ushort processorIndex;
            private byte kindAndUnknownCpu;
            internal byte State;

            // 真实 CPU 索引占满 ushort 来源未知的兼容 Ready 记录借用种类保留位 仍保持每条 16 字节
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
#if PAVISE_SELFTEST
            internal int Tid;
#endif
            internal Transition Value;
        }
        private sealed class Bucket
        {
#if PAVISE_SELFTEST
            internal long Creation;
#endif
            internal readonly List<Transition[]> Blocks = new List<Transition[]>();
            internal int Count;
        }
#if PAVISE_SELFTEST
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
            internal int State, Cpu = -1, Conflicts, EarlyWakes;
            // 运行中收到唤醒时暂存 待线程下次转换确认是否为切出前唤醒
            internal bool WakePending;
            internal long WakeAt;
            internal string WakeDetail;
            internal long At, Known, Running, Ready;
            internal long ReadyFirst, ReadySecond, KnownFirst, KnownSecond;
            internal int ReadyFirstIntervals, ReadySecondIntervals;
            internal readonly List<double> Runs, Readies;
            internal readonly Dictionary<int, long> Cpus;
            internal readonly int IntervalCapacity;
            internal int RunningBursts, ReadyIntervals;
            internal Track(int tid, long creation, long begin, long half, long end, int transitionCount)
            {
                Tid = tid; Creation = creation; At = Begin = begin; Half = half; End = end;
                IntervalCapacity = (int)Math.Min(int.MaxValue, (long)transitionCount + 1);
                Cpus = new Dictionary<int, long>();
                Runs = new List<double>();
                Readies = new List<double>();
            }
        }
#endif

        private readonly Dictionary<int, Bucket> buckets = new Dictionary<int, Bucket>();
#if PAVISE_SELFTEST
        private readonly List<Present> presents;
        private readonly int trackedThreads;
        private long recordedThrough;
#endif
        private readonly long begin, half, end, frequency;
        private int recordedTransitions, allocatedBlocks, sequence;
        private string invalid;
        private string resourceLimitDetail;
        internal bool ResourceLimitReached { get { return resourceLimitDetail != null; } }
        internal string ResourceLimitDetail { get { return resourceLimitDetail ?? ""; } }
        internal ThreadRelationCollector(Dictionary<int, long> threads, long start, long midpoint, long finish, long clockFrequency)
        {
#if PAVISE_SELFTEST
            presents = new List<Present>(512);
            trackedThreads = threads.Count;
#endif
            begin = start; half = midpoint; end = finish; frequency = clockFrequency;
            if (frequency <= 0 || begin >= half || half >= end)
            { invalid = Logger.Text("采样范围不可核验", "Sampling window unverifiable"); return; }
            foreach (KeyValuePair<int, long> identity in threads)
            {
                var bucket = new Bucket();
#if PAVISE_SELFTEST
                bucket.Creation = identity.Value;
#endif
                buckets.Add(identity.Key, bucket);
                if (identity.Key <= 0 || identity.Value <= 0) invalid = Logger.Text("采样线程身份不可核验", "Sampled thread identities unverifiable");
            }
        }

        internal void Invalidate(string reason) { if (invalid == null) invalid = reason; }

        private void ReachResourceLimit(string detail)
        { if (resourceLimitDetail == null) resourceLimitDetail = detail; Invalidate(detail); }

        internal void Switch(long time, int cpu, int incoming, int outgoing, byte oldState)
        {
            if (invalid != null || time < begin || time > end) return;
            if (cpu < 0 || cpu > ushort.MaxValue) { Invalidate(Logger.Text("处理器编号超过关系摘要支持范围", "Processor number exceeds the range supported by the relation summary")); return; }
            Add(time, outgoing, cpu, 0, oldState);
            Add(time, incoming, cpu, 2, 2);
        }

#if PAVISE_SELFTEST
        internal void Ready(long time, int target, byte flags)
        {
            // 这个重载没有来源 CPU 证据 保留未知 不与真实的 CPU65535 混同
            if ((flags & 6) == 0) Add(time, target, -1, 1, flags);
        }
#endif

        internal void Ready(long time, int target, byte flags, int sourceCpu)
        {
            if (invalid != null || time < begin || time > end) return;
            if (sourceCpu < 0 || sourceCpu > ushort.MaxValue) { Invalidate(Logger.Text("处理器编号超过关系摘要支持范围", "Processor number exceeds the range supported by the relation summary")); return; }
            // 栈或地址空间尚未换入时还不能派发 后续真正 Ready 的事件才开始计时
            // Kind=1 不读取 State 重建状态 复用该字节保留原始标志 不增加事件数组大小
            if ((flags & 6) == 0) Add(time, target, sourceCpu, 1, flags);
        }

        private void Add(long time, int tid, int cpu, byte kind, byte state)
        {
            Bucket bucket;
            if (invalid != null || time < begin || time > end || !buckets.TryGetValue(tid, out bucket)) return;
            // 软水位不丢弃高频线程 超出应急容量 超出整数寻址范围或实际分配失败时整窗拒绝
            if (recordedTransitions == int.MaxValue) { Invalidate(Logger.Text("状态转换数量超过集合可寻址范围", "State transition count exceeds the addressable range of the collection")); return; }
            int offset = bucket.Count % TransitionsPerBlock;
            if (offset == 0)
            {
                if (allocatedBlocks >= EmergencyMaximumBlocks)
                { ReachResourceLimit(Logger.Text("关系状态块超过完整采集应急上限", "Relation state blocks exceed the full-capture emergency limit")); return; }
                try { bucket.Blocks.Add(new Transition[TransitionsPerBlock]); }
                catch (OutOfMemoryException) { ReachResourceLimit(Logger.Text("调度状态块分配失败，实际内存不足", "Scheduling state block allocation failed, out of memory")); return; }
                allocatedBlocks++;
            }
            bucket.Blocks[bucket.Count / TransitionsPerBlock][offset] = new Transition {
                Time = time, Sequence = sequence++, Cpu = cpu, Kind = kind, State = state };
            bucket.Count++;
            recordedTransitions++;
#if PAVISE_SELFTEST
            recordedThrough = Math.Max(recordedThrough, time);
#endif
        }

#if PAVISE_SELFTEST
        internal void SuccessfulPresent(int tid, long time)
        {
            if (presents == null || invalid != null || time < begin || time > end || !buckets.ContainsKey(tid)) return;
            if (presents.Count >= EmergencyMaximumPresents)
            { ReachResourceLimit(Logger.Text("成功呈现记录超过完整采集应急上限", "Successful present records exceed the full-capture emergency limit")); return; }
            try { presents.Add(new Present { Tid = tid, Time = time }); }
            catch (OutOfMemoryException) { ReachResourceLimit(Logger.Text("画面提交记录分配失败，实际内存不足", "Present record allocation failed, out of memory")); return; }
            recordedThrough = Math.Max(recordedThrough, time);
        }

        internal ThreadRelationSnapshot Build(FrameCoordinationSample sample, int presenter,
            Dictionary<long, int>[] edges, FrameCaptureIssue captureIssue, Func<int, long, bool> verifyIdentity,
            Func<bool> continueBuild = null)
        {
            try
            {
                if (ResourceLimitReached || captureIssue == FrameCaptureIssue.EventBudget || captureIssue == FrameCaptureIssue.SystemEventBudget
                    || captureIssue == FrameCaptureIssue.EdgeBudget || captureIssue == FrameCaptureIssue.CpuBudget || captureIssue == FrameCaptureIssue.ThreadBudget)
                    return new ThreadRelationSnapshot(sample.Pid, sample.Birth, begin, end, frequency, false,
                        ResourceLimitReached ? ResourceLimitDetail : Logger.Text("完整采集触发应急保护，未重建部分图", "Full capture hit emergency protection, partial graph not rebuilt"), sample.LostEvents, null, null,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, trackedThreads, recordedTransitions, presents.Count,
                        recordedThrough, allocatedBlocks, 0, null, half);
                return BuildCore(sample, presenter, edges, captureIssue, verifyIdentity, continueBuild);
            }
            catch (BuildStoppedException)
            {
                return new ThreadRelationSnapshot(sample.Pid, sample.Birth, begin, end, frequency, false,
                    Logger.Text("摘要处理继续资格已撤销，未发布部分摘要", "Summary processing eligibility revoked, partial summary not published"), sample.LostEvents, null, null,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, trackedThreads, recordedTransitions, presents.Count,
                    recordedThrough, allocatedBlocks, 0, null, half);
            }
            catch (OutOfMemoryException)
            {
                sample.Reject(FrameCaptureIssue.CaptureFailed, Logger.Text("摘要分配失败，实际内存不足，本轮样本弃用", "Summary allocation failed, out of memory, sample discarded for this round"));
                return new ThreadRelationSnapshot(sample.Pid, sample.Birth, begin, end, frequency, false,
                    Logger.Text("摘要分配失败，实际内存不足，未发布部分摘要", "Summary allocation failed, out of memory, partial summary not published"), sample.LostEvents, null, null,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, trackedThreads, recordedTransitions, presents.Count,
                    recordedThrough, allocatedBlocks, 0, null, half);
            }
        }
#endif

        private sealed class BuildStoppedException : Exception { }

        private static void CheckBuild(Func<bool> continueBuild)
        { if (continueBuild != null && !continueBuild()) throw new BuildStoppedException(); }

        // 比较顺序和排序实现与不可取消时相同 只在长排序里定期插入检查点 取消时不发布未完成的摘要
        private static void SortForBuild<T>(List<T> values, Comparison<T> comparison, Func<bool> continueBuild)
        {
            if (continueBuild == null) { values.Sort(comparison); return; }
            CheckBuild(continueBuild);
            int comparisons = 0;
            try
            {
                values.Sort(delegate(T left, T right)
                {
                    if ((comparisons++ & 1023) == 0) CheckBuild(continueBuild);
                    return comparison(left, right);
                });
            }
            catch (InvalidOperationException error)
            {
                // List.Sort 包装比较器异常 只还原本方取消 其他排序错误继续向外报告
                Exception cause = error;
                while (cause is InvalidOperationException && cause.InnerException != null) cause = cause.InnerException;
                if (cause is BuildStoppedException) throw new BuildStoppedException();
                throw;
            }
            CheckBuild(continueBuild);
        }

#if PAVISE_SELFTEST
        // 数值默认排序保留框架快路径 大列表按块排序 再复用短侧缓冲归并 避免逐次比较包装委托
        private static void SortDefaultForBuild<T>(List<T> values, Func<bool> continueBuild)
        {
            if (continueBuild == null) { values.Sort(); return; }
            const int blockSize = 8192;
            CheckBuild(continueBuild);
            int count = values.Count;
            if (count <= blockSize)
            {
                values.Sort();
                CheckBuild(continueBuild);
                return;
            }
            for (int offset = 0; offset < count;)
            {
                int length = Math.Min(blockSize, count - offset);
                CheckBuild(continueBuild);
                values.Sort(offset, length, null);
                CheckBuild(continueBuild);
                offset += length;
            }
            int bufferLength = 0;
            for (int width = blockSize; width < count;)
            {
                bufferLength = Math.Max(bufferLength, Math.Min(width, count - width));
                if (width >= count - width) break;
                width *= 2;
            }
            T[] buffer = null;
            var comparer = Comparer<T>.Default;
            for (int width = blockSize; width < count;)
            {
                for (int start = 0; start < count - width;)
                {
                    CheckBuild(continueBuild);
                    int middle = start + width;
                    int end = middle + Math.Min(width, count - middle);
                    if (comparer.Compare(values[middle - 1], values[middle]) > 0)
                    {
                        if (buffer == null) buffer = new T[bufferLength];
                        MergeDefaultForBuild(values, start, middle, end, buffer, comparer, continueBuild);
                    }
                    start = end;
                }
                if (width >= count - width) break;
                width *= 2;
            }
            CheckBuild(continueBuild);
        }

        private static void MergeDefaultForBuild<T>(List<T> values, int start, int middle, int end,
            T[] buffer, IComparer<T> comparer, Func<bool> continueBuild)
        {
            bool copyLeft = middle - start <= end - middle;
            int copied = copyLeft ? middle - start : end - middle;
            int source = copyLeft ? start : middle;
            for (int offset = 0; offset < copied;)
            {
                CheckBuild(continueBuild);
                int length = Math.Min(8192, copied - offset);
                values.CopyTo(source + offset, buffer, offset, length);
                offset += length;
            }
            int moves = 0;
            if (copyLeft)
            {
                int left = 0, right = middle, write = start;
                while (left < copied && right < end)
                {
                    if ((moves++ & 1023) == 0) CheckBuild(continueBuild);
                    values[write++] = comparer.Compare(buffer[left], values[right]) <= 0 ? buffer[left++] : values[right++];
                }
                while (left < copied)
                {
                    if ((moves++ & 1023) == 0) CheckBuild(continueBuild);
                    values[write++] = buffer[left++];
                }
            }
            else
            {
                int left = middle - 1, right = copied - 1, write = end - 1;
                while (left >= start && right >= 0)
                {
                    if ((moves++ & 1023) == 0) CheckBuild(continueBuild);
                    values[write--] = comparer.Compare(values[left], buffer[right]) > 0 ? values[left--] : buffer[right--];
                }
                while (right >= 0)
                {
                    if ((moves++ & 1023) == 0) CheckBuild(continueBuild);
                    values[write--] = buffer[right--];
                }
            }
        }
#endif

#if PAVISE_SELFTEST
        private ThreadRelationSnapshot BuildCore(FrameCoordinationSample sample, int presenter,
            Dictionary<long, int>[] edges, FrameCaptureIssue captureIssue, Func<int, long, bool> verifyIdentity,
            Func<bool> continueBuild)
        {
            CheckBuild(continueBuild);
            string reason = invalid;
            if (captureIssue != FrameCaptureIssue.None) reason = Logger.Text("采集未通过核验：", "Capture failed verification: ") + captureIssue;
            if (sample.LostEvents != 0) reason = Logger.Text("采样事件丢失", "Sample events lost");
            Bucket presenterBucket = null;
            bool knownPresenter = presenter > 0 && buckets.TryGetValue(presenter, out presenterBucket);
            long presenterBirth = knownPresenter ? presenterBucket.Creation : 0;
            if (!knownPresenter) reason = reason ?? Logger.Text("未取得稳定呈现线程", "No stable presenter thread obtained");
            int worker = knownPresenter ? ChooseWorker(presenter, edges) : 0;
            Bucket workerBucket = worker > 0 ? buckets[worker] : null;
            long workerBirth = workerBucket != null ? workerBucket.Creation : 0;
            if (verifyIdentity != null && knownPresenter && !verifyIdentity(presenter, presenterBirth)) reason = Logger.Text("呈现线程身份已变化或不可读", "Presenter thread identity changed or unreadable");
            if (verifyIdentity != null && worker > 0 && !verifyIdentity(worker, workerBirth)) reason = Logger.Text("伴随线程身份已变化或不可读", "Companion thread identity changed or unreadable");
            // 暂存区只随当前要重建的角色增长 后续逐线程复用 不按全游戏转换总数预留
            var selected = new List<SelectedTransition>();
            SelectTransitions(selected, presenter, presenterBucket, continueBuild);
            SelectTransitions(selected, worker, workerBucket, continueBuild);
            CheckBuild(continueBuild);
            SortForBuild(selected, CompareTransitions, continueBuild);
            CheckBuild(continueBuild);
            Track a = knownPresenter ? new Track(presenter, presenterBirth, begin, half, end, presenterBucket.Count) : null;
            Track b = worker > 0 ? new Track(worker, workerBirth, begin, half, end, workerBucket.Count) : null;
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
            if (sameCpuOverlap) reason = reason ?? Logger.Text("不同线程在同一CPU出现重叠运行区间", "Different threads have overlapping running intervals on the same CPU");
            if ((a != null && a.Conflicts != 0) || (b != null && b.Conflicts != 0)) reason = reason ?? Logger.Text("线程状态重建存在歧义", "Thread state reconstruction is ambiguous");
            if (a != null && a.Known == 0) reason = reason ?? Logger.Text("未取得可重建的呈现线程调度区间", "No reconstructable presenter thread scheduling intervals");
            if (b != null && b.Known == 0) reason = reason ?? Logger.Text("未取得可重建的伴随线程调度区间", "No reconstructable companion thread scheduling intervals");
            var intervals = PresentIntervals(presenter, continueBuild);
            bool valid = reason == null;
            CheckBuild(continueBuild);
            ThreadRelationMetrics presenterMetrics = Metrics(a, valid, continueBuild), workerMetrics = Metrics(b, valid, continueBuild);
            ReleaseIntervals(a); ReleaseIntervals(b);
            CheckBuild(continueBuild);
            return new ThreadRelationSnapshot(sample.Pid, sample.Birth, begin, end, frequency, valid,
                reason ?? (worker == 0 ? Logger.Text("未取得稳定双向唤醒线程", "No stable bidirectional wake thread obtained") : Logger.Text("固定线程身份、双向唤醒与调度区间已记录", "Fixed thread identities, bidirectional wakes and scheduling intervals recorded")), sample.LostEvents,
                presenterMetrics, workerMetrics, Edge(edges, 0, worker, presenter), Edge(edges, 1, worker, presenter),
                Edge(edges, 0, presenter, worker), Edge(edges, 1, presenter, worker), Milliseconds(overlap), Milliseconds(runningOverlap),
                intervals.Count, Percentile(intervals, .50), Percentile(intervals, .95), Percentile(intervals, .99),
                trackedThreads, recordedTransitions, presents.Count, recordedThrough, allocatedBlocks, 0,
                string.Join(" | ", conflictDetails.ToArray()), half);
        }
#endif

        private static int CompareTransitions(SelectedTransition left, SelectedTransition right)
        {
            int order = left.Value.Time.CompareTo(right.Value.Time);
            // 等时转换保留接收顺序 不按事件类型猜测跨CPU因果
            return order == 0 ? left.Value.Sequence.CompareTo(right.Value.Sequence) : order;
        }

        private static void SelectTransitions(List<SelectedTransition> selected, int tid, Bucket bucket, Func<bool> continueBuild)
        {
            if (bucket == null) return;
            CheckBuild(continueBuild);
            int required = selected.Count + bucket.Count;
            if (selected.Capacity < required) selected.Capacity = required;
            for (int index = 0; index < bucket.Count; index++)
            {
                if ((index & 1023) == 0) CheckBuild(continueBuild);
                selected.Add(new SelectedTransition {
#if PAVISE_SELFTEST
                    Tid = tid,
#endif
                    Value = bucket.Blocks[index / TransitionsPerBlock][index % TransitionsPerBlock] });
            }
        }

#if PAVISE_SELFTEST
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
#endif

        // 切出前唤醒最多相隔 100 微秒 容纳中断引起的送达差异
        internal const int EarlyWakeToleranceMicroseconds = 100;

        private static long EarlyWakeTicks(long frequency)
        { return Math.Max(1, frequency * EarlyWakeToleranceMicroseconds / 1000000); }

#if PAVISE_SELFTEST
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

        // 挂起的唤醒没有等到切出 按原判据记一次歧义 状态从唤醒那一刻起未知
        private static void RejectPendingWake(Track track, long frequency, List<string> details)
        {
            Finish(track, track.WakeAt, frequency);
            track.Conflicts++; track.State = 0; track.At = track.WakeAt;
            if (track.WakeDetail != null && details != null && details.Count < 4) details.Add(track.WakeDetail);
            track.WakePending = false; track.WakeDetail = null;
        }

        // 关窗时仍未切出 容许间隔内保留运行尾段 超时记为歧义
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
                // 等切出期间重复到达的唤醒不另算 间隔仍从第一次算起
                if (next.Kind == 1) return;
                if (next.Kind == 0 && next.Cpu == track.Cpu && next.Time - track.WakeAt <= EarlyWakeTicks(frequency))
                {
                // 唤醒可能早于切出送达 非终止线程在切出后直接进入就绪状态
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
                // 运行中收到唤醒时保持计时 暂存事件以待后续确认
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
                track.State = 2; track.Cpu = next.Cpu;
            }
            else if (next.Kind == 1)
            {
                track.State = 1;
            }
            else
            {
                // Ready Standby DeferredReady 都表示仍有运行需求 Waiting 才是阻塞
                track.State = next.State == 1 || next.State == 3 || next.State == 7 ? 1 : next.State == 5 ? 5 : 0;
                track.Cpu = -1;
            }
            track.At = next.Time;
        }

        private static void AppendAdjacentSwitch(StringBuilder text, List<SelectedTransition> selected, int selectedIndex, int tid, int direction)
        {
            text.Append(direction < 0 ? "；前CSwitch={" : "；后CSwitch={");
            // 仅在已选角色的现有序列内查找 最多四处冲突复用同一序列 不另存全量事件
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
            if (track.State == 2)
            {
                long previous;
                track.Cpus.TryGetValue(track.Cpu, out previous);
                track.Cpus[track.Cpu] = previous + ticks;
            }
            if (track.Runs == null) return;
            // 在已重建的原区间上裁剪半窗 不增加转换或把跨界等待拆成两个分位数样本
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
                if (ticks > 0)
                {
                    track.RunningBursts++;
                    if (track.Runs != null) AddInterval(track.Runs, ticks * 1000.0 / frequency, track.IntervalCapacity);
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
                    // 同一区间只归属其起点半窗 后半可有等待时长而没有新起始片段
                    if (track.At < track.Half) track.ReadyFirstIntervals++;
                    else track.ReadySecondIntervals++;
                }
            }
        }

        private static void AddInterval(List<double> intervals, double value, int capacity)
        {
            if (intervals.Count >= capacity) throw new InvalidOperationException(Logger.Text("线程区间数量超过转换预算", "Thread interval count exceeds the transition budget"));
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

        private ThreadRelationMetrics Metrics(Track track, bool complete, Func<bool> continueBuild)
        {
            if (track == null) return null;
            if (track.Runs != null)
            {
                SortDefaultForBuild(track.Runs, continueBuild);
                SortDefaultForBuild(track.Readies, continueBuild);
            }
            return new ThreadRelationMetrics(track.Tid, track.Creation, Milliseconds(track.Running), Milliseconds(track.Ready), Milliseconds(track.Known),
                track.RunningBursts, Percentile(track.Runs, .95), Percentile(track.Runs, 1), track.ReadyIntervals,
                Percentile(track.Readies, .95), Percentile(track.Readies, 1), track.Conflicts, CpuDistribution(track.Cpus),
                complete && track.KnownFirst > 0 ? Milliseconds(track.ReadyFirst) : -1,
                complete && track.KnownSecond > 0 ? Milliseconds(track.ReadySecond) : -1,
                complete && track.KnownFirst > 0 ? Milliseconds(track.KnownFirst) : -1,
                complete && track.KnownSecond > 0 ? Milliseconds(track.KnownSecond) : -1,
                complete && track.KnownFirst > 0 ? track.ReadyFirstIntervals : -1,
                complete && track.KnownSecond > 0 ? track.ReadySecondIntervals : -1, track.EarlyWakes);
        }
#endif

        private string CpuDistribution(Dictionary<int, long> running)
        {
            var keys = new List<int>(running.Keys); keys.Sort();
            var cpus = new StringBuilder();
            foreach (int cpu in keys)
            {
                if (cpus.Length > 0) cpus.Append(',');
                cpus.Append("CPU").Append(cpu).Append(':').Append(Milliseconds(running[cpu]).ToString("F3", CultureInfo.InvariantCulture)).Append("ms");
            }
            return cpus.Length == 0 ? "未知" : cpus.ToString();
        }

#if PAVISE_SELFTEST
        private List<double> PresentIntervals(int presenter, Func<bool> continueBuild)
        {
            var stamps = new List<long>();
            int visited = 0;
            foreach (Present value in presents)
            {
                if ((visited++ & 1023) == 0) CheckBuild(continueBuild);
                if (value.Tid == presenter) stamps.Add(value.Time);
            }
            SortDefaultForBuild(stamps, continueBuild);
            var intervals = new List<double>();
            for (int index = 1; index < stamps.Count; index++)
            {
                if ((index & 1023) == 0) CheckBuild(continueBuild);
                if (stamps[index] > stamps[index - 1]) intervals.Add(Milliseconds(stamps[index] - stamps[index - 1]));
            }
            SortDefaultForBuild(intervals, continueBuild);
            return intervals;
        }
#endif

        private double Milliseconds(long ticks) { return frequency > 0 ? ticks * 1000.0 / frequency : 0; }
#if PAVISE_SELFTEST
        private static double Percentile(List<double> sorted, double fraction)
        { return sorted == null ? -1 : sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(sorted.Count * fraction) - 1))]; }
#endif
    }
}
