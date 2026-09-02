// @author bdth 2074055628@qq.com
// 文件用途 监听系统进程启动事件并唤醒调度线程
using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;

namespace PaviseApp
{
    internal enum ProcessChangeKind { Started, Stopped }

    internal sealed class ProcessChange
    {
        public int Pid;
        public int ParentPid;
        public long ParentCreation;
        public int Session = -1;
        public string Name;
        public string Path;
        public long Creation;
        public long Sequence;
        public ProcessChangeKind Kind;
    }

    // 进程启停按批交付而不是逐个回调 一次游戏启动会带出启动器
    //   中转和本体好几个进程 攒成一批才能在同一份快照里认全父子关系
    internal sealed class ProcessChangeBatch
    {
        public readonly ProcessChange[] Changes;
        // 内核缓冲溢出过 这批漏了事件 父子关系不再可信
        //   检测标脏走全量重扫 家族历史直接清空重来
        public readonly bool Overflowed;

        public ProcessChangeBatch(ProcessChange[] changes, bool overflowed)
        {
            Changes = changes ?? new ProcessChange[0];
            Overflowed = overflowed;
        }
    }

    internal sealed class ProcNotify
    {

        public event Action Changed;
        public event Action<ProcessChangeBatch> BatchChanged;
        public Func<string, int, bool> CaptureStartIdentity;
        public Func<int, string, int, bool> CaptureParentIdentity;
        public Func<string, int, bool> FastTrack;
        public event Action FastStart;

        private EtwProcessWatcher etw;
        private long lastFastStartTicks;
        private const int FastStartMinGapMs = 250;

        private ManagementEventWatcher startW, stopW;
        private Timer coalesce;
        private int pending;
        private int accepting;
        private int lifecycleGeneration;
        private readonly object lifecycleSync = new object();
        private readonly object batchSync = new object();
        private readonly object dispatchSync = new object();
        private readonly Dictionary<long, ProcessChange> changes = new Dictionary<long, ProcessChange>();
        private bool overflowed;
        private volatile bool active;
        private long sequence;
        private const int WindowMs = 750;
        private const int MaxBatchChanges = 256;

        public bool IsActive { get { return active; } }

        public void Start()
        {

            lock (dispatchSync)
            {
                lock (lifecycleSync)
                {
                    if (Volatile.Read(ref accepting) != 0) return;
                    int generation = Interlocked.Increment(
                        ref lifecycleGeneration);
                    coalesce = new Timer(
                        OnWindow, generation, Timeout.Infinite, Timeout.Infinite);
                    try
                    {
                        startW = Watch(
                            "Win32_ProcessStartTrace",
                            ProcessChangeKind.Started, generation);
                        stopW = Watch(
                            "Win32_ProcessStopTrace",
                            ProcessChangeKind.Stopped, generation);
                        Interlocked.Exchange(ref accepting, 1);
                        active = true;
                        StartFastPath();
                    }
                    catch (Exception ex)
                    {
                        active = false;
                        Interlocked.Exchange(ref accepting, 0);
                        Interlocked.Increment(ref lifecycleGeneration);
                        Kill(ref startW);
                        Kill(ref stopW);
                        DisposeTimer();
                        DrainPending();
                        Logger.Warn(Lang.T("log.procnotify.1") + ex.Message);
                    }
                }
            }
        }

        private void StartFastPath()
        {
            if (FastTrack == null) return;
            try
            {
                if (!Native.IsElevated()) return;
                etw = new EtwProcessWatcher("Pavise.ProcWatch");
                etw.ProcessStarted += OnFastProcessStart;
                if (etw.Start())
                    Logger.Log(Lang.T("log.procnotify.2"));
                else
                {
                    Logger.Log(Lang.T("log.procnotify.3") + etw.LastError);
                    try { etw.Dispose(); } catch { }
                    etw = null;
                }
            }
            catch { etw = null; }
        }

