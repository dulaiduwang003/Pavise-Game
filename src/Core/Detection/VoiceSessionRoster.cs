// @author bdth 2074055628@qq.com
// 文件用途 正在使用麦克风的进程名单 按音频采集会话枚举 供后台压制豁免 名单靠名字点不全 这里按行为兜底
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // 名单里的一条 创建时间当身份 确认时间管寿命 合并旧表时旧条目不续期
    internal sealed class VoiceSessionEntry
    {
        public long Creation;
        public long Confirmed;
    }

    // 只读 只枚举 不改任何音频状态
    //   一个后台进程开着采集流 十有八九是语音开黑 或者在录音 压它就是压语音
    //   语音软件名单（OverlayHostCatalog）点不全 新出的和改名的都漏 这里按"正在开麦"这个行为兜底
    //   枚举走 AudioSrv 的 RPC 几毫秒 不放在扫描线程上 丢线程池刷新 扫描只读上一份快照
    //   三秒一刷 没有可用快照时最多等 50 毫秒 让首轮 Sweep 也拿得到 一次刷新只等一回 等不到就按空集走
    //   错误取向 漏压一个语音进程比压错一个语音进程轻 所以部分失败合并旧表 整体失败保旧表
    //   每条有自己的确认时间 一分钟没再确认到就不算 合并不给旧条目续期
    //   名单记 pid 加创建时间 pid 被复用时新进程对不上创建时间 不白拿豁免
    //   枚举卡死超过十秒就换代重发 迟到的结果按代际丢弃 累计卡死三次永久停用 不往线程池里无限漏线程
    //   状态全部在一把短锁下改 COM 调用与等待都在锁外
    internal static class VoiceSessionRoster
    {
        internal const long TtlTicks = 3 * TimeSpan.TicksPerSecond;
        internal const long MaxAgeTicks = 60 * TimeSpan.TicksPerSecond;
        internal const long BackoffTicks = 60 * TimeSpan.TicksPerSecond;
        internal const long StallTicks = 10 * TimeSpan.TicksPerSecond;
        internal const int BackoffAfterFailures = 3;
        internal const int GiveUpAfterStalls = 3;
        internal const int FirstWaitMs = 50;
        private const long WaitOnlyIfStartedWithinTicks = TimeSpan.TicksPerSecond;

        private static readonly Dictionary<int, VoiceSessionEntry> Empty = new Dictionary<int, VoiceSessionEntry>();
        private static readonly object gate = new object();
        private static Dictionary<int, VoiceSessionEntry> current = Empty;
        private static long snapshotTicks;      // 上次拿到可用名单的时间 决定整表可用性
        private static long attemptTicks;       // 上次发起探测的时间 决定 TTL 与退避
        private static long waitedAttemptTicks; // 已经为哪次探测等过一回
        private static int attemptGeneration;   // 每次发起探测加一 迟到的结果对不上就丢
        private static bool refreshing;
        private static int failures;
        private static int stalls;              // 累计 不清零 卡死过的线程未必退出
        private static bool unavailable;
        private static readonly ManualResetEvent refreshDone = new ManualResetEvent(true);

#if PAVISE_SELFTEST
        internal static Func<Dictionary<int, long>> ProbeForTest;
        internal static Action<Action> DispatchForTest;
        internal static Func<long> ClockForTest;
        internal static void ResetForTest()
        {
            lock (gate)
            {
                current = Empty;
                snapshotTicks = 0; attemptTicks = 0; waitedAttemptTicks = 0;
                refreshing = false; failures = 0; stalls = 0; unavailable = false;
                refreshDone.Set();
            }
            ProbeForTest = null; DispatchForTest = null; ClockForTest = null;
        }
        internal static int FailuresForTest { get { lock (gate) return failures; } }
        internal static int StallsForTest { get { lock (gate) return stalls; } }
        internal static bool UnavailableForTest { get { lock (gate) return unavailable; } }
#endif

        private static long Now()
        {
#if PAVISE_SELFTEST
            if (ClockForTest != null) return ClockForTest();
#endif
            return DateTime.UtcNow.Ticks;
        }

        // 调用方知道创建时间就带上 对不上的是复用了同一个 pid 的另一个进程
        public static bool IsCapturing(int pid, long creation)
        {
            if (pid <= 4) return false;
            long now = Now();
            VoiceSessionEntry entry;
            if (!Snapshot(now).TryGetValue(pid, out entry)) return false;
            // Snapshot 里可能等过刷新 条目的确认时间会比进来时的 now 新 按判定时刻重取 未来的确认时间算新鲜
            long check = Now();
            if (check < now) check = now;
            if (check - entry.Confirmed > MaxAgeTicks) return false;
            return creation <= 0 || entry.Creation <= 0 || entry.Creation == creation;
        }

        // 过期就发一次后台刷新 本次仍返回旧快照 刷新中不叠加 卡死过久就换代重发
        //   从没成功过或快照过期时 为刚发出的刷新等一小会 一次刷新只等一回
        internal static Dictionary<int, VoiceSessionEntry> Snapshot(long now)
        {
            Action work = null;
            bool mustWait = false;
            lock (gate)
            {
                if (unavailable) return Empty;
                if (refreshing && attemptTicks != 0 && now >= attemptTicks && now - attemptTicks >= StallTicks)
                {
                    // 探测线程卡在 COM 里没回来 换代 让它的结果作废 允许再发一次
                    attemptGeneration++;
                    failures++;
                    if (++stalls >= GiveUpAfterStalls) { unavailable = true; return Empty; }
                    refreshing = false;
                    refreshDone.Set();
                }
                long wait = failures >= BackoffAfterFailures ? BackoffTicks : TtlTicks;
                bool due = attemptTicks == 0 || now < attemptTicks || now - attemptTicks >= wait;
                if (due && !refreshing)
                {
                    refreshing = true;
                    attemptTicks = now;
                    int generation = ++attemptGeneration;
                    refreshDone.Reset();
                    work = delegate { Refresh(generation); };
                }
                if (!UsableLocked(now) && attemptTicks != 0 && now >= attemptTicks
                    && now - attemptTicks < WaitOnlyIfStartedWithinTicks && waitedAttemptTicks != attemptTicks)
                {
                    waitedAttemptTicks = attemptTicks;
                    mustWait = true;
                }
            }
            if (work != null)
            {
#if PAVISE_SELFTEST
                if (DispatchForTest != null) DispatchForTest(work);
                else
#endif
                ThreadPool.QueueUserWorkItem(delegate { work(); });
            }
            if (mustWait)
            {
                refreshDone.WaitOne(FirstWaitMs);
                // 等完时钟已经往前走 快照的时间戳可能比进来时的 now 新 按等完的时间判
                long after = Now();
                if (after > now) now = after;
            }
            lock (gate) return UsableLocked(now) ? current : Empty;
        }

        // 快照时间戳比 now 新说明刚有一次提交落在取时间之后 那是最新鲜的 不能判成不可用
        private static bool UsableLocked(long now)
        {
            return snapshotTicks != 0 && now - snapshotTicks <= MaxAgeTicks;
        }

        private static void Refresh(int generation)
        {
            Dictionary<int, long> fresh = null;
            bool complete = false;
            try
            {
#if PAVISE_SELFTEST
                if (ProbeForTest != null) { fresh = ProbeForTest(); complete = fresh != null; }
                else
#endif
                fresh = Enumerate(out complete);
            }
            catch { fresh = null; }
            finally
            {
                lock (gate)
                {
                    // 换代之后到的结果一律丢 它对应的那次探测已经被判卡死 停用之后也不再发布
                    if (generation == attemptGeneration && !unavailable)
                    {
                        if (fresh != null)
                        {
                            long taken = Now();
                            var merged = new Dictionary<int, VoiceSessionEntry>();
                            foreach (KeyValuePair<int, long> pair in fresh)
                                merged[pair.Key] = new VoiceSessionEntry { Creation = pair.Value, Confirmed = taken };
                            // 部分端点没读成 把旧表里还没过寿命的并进来 保留它们原来的确认时间 不续期
                            if (!complete)
                                foreach (KeyValuePair<int, VoiceSessionEntry> old in current)
                                    if (!merged.ContainsKey(old.Key) && taken - old.Value.Confirmed <= MaxAgeTicks)
                                        merged[old.Key] = old.Value;
                            current = merged;
                            snapshotTicks = taken;
                            failures = complete ? 0 : failures + 1;
                        }
                        else failures++;
                        refreshDone.Set();
                        refreshing = false;
                    }
                }
            }
        }

        private const int DataFlowCapture = 1;
        private const int DeviceStateActive = 1;
        private const int SessionStateActive = 1;
        private const int ClsCtxAll = 0x17;
        private static readonly Guid SessionManager2Iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        private static readonly Guid DeviceEnumeratorClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        // 不声明 ComImport 的 coclass 而用 CLSID 直接创建
        //   同一进程里 AudioLowLatency 也为这个 CLSID 声明了一个 ComImport 类
        //   运行库对同一 CLSID 只认第一个类型 第二处 new 出来的对象转不成自己的类型 直接抛 InvalidCast
        //   台架实测过 通过 CLSID 创建的是无类型 RCW 转接口走 QueryInterface 不受影响
        private static IMMDeviceEnumerator CreateEnumerator()
        {
            Type type = Type.GetTypeFromCLSID(DeviceEnumeratorClsid);
            if (type == null) return null;
            return (IMMDeviceEnumerator)Activator.CreateInstance(type);
        }

        // 所有活动采集端点上处于 Active 状态的会话 取其进程号 再查一次创建时间当身份
        //   HRESULT 只把负值当失败 GetProcessId 对跨进程会话返回正的 S_ 码并给出创建进程
        //   任一端点或会话读不成 complete 置假 调用方决定怎么合并
        private static Dictionary<int, long> Enumerate(out bool complete)
        {
            complete = true;
            var result = new Dictionary<int, long>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection devices = null;
            try
            {
                enumerator = CreateEnumerator();
                if (enumerator == null) { complete = false; return null; }
                if (enumerator.EnumAudioEndpoints(DataFlowCapture, DeviceStateActive, out devices) < 0
                    || devices == null) { complete = false; return null; }
                uint count;
                if (devices.GetCount(out count) < 0) { complete = false; return null; }
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        if (devices.Item(i, out device) < 0 || device == null) { complete = false; continue; }
                        if (!CollectDevice(device, result)) complete = false;
                    }
                    finally { if (device != null) Marshal.ReleaseComObject(device); }
                }
                return result;
            }
            finally
            {
                if (devices != null) Marshal.ReleaseComObject(devices);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        private static bool CollectDevice(IMMDevice device, Dictionary<int, long> result)
        {
            object raw;
            Guid iid = SessionManager2Iid;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out raw) < 0 || raw == null) return false;
            IAudioSessionManager2 manager = null;
            IAudioSessionEnumerator sessions = null;
            bool ok = true;
            try
            {
                manager = (IAudioSessionManager2)raw;
                if (manager.GetSessionEnumerator(out sessions) < 0 || sessions == null) return false;
                int count;
                if (sessions.GetCount(out count) < 0) return false;
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 session = null;
                    try
                    {
                        if (sessions.GetSession(i, out session) < 0 || session == null) { ok = false; continue; }
                        int state;
                        if (session.GetState(out state) < 0) { ok = false; continue; }
                        if (state != SessionStateActive) continue;
                        uint pid;
                        if (session.GetProcessId(out pid) < 0) { ok = false; continue; }
                        if (pid <= 4 || result.ContainsKey((int)pid)) continue;
                        result[(int)pid] = CreationOf((int)pid);
                    }
                    finally { if (session != null) Marshal.ReleaseComObject(session); }
                }
                return ok;
            }
            finally
            {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
                if (manager != null) Marshal.ReleaseComObject(manager);
                else Marshal.ReleaseComObject(raw);
            }
        }

        // 查不到就记 0 调用方对 0 不做身份校验 只是少一层保险 不是漏保
        private static long CreationOf(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                long creation; long cpu; ulong io;
                return Native.QueryProcessSample(h, out creation, out cpu, out io) ? creation : 0;
            }
            catch { return 0; }
            finally { Native.CloseHandle(h); }
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object activated);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        // IAudioSessionManager 与 IAudioSessionManager2 共用一张虚表 按顺序全量声明
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
            [PreserveSig] int RegisterSessionNotification(IntPtr notification);
            [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
            [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            // 声明成 Control2 让封送层直接 QueryInterface 拿到带进程号的那一版
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        // IAudioSessionControl 与 IAudioSessionControl2 共用一张虚表 按顺序全量声明
        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }
    }
}
