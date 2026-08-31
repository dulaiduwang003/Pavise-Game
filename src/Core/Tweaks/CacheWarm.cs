// @author bdth 2074055628@qq.com
// 文件用途 缓存预热 实验功能 对局稳定后把游戏资产预读进系统待机缓存 后续加载少走磁盘
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // 缓存预热 面向换图和素材流式加载的读盘等待
    //   对局稳定后 用一条最低磁盘优先级的线程把游戏目录里的大文件顺序读一遍
    //   页面进入系统待机缓存 之后游戏再读这些文件时命中内存 不用等磁盘
    //
    // 为什么这是零残留
    //   纯读取 不写任何系统状态 没有要撤销的东西 没有日志帐没有熔断
    //   待机缓存本身记在可用内存里 内存紧张时系统自己回收 预热不与任何人抢内存
    //
    // 读取纪律 一条都不能少
    //   背景 IO 模式拿最低磁盘优先级 游戏的读请求永远排在预热前面
    //   背景模式会把内存优先级一起压到 1 那预热就白做 必须抬回正常并回读核实
    //   分块限速 每块之间歇一口气 即使在低优先级下也不长时间占满队列
    //   交流供电 可用内存充足 游戏盘是固态 三个条件缺一个就整局不做
    //   机械硬盘上预热抢的是寻道 那是纯伤害 判断不了盘的类型也不做
    internal static class CacheWarm
    {
        internal const string EnabledKey = "GmCacheWarm";

        internal const long WarmBudgetBytes = 2L * 1024 * 1024 * 1024; // 最多预热 2 GB
        internal const long MinFileBytes = 4L * 1024 * 1024;           // 小文件不值得 寻道占比太高
        internal const int ChunkBytes = 8 * 1024 * 1024;
        internal const int ChunkGapMs = 50;                            // 分块间歇 压住吞吐
        internal const ulong MinAvailStartBytes = 4UL * 1024 * 1024 * 1024; // 起步门槛
        internal const ulong MinAvailFloorBytes = 2UL * 1024 * 1024 * 1024; // 预热中跌破就停
        internal const int EnumerateFileCap = 32768;                   // 枚举上限 病态目录树兜底

        internal struct WarmCandidate
        {
            public string Path;
            public long Size;
            public long AccessTicks;
        }

        internal struct WarmPick
        {
            public string Path;
            public long Take;
        }

        internal sealed class WarmResult
        {
            public bool Ran;
            public string SkipKey;
            public long WarmedBytes;
            public int WarmedFiles;
            public bool Aborted;
        }

        public static WarmResult Run(string root, Func<bool> abort)
        {
            var result = new WarmResult();
            if (abort == null) abort = delegate { return false; };
            if (string.IsNullOrEmpty(root)) return Skip(result, "log.cachewarm.4");
            if (!TryOnAcPower()) return Skip(result, "log.cachewarm.5");
            ulong total, avail;
            if (!TryMemoryStatus(out total, out avail) || avail < MinAvailStartBytes)
                return Skip(result, "log.cachewarm.3");
            // 固态确认不了就当不是 预热在机械盘上是纯伤害 宁可不做
            if (TrySeekPenalty(root) != false) return Skip(result, "log.cachewarm.2");
            // 拿不到"最低 IO 优先级 + 正常内存优先级"的组合就一个字节都不读
            //   缺前者会跟游戏抢磁盘 缺后者预热页第一批被挤掉 白读
            //   目录枚举的元数据 IO 同样砸在游戏盘上 必须也在低优先级下进行
            if (!TryEnterPoliteRead()) return Skip(result, "log.cachewarm.7");
            try
            {
                List<WarmPick> picks = SelectFiles(EnumerateCandidates(root, abort), WarmBudgetBytes);
                if (abort()) { result.Aborted = true; return result; }
                if (picks.Count == 0) return Skip(result, "log.cachewarm.8");
                int started = Environment.TickCount;
                foreach (WarmPick pick in picks)
                {
                    bool stop;
                    long got = WarmOneFile(pick.Path, pick.Take, abort, out stop);
                    if (got > 0) { result.WarmedBytes += got; result.WarmedFiles++; }
                    if (stop) { result.Aborted = true; break; }
                }
                result.Ran = true;
                int seconds = (Environment.TickCount - started) / 1000;
                if (result.Aborted)
                    Logger.Log(Lang.F("log.cachewarm.6", Gb(result.WarmedBytes)));
                else
                    Logger.Log(Lang.F("log.cachewarm.1", Gb(result.WarmedBytes),
                        result.WarmedFiles.ToString(CultureInfo.InvariantCulture),
                        seconds.ToString(CultureInfo.InvariantCulture)));
                return result;
            }
            finally { ExitPoliteRead(); }
        }

        private static WarmResult Skip(WarmResult result, string key)
        {
            result.SkipKey = key;
            Logger.Log(Lang.T(key));
            return result;
        }

        private static string Gb(long bytes)
        {
            return (bytes / 1073741824.0).ToString("F1", CultureInfo.InvariantCulture);
        }

        // 纯函数 热度用最后访问时间近似 访问时间冻结的卷上退化为按修改时间排 不致命
        //   预算切到大文件中段就停 尾巴不足一个小文件门槛的不凑数
        internal static List<WarmPick> SelectFiles(List<WarmCandidate> files, long budget)
        {
            var picks = new List<WarmPick>();
            if (files == null || budget <= 0) return picks;
            var eligible = new List<WarmCandidate>();
            foreach (WarmCandidate file in files)
                if (file.Size >= MinFileBytes && !string.IsNullOrEmpty(file.Path))
                    eligible.Add(file);
            eligible.Sort(delegate(WarmCandidate a, WarmCandidate b)
            {
                int byAccess = b.AccessTicks.CompareTo(a.AccessTicks);
                return byAccess != 0 ? byAccess : b.Size.CompareTo(a.Size);
            });
            long remaining = budget;
            foreach (WarmCandidate file in eligible)
            {
                if (remaining < MinFileBytes) break;
                long take = file.Size < remaining ? file.Size : remaining;
                picks.Add(new WarmPick { Path = file.Path, Take = take });
                remaining -= take;
            }
            return picks;
        }

        // 单文件分块读 每块之前查中止和内存水位 跌破水位或被中止都立刻收手
        internal delegate int WarmChunkRead(byte[] buffer, int count);

        internal static long WarmChunkLoop(byte[] buffer, long take, WarmChunkRead read,
            Func<bool> abort, out bool stop)
        {
            stop = false;
            long done = 0;
            while (done < take)
            {
                if (abort()) { stop = true; break; }
                ulong total, avail;
                if (!TryMemoryStatus(out total, out avail) || avail < MinAvailFloorBytes)
                {
                    stop = true;
                    break;
                }
                // 对局中拔了电源同样收手 交流供电是纪律条件 不是只在起步查一次的门槛
                if (!TryOnAcPower()) { stop = true; break; }
                int want = (int)Math.Min(buffer.Length, take - done);
                int got;
                try { got = read(buffer, want); }
                catch { break; }
                if (got <= 0) break;
                done += got;
                Sleep(ChunkGapMs);
            }
            return done;
        }

        private static long WarmOneFile(string path, long take, Func<bool> abort, out bool stop)
        {
#if PAVISE_SELFTEST
            if (WarmFileForTest != null) return WarmFileForTest(path, take, out stop);
            throw new InvalidOperationException("CacheWarm file reads require an injected test double.");
#else
            stop = false;
            try
            {
                // 共享读打开 永不阻塞游戏自己的句柄 打不开就跳过这一个
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                {
                    if (warmBuffer == null) warmBuffer = new byte[ChunkBytes];
                    return WarmChunkLoop(warmBuffer, take,
                        delegate(byte[] buffer, int count) { return stream.Read(buffer, 0, count); },
                        abort, out stop);
                }
            }
            catch { return 0; }
#endif
        }

#if !PAVISE_SELFTEST
        // 只在预热工作线程上用 一次一局 不并发
        [ThreadStatic] private static byte[] warmBuffer;
#endif

        private static void Sleep(int milliseconds)
        {
#if PAVISE_SELFTEST
            if (SleepForTest != null) SleepForTest(milliseconds);
#else
            Thread.Sleep(milliseconds);
#endif
        }

        // 目录枚举 跳过重解析点 防符号链接跳出游戏根或成环 单目录失败不连坐
        //   上限按走过的条目数计 不按收下的大文件数 病态目录树恰恰是海量小文件
        //   逐目录查一次中止 对局结束或换局时枚举也要立刻收手
        private static List<WarmCandidate> EnumerateCandidates(string root, Func<bool> abort)
        {
#if PAVISE_SELFTEST
            if (EnumerateForTest != null) return EnumerateForTest(root);
            throw new InvalidOperationException("CacheWarm enumeration requires an injected test double.");
#else
            var list = new List<WarmCandidate>();
            var dirs = new Stack<string>();
            dirs.Push(root);
            int visited = 0;
            while (dirs.Count > 0)
            {
                if (abort()) break;
                string dir = dirs.Pop();
                try
                {
                    foreach (FileSystemInfo entry in new DirectoryInfo(dir).GetFileSystemInfos())
                    {
                        if (++visited > EnumerateFileCap) return list;
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var file = entry as FileInfo;
                        if (file == null)
                        {
                            if (entry is DirectoryInfo) dirs.Push(entry.FullName);
                            continue;
                        }
                        if (file.Length < MinFileBytes) continue;
                        long access;
                        try { access = file.LastAccessTimeUtc.Ticks; }
                        catch { access = 0; }
                        if (access == 0)
                            try { access = file.LastWriteTimeUtc.Ticks; }
                            catch { access = 0; }
                        list.Add(new WarmCandidate { Path = file.FullName, Size = file.Length, AccessTicks = access });
                    }
                }
                catch { }
            }
            return list;
#endif
        }

        // 四个原语 隔离测试必须注入 生产走原生调用
        private static bool TryOnAcPower()
        {
#if PAVISE_SELFTEST
            if (PowerForTest != null) return PowerForTest();
            throw new InvalidOperationException("CacheWarm power status requires an injected test double.");
#else
            return Native.OnAcPower();
#endif
        }

        private static bool TryMemoryStatus(out ulong total, out ulong avail)
        {
            total = 0; avail = 0;
#if PAVISE_SELFTEST
            if (MemoryStatusForTest != null) return MemoryStatusForTest(out total, out avail);
            throw new InvalidOperationException("CacheWarm memory status requires an injected test double.");
#else
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (!GlobalMemoryStatusEx(ref status)) return false;
                total = status.TotalPhys; avail = status.AvailPhys;
                return true;
            }
            catch { return false; }
#endif
        }

        // true 有寻道惩罚 false 固态 null 判断不了 调用方把 null 当 true 用
        private static bool? TrySeekPenalty(string root)
        {
#if PAVISE_SELFTEST
            if (SeekPenaltyForTest != null) return SeekPenaltyForTest(root);
            throw new InvalidOperationException("CacheWarm disk probing requires an injected test double.");
#else
            try
            {
                // 按卷问 不按盘符问 游戏根可能坐在挂载进 C:\ 的另一块盘的卷上
                //   卷根不是"X:\"形状说明是挂载点卷 没有盘符可打开 判不了按机械处理
                var volume = new System.Text.StringBuilder(261);
                if (!GetVolumePathNameW(Path.GetFullPath(root), volume, volume.Capacity))
                    return null;
                string volumeRoot = volume.ToString();
                if (volumeRoot.Length != 3 || volumeRoot[1] != ':' || volumeRoot[2] != '\\')
                    return null;
                IntPtr handle = CreateFileW("\\\\.\\" + char.ToUpperInvariant(volumeRoot[0]) + ":",
                    0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle == InvalidHandle) return null;
                try
                {
                    var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty };
                    DeviceSeekPenaltyDescriptor descriptor;
                    uint returned;
                    if (!DeviceIoControl(handle, IoctlStorageQueryProperty,
                            ref query, (uint)Marshal.SizeOf(typeof(StoragePropertyQuery)),
                            out descriptor, (uint)Marshal.SizeOf(typeof(DeviceSeekPenaltyDescriptor)),
                            out returned, IntPtr.Zero))
                        return null;
                    return descriptor.IncursSeekPenalty != 0;
                }
                finally { Native.CloseHandle(handle); }
            }
            catch { return null; }
