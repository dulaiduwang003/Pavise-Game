using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    // 根据运行时及内核的 Present 事件识别画面提交者，不依赖图形接口类型；文件名、启动器和窗口都不能证明提交归属。

    internal sealed class PresentActivitySample
    {
        private readonly Dictionary<int, int> frames;
        internal readonly bool Valid;
        internal readonly string Failure;
        internal readonly int WindowMs;
        // 通道送达事件总数；未知时为 -1。
        internal readonly int Events;

        // 采样无效时的现场读数，只写进告警日志；不参与判定，也不改变 Failure 的原有文本。
        internal readonly string Diagnostics;

        private PresentActivitySample(bool valid, string failure, Dictionary<int, int> frames, int windowMs, int events, string diagnostics = null)
        {
            Valid = valid; Failure = failure ?? ""; WindowMs = windowMs; Events = events; Diagnostics = diagnostics ?? "";
            this.frames = frames ?? new Dictionary<int, int>();
        }

        internal static PresentActivitySample Unavailable(string failure)
        { return Unavailable(failure, null); }

        internal static PresentActivitySample Unavailable(string failure, string diagnostics)
        { return new PresentActivitySample(false, string.IsNullOrEmpty(failure) ? "unavailable" : failure, null, 0, -1, diagnostics); }

        internal static PresentActivitySample FromFrames(IDictionary<int, int> frames, int windowMs)
        { return FromFrames(frames, windowMs, -1); }

        internal static PresentActivitySample FromFrames(IDictionary<int, int> frames, int windowMs, int events)
        { return new PresentActivitySample(windowMs > 0, windowMs > 0 ? "" : "empty-window", frames == null ? null : new Dictionary<int, int>(frames), windowMs, events); }

        internal int FramesOf(int pid)
        { int value; return pid > 0 && frames.TryGetValue(pid, out value) ? value : 0; }

        internal double FpsOf(int pid)
        { return WindowMs > 0 ? FramesOf(pid) * 1000.0 / WindowMs : 0.0; }

        internal int ProcessCount { get { return frames.Count; } }

        internal KeyValuePair<int, int>[] Entries()
        { return new List<KeyValuePair<int, int>>(frames).ToArray(); }
    }

    internal sealed class PresentActivityCapture
    {
        internal enum Kind { None, RuntimeDxgi, RuntimeD3d9, KernelPresent, KernelHistory }

        private sealed class Counts
        {
            internal int Dxgi, D3d9, KernelPresent, KernelHistory, KernelDetailed;
            // 同一帧可能同时产生运行时和内核事件，每个来源只计一次。
            internal int Frames
            { get { return Math.Max(Math.Max(Dxgi, D3d9), Math.Max(KernelPresent, Math.Max(KernelHistory, KernelDetailed))); } }
        }

        private readonly object gate = new object();
        private readonly Dictionary<int, Counts> counts = new Dictionary<int, Counts>();
        private readonly FrameCoordinationTrace trace = new FrameCoordinationTrace();
        private readonly Stopwatch clock = new Stopwatch();
        private Native.EventRecordCallback receive;
        private bool finishing, finished;
        private int receiveFailure;
        // 包含非提交事件的送达总数。
        private int seen;
        // 丢事件诊断读数，只由采样线程写入：送达事件覆盖的处理器、首末事件的会话时间戳，以及实时通道自己上报的丢失标记。
        // 用于在下一份日志里区分丢失集中在窗口的某一段、某些处理器，还是消费端没有跟上。
        private static readonly Guid RealtimeLoss = new Guid("6a399ae0-4bc6-4de9-870b-3657f8947e7e");
        private long startStamp, firstStamp, lastStamp, firstLossStamp;
        private ulong processors;
        private int lostEventMarks, lostBufferMarks, lostFileMarks;

        private PresentActivityCapture() { }

        internal static PresentActivityCapture TryStart(out string failure)
        {
            failure = "";
            var capture = new PresentActivityCapture();
            capture.receive = capture.Receive;
            try
            {
                // 会话使用 QPC 时钟且消费端保留原始时间戳，与这里的计数同源。
                capture.startStamp = Stopwatch.GetTimestamp();
                capture.clock.Start();
                if (capture.trace.StartPresentProbe(capture.receive)) return capture;
                failure = "start-error-" + capture.trace.Error + "@" + capture.trace.Stage + " (" + capture.trace.FailureDetail + ")";
            }
            catch (Exception error) { failure = "start-" + error.GetType().Name; }
            try { capture.trace.Stop(); } catch { }
            return null;
        }

        // 事件丢失、停止失败或记录未结清都会使采样无效，并保留其恢复债务。
        internal PresentActivitySample Finish()
        {
            lock (gate)
            {
                if (finishing) return PresentActivitySample.Unavailable("finished");
                finishing = true;
            }
            clock.Stop();
            int windowMs = (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
            bool stopped = false;
            try { stopped = trace.Stop(); } catch { }
            var frames = new Dictionary<int, int>();
            lock (gate)
            {
                finished = true;
                if (!stopped || trace.Error != 0 || trace.Lost != 0 || !trace.Drained)
                    return PresentActivitySample.Unavailable("stop-error-" + trace.Error + "-lost-" + trace.Lost
                        + "(" + trace.LossDetail + " seen=" + Volatile.Read(ref seen) + " " + trace.FailureDetail + ")", DiagnosticsLocked(windowMs));
                int failure = Volatile.Read(ref receiveFailure);
                if (failure != 0)
                    return PresentActivitySample.Unavailable(failure == 2 ? "consumer-error-OutOfMemoryException" : "consumer-error",
                        DiagnosticsLocked(windowMs));
                foreach (KeyValuePair<int, Counts> pair in counts)
                {
                    int value = pair.Value.Frames;
                    if (value > 0) frames[pair.Key] = value;
                }
            }
            return PresentActivitySample.FromFrames(frames, windowMs, Volatile.Read(ref seen));
        }

        // 调用方持有 gate。时间以采样开始为零点，单位毫秒；n/a 表示没有任何事件送达。
        private string DiagnosticsLocked(int windowMs)
        {
            int dxgi = 0, d3d9 = 0, kernel = 0, history = 0, detailed = 0;
            foreach (Counts value in counts.Values)
            { dxgi += value.Dxgi; d3d9 += value.D3d9; kernel += value.KernelPresent; history += value.KernelHistory; detailed += value.KernelDetailed; }
            long first = Volatile.Read(ref firstStamp), last = Volatile.Read(ref lastStamp), loss = Volatile.Read(ref firstLossStamp);
            return "window=" + windowMs + "ms cpus=0x" + Volatile.Read(ref processors).ToString("X")
                + " firstEvent=" + Offset(first) + " lastEvent=" + Offset(last)
                + " kinds=dxgi:" + dxgi + ",d3d9:" + d3d9 + ",kernel:" + kernel + ",history:" + history + ",detailed:" + detailed
                + " rtLost=event:" + Volatile.Read(ref lostEventMarks) + ",buffer:" + Volatile.Read(ref lostBufferMarks) + ",file:" + Volatile.Read(ref lostFileMarks)
                + (loss == 0 ? "" : "@" + Offset(loss)) + " " + trace.SessionStats;
        }
        private string Offset(long stamp)
        { return stamp == 0 ? "n/a" : ((stamp - startStamp) * 1000 / Stopwatch.Frequency) + "ms"; }

        internal static Kind Classify(Guid provider, ushort id, byte opcode)
        {
            if (provider == FrameCoordinationSampler.Dxgi)
                return (id == 42 || id == 55) ? Kind.RuntimeDxgi : Kind.None;            // 事件名：Present_Start、PresentMultiplaneOverlay_Start。
            if (provider == FrameCoordinationSampler.D3d9)
                return id == 1 ? Kind.RuntimeD3d9 : Kind.None;                          // 事件名：Present_Start。
            if (provider == FrameCoordinationTrace.DxgKrnl)
            {
                if (id == 184) return Kind.KernelPresent;                              // 事件名：Present（D3DKMTPresent）、PresentHistory(Detailed)_Start。
                if ((id == 171 || id == 215) && opcode == 1) return Kind.KernelHistory;
            }
            return Kind.None;
        }

        internal void Count(int pid, Kind kind, ushort id)
        {
            if (pid <= 4 || kind == Kind.None) return;
            lock (gate)
            {
                if (finished) return;
                Counts value;
                if (!counts.TryGetValue(pid, out value)) counts[pid] = value = new Counts();
                switch (kind)
                {
                    case Kind.RuntimeDxgi: value.Dxgi++; break;
                    case Kind.RuntimeD3d9: value.D3d9++; break;
                    case Kind.KernelPresent: value.KernelPresent++; break;
                    case Kind.KernelHistory: if (id == 215) value.KernelDetailed++; else value.KernelHistory++; break;
                }
            }
        }

        private void Receive(ref Native.EventRecord record)
        {
            if (Volatile.Read(ref finished) || Volatile.Read(ref receiveFailure) != 0) return;
            try
            {
                Interlocked.Increment(ref seen);
                Native.EventDescriptor descriptor = record.EventHeader.EventDescriptor;
                long stamp = record.EventHeader.TimeStamp;
                if (firstStamp == 0) Volatile.Write(ref firstStamp, stamp);
                Volatile.Write(ref lastStamp, stamp);
                // 缓冲上下文的低字节是写入该事件的处理器编号；超过 64 个处理器时折叠，只用于诊断。
                Volatile.Write(ref processors, processors | (1UL << (int)(record.BufferContext & 63)));
                if (record.EventHeader.ProviderId == RealtimeLoss)
                {
                    // 事件类型 32、33、34 依次表示实时通道丢失了事件、缓冲或备份文件。
                    if (descriptor.Opcode == 32) Interlocked.Increment(ref lostEventMarks);
                    else if (descriptor.Opcode == 33) Interlocked.Increment(ref lostBufferMarks);
                    else if (descriptor.Opcode == 34) Interlocked.Increment(ref lostFileMarks);
                    if (firstLossStamp == 0) Volatile.Write(ref firstLossStamp, stamp);
                }
#if PAVISE_SELFTEST
                Guid source = record.EventHeader.ProviderId;
                if (source == FrameCoordinationSampler.Dxgi || source == FrameCoordinationSampler.D3d9 || source == FrameCoordinationTrace.DxgKrnl)
                { if (!FrameCoordinationTrace.PresentIdListed(source, descriptor.Id)) Interlocked.Increment(ref unlisted); }
                else Interlocked.Increment(ref foreign);
#endif
                Kind kind = Classify(record.EventHeader.ProviderId, descriptor.Id, descriptor.Opcode);
                if (kind != Kind.None) Count((int)record.EventHeader.ProcessId, kind, descriptor.Id);
            }
            catch (Exception error)
            {
                // 托管解码或计数分配失败同样破坏窗口完整性；不能把已有部分计数继续作为入场证据。
                // 这里只存首次失败码，OOM 分支不再分配诊断字符串。
                Interlocked.CompareExchange(ref receiveFailure, error is OutOfMemoryException ? 2 : 1, 0);
            }
        }

#if PAVISE_SELFTEST
        private PresentActivityCapture(FrameCoordinationTrace ownedTrace) { trace = ownedTrace; startStamp = Stopwatch.GetTimestamp(); clock.Start(); }
        internal static PresentActivityCapture ForTest() { var capture = new PresentActivityCapture(); capture.startStamp = Stopwatch.GetTimestamp(); capture.clock.Start(); return capture; }
        internal void ReceiveForTest(ref Native.EventRecord record) { Receive(ref record); }
        internal static PresentActivityCapture CleanupForTest()
        { return new PresentActivityCapture(FrameCoordinationTrace.CleanupFixtureForTest()); }
        // 各提供程序的全进程事件数，用于核对标识过滤。
        internal string SourcesForTest()
        {
            int dxgi = 0, d3d9 = 0, kernel = 0, history = 0, detailed = 0;
            lock (gate) foreach (Counts value in counts.Values)
            { dxgi += value.Dxgi; d3d9 += value.D3d9; kernel += value.KernelPresent; history += value.KernelHistory; detailed += value.KernelDetailed; }
            return "dxgi=" + dxgi + " d3d9=" + d3d9 + " kernel=" + kernel + " history=" + history + " detailed=" + detailed
                + " seen=" + Volatile.Read(ref seen);
        }
        internal int SourceForTest(int pid, Kind kind)
        {
            lock (gate)
            {
                Counts value;
                if (!counts.TryGetValue(pid, out value)) return 0;
                return kind == Kind.RuntimeDxgi ? value.Dxgi : kind == Kind.RuntimeD3d9 ? value.D3d9
                    : kind == Kind.KernelPresent ? value.KernelPresent : value.KernelHistory + value.KernelDetailed;
            }
        }
        internal int SeenForTest { get { return Volatile.Read(ref seen); } }
        // 提交提供程序的清单外事件应为零；foreign 单独统计其他提供程序事件。
        private int unlisted, foreign;
        internal int UnlistedForTest { get { return Volatile.Read(ref unlisted); } }
        internal int ForeignForTest { get { return Volatile.Read(ref foreign); } }
        internal int UnfilteredForTest { get { return trace.UnfilteredProviders; } }
        internal PresentActivitySample SnapshotForTest(int windowMs)
        {
            var frames = new Dictionary<int, int>();
            lock (gate) foreach (KeyValuePair<int, Counts> pair in counts) if (pair.Value.Frames > 0) frames[pair.Key] = pair.Value.Frames;
            return PresentActivitySample.FromFrames(frames, windowMs);
        }
#endif
    }
}
