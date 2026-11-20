using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 完整采集的原生前置处理 源码在 src/Native/PaviseEtw.cpp
    // 正式构建（PAVISE_EMBEDDED_NATIVE）由 build.cmd 把原生代码链进 Pavise.exe 导出函数就在主模块里 不写出也不加载任何文件
    // 测试构建只有 csc 没有链接这一步 仍把 NativeEtwImage.cs 里的 DLL 落盘后加载 落盘与核对见 NativeImageFile
    // 完整采集只走这条路径 缺少原生代码 镜像损坏 写出失败 内容或接口不符 32 位进程时 完整采集直接失败并写明原因
    // 不换回 C# 逐事件处理 只抓画面提交的轻量采集事件很少 仍由 C# 直接处理
    internal static class NativeEtw
    {
        internal const string FileName = "PaviseEtw.dll";
        internal const int Abi = 1;

        internal const byte KindSwitch = 1, KindReady = 2, KindPresent = 3, KindOrderReversed = 4,
            KindSwitchShort = 5, KindReadyShort = 6, KindDiscontinuity = 7;
        internal const int FaultNone = 0, FaultSystemEvents = 1, FaultCpu = 2, FaultMemory = 3, FaultReader = 4, FaultRecords = 5;

        // 以下四个结构和 PaviseEtw.cpp 一一对应 加载时按 PvEtwLayout 报回的大小核对
        [StructLayout(LayoutKind.Sequential)]
        internal struct Config
        {
            internal int Size, Pid;
            internal long Begin, End;
            internal IntPtr Threads, ConsumerCpuMs, ReadProperty;
            internal int ThreadCount, EmergencySystemEvents, CpuCheckInterval, LayoutObservation;
            internal double EmergencyCaptureCpuMs, CallerCpuBeforeEvents;
            internal int EmergencyRecords, Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Record
        {
            internal long Time;
            internal int Count, Switches, A, B;
            internal ushort Cpu;
            internal byte Kind, State;
            internal int Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PresentRecord
        {
            internal int ThreadId;
            internal ushort Id;
            internal byte Version, IsDxgi;
            internal uint FlagsStatus, FlagsValue, StreamStatus, UserDataLength, Properties;
            internal ulong Stream;
            internal int Result, Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Stats
        {
            internal int Size, Count, Switches, Fault, Records, Presents, Blocks, BlockRecords;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AbiFunction();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LayoutFunction([Out] int[] sizes, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CreateFunction(ref Config config);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PointerFunction();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int HandleFunction(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ActionFunction(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int StatsFunction(IntPtr handle, ref Stats stats);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr BlockFunction(IntPtr handle, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr DataFunction(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SeedFunction(IntPtr handle, int count);
        // 原生回调每 4096 个事件经它读一次消费线程 CPU 和托管处理用同一个读数来源
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate double CpuFunction();
        // 测试替换了 ProcessTrace 时 由托管代码把事件交给原生回调
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void EventFunction(ref Native.EventRecord record);

        internal sealed class Api
        {
            internal IntPtr Module, Callback;
            internal string Path;
            internal CreateFunction Create;
            internal HandleFunction Fault;
            internal ActionFunction Stop, Destroy;
            internal StatsFunction ReadStats;
            internal BlockFunction Block;
            internal DataFunction Presents;
            internal SeedFunction SeedCount;
            internal EventFunction Deliver;
        }

        private static readonly object gate = new object();
        private static bool attempted;
        private static Api api;
        // 加载原因每个进程只算一次并缓存 缓存值是中文 英文在这里配对登记 取值时再按当前语言返回 以后登记的语言也能拿英文去查译文
        // NativeCpu 和 NativeImageFile 共用 放在这里是因为内嵌构建不编译 NativeImageFile
        // 静态字段按书写顺序初始化 登记表必须写在用到它的 status 前面
        private static readonly Dictionary<string, string> loadReasonEnglish = new Dictionary<string, string>(StringComparer.Ordinal);

        private static string status = LoadReason("未尝试", "not attempted");

        internal static string LoadReason(string chinese, string english)
        {
            // 原因种类有限 带上错误码和异常名也到不了上限 上限只防异常输入撑大字典
            lock (loadReasonEnglish) if (loadReasonEnglish.Count < 64 || loadReasonEnglish.ContainsKey(chinese)) loadReasonEnglish[chinese] = english;
            return chinese;
        }

        internal static string LoadReasonText(string chinese)
        {
            if (string.IsNullOrEmpty(chinese)) return chinese ?? "";
            string english;
            lock (loadReasonEnglish) if (!loadReasonEnglish.TryGetValue(chinese, out english)) english = chinese;
            return Logger.Text(chinese, english);
        }

        internal static string Status { get { lock (gate) return LoadReasonText(status); } }

        internal static Api Loaded
        {
            get
            {
                lock (gate)
                {
                    if (!attempted)
                    {
                        attempted = true;
                        string reason;
                        api = Load(out reason);
                        status = reason;
                        Logger.Info(Logger.Format("原生事件处理：{0}", "Native event processing: {0}", LoadReasonText(reason)));
                    }
                    return api;
                }
            }
        }

#if PAVISE_EMBEDDED_NATIVE
        private static Api Load(out string reason)
        {
            try
            {
                if (IntPtr.Size != 8) { reason = LoadReason("32 位进程", "32-bit process"); return null; }
                IntPtr module = GetModuleHandleW(null);
                if (module == IntPtr.Zero || GetProcAddress(module, "PvEtwAbi") == IntPtr.Zero) { reason = LoadReason("程序里没有编入原生组件", "native component is not built into the program"); return null; }
                Api loaded = Bind(module, null, out reason);
                if (loaded == null) return null;
                reason = LoadReason("已加载 内置", "loaded built-in");
                return loaded;
            }
            catch (Exception ex)
            {
                reason = LoadReason("原生组件准备失败 " + ex.GetType().Name, "native component preparation failed " + ex.GetType().Name);
                return null;
            }
        }

        // 数据目录 native<哈希前 16 位> 下的原生组件是旧安装留下的 原生代码已编进 exe 启动时清掉这些文件
        // 只删认得的文件名 重解析点和正被占用的文件跳过 目录空了才删
        internal static void RetireExtractedFiles()
        {
            try
            {
                string data = Paths.Data;
                if (string.IsNullOrEmpty(data)) return;
                string nativeRoot = Path.Combine(data, "native");
                if (!Directory.Exists(nativeRoot) || (File.GetAttributes(nativeRoot) & FileAttributes.ReparsePoint) != 0) return;
                foreach (string directory in Directory.GetDirectories(nativeRoot))
                {
                    if (!OldImageDirectory(directory)) continue;
                    try
                    {
                        foreach (string name in new[] { FileName, "PaviseGuard.exe" })
                        {
                            string file = Path.Combine(directory, name);
                            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
                        }
                        if (Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory, false);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                if (Directory.GetFileSystemEntries(nativeRoot).Length == 0) Directory.Delete(nativeRoot, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool OldImageDirectory(string directory)
        {
            string name = Path.GetFileName(directory);
            if (name == null || name.Length != 16 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (char c in name) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string name);
#else
        internal static void RetireExtractedFiles() { }

        private static Api Load(out string reason)
        {
            try
            {
                if (IntPtr.Size != 8) { reason = LoadReason("32 位进程", "32-bit process"); return null; }
                string path;
                using (FileStream held = NativeImageFile.Prepare(NativeEtwImage.Base64, NativeEtwImage.Length, NativeEtwImage.Sha256, FileName, out path, out reason))
                {
                    if (held == null) return null;
                    // 依赖只从系统目录取 文件在核对后一直禁止改写 直到映射完成
                    IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LoadLibrarySearchSystem32);
                    if (module == IntPtr.Zero) { int loadError = Marshal.GetLastWin32Error(); reason = LoadReason("原生组件加载失败 code=" + loadError, "native component load failed code=" + loadError); return null; }
                    Api loaded = Bind(module, path, out reason);
                    if (loaded == null) { FreeLibrary(module); return null; }
                    reason = LoadReason("已加载 " + NativeEtwImage.Sha256.Substring(0, 16), "loaded " + NativeEtwImage.Sha256.Substring(0, 16));
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                reason = LoadReason("原生组件准备失败 " + ex.GetType().Name, "native component preparation failed " + ex.GetType().Name);
                return null;
            }
        }
#endif

        private static Api Bind(IntPtr module, string path, out string reason)
        {
            var abi = Export<AbiFunction>(module, "PvEtwAbi");
            var layout = Export<LayoutFunction>(module, "PvEtwLayout");
            var callback = Export<PointerFunction>(module, "PvEtwCallback");
            var loaded = new Api {
                Module = module, Path = path,
                Create = Export<CreateFunction>(module, "PvEtwCreate"),
                Fault = Export<HandleFunction>(module, "PvEtwFault"),
                Stop = Export<ActionFunction>(module, "PvEtwStop"),
                Destroy = Export<ActionFunction>(module, "PvEtwDestroy"),
                ReadStats = Export<StatsFunction>(module, "PvEtwStats"),
                Block = Export<BlockFunction>(module, "PvEtwBlock"),
                Presents = Export<DataFunction>(module, "PvEtwPresents"),
                SeedCount = Export<SeedFunction>(module, "PvEtwSeedCount") };
            if (abi == null || layout == null || callback == null || loaded.Create == null || loaded.Fault == null || loaded.Stop == null
                || loaded.Destroy == null || loaded.ReadStats == null || loaded.Block == null || loaded.Presents == null || loaded.SeedCount == null)
            { reason = LoadReason("原生组件缺少导出函数", "native component is missing exported functions"); return null; }
            int version = abi();
            if (version != Abi) { reason = LoadReason("原生组件接口版本 " + version + " 与程序的 " + Abi + " 不符", "native component interface version " + version + " vs program " + Abi + " (mismatch)"); return null; }
            var sizes = new int[4];
            if (layout(sizes, sizes.Length) != 4 || sizes[0] != Marshal.SizeOf(typeof(Config)) || sizes[1] != Marshal.SizeOf(typeof(Record))
                || sizes[2] != Marshal.SizeOf(typeof(PresentRecord)) || sizes[3] != Marshal.SizeOf(typeof(Stats)))
            { reason = LoadReason("原生组件结构布局与程序不符", "native component struct layout does not match the program"); return null; }
            loaded.Callback = callback();
            if (loaded.Callback == IntPtr.Zero) { reason = LoadReason("原生组件没有事件回调", "native component has no event callback"); return null; }
            loaded.Deliver = (EventFunction)Marshal.GetDelegateForFunctionPointer(loaded.Callback, typeof(EventFunction));
            reason = "";
            return loaded;
        }

        private static T Export<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
#if !PAVISE_EMBEDDED_NATIVE

        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
#endif
    }

    // 一次完整采集的原生缓冲 消费线程退出且采集方用完之后才释放
    // 消费线程迟迟不退出时缓冲一直保留 不能在回调可能还在写的时候释放
    internal sealed class NativeEtwCapture
    {
        // 原生回调会经函数指针读 CPU 缓冲释放前委托必须一直可达
        private static readonly HashSet<NativeEtwCapture> live = new HashSet<NativeEtwCapture>();
        private readonly NativeEtw.Api api;
        private readonly NativeEtw.CpuFunction cpu;
        private readonly Delegate property;
        private readonly object gate = new object();
        private volatile IntPtr handle;
        private bool consumerReturned, released;

        private NativeEtwCapture(NativeEtw.Api owner, NativeEtw.CpuFunction reading, Delegate reader)
        { api = owner; cpu = reading; property = reader; }

        internal IntPtr Callback { get { return api.Callback; } }

        internal IntPtr Context { get { lock (gate) return handle; } }

        internal static int LiveForTest { get { lock (live) return live.Count; } }

        // threads 是本次采集固定的游戏线程集合 consumerCpuMs 与 propertyReader 都在消费线程上调用
        // propertyReader 必须是带 UnmanagedFunctionPointer 的委托 签名与 PaviseEtw.cpp 的 PropertyReading 一致 且不能抛出异常
        // 原生留下的游戏相关记录上限 和回放的关系状态块容量相同 到了就整窗作废 原因和回放超出状态块上限一样
        internal static int EmergencyRecordsForTest = 0;
        internal static int EmergencyRecords
        {
            get { return EmergencyRecordsForTest > 0 ? EmergencyRecordsForTest : ThreadRelationCollector.EmergencyMaximumBlocks * ThreadRelationCollector.TransitionsPerBlock; }
        }

        internal static NativeEtwCapture Create(int pid, long begin, long end, ICollection<int> threads, bool layoutObservation, double callerCpuBeforeEvents,
            Func<double> consumerCpuMs, Delegate propertyReader, out string failure)
        {
            NativeEtw.Api loaded = NativeEtw.Loaded;
            if (loaded == null) { failure = Logger.Format("原生事件处理不可用：{0}", "Native event processing unavailable: {0}", NativeEtw.Status); return null; }
            if (threads == null || consumerCpuMs == null || propertyReader == null) { failure = Logger.Text("原生事件处理参数缺失", "native event processing arguments missing"); return null; }
            var ids = new int[threads.Count];
            threads.CopyTo(ids, 0);
            Array.Sort(ids);
            var capture = new NativeEtwCapture(loaded, delegate
            {
                // 异常不能穿过原生栈帧 返回负无穷 原生在这个检查点停下 回放按托管处理的解码失败弃样
                try { return consumerCpuMs(); }
                catch (Exception) { return double.NegativeInfinity; }
            }, propertyReader);
            GCHandle pin = GCHandle.Alloc(ids, GCHandleType.Pinned);
            try
            {
                var config = new NativeEtw.Config {
                    Size = Marshal.SizeOf(typeof(NativeEtw.Config)), Pid = pid, Begin = begin, End = end,
                    Threads = pin.AddrOfPinnedObject(), ConsumerCpuMs = Marshal.GetFunctionPointerForDelegate(capture.cpu),
                    ReadProperty = Marshal.GetFunctionPointerForDelegate(capture.property),
                    ThreadCount = ids.Length, LayoutObservation = layoutObservation ? 1 : 0,
                    EmergencySystemEvents = FrameCoordinationSampler.EmergencySystemEvents,
                    CpuCheckInterval = FrameCoordinationSampler.CpuCheckInterval,
                    EmergencyCaptureCpuMs = FrameCoordinationSampler.EmergencyCaptureCpuMs,
                    CallerCpuBeforeEvents = callerCpuBeforeEvents, EmergencyRecords = EmergencyRecords };
                IntPtr created = loaded.Create(ref config);
                if (created == IntPtr.Zero) { failure = Logger.Text("原生事件处理拒绝了采集参数或内存不足", "native event processing rejected the capture parameters or ran out of memory"); return null; }
                capture.handle = created;
                lock (live) live.Add(capture);
                failure = null;
                return capture;
            }
            finally { pin.Free(); }
        }

        // 采集等待期间轮询 原生应急停止后可以提前收工
        internal int Fault { get { lock (gate) return handle == IntPtr.Zero ? NativeEtw.FaultNone : api.Fault(handle); } }

        internal void Stop() { lock (gate) if (handle != IntPtr.Zero) api.Stop(handle); }

        // 只给替换了 ProcessTrace 的测试用 真实采集时系统直接调用原生回调
        // 调用方就是消费者 缓冲在它返回前不会释放
        internal void Deliver(ref Native.EventRecord record)
        {
            IntPtr current = handle;
            if (current == IntPtr.Zero) return;
            record.UserContext = current;
            api.Deliver(ref record);
        }

        internal bool SeedCountForTest(int count) { lock (gate) return handle != IntPtr.Zero && api.SeedCount(handle, count) == 1; }

        internal void ConsumerReturned() { lock (gate) { consumerReturned = true; DestroyIfDoneLocked(); } }

        internal void Release() { lock (gate) { released = true; DestroyIfDoneLocked(); } }

        private void DestroyIfDoneLocked()
        {
            if (!consumerReturned || !released || handle == IntPtr.Zero) return;
            api.Destroy(handle);
            handle = IntPtr.Zero;
            lock (live) live.Remove(this);
        }

        // 只在消费线程退出后 Release 之前读取 visit 返回 false 时停止
        internal unsafe bool Read(out NativeEtw.Stats stats, Func<NativeEtw.Record, NativeEtw.PresentRecord, bool> visit)
        {
            stats = new NativeEtw.Stats { Size = Marshal.SizeOf(typeof(NativeEtw.Stats)) };
            lock (gate)
            {
                if (handle == IntPtr.Zero || !consumerReturned || released || api.ReadStats(handle, ref stats) != 1) return false;
                if (stats.Records < 0 || stats.Presents < 0 || stats.BlockRecords <= 0) return false;
                var presents = (NativeEtw.PresentRecord*)api.Presents(handle);
                int remaining = stats.Records;
                for (int block = 0; remaining > 0; block++)
                {
                    var records = (NativeEtw.Record*)api.Block(handle, block);
                    if (records == null) return false;
                    int count = Math.Min(remaining, stats.BlockRecords);
                    for (int i = 0; i < count; i++)
                    {
                        NativeEtw.Record record = records[i];
                        var present = default(NativeEtw.PresentRecord);
                        if (record.Kind == NativeEtw.KindPresent)
                        {
                            if (presents == null || record.A < 0 || record.A >= stats.Presents) return false;
                            present = presents[record.A];
                        }
                        if (!visit(record, present)) return true;
                    }
                    remaining -= count;
                }
                return true;
            }
        }
    }
}