#endif
        }

        private static bool TryEnterPoliteRead()
        {
#if PAVISE_SELFTEST
            if (PoliteReadForTest != null) return PoliteReadForTest();
            throw new InvalidOperationException("CacheWarm read priority requires an injected test double.");
#else
            try
            {
                if (!SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin)) return false;
                var wanted = new MemoryPriorityInformation { MemoryPriority = MemoryPriorityNormal };
                MemoryPriorityInformation applied;
                uint size = (uint)Marshal.SizeOf(typeof(MemoryPriorityInformation));
                // 返回码不作数 回读才作数 内存优先级没抬回正常等于白读一遍
                if (!SetThreadInformation(GetCurrentThread(), ThreadMemoryPriorityClass, ref wanted, size)
                    || !GetThreadInformation(GetCurrentThread(), ThreadMemoryPriorityClass, out applied, size)
                    || applied.MemoryPriority != MemoryPriorityNormal)
                {
                    SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
                    return false;
                }
                return true;
            }
            catch { return false; }
#endif
        }

        private static void ExitPoliteRead()
        {
#if !PAVISE_SELFTEST
            try { SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd); }
            catch { }
#endif
        }

#if PAVISE_SELFTEST
        internal delegate bool MemoryStatusOverride(out ulong total, out ulong avail);
        internal delegate long WarmFileOverride(string path, long take, out bool stop);
        internal static Func<bool> PowerForTest;
        internal static MemoryStatusOverride MemoryStatusForTest;
        internal static Func<string, bool?> SeekPenaltyForTest;
        internal static Func<string, List<WarmCandidate>> EnumerateForTest;
        internal static WarmFileOverride WarmFileForTest;
        internal static Func<bool> PoliteReadForTest;
        internal static Action<int> SleepForTest;

        internal static void ResetForTest()
        {
            PowerForTest = null;
            MemoryStatusForTest = null;
            SeekPenaltyForTest = null;
            EnumerateForTest = null;
            WarmFileForTest = null;
            PoliteReadForTest = null;
            SleepForTest = null;
        }
#endif

#if !PAVISE_SELFTEST
        private const uint IoctlStorageQueryProperty = 0x2D1400;
        private const int StorageDeviceSeekPenaltyProperty = 7;
        private const uint FileShareReadWrite = 0x3;
        private const uint OpenExisting = 3;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        private const int ThreadModeBackgroundBegin = 0x00010000;
        private const int ThreadModeBackgroundEnd = 0x00020000;
        private const int ThreadMemoryPriorityClass = 0;
        private const uint MemoryPriorityNormal = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StoragePropertyQuery
        {
            public int PropertyId;
            public int QueryType;
            public int AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceSeekPenaltyDescriptor
        {
            public uint Version;
            public uint Size;
            public byte IncursSeekPenalty;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryPriorityInformation
        {
            public uint MemoryPriority;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetVolumePathNameW(string fileName,
            System.Text.StringBuilder volumePathName, int bufferLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint code,
            ref StoragePropertyQuery input, uint inputSize,
            out DeviceSeekPenaltyDescriptor output, uint outputSize,
            out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadInformation(IntPtr thread, int informationClass,
            ref MemoryPriorityInformation information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadInformation(IntPtr thread, int informationClass,
            out MemoryPriorityInformation information, uint size);
#endif
    }
}