        private void OnFastProcessStart(int pid, long stamp)
        {
            try
            {
                if (FastTrack == null || FastStart == null || pid <= 4) return;
                IntPtr h = Native.OpenProcess(
                    Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return;
                string name;
                int session;
                try
                {
                    name = ResolveCurrentProcessName(Native.ImagePath(h));
                    if (!Native.TryGetLiveProcessSessionId(h, pid, out session)) return;
                }
                finally { Native.CloseHandle(h); }
                FastTrackMaybe(name, session);
            }
            catch { }
        }

        private void FastTrackMaybe(string name, int session)
        {
            try
            {
                Func<string, int, bool> track = FastTrack;
                Action fire = FastStart;
                if (track == null || fire == null) return;
                if (string.IsNullOrEmpty(name) || !track(name, session)) return;
                long now = DateTime.UtcNow.Ticks;
                long last = Interlocked.Read(ref lastFastStartTicks);
                if (last != 0 && now - last < FastStartMinGapMs * TimeSpan.TicksPerMillisecond) return;
                if (Interlocked.CompareExchange(ref lastFastStartTicks, now, last) != last) return;
                fire();
            }
            catch { }
        }

        private ManagementEventWatcher Watch(
            string cls, ProcessChangeKind kind, int generation)
        {
            var w = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM " + cls));
            w.Scope.Options.EnablePrivileges = true;
            w.EventArrived += delegate(object sender, EventArrivedEventArgs args)
            {
                OnEvent(kind, args, generation);
            };
            try { w.Start(); }
            catch { try { w.Dispose(); } catch { } throw; }
            return w;
        }

        private void OnEvent(
            ProcessChangeKind kind, EventArrivedEventArgs e, int generation)
        {
            if (!IsAccepting(generation))
            {
                DisposeNewEvent(e);
                return;
            }
            ProcessChange change = null;
            try
            {
                ManagementBaseObject ne = e.NewEvent;
                if (ne != null)
                {
                    try
                    {
                        int pid = Convert.ToInt32(ne["ProcessID"]);
                        string name = Convert.ToString(ne["ProcessName"]);
                        if (pid > 0)
                        {
                            change = new ProcessChange
                            {
                                Pid = pid,
                                Name = StripExe(name),
                                Kind = kind,
                                Sequence = Interlocked.Increment(ref sequence)
                            };
                            try { change.ParentPid = Convert.ToInt32(ne["ParentProcessID"]); } catch { }
                            try { change.Session = Convert.ToInt32(ne["SessionID"]); } catch { }
                            if (kind == ProcessChangeKind.Started)
                            {
                                bool captureIdentity = false;
                                try
                                {
                                    Func<string, int, bool> need =
                                        CaptureStartIdentity;
                                    captureIdentity = need != null
                                        && need(change.Name, change.Session);
                                }
                                catch { }
                                bool captureParent = false;
                                try
                                {
                                    Func<int, string, int, bool> need =
                                        CaptureParentIdentity;
                                    captureParent = need != null
                                        && need(change.ParentPid, change.Name,
                                            change.Session);
                                }
                                catch { }
                                if (captureIdentity || captureParent)
                                {
                                    int eventParentPid = change.ParentPid;
                                    int eventSession = change.Session;

                                    change.ParentPid = 0;
                                    change.ParentCreation = 0;
                                    change.Session = -1;
                                    change.Name = "";
                                    change.Path = null;
                                    change.Creation = 0;
                                    IntPtr handle = Native.OpenProcess(
                                        Native.PROCESS_QUERY_LIMITED_INFORMATION
                                            | Native.SYNCHRONIZE,
                                        false, pid);
                                    if (handle != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            long cpu;
                                            ulong io;
                                            long creation;
                                            int currentSession;
                                            string currentPath =
                                                Native.ImagePath(handle);
                                            string currentName =
                                                ResolveCurrentProcessName(
                                                    currentPath);
                                            int verifiedSession =
                                                ResolveVerifiedSessionId(
                                                    eventSession,
                                                    Native.TryGetLiveProcessSessionId(
                                                        handle, pid,
                                                        out currentSession),
                                                    currentSession);
                                            if (verifiedSession >= 0
                                                && currentName.Length > 0
                                                && Native.QueryProcessSample(
                                                    handle, out creation,
                                                    out cpu, out io)
                                                && creation > 0)
                                            {
                                                change.Session =
                                                    verifiedSession;
                                                change.Name = currentName;
                                                change.Path = currentPath;
                                                change.Creation = creation;
                                                change.ParentPid =
                                                    ResolveVerifiedParentPid(
                                                        eventParentPid,
                                                        Native.ParentProcessId(
                                                            handle));
                                            }
                                        }
                                        finally { Native.CloseHandle(handle); }
                                    }
                                }

                                if (captureParent && change.ParentPid > 0)
                                {
                                    IntPtr parent = Native.OpenProcess(
                                        Native.PROCESS_QUERY_LIMITED_INFORMATION,
                                        false, change.ParentPid);
                                    if (parent != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            long cpu;
                                            ulong io;
                                            Native.QueryProcessSample(parent,
                                                out change.ParentCreation, out cpu, out io);
                                        }
                                        finally { Native.CloseHandle(parent); }
                                    }
                                }
                            }
                        }
                    }
                    finally { ne.Dispose(); }
                }
            }
            catch { }
            if (change == null) return;
            if (change.Kind == ProcessChangeKind.Started)
                FastTrackMaybe(change.Name, change.Session);

