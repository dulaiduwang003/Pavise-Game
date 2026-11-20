using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    // 键鼠 手柄的驱动软件要读电量 改灯效 宏和 DPI 就得一直开着设备的厂商私有接口
    // 按这个行为认外设软件 不靠品牌名单 名单没收录的小厂驱动也能放行
    // 只认厂商私有接口和手柄接口 键盘鼠标输入 媒体键 耳机通话键这些标准接口由系统统一处理
    // 聊天和音乐软件只会碰标准接口 结构上就进不来 不用按名字排除
    // 只看后台治理已经判成普通后台的进程 游戏 反作弊和系统保护进程走不到这里 不会被打开句柄表
    internal static class InputDeviceHolders
    {
        private const int ScanDebounceMs = 400;
        private const int MinWakeScanGapMs = 3000;
        private const int ConfirmDelayMs = 2000;
        private const int RescanMs = 20000;
        private const int CandidateTtlMs = 10000;
        private const int IdleExitMs = 30000;
        private const int QueryTimeoutMs = 200;
        private const int MaxStuckQueries = 3;
        // 单个进程的句柄表 句柄特别多的进程也够用 超过就跳过这个进程
        private const int MaxHandleTableBytes = 64 << 20;

        // 只查候选进程自己的句柄表 不拍全系统的 本机实测 30 个候选 1.7ms 对比全系统表 45ms
        private const int ProcessHandleInformation = 51;
        private const int ObjectNameInformation = 1;
        private const int FileFsDeviceInformation = 4;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const uint ProcessDupHandle = 0x40;
        private const uint ProcessQueryInformation = 0x400;
        private const uint DuplicateSameAccess = 2;
        private const uint DigcfPresent = 0x2;
        private const uint DigcfDeviceInterface = 0x10;
        private const uint SpdrpPhysicalDeviceObjectName = 0x0E;

        private const uint SpdrpHardwareId = 0x01;

        private static readonly Guid HidInterfaceGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

        private sealed class Candidate
        {
            internal long Creation;
            internal string Name;
            internal int SeenTick;
            internal bool Holder;
            internal bool Scanned;
            internal bool Suspect;
        }

        private static readonly object sync = new object();
        private static readonly Dictionary<int, Candidate> candidates = new Dictionary<int, Candidate>();
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static Thread scanner;
        private static int lastCallTick;
        private static string[] holderNames = new string[0];

        // 下面几项只在扫描线程里读写
        private static volatile bool scanDisabled;
        private static int fileTypeIndex = -1;
        private static HashSet<int> hidDeviceTypes;
        private static int stuckQueries;
        private static QueryWorker worker;
        private static IntPtr handleBuffer = IntPtr.Zero;
        private static int handleBytes;
        private static int lastScanTick;
        private static int lastFullScanTick;
        private static readonly int selfPid = System.Diagnostics.Process.GetCurrentProcess().Id;

        // 后台扫描每轮对准备压制的进程调用 已确认开着 HID 设备的返回 true
        // 新进程先登记 交给扫描线程异步核对 通常一两轮扫描内就会放行
        internal static bool IsHolder(int pid, long creation, string name)
        {
            if (pid <= 4 || creation <= 0 || !Environment.Is64BitProcess) return false;
            bool kick = false;
            lock (sync)
            {
                int now = Environment.TickCount;
                lastCallTick = now;
                Candidate candidate;
                if (candidates.TryGetValue(pid, out candidate) && candidate.Creation == creation)
                {
                    candidate.SeenTick = now;
                    return candidate.Holder;
                }
                if (scanDisabled) return false;
                candidates[pid] = new Candidate { Creation = creation, Name = name, SeenTick = now };
                kick = true;
                if (scanner == null)
                {
                    scanner = new Thread(ScanLoop);
                    scanner.IsBackground = true;
                    scanner.Name = "Pavise input device holders";
                    scanner.Priority = ThreadPriority.BelowNormal;
                    scanner.Start();
                }
            }
            if (kick) wake.Set();
            return false;
        }

        internal static string[] HolderNamesForDisplay()
        {
            lock (sync)
            {
                return (string[])holderNames.Clone();
            }
        }

        private static void ScanLoop()
        {
            lastFullScanTick = Environment.TickCount - RescanMs;
            lastScanTick = Environment.TickCount - MinWakeScanGapMs;
            int waitMs = RescanMs;
            while (true)
            {
                bool woken = wake.WaitOne(waitMs);
                if (woken)
                {
                    // 新进程攒一批再扫 连续冒出新进程时也至少隔几秒才查一轮
                    int sinceLast = Environment.TickCount - lastScanTick;
                    Thread.Sleep(Math.Min(MinWakeScanGapMs, Math.Max(ScanDebounceMs, MinWakeScanGapMs - sinceLast)));
                }
                var batch = new Dictionary<int, long>();
                lock (sync)
                {
                    int now = Environment.TickCount;
                    if (scanDisabled)
                    {
                        // 停用后只是不再扫描 已确认的放行结果留着继续生效
                        scanner = null;
                        FreeHandleBuffer();
                        return;
                    }
                    if (now - lastCallTick > IdleExitMs)
                    {
                        // 对局结束就退出 清掉登记和缓冲区 下次对局重新核对
                        candidates.Clear();
                        holderNames = new string[0];
                        scanner = null;
                        FreeHandleBuffer();
                        return;
                    }
                    // 平时只核对新登记的进程 定期再把全部候选重扫一遍 发现后来才打开设备或已经关掉设备的
                    bool full = now - lastFullScanTick >= RescanMs;
                    var expired = new List<int>();
                    foreach (KeyValuePair<int, Candidate> pair in candidates)
                    {
                        if (now - pair.Value.SeenTick > CandidateTtlMs) expired.Add(pair.Key);
                        else if (full || !pair.Value.Scanned || pair.Value.Suspect) batch[pair.Key] = pair.Value.Creation;
                    }
                    foreach (int pid in expired) candidates.Remove(pid);
                    lastScanTick = now;
                    if (full) lastFullScanTick = now;
                }
                waitMs = RescanMs;
                if (batch.Count == 0) continue;
                HashSet<int> found;
                try
                {
                    found = Scan(batch);
                }
                catch (Exception ex)
                {
                    scanDisabled = true;
                    Logger.Warn(Lang.T("log.inputdeviceholders.3") + ex.Message);
                    continue;
                }
                if (found == null) continue;
                if (Publish(batch, found))
                {
                    waitMs = ConfirmDelayMs;
                }
                else
                {
                    int untilFull = RescanMs - (Environment.TickCount - lastFullScanTick);
                    waitMs = Math.Min(RescanMs, Math.Max(1000, untilFull));
                }
            }
        }

        // 返回是否还有待复查的进程
        private static bool Publish(Dictionary<int, long> batch, HashSet<int> found)
        {
            var added = new List<string>();
            var removed = new List<string>();
            bool pendingConfirm = false;
            lock (sync)
            {
                var names = new List<string>();
                foreach (KeyValuePair<int, Candidate> pair in candidates)
                {
                    long scannedCreation;
                    if (batch.TryGetValue(pair.Key, out scannedCreation) && scannedCreation == pair.Value.Creation)
                    {
                        Candidate candidate = pair.Value;
                        bool seen = found.Contains(pair.Key);
                        // 有的程序会周期性地开一下设备马上关掉 比如远程桌面枚举设备
                        // 第一次看到只记嫌疑 隔两秒复查还开着才放行 真正的外设驱动会一直开着
                        if (seen && !candidate.Holder)
                        {
                            if (candidate.Suspect)
                            {
                                candidate.Holder = true;
                                candidate.Suspect = false;
                                added.Add(candidate.Name + " (" + pair.Key + ")");
                            }
                            else
                            {
                                candidate.Suspect = true;
                                pendingConfirm = true;
                            }
                        }
                        else if (!seen)
                        {
                            if (candidate.Holder) removed.Add(candidate.Name + " (" + pair.Key + ")");
                            candidate.Holder = false;
                            candidate.Suspect = false;
                        }
                        candidate.Scanned = true;
                    }
                    if (pair.Value.Holder && !names.Contains(pair.Value.Name)) names.Add(pair.Value.Name);
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                holderNames = names.ToArray();
            }
            if (added.Count > 0) Logger.Log(() => Lang.T("log.inputdeviceholders.1") + string.Join(" ", added));
            if (removed.Count > 0) Logger.Log(() => Lang.T("log.inputdeviceholders.2") + string.Join(" ", removed));
            return pendingConfirm;
        }

        private static HashSet<int> Scan(Dictionary<int, long> batch)
        {
            var found = new HashSet<int>();
            var openedDevices = new List<IntPtr>();
            try
            {
                HashSet<string> pdos = HidDeviceObjectNames(fileTypeIndex < 0 || hidDeviceTypes == null ? openedDevices : null);
                if (pdos.Count == 0) return found;
                if (hidDeviceTypes == null)
                {
                    hidDeviceTypes = new HashSet<int>();
                    foreach (IntPtr device in openedDevices)
                    {
                        int type;
                        if (TryDeviceType(device, out type)) hidDeviceTypes.Add(type);
                    }
                    if (hidDeviceTypes.Count == 0) { hidDeviceTypes = null; return found; }
                }
                if (fileTypeIndex < 0)
                {
                    // 文件句柄的类型编号从本进程自己的句柄表里认 用的就是刚打开的这些设备句柄
                    if (openedDevices.Count == 0) return found;
                    var mine = new HashSet<long>();
                    foreach (IntPtr device in openedDevices) mine.Add(device.ToInt64());
                    fileTypeIndex = OwnFileTypeIndex(mine);
                    if (fileTypeIndex < 0) return found;
                }
                foreach (KeyValuePair<int, long> pair in batch)
                {
                    if (scanDisabled) break;
                    if (pair.Key == selfPid) continue;
                    if (ProcessHoldsHid(pair.Key, pair.Value, pdos)) found.Add(pair.Key);
                }
                return scanDisabled ? null : found;
            }
            finally
            {
                foreach (IntPtr device in openedDevices) CloseHandle(device);
            }
        }

        private static bool ProcessHoldsHid(int pid, long creation, HashSet<string> pdos)
        {
            IntPtr process = OpenProcess(ProcessDupHandle | ProcessQueryInformation, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                long born, exit, kernel, user;
                if (!GetProcessTimes(process, out born, out exit, out kernel, out user) || born != creation) return false;
                var handles = new List<IntPtr>();
                if (!ReadFileHandles(process, fileTypeIndex, handles)) return false;
                IntPtr self = GetCurrentProcess();
                foreach (IntPtr remote in handles)
                {
                    IntPtr local;
                    if (!DuplicateHandle(process, remote, self, out local, 0, false, DuplicateSameAccess)) continue;
                    // 先查设备类型 管道和磁盘文件直接跳过 只有设备类型对得上才查对象名
                    // 两步都放到可放弃的工作线程里 卡住的句柄留给卡住的线程 不在这里关
                    object type;
                    if (!RunTimed(delegate { int value; return TryDeviceType(local, out value) ? (object)value : null; }, out type)) return false;
                    if (type == null || !hidDeviceTypes.Contains((int)type)) { CloseHandle(local); continue; }
                    object name;
                    if (!RunTimed(delegate { return ObjectName(local); }, out name)) return false;
                    CloseHandle(local);
                    string text = name as string;
                    if (text == null || !text.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase)) continue;
                    int tail = text.IndexOf('\\', "\\Device\\".Length);
                    if (pdos.Contains(tail < 0 ? text : text.Substring(0, tail))) return true;
                }
                return false;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static bool RunTimed(Func<object> query, out object result)
        {
            if (worker == null) worker = new QueryWorker();
            if (worker.Run(query, QueryTimeoutMs, out result)) return true;
            worker = null;
            stuckQueries++;
            if (stuckQueries >= MaxStuckQueries && !scanDisabled)
            {
                // 已确认的放行结果继续有效 只是不再扫描新进程
                scanDisabled = true;
                Logger.Warn(Lang.T("log.inputdeviceholders.4") + stuckQueries);
            }
            return false;
        }

        private sealed class QueryWorker
        {
            private readonly AutoResetEvent start = new AutoResetEvent(false);
            private readonly AutoResetEvent done = new AutoResetEvent(false);
            private Func<object> job;
            private object result;

            internal QueryWorker()
            {
                var thread = new Thread(Loop);
                thread.IsBackground = true;
                thread.Name = "Pavise input device query";
                thread.Start();
            }

            private void Loop()
            {
                while (true)
                {
                    start.WaitOne();
                    object value = null;
                    try { value = job(); } catch { }
                    result = value;
                    done.Set();
                }
            }

            internal bool Run(Func<object> query, int timeoutMs, out object value)
            {
                job = query;
                start.Set();
                bool finished = done.WaitOne(timeoutMs);
                value = finished ? result : null;
                return finished;
            }
        }

        // 把一个进程自己的句柄表读进共用缓冲区 返回条目数 读不到返回 -1
        private static long ReadHandleTable(IntPtr process)
        {
            if (handleBuffer == IntPtr.Zero)
            {
                handleBytes = 256 << 10;
                handleBuffer = Marshal.AllocHGlobal(handleBytes);
            }
            while (true)
            {
                int needed;
                int status = NtQueryInformationProcess(process, ProcessHandleInformation, handleBuffer, handleBytes, out needed);
                if (status == StatusInfoLengthMismatch)
                {
                    long next = Math.Max((long)handleBytes * 2, (long)needed + (64 << 10));
                    if (next > MaxHandleTableBytes) return -1;
                    FreeHandleBuffer();
                    handleBytes = (int)next;
                    handleBuffer = Marshal.AllocHGlobal(handleBytes);
                    continue;
                }
                if (status != 0) return -1;
                long total = Marshal.ReadIntPtr(handleBuffer).ToInt64();
                long capacity = (handleBytes - IntPtr.Size * 2) / (IntPtr.Size * 3 + 16);
                return Math.Min(total, capacity);
            }
        }

        // 每条依次是 句柄值 句柄计数 指针计数 访问掩码 类型编号 属性 保留 类型编号在三个指针宽之后再偏 4 字节
        private static bool ReadFileHandles(IntPtr process, int typeIndex, List<IntPtr> into)
        {
            long count = ReadHandleTable(process);
            if (count < 0) return false;
            int ptr = IntPtr.Size;
            int entrySize = ptr * 3 + 16;
            long baseAddress = handleBuffer.ToInt64() + ptr * 2;
            for (long i = 0; i < count; i++)
            {
                IntPtr entry = new IntPtr(baseAddress + i * entrySize);
                if (Marshal.ReadInt32(entry, ptr * 3 + 4) == typeIndex) into.Add(Marshal.ReadIntPtr(entry));
            }
            return true;
        }

        private static int OwnFileTypeIndex(HashSet<long> mine)
        {
            long count = ReadHandleTable(GetCurrentProcess());
            if (count < 0) return -1;
            int ptr = IntPtr.Size;
            int entrySize = ptr * 3 + 16;
            long baseAddress = handleBuffer.ToInt64() + ptr * 2;
            for (long i = 0; i < count; i++)
            {
                IntPtr entry = new IntPtr(baseAddress + i * entrySize);
                if (mine.Contains(Marshal.ReadIntPtr(entry).ToInt64())) return Marshal.ReadInt32(entry, ptr * 3 + 4);
            }
            return -1;
        }

        private static void FreeHandleBuffer()
        {
            if (handleBuffer != IntPtr.Zero) Marshal.FreeHGlobal(handleBuffer);
            handleBuffer = IntPtr.Zero;
            handleBytes = 0;
        }

        // 系统在硬件号里标了接口用途 厂商私有接口是 FF00 到 FFFF 手柄是 HID_DEVICE_SYSTEM_GAME
        // 键盘 鼠标 媒体键 系统控制 耳机通话键都不算
        internal static bool IsDriverChannel(string[] hardwareIds)
        {
            if (hardwareIds == null) return false;
            foreach (string id in hardwareIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (id.StartsWith("HID_DEVICE_UP:FF", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("HID_DEVICE_UPR:FF", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(id, "HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // 列出在场的厂商私有接口和手柄接口的物理设备对象名 需要时顺手以零权限打开 用来认出这类句柄的类型
        private static HashSet<string> HidDeviceObjectNames(List<IntPtr> openInto)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Guid guid = HidInterfaceGuid;
            IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return names;
            try
            {
                var data = new SpDevinfoData();
                data.cbSize = (uint)Marshal.SizeOf(typeof(SpDevinfoData));
                byte[] buffer = new byte[1024];
                byte[] ids = new byte[4096];
                for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
                {
                    uint type, required;
                    if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, out type, ids, (uint)ids.Length, out required)) continue;
                    string idText = Encoding.Unicode.GetString(ids, 0, (int)Math.Min(required, (uint)ids.Length));
                    if (!IsDriverChannel(idText.Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))) continue;
                    if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpPhysicalDeviceObjectName, out type, buffer, (uint)buffer.Length, out required)) continue;
                    int length = (int)Math.Min(required, (uint)buffer.Length);
                    string name = Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
                    if (!name.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase) || !names.Add(name) || openInto == null) continue;
                    IntPtr device = CreateFileW("\\\\?\\GLOBALROOT" + name, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (device != IntPtr.Zero && device != new IntPtr(-1)) openInto.Add(device);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return names;
        }

        private static bool TryDeviceType(IntPtr handle, out int type)
        {
            type = 0;
            byte[] status = new byte[IntPtr.Size * 2];
            byte[] info = new byte[8];
            if (NtQueryVolumeInformationFile(handle, status, info, info.Length, FileFsDeviceInformation) != 0) return false;
            type = BitConverter.ToInt32(info, 0);
            return true;
        }

        private static object ObjectName(IntPtr handle)
        {
            const int size = 2048;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int returned;
                if (NtQueryObject(handle, ObjectNameInformation, buffer, size, out returned) != 0) return null;
                int length = (ushort)Marshal.ReadInt16(buffer);
                IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return length > 0 && text != IntPtr.Zero ? Marshal.PtrToStringUni(text, length / 2) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returned);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returned);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryVolumeInformationFile(IntPtr handle, byte[] ioStatus, byte[] buffer, int length, int infoClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevinfoData data, uint property, out uint type, byte[] buffer, uint size, out uint required);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
