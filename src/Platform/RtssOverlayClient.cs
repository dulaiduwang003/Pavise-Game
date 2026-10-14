using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class RtssFrameSample
    {
        internal int Pid;
        internal int ViewportWidth, ViewportHeight;
        internal long Creation;
        internal string Name;
        internal double? Fps, FrameTimeMs, GpuFrameTimeMs, AverageFps, Low1, Low01, GameRamMiB;
        internal double[] FrameTimes;
        internal long FrameSequence;
        internal bool BenchmarkActive;
        internal long? AdapterLuid;
    }

    // 温度符号按 RTSS 当前代码页编码，中文代码页不能直接用默认编码。
    internal static class RtssTextEncoding
    {
        internal static readonly uint NativeCodePage = GetACP();
        [DllImport("kernel32.dll")] private static extern uint GetACP();

        internal static int GetByteCount(string text, uint codePage)
        { return ConvertText(text ?? "", codePage, null); }

        internal static byte[] Encode(string text, uint codePage)
        {
            text = text ?? "";
            byte[] bytes = new byte[GetByteCount(text, codePage)];
            ConvertText(text, codePage, bytes);
            return bytes;
        }
        private static int ConvertText(string text, uint codePage, byte[] destination)
        {
            Encoding encoding = Encoding.GetEncoding((int)codePage);
            int start = 0, position = 0;
            for (int index = 0; index <= text.Length; index++)
            {
                bool degree = codePage != 65001 && index + 1 < text.Length && text[index] == '\u00b0' && text[index + 1] == 'C';
                if (index == text.Length || degree)
                {
                    int length = index - start;
                    position += destination == null ? encoding.GetByteCount(text.Substring(start, length))
                        : encoding.GetBytes(text, start, length, destination, position);
                    if (degree)
                    {
                        if (destination != null) destination[position] = 0xb0;
                        position++; start = index + 1;
                    }
                }
            }
            return position;
        }
    }

    internal enum RtssReadState { Ready, InvalidMapping, RendererMissing, FramesPending, IdentityUnavailable, ChangedDuringRead }
    internal enum RtssPublishState { Ready, InvalidMapping, Busy, NoSlot }

    // 数组步长取自实时共享头，不能使用完整 SDK 结构大小。
    internal sealed class RtssMemorySession : IDisposable
    {
        internal const uint Signature = 0x52545353;
        internal const int FrameBufferOffset = 924, FrameBufferPositionOffset = 5020, NativeFramerateOffset = 5024;
        internal const int StatisticsFlagsOffset = 284, StatisticsAverageOffset = 308,
            StatisticsLow1Offset = 9176, StatisticsLow01Offset = 9180,
            ProcessCountersFlagsOffset = 9192, ProcessCountersCountOffset = 9196,
            ProcessCountersPeriodOffset = 9200, ProcessCountersTimestampOffset = 9208;
        internal const int GpuRenderStartOffset = 9320, GpuRenderEndOffset = 9328, GpuFrameTimeOffset = 9340;
        internal const int ExtendedText2Offset = 266752;
        internal const int ResolutionXOffset = 9224, ResolutionYOffset = 9228;
        internal RtssReadState ReadState { get; private set; }
        internal RtssPublishState PublishState { get; private set; }
        private readonly MemoryMappedFile mapping;
        private readonly MemoryMappedViewAccessor view;
        private readonly string owner;
        private readonly uint textCodePage;
        private readonly uint[] frameScratch = new uint[1024];
        private int slot = -1, previousPid, previousPosition;
        private bool positionReady;
        private uint previousSampleTick;
        private long previousBirth, frameSequence;
        private ulong previousGpuEnd;
        private uint lastGpuUpdateTick;
        private double? lastGpuFrameTime;
        private bool gpuTimingLive;
        private string previousText;
        private bool disposed;
#if PAVISE_SELFTEST
        internal Action AfterFrameCopyForTest;
        internal Func<uint> GpuClockForTest;
#endif
        private struct Header
        {
            internal uint Version;
            internal int AppSize, AppOffset, AppCount, OsdSize, OsdOffset, OsdCount;
        }
        internal RtssMemorySession(string mappingName, string ownerName)
            : this(mappingName, ownerName, RtssTextEncoding.NativeCodePage) { }
#if PAVISE_SELFTEST
        internal RtssMemorySession(string mappingName, string ownerName, uint codePage)
#else
        private RtssMemorySession(string mappingName, string ownerName, uint codePage)
#endif
        {
            owner = ownerName; textCodePage = codePage;
            mapping = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.ReadWrite);
            try { view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite); }
            catch { mapping.Dispose(); throw; }
        }
        private bool ReadHeader(out Header header)
        {
            header = new Header();
            if (disposed || view.Capacity < 40 || view.Capacity > 64L * 1024 * 1024
                || view.ReadUInt32(0) != Signature) return false;
            header.Version = view.ReadUInt32(4);
            if ((header.Version >> 16) != 2 || header.Version < 0x0002000e) return false;
            header.AppSize = view.ReadInt32(8); header.AppOffset = view.ReadInt32(12); header.AppCount = view.ReadInt32(16);
            header.OsdSize = view.ReadInt32(20); header.OsdOffset = view.ReadInt32(24); header.OsdCount = view.ReadInt32(28);
            int headerSize = header.Version >= 0x00020010 ? 72 : 40;
            if (header.Version >= 0x00020012) headerSize = 80;
            if (header.Version >= 0x00020013) headerSize = 96;
            return Range(header.AppOffset, header.AppSize, header.AppCount, 296, 65536, 4096, headerSize)
                && Range(header.OsdOffset, header.OsdSize, header.OsdCount, 4608, 524288, 256, headerSize)
                && ((long)header.AppOffset + (long)header.AppSize * header.AppCount <= header.OsdOffset
                    || (long)header.OsdOffset + (long)header.OsdSize * header.OsdCount <= header.AppOffset);
        }
        private bool Range(int offset, int size, int count, int minimum, int maximum, int maxCount, int headerSize)
        {
            return offset >= headerSize && (offset & 3) == 0 && size >= minimum && size <= maximum
                && (size & 3) == 0 && count > 0 && count <= maxCount
                && (long)offset + (long)size * count <= view.Capacity;
        }
        private string ReadString(long offset, int capacity)
        {
            byte[] bytes = new byte[capacity]; view.ReadArray(offset, bytes, 0, capacity);
            int end = Array.IndexOf(bytes, (byte)0);
            return Encoding.Default.GetString(bytes, 0, end < 0 ? capacity : end);
        }
        private void WriteString(long offset, int capacity, string text)
        {
            byte[] bytes = RtssTextEncoding.Encode(text, textCodePage);
            if (bytes.Length >= capacity) throw new ArgumentException("RTSS text exceeds slot capacity");
            view.WriteArray(offset, bytes, 0, bytes.Length); view.Write(offset + bytes.Length, (byte)0);
        }
        internal bool TryRead(int preferredPid, Func<int, long> identity, out RtssFrameSample sample)
        {
            ReadState = RtssReadState.InvalidMapping;
            bool read = TryReadCore(preferredPid, identity, out sample);
            // 目标失效时中断帧序列连续性。
            if (!read) { positionReady = false; ResetGpuTiming(); }
            return read;
        }
        private bool TryReadCore(int preferredPid, Func<int, long> identity, out RtssFrameSample sample)
        {
            sample = null; Header header;
            if (preferredPid <= 4 || identity == null || !ReadHeader(out header)) return false;
            int index = -1;
            // 启动器不能替代已确认的游戏渲染进程。
            for (int i = 0; i < header.AppCount; i++)
                if (view.ReadInt32((long)header.AppOffset + (long)i * header.AppSize) == preferredPid) { index = i; break; }
            if (index < 0) { ReadState = RtssReadState.RendererMissing; return false; }
            long offset = (long)header.AppOffset + (long)index * header.AppSize;
            int targetPid = view.ReadInt32(offset);
            uint time0 = view.ReadUInt32(offset + 268), time1 = view.ReadUInt32(offset + 272);
            uint period = unchecked(time1 - time0);
            if (targetPid <= 4 || time1 == 0 || unchecked((uint)Environment.TickCount - time1) > 2500)
            { ReadState = RtssReadState.FramesPending; return false; }
            long birth = identity(targetPid);
            if (birth <= 0) { ReadState = RtssReadState.IdentityUnavailable; return false; }
            if (targetPid != previousPid || birth != previousBirth)
            { previousPid = targetPid; previousBirth = birth; positionReady = false; frameSequence = 0; ResetGpuTiming(); }
            sample = new RtssFrameSample { Pid = targetPid, Creation = birth,
                Name = Path.GetFileName(ReadString(offset + 4, 260)), AdapterLuid = null };
            uint frameCount = view.ReadUInt32(offset + 276), frameTime = view.ReadUInt32(offset + 280);
            // 零帧率表示尚未初始化。
            if (header.AppSize >= NativeFramerateOffset + 4)
                sample.Fps = MonitoringFrameWindow.Valid(view.ReadUInt32(offset + NativeFramerateOffset) / 10.0, 0.1, 100000);
            if (!sample.Fps.HasValue && time0 != 0 && period > 0 && period <= 10000 && frameCount > 0)
                sample.Fps = MonitoringFrameWindow.Valid(1000.0 * frameCount / period, 0.1, 100000);
            // SDK v2.20 的实际渲染尺寸优先于桌面窗口尺寸。
            if (header.Version >= 0x00020014 && header.AppSize >= ResolutionYOffset + 4)
            {
                int width = view.ReadInt32(offset + ResolutionXOffset), height = view.ReadInt32(offset + ResolutionYOffset);
                if (ValidViewport(width, height) && width == view.ReadInt32(offset + ResolutionXOffset)
                    && height == view.ReadInt32(offset + ResolutionYOffset))
                { sample.ViewportWidth = width; sample.ViewportHeight = height; }
            }
            if (header.AppSize >= FrameBufferPositionOffset + 4)
            {
                // 使用最新实际帧间隔，旧显示计数可能是周期内最大值。
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    int latestPosition = view.ReadInt32(offset + FrameBufferPositionOffset);
                    uint latest = view.ReadUInt32(offset + FrameBufferOffset + ((latestPosition - 1) & 1023) * 4);
                    if (latestPosition == view.ReadInt32(offset + FrameBufferPositionOffset))
                    { sample.FrameTimeMs = MonitoringFrameWindow.Valid(latest / 1000.0, 0.001, 10000); break; }
                }
            }
            else if (frameTime > 0) sample.FrameTimeMs = MonitoringFrameWindow.Valid(frameTime / 1000.0, 0.001, 10000);
            MonitoringMetrics metrics = MonitoringPreferences.Metrics;
            ReadGpuFrameTime(header, offset, metrics, sample);
            ReadStatistics(header, offset, metrics, sample);
            ReadProcessMemory(header, offset, metrics, sample);
            if (header.AppSize >= FrameBufferPositionOffset + 8 && (metrics & MonitoringMetrics.FrameStatistics) != 0)
            {
                int position = view.ReadInt32(offset + FrameBufferPositionOffset);
                uint sampleTick = unchecked((uint)Environment.TickCount);
                int delta = positionReady ? unchecked(position - previousPosition) : 0;
                if (delta < 0 && position >= 0 && position < 1024 && previousPosition < 1024)
                    delta = (position - previousPosition) & 1023;
                if (positionReady && position >= 0 && position < 1024 && previousPosition >= 0 && previousPosition < 1024
                    && sample.Fps.HasValue && sample.Fps.Value * unchecked(sampleTick - previousSampleTick) / 1000.0 >= 1024)
                    delta = 1025; // 环形缓冲区跨圈不明时按数据断点处理。
                if (delta > 0 && delta <= 1024)
                {
                    var intervals = new double[delta];
                    int first = (position - delta) & 1023, contiguous = Math.Min(delta, 1024 - first);
                    view.ReadArray(offset + FrameBufferOffset + first * 4, frameScratch, 0, contiguous);
                    if (contiguous < delta)
                        view.ReadArray(offset + FrameBufferOffset, frameScratch, contiguous, delta - contiguous);
#if PAVISE_SELFTEST
                    if (AfterFrameCopyForTest != null) AfterFrameCopyForTest();
#endif
                    int after = view.ReadInt32(offset + FrameBufferPositionOffset);
                    int advanced = unchecked(after - position);
                    if (advanced < 0 && after >= 0 && after < 1024 && position >= 0 && position < 1024)
                        advanced = (after - position) & 1023;
                    if (advanced >= 0 && (long)delta + advanced <= 1024)
                    {
                        for (int i = 0; i < delta; i++) intervals[i] = frameScratch[i] / 1000.0;
                        sample.FrameTimes = intervals; frameSequence += delta;
                    }
                    else sample.FrameTimes = null;
                }
                else if (delta < 0 || delta > 1024) frameSequence += Math.Max(1, delta);
                else sample.FrameTimes = new double[0];
                previousPosition = position;
                positionReady = true; previousSampleTick = sampleTick;
                sample.FrameSequence = frameSequence;
            }
            else positionReady = false;
            if (view.ReadInt32(offset) != targetPid || view.ReadUInt32(offset + 272) != time1)
            { sample = null; ReadState = RtssReadState.ChangedDuringRead; return false; }
            ReadState = RtssReadState.Ready;
            return true;
        }
        internal static bool ValidViewport(int width, int height)
        { return width >= 320 && width <= 16384 && height >= 240 && height <= 16384; }
        private void ResetGpuTiming()
        { previousGpuEnd = 0; lastGpuUpdateTick = 0; lastGpuFrameTime = null; gpuTimingLive = false; }
        private void ReadGpuFrameTime(Header header, long offset, MonitoringMetrics metrics, RtssFrameSample sample)
        {
            // Reflex 时间为 GPU 完成到完成的微秒间隔，并非忙碌时间。
            if ((metrics & MonitoringMetrics.FrameTime) == 0 || header.Version < 0x00020015
                || header.AppSize < GpuFrameTimeOffset + 4) { ResetGpuTiming(); return; }
            ulong start = view.ReadUInt64(offset + GpuRenderStartOffset), end = view.ReadUInt64(offset + GpuRenderEndOffset);
            uint value = view.ReadUInt32(offset + GpuFrameTimeOffset);
            double? milliseconds = MonitoringFrameWindow.Valid(value / 1000.0, 0.001, 10000);
            if (start == 0 || end <= start || !milliseconds.HasValue
                || view.ReadUInt64(offset + GpuRenderStartOffset) != start
                || view.ReadUInt64(offset + GpuRenderEndOffset) != end
                || view.ReadUInt32(offset + GpuFrameTimeOffset) != value)
            { ResetGpuTiming(); return; }
            uint now = unchecked((uint)Environment.TickCount);
#if PAVISE_SELFTEST
            if (GpuClockForTest != null) now = GpuClockForTest();
#endif
            if (previousGpuEnd == 0 || end < previousGpuEnd)
            {
                // 连接时先观察标记推进，避免旧标记被当作新帧。
                previousGpuEnd = end; lastGpuUpdateTick = now; lastGpuFrameTime = null; gpuTimingLive = false;
                return;
            }
            if (end > previousGpuEnd)
            { previousGpuEnd = end; lastGpuUpdateTick = now; lastGpuFrameTime = milliseconds; gpuTimingLive = true; }
            if (gpuTimingLive && unchecked(now - lastGpuUpdateTick) <= 2500)
                sample.GpuFrameTimeMs = lastGpuFrameTime;
        }
        private void ReadStatistics(Header header, long offset, MonitoringMetrics metrics, RtssFrameSample sample)
        {
            const MonitoringMetrics exported = MonitoringMetrics.AverageFps | MonitoringMetrics.Low1 | MonitoringMetrics.Low01;
            if ((metrics & exported) == 0 || header.AppSize < StatisticsFlagsOffset + 4) return;
            uint flags = view.ReadUInt32(offset + StatisticsFlagsOffset);
            sample.BenchmarkActive = (flags & 1) != 0;
            if (!sample.BenchmarkActive || header.AppSize < StatisticsAverageOffset + 4) return;
            uint start = view.ReadUInt32(offset + 288), end = view.ReadUInt32(offset + 292);
            uint count = view.ReadUInt32(offset + 300), frames = view.ReadUInt32(offset + 296);
            uint duration = unchecked(end - start);
            // STATFLAG_RECORD 由 RTSS 或用户管理，停止录制后的统计不算实时读数。
            if ((flags & 1) == 0 || start == 0 || end == 0 || count == 0 || frames == 0
                || duration == 0 || duration > 0x7fffffff
                || unchecked((uint)Environment.TickCount - end) > 2500) return;
            double? average = (metrics & MonitoringMetrics.AverageFps) != 0
                ? MonitoringFrameWindow.Valid(view.ReadUInt32(offset + StatisticsAverageOffset) / 10.0, 0.1, 100000) : null;
            double? low1 = null, low01 = null;
            if (header.Version >= 0x0002000d)
            {
                if ((metrics & MonitoringMetrics.Low1) != 0 && header.AppSize >= StatisticsLow1Offset + 4)
                    low1 = MonitoringFrameWindow.Valid(view.ReadUInt32(offset + StatisticsLow1Offset) / 10.0, 0.1, 100000);
                if ((metrics & MonitoringMetrics.Low01) != 0 && header.AppSize >= StatisticsLow01Offset + 4)
                    low01 = MonitoringFrameWindow.Valid(view.ReadUInt32(offset + StatisticsLow01Offset) / 10.0, 0.1, 100000);
            }
            // 统计重置期间不拼接不同录制的数据。
            if (view.ReadUInt32(offset + StatisticsFlagsOffset) != flags
                || view.ReadUInt32(offset + 288) != start || view.ReadUInt32(offset + 292) != end
                || view.ReadUInt32(offset + 296) != frames || view.ReadUInt32(offset + 300) != count) return;
            sample.AverageFps = average; sample.Low1 = low1; sample.Low01 = low01;
        }
        private void ReadProcessMemory(Header header, long offset, MonitoringMetrics metrics, RtssFrameSample sample)
        {
            if ((metrics & MonitoringMetrics.GameRam) == 0 || header.Version < 0x00020012
                || header.AppSize < ProcessCountersTimestampOffset + 4) return;
            int stride = view.ReadInt32(72), arrayOffset = view.ReadInt32(76);
            int count = view.ReadInt32(offset + ProcessCountersCountOffset);
            uint period = view.ReadUInt32(offset + ProcessCountersPeriodOffset);
            uint stamp = view.ReadUInt32(offset + ProcessCountersTimestampOffset);
            int fixedEnd = header.Version >= 0x00020015 ? 9344 : header.Version >= 0x00020014 ? 9232
                : header.Version >= 0x00020013 ? 9224 : 9212;
            if (period == 0 || stamp == 0 || unchecked((uint)Environment.TickCount - stamp) > 2500
                || count <= 0 || count > 256 || stride < 12 || stride > 1024 || (stride & 3) != 0
                || arrayOffset < fixedEnd || (arrayOffset & 3) != 0
                || (long)arrayOffset + (long)stride * count > header.AppSize) return;
            double? memory = null;
            for (int i = 0; i < count; i++)
            {
                long counter = offset + arrayOffset + (long)i * stride;
                if (view.ReadUInt32(counter) == 1 && view.ReadUInt32(counter + 4) == 0)
                { memory = view.ReadUInt32(counter + 8) / 1024.0; break; }
            }
            if (view.ReadInt32(offset + ProcessCountersCountOffset) != count
                || view.ReadUInt32(offset + ProcessCountersPeriodOffset) != period
                || view.ReadUInt32(offset + ProcessCountersTimestampOffset) != stamp
                || view.ReadInt32(72) != stride || view.ReadInt32(76) != arrayOffset) return;
            sample.GameRamMiB = memory;
        }
        // 共享忙位必须跨进程原子更新。
        private unsafe bool TryBusy(out byte* pointer)
        {
            pointer = null; view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            if (Interlocked.CompareExchange(ref *((int*)(pointer + 36)), 1, 0) == 0) return true;
            view.SafeMemoryMappedViewHandle.ReleasePointer(); pointer = null; return false;
        }
        internal unsafe bool Publish(string text)
        {
            PublishState = RtssPublishState.InvalidMapping;
            Header header; if (!ReadHeader(out header)) return false;
            // 占用槽位前校验字节容量，包含终止符。
            if (RtssTextEncoding.GetByteCount(text, textCodePage) >= 4096
                || RtssTextEncoding.GetByteCount(owner, textCodePage) >= 256
                || (text != null && text.IndexOf('\0') >= 0)
                || (owner != null && owner.IndexOf('\0') >= 0))
                throw new ArgumentException("RTSS text exceeds slot capacity or contains a terminator");
            byte* pointer; if (!TryBusy(out pointer)) { PublishState = RtssPublishState.Busy; return false; }
            try
            {
                if (!ReadHeader(out header)) return false;
                slot = -1;
                for (int i = 1; i < header.OsdCount; i++)
                {
                    string candidate = ReadString((long)header.OsdOffset + (long)i * header.OsdSize + 256, 256);
                    if (candidate == owner) { slot = i; break; }
                    if (candidate.Length == 0 && slot < 0) slot = i;
                }
                if (slot < 0) { PublishState = RtssPublishState.NoSlot; return false; }
                long offset = (long)header.OsdOffset + (long)slot * header.OsdSize;
                bool claimed = ReadString(offset + 256, 256) == owner;
                if (claimed && text == previousText) { PublishState = RtssPublishState.Ready; return true; }
                WriteString(offset + 256, 256, owner);
                WriteString(offset, 256, "");
                if (header.Version >= 0x00020014 && header.OsdSize >= ExtendedText2Offset + 32768)
                    WriteString(offset + ExtendedText2Offset, 32768, "");
                WriteString(offset + 512, 4096, text);
                view.Write(32, unchecked(view.ReadUInt32(32) + 1));
                previousText = text;
                PublishState = RtssPublishState.Ready;
                return true;
            }
            finally { Interlocked.Exchange(ref *((int*)(pointer + 36)), 0); view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }
        internal unsafe bool Clear()
        {
            Header header; if (!ReadHeader(out header)) return true;
            byte* pointer; if (!TryBusy(out pointer)) return false;
            try
            {
                if (!ReadHeader(out header)) return true;
                bool changed = false;
                for (int i = 1; i < header.OsdCount; i++)
                {
                    long offset = (long)header.OsdOffset + (long)i * header.OsdSize;
                    if (ReadString(offset + 256, 256) != owner) continue;
                    WriteString(offset, 256, ""); WriteString(offset + 512, 4096, ""); WriteString(offset + 256, 256, "");
                    if (header.Version >= 0x00020014 && header.OsdSize >= ExtendedText2Offset + 32768)
                        WriteString(offset + ExtendedText2Offset, 32768, "");
                    changed = true;
                }
                if (changed) view.Write(32, unchecked(view.ReadUInt32(32) + 1));
                slot = -1; previousText = null;
                return true;
            }
            finally { Interlocked.Exchange(ref *((int*)(pointer + 36)), 0); view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }
        public void Dispose()
        {
            if (disposed) return;
            try
            {
                var wait = Stopwatch.StartNew();
                while (!Clear() && wait.ElapsedMilliseconds < 250) Thread.Sleep(5);
            }
            finally { disposed = true; view.Dispose(); mapping.Dispose(); }
        }
    }

    internal static class RtssOverlayClient
    {
        private static readonly object gate = new object();
        private static RtssMemorySession session;
        private static readonly RtssStartup startup = new RtssStartup();
        private static RtssComponentInfo component;
        private static long componentCheckStamp;
        private static string status = "";
        internal static string Status { get { return Volatile.Read(ref status); } }
#if PAVISE_SELFTEST
        internal static Func<RtssMemorySession> SessionFactoryForTest;
        internal static Func<int[]> ForegroundViewportForTest;
        internal static Func<int, long> IdentityForTest;
        internal static RtssStartup StartupForTest;
#endif
        private static RtssStartup Startup
        {
            get
            {
#if PAVISE_SELFTEST
                if (StartupForTest != null) return StartupForTest;
#endif
                return startup;
            }
        }
        internal static Func<int> PreferredPid;
        internal static Func<int, long> PreferredBirth;
        internal static Func<bool> SessionActive;
        internal static Func<bool> SessionPreparing;
        internal static bool GamePreparing { get { return SessionPreparing != null && SessionPreparing(); } }
        internal static bool GameActive
        {
            get
            {
                if (SessionActive != null) return SessionActive();
                int pid; long birth;
                return TryGetGuardedRenderer(out pid, out birth);
            }
        }
        internal static Func<PerformancePreset> ActiveMode;
        internal static int AccentRgb()
        {
            PerformancePreset mode = ActiveMode == null ? Theme.CurrentMode : ActiveMode();
            return Theme.ModeColor(mode).ToArgb() & 0xFFFFFF;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
        [StructLayout(LayoutKind.Sequential)] private struct ClientRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ClientRect rect);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long birth, out long exit, out long kernel, out long user);
        private static long Identity(int pid)
        {
#if PAVISE_SELFTEST
            return IdentityForTest == null ? 0 : IdentityForTest(pid);
#else
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return 0;
            try { long birth, exit, kernel, user; return GetProcessTimes(handle, out birth, out exit, out kernel, out user) && exit == 0 ? birth : 0; }
            finally { Native.CloseHandle(handle); }
#endif
        }
        private static bool Connect()
        {
#if PAVISE_SELFTEST
            if (SessionFactoryForTest == null) return false;
            if (StartupForTest != null && !Startup.EnsureRunning("fixture.exe", out status)) return false;
            if (session == null) session = SessionFactoryForTest();
            status = "";
            return session != null;
#else
            long now = Stopwatch.GetTimestamp();
            if (component == null || (now - componentCheckStamp) / (double)Stopwatch.Frequency >= 30)
            {
                component = RtssComponent.Detect(); componentCheckStamp = now;
                // 连接建立后也要检查来源变化 停掉旧的托盘维护和排队任务
                if (component.Installed && !component.InstalledByPavise) RtssTrayIcon.Hide(component);
            }
            if (!component.Installed || !component.SupportsOverlay || component.InstallationInProgress
                || component.UninstallationInProgress)
            { status = ComponentUnavailableText(component); CloseConnection(); return false; }
            // 游戏可能保留已退出 RTSS 的映射，连接前确认服务器仍在运行。
            bool running = Startup.EnsureRunning(component.Path, out status);
            if (!running) { CloseConnection(); return false; }
            component.Running = true;
            if (session != null) return true;
            try
            {
                session = new RtssMemorySession("RTSSSharedMemoryV2", "Pavise.Performance.v1");
                RtssTrayIcon.Hide(component); status = ""; return true;
            }
            catch { status = RtssStartup.StartingStatus; }
            return false;
#endif
        }
        internal static string ComponentUnavailableText(RtssComponentInfo info)
        {
            if (info != null && !string.IsNullOrEmpty(info.Error)) return info.Error;
            if (info != null && (info.InstallationInProgress || info.UninstallationInProgress))
                return AutomaticPolicy.Text("RTSS 组件正在变更，完成后自动连接", "RTSS component operation in progress; reconnecting when complete");
            if (info == null || !info.Installed)
                return AutomaticPolicy.Text("未检测到 RTSS，请安装或重新检测组件", "RTSS was not found; install or detect the component again");
            return AutomaticPolicy.Text("RTSS 版本不受支持，请更新组件", "RTSS version is unsupported; update the component");
        }
        internal static ProcessStartInfo QuietStartInfo(string executable, out string reason)
        { return RtssStartup.PrepareStartInfo(executable, out reason); }
        internal static void Prepare()
        {
            lock (gate)
            {
                if (!GameActive && !GamePreparing) return;
                try { Connect(); } catch { status = ConnectionFailureText(); ResetMapping(); }
            }
        }
        internal static bool TryGetGuardedRenderer(out int pid, out long creation)
        {
            Func<int> getPid = PreferredPid;
            Func<int, long> getBirth = PreferredBirth;
            pid = getPid == null ? 0 : getPid();
            creation = pid > 4 && getBirth != null ? getBirth(pid) : 0;
            return pid > 4 && creation > 0 && getPid() == pid && getBirth(pid) == creation;
        }
        internal static bool MatchesGuardedRenderer(int pid, long creation)
        {
            int guardedPid; long guardedCreation;
            return pid > 4 && creation > 0 && TryGetGuardedRenderer(out guardedPid, out guardedCreation)
                && guardedPid == pid && guardedCreation == creation;
        }
        internal static bool TryReadTarget(out RtssFrameSample sample)
        {
            lock (gate)
            {
                sample = null;
                try
                {
                    int pid; long expectedBirth;
                    if (!GameActive) { Close(); return false; }
                    if (!TryGetGuardedRenderer(out pid, out expectedBirth))
                    { status = AutomaticPolicy.Text("等待守护确认游戏渲染进程", "Waiting for the guarded game renderer"); Clear(); return false; }
                    if (ForegroundPid() != pid)
                    { status = AutomaticPolicy.Text("游戏不在前台，返回游戏后继续监测", "Game is not foreground; monitoring resumes when you return"); Clear(); return false; }
                    // 预热不绕过前台游戏身份检查。
                    if (!Connect()) return false;
                    if (!session.TryRead(pid, delegate(int candidate) {
                        return candidate == pid && Identity(candidate) == expectedBirth ? expectedBirth : 0;
                    }, out sample))
                    {
                        status = ReadFailureText(session.ReadState);
                        if (session.ReadState == RtssReadState.InvalidMapping) ResetMapping(); else Clear();
                        return false;
                    }
                    if (!MatchesGuardedRenderer(sample.Pid, sample.Creation))
                    { sample = null; status = ReadFailureText(RtssReadState.ChangedDuringRead); Clear(); return false; }
                    status = "";
                    return true;
                }
                catch { status = ConnectionFailureText(); ResetMapping(); return false; }
            }
        }
        private static string ConnectionFailureText()
        { return AutomaticPolicy.Text("RTSS 数据连接暂不可用，正在自动重试", "RTSS data connection is unavailable; retrying automatically"); }
        internal static string ReadFailureText(RtssReadState state)
        {
            switch (state)
            {
                case RtssReadState.InvalidMapping:
                    return AutomaticPolicy.Text("RTSS 数据连接失效，正在重连", "RTSS data connection expired; reconnecting");
                case RtssReadState.RendererMissing:
                    return AutomaticPolicy.Text("RTSS 尚未连接此游戏，等待游戏帧数据", "RTSS has not connected to this game; waiting for frames");
                case RtssReadState.FramesPending:
                    return AutomaticPolicy.Text("RTSS 已找到游戏，等待有效帧数据", "RTSS found the game; waiting for current frame data");
                case RtssReadState.IdentityUnavailable:
                    return AutomaticPolicy.Text("无法确认游戏进程身份，稍后自动重试", "Game process identity could not be verified; retrying automatically");
                default:
                    return AutomaticPolicy.Text("游戏渲染状态变化中，正在重新确认", "Game renderer is changing; verifying again");
            }
        }
        private static void ForegroundViewport(out int pid, out int width, out int height)
        {
            pid = 0; width = 1920; height = 1080;
#if PAVISE_SELFTEST
            int[] viewport = ForegroundViewportForTest == null ? null : ForegroundViewportForTest();
            if (viewport != null && viewport.Length == 3) { pid = viewport[0]; width = viewport[1]; height = viewport[2]; }
#else
            IntPtr window = GetForegroundWindow(); GetWindowThreadProcessId(window, out pid);
            ClientRect rect;
            if (GetClientRect(window, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
            { width = rect.Right - rect.Left; height = rect.Bottom - rect.Top; }
#endif
        }
        private static int ForegroundPid()
        {
#if PAVISE_SELFTEST
            int[] viewport = ForegroundViewportForTest == null ? null : ForegroundViewportForTest();
            return viewport != null && viewport.Length == 3 ? viewport[0] : 0;
#else
            return GameSessionDetector.ForegroundPid();
#endif
        }
        internal static void ResolveViewport(MonitoringSnapshot snapshot, int clientWidth, int clientHeight,
            out int width, out int height)
        {
            width = clientWidth; height = clientHeight;
            if (snapshot != null && RtssMemorySession.ValidViewport(snapshot.ViewportWidth, snapshot.ViewportHeight))
            { width = snapshot.ViewportWidth; height = snapshot.ViewportHeight; }
        }
        internal static void Publish(MonitoringSnapshot snapshot, MonitoringLayout layout)
        {
            lock (gate)
            {
                if (snapshot == null || snapshot.Pid <= 4) { Clear(); return; }
                try
                {
                    if (!MatchesGuardedRenderer(snapshot.Pid, snapshot.Creation)) { Clear(); return; }
                    if (!Connect()) return;
                    int pid, width, height; ForegroundViewport(out pid, out width, out height);
                    if (pid != snapshot.Pid) { session.Clear(); return; }
                    if (!MatchesGuardedRenderer(snapshot.Pid, snapshot.Creation)
                        || Identity(snapshot.Pid) != snapshot.Creation) { session.Clear(); return; }
                    ResolveViewport(snapshot, width, height, out width, out height);
                    if (!session.Publish(FormatForViewport(snapshot, layout, component != null && component.SupportsFontOverride, width, height)))
                    {
                        status = session.PublishState == RtssPublishState.NoSlot
                            ? AutomaticPolicy.Text("RTSS 悬浮栏槽位已满，请关闭其他不需要的悬浮栏", "RTSS overlay slots are full; close other overlays you do not need")
                            : session.PublishState == RtssPublishState.Busy
                                ? AutomaticPolicy.Text("RTSS 数据正在更新，下一轮自动重试", "RTSS data is busy; retrying on the next update")
                                : ReadFailureText(RtssReadState.InvalidMapping);
                        if (session.PublishState == RtssPublishState.InvalidMapping) ResetMapping();
                    }
                    else status = "";
                }
                catch { status = ConnectionFailureText(); ResetMapping(); }
            }
        }
        internal static string FormatForViewport(MonitoringSnapshot snapshot, MonitoringLayout layout,
            bool fontOverride, int width, int height)
        {
            // 最小化窗口不回退为全屏宽度。
            if (width > 0 && width < 320 || height > 0 && height < 240) return "";
            int opacity, x, y, scale, spacing; MonitoringPreferences.GetAppearance(out opacity, out x, out y, out scale, out spacing);
            return RtssOverlayFormat.Format(snapshot, layout, MonitoringPreferences.Metrics,
                fontOverride, width, height, AccentRgb(), opacity, x, y, scale, spacing);
        }
        internal static void Clear() { lock (gate) { if (session != null) try { session.Clear(); } catch { ResetMapping(); } } }
        private static void CloseConnection()
        {
            Startup.ForgetLiveServer();
            ResetMapping();
        }
        private static void ResetMapping()
        {
            // 映射重建不改变服务器存活探测的节流。
            if (session != null) { try { session.Dispose(); } catch { } session = null; }
        }
        internal static void ComponentChanged()
        { lock (gate) { CloseConnection(); component = null; componentCheckStamp = 0; status = ""; } }
        internal static void Close()
        {
            lock (gate)
            {
                CloseConnection();
                Startup.StopOwned();
                RtssTrayIcon.Forget();
                component = null; componentCheckStamp = 0;
                status = AutomaticPolicy.Text("等待游戏对局，RTSS 按需启动", "Waiting for a game; RTSS starts on demand");
            }
        }
    }
}