            Timer timer = null;
            bool armTimer = false;
            lock (batchSync)
            {

                if (!IsAccepting(generation)) return;
                long key = ((long)(uint)change.Pid << 1)
                    | (change.Kind == ProcessChangeKind.Stopped ? 1L : 0L);
                if (changes.Count < MaxBatchChanges || changes.ContainsKey(key))
                    changes[key] = change;
                else overflowed = true;
                if (Interlocked.Exchange(ref pending, 1) == 0)
                {
                    timer = coalesce;
                    armTimer = true;
                }
            }
            if (armTimer)
            {
                try
                {
                    if (IsAccepting(generation) && timer != null)
                        timer.Change(WindowMs, Timeout.Infinite);
                }
                catch { }
            }
        }

        private void OnWindow(object state)
        {
            int generation = state is int ? (int)state : -1;

            lock (dispatchSync)
            {
                if (!IsAccepting(generation)) return;
                ProcessChangeBatch batch;
                lock (batchSync)
                {
                    var copy = new ProcessChange[changes.Count];
                    changes.Values.CopyTo(copy, 0);
                    Array.Sort(copy, delegate(ProcessChange left, ProcessChange right)
                    {
                        return left.Sequence.CompareTo(right.Sequence);
                    });
                    batch = new ProcessChangeBatch(copy, overflowed);
                    changes.Clear();
                    overflowed = false;
                    Interlocked.Exchange(ref pending, 0);
                }
                if (batch.Changes.Length == 0 && !batch.Overflowed) return;
                Action<ProcessChangeBatch> detailed = BatchChanged;
                if (detailed != null)
                    foreach (Action<ProcessChangeBatch> handler
                        in detailed.GetInvocationList())
                    {
                        if (!IsAccepting(generation)) return;
                        try { handler(batch); } catch { }
                    }
                Action h = Changed;
                if (h != null)
                    foreach (Action handler in h.GetInvocationList())
                    {
                        if (!IsAccepting(generation)) return;
                        try { handler(); } catch { }
                    }
            }
        }

        public void Stop()
        {

            lock (dispatchSync)
            {
                lock (lifecycleSync)
                {

                    Interlocked.Exchange(ref accepting, 0);
                    Interlocked.Increment(ref lifecycleGeneration);
                    active = false;
                    if (etw != null) { try { etw.Dispose(); } catch { } etw = null; }
                    Kill(ref startW);
                    Kill(ref stopW);
                    DisposeTimer();
                    DrainPending();
                }
            }
        }

        private bool IsAccepting(int generation)
        {
            return Volatile.Read(ref accepting) != 0
                && Volatile.Read(ref lifecycleGeneration) == generation;
        }

        private void DisposeTimer()
        {
            Timer timer = coalesce;
            coalesce = null;
            if (timer != null) { try { timer.Dispose(); } catch { } }
        }

        private void DrainPending()
        {
            lock (dispatchSync)
            {
                lock (batchSync)
                {
                    changes.Clear();
                    overflowed = false;
                    Interlocked.Exchange(ref pending, 0);
                }
            }
        }

        private static void Kill(ref ManagementEventWatcher w)
        {
            if (w == null) return;
            try { w.Stop(); } catch { }
            try { w.Dispose(); } catch { }
            w = null;
        }

        private static string StripExe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4) : name;
        }

        internal static int ResolveVerifiedParentPid(
            int eventParentPid, int currentParentPid)
        {
            if (currentParentPid <= 0) return 0;
            if (eventParentPid > 0 && eventParentPid != currentParentPid)
                return 0;
            return currentParentPid;
        }

        internal static int ResolveVerifiedSessionId(
            int eventSession, bool currentSessionKnown, int currentSession)
        {
            if (!currentSessionKnown || eventSession < 0
                || currentSession < 0 || eventSession != currentSession)
                return -1;
            return currentSession;
        }

        internal static string ResolveCurrentProcessName(
            string currentImagePath)
        {
            if (string.IsNullOrEmpty(currentImagePath)) return "";
            try
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(
                    currentImagePath);
                return string.IsNullOrEmpty(name) ? "" : name;
            }
            catch { return ""; }
        }

        private static void DisposeNewEvent(EventArrivedEventArgs e)
        {
            try
            {
                ManagementBaseObject value = e == null ? null : e.NewEvent;
                if (value != null) value.Dispose();
            }
            catch { }
        }
    }
}
