using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
    internal static partial class CriticalThreadGuardian
    {

        private sealed class ParentGuardian : IHardThreadGuardian, IDisposable
        {
            private sealed class CleanupFiles : IHardThreadCleanupFiles
            {
                private readonly ParentGuardian owner;

                private readonly HardThreadFileStore store;

                private readonly int[] openErrors = new int[6];

                // 仅记录清理阻塞阶段，不参与判定或次数结算。
                internal string Blocker, Deferred;

                private static string PartName(HardThreadCleanupPart part)
                {
                    switch (part)
                    {
                    case HardThreadCleanupPart.Ledger: return "亲和性账本";
                    case HardThreadCleanupPart.SessionLock: return "会话锁";
                    case HardThreadCleanupPart.Configuration: return "守护配置";
                    case HardThreadCleanupPart.Executable: return "守护程序";
                    case HardThreadCleanupPart.Registration: return "登记文件";
                    default: return "守护目录";
                    }
                }

                private Scope Original
                {
                    get
                    {
                        return owner.OriginalScope;
                    }
                }

                private string DirectoryPath
                {
                    get
                    {
                        return Path.Combine(Original.Root, "guards", Original.Target.SessionId.ToString("N"));
                    }
                }

                private string ExecutablePath
                {
                    get
                    {
                        return Path.Combine(DirectoryPath, "Pavise.HardThreadGuardian.exe");
                    }
                }

                public bool OriginalSourceReady
                {
                    get
                    {
                        try
                        {
                            // 逐阶段记录阻塞原因，便于定位清理失败。
                            if (!owner.childGone || !HardThreadCleanupPolicy.ChildSettled(owner.SpawnSucceeded, owner.ChildState))
                            {
                                Blocker = "守护子进程退出尚未确认";
                                return false;
                            }
                            if (owner.Parent == null || owner.Parent.State != 0 || !owner.Parent.Actor.Same(Original.Parent) || !owner.Parent.VerifyImage())
                            {
                                Blocker = "本进程身份或程序文件核验未通过";
                                return false;
                            }
                            if (owner.Root == null || !owner.Root.Valid || owner.Root.Identity != Original.RootIdentity || owner.GuardsDirectory == null || !owner.GuardsDirectory.Valid)
                            {
                                Blocker = "守护根目录身份已变化或句柄失效";
                                return false;
                            }
                            RequireAcl(Original.Root, Original.Sid);
                            if (!store.MatchesSettled(owner.settledSource, owner.cleanupState.Removed(HardThreadCleanupPart.Ledger)))
                            {
                                Blocker = "亲和性账本或统一线程账本与结清时的记录不一致";
                                return false;
                            }
                            foreach (HardThreadCleanupPart value in Enum.GetValues(typeof(HardThreadCleanupPart)))
                            {
                                string path = PartPath(value);
                                if (owner.cleanupState.Removed(value))
                                {
                                    if (!Missing(path))
                                    {
                                        Blocker = PartName(value) + "已删除后重新出现";
                                        return false;
                                    }
                                }
                                else
                                {
                                    if (owner.cleanupState.Deferred(value))
                                    {
                                        continue;
                                    }
                                    if (value == HardThreadCleanupPart.Directory)
                                    {
                                        if (owner.SessionDirectory != null)
                                        {
                                            if (!owner.SessionDirectory.Valid)
                                            {
                                                Blocker = "守护目录句柄失效";
                                                return false;
                                            }
                                            owner.directoryIdentity = owner.SessionDirectory.Identity;
                                        }
                                        if (string.IsNullOrEmpty(owner.directoryIdentity))
                                        {
                                            Blocker = "守护目录身份未知";
                                            return false;
                                        }
                                    }
                                    else if (!VerifyFile(path, ExpectedHash(value)))
                                    {
                                        Blocker = PartName(value) + "内容与原记录不一致";
                                        return false;
                                    }
                                }
                            }
                            return true;
                        }
                        catch (Exception error)
                        {
                            Blocker = "守护源核验异常：" + Brief(error);
                            owner.RememberClosureFailure(error);
                            return false;
                        }
                    }
                }

                public bool ClosureCertain
                {
                    get
                    {
                        return !owner.closeFault;
                    }
                }

                internal CleanupFiles(ParentGuardian original, HardThreadFileStore store)
                {
                    owner = original;
                    this.store = store;
                }

                private string PartPath(HardThreadCleanupPart part)
                {
                    string sessionIdText = Original.Target.SessionId.ToString("N");
                    switch (part)
                    {
                    case HardThreadCleanupPart.Ledger:
                        return Path.Combine(Original.Root, "hard-thread-" + sessionIdText + ".xml");
                    case HardThreadCleanupPart.SessionLock:
                        return Path.Combine(Original.Root, "hard-thread-" + sessionIdText + ".lock");
                    case HardThreadCleanupPart.Configuration:
                        return ExecutablePath + ".config";
                    case HardThreadCleanupPart.Executable:
                        return ExecutablePath;
                    case HardThreadCleanupPart.Registration:
                        return Path.Combine(DirectoryPath, "scope.xml");
                    default:
                        return DirectoryPath;
                    }
                }

                private string ExpectedHash(HardThreadCleanupPart part)
                {
                    switch (part)
                    {
                    case HardThreadCleanupPart.Ledger:
                        return owner.settledSource.JournalHash;
                    case HardThreadCleanupPart.SessionLock:
                        return Hash(new byte[0]);
                    case HardThreadCleanupPart.Configuration:
                        if (!owner.ConfigurationCreated)
                        {
                            return null;
                        }
                        return Hash(Text("<configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.7.2\"/></startup></configuration>"));
                    case HardThreadCleanupPart.Executable:
                        if (!owner.ExecutableCreated)
                        {
                            return null;
                        }
                        return Original.Parent.ImageHash;
                    case HardThreadCleanupPart.Registration:
                        if (!owner.RegistrationCreated || owner.Scope == null)
                        {
                            return null;
                        }
                        return owner.Scope.Digest;
                    default:
                        return null;
                    }
                }

                private static bool Missing(string path)
                {
                    try
                    {
                        File.GetAttributes(path);
                        return false;
                    }
                    catch (FileNotFoundException)
                    {
                        return true;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        return true;
                    }
                }

                private static bool VerifyFile(string path, string expected)
                {
                    if (expected == null)
                    {
                        return Missing(path);
                    }
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                    FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    bool valid = false;
                    HardThreadConstructionCleanup.AcquireAndRelease(delegate
                    {
                        if (source.Length <= 67108864)
                        {
                            valid = Hash(source) == expected;
                        }
                    }, delegate
                    {
                        HardThreadConstructionCleanup.CloseOwned(source);
                    });
                    return valid;
                }

                public bool DeleteExact(HardThreadCleanupPart part)
                {
                    try
                    {
                        if (part == HardThreadCleanupPart.Executable && owner.Child != null)
                        {
                            owner.Child.CloseImage();
                        }
                        if (part == HardThreadCleanupPart.Registration && owner.Source != null)
                        {
                            owner.Source.Dispose();
                            owner.Source = null;
                        }
                        if (part == HardThreadCleanupPart.Directory && owner.SessionDirectory != null)
                        {
                            owner.SessionDirectory.Dispose();
                            owner.SessionDirectory = null;
                        }
                        string path = PartPath(part);
                        string expected = ExpectedHash(part);
                        if (part != HardThreadCleanupPart.Directory && expected == null)
                        {
                            bool absent = Missing(path);
                            if (!absent) Blocker = PartName(part) + "并非本次创建，却仍然存在";
                            return absent;
                        }
                        NoReparse(Path.GetDirectoryName(path));
                        IntPtr handle = CreateFile(path, 2147549184u, 5u, IntPtr.Zero, 3u, (part == HardThreadCleanupPart.Directory) ? 35651584u : 2097152u, IntPtr.Zero);
                        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                        {
                            openErrors[(int)part] = Marshal.GetLastWin32Error();
                            Blocker = PartName(part) + "无法以删除权限打开，错误码=" + openErrors[(int)part];
                            return false;
                        }
                        bool marked = false;
                        string refused = "文件信息不可读或为重解析点";
                        HardThreadConstructionCleanup.AcquireAndRelease(delegate
                        {
                            FileIdentity info;
                            if (GetFileInformationByHandle(handle, out info) && (info.Attributes & 0x400) == 0)
                            {
                                if (part == HardThreadCleanupPart.Directory)
                                {
                                    if ((info.Attributes & 0x10) == 0 || FileKey(info) != owner.directoryIdentity)
                                    {
                                        refused = "目录身份与原记录不符";
                                        return;
                                    }
                                }
                                else
                                {
                                    if ((info.Attributes & 0x10u) != 0)
                                    {
                                        refused = "路径已变成目录";
                                        return;
                                    }
                                    using (SafeFileHandle handle2 = new SafeFileHandle(handle, false))
                                    {
                                        using (FileStream fileStream = new FileStream(handle2, FileAccess.Read))
                                        {
                                            if (fileStream.Length > 67108864 || Hash(fileStream) != expected)
                                            {
                                                refused = "内容与原记录不一致";
                                                return;
                                            }
                                        }
                                    }
                                }
                                DeleteDisposition deleteDisposition = default(DeleteDisposition);
                                deleteDisposition.Delete = 1;
                                DeleteDisposition information = deleteDisposition;
                                marked = SetFileInformationByHandle(handle, 4, ref information, 1u);
                                if (!marked) refused = "删除标记被系统拒绝，错误码=" + Marshal.GetLastWin32Error();
                            }
                        }, delegate
                        {
                            bool uncertain = false;
                            HardThreadRawHandleClosure.Close(ref handle, ref uncertain, CloseHandle);
                        });
                        if (!marked) Blocker = PartName(part) + "未删除：" + refused;
                        return marked;
                    }
                    catch (Exception error)
                    {
                        Blocker = PartName(part) + "清理异常：" + Brief(error);
                        owner.RememberClosureFailure(error);
                        return false;
                    }
                }

                public bool HeldElsewhere(HardThreadCleanupPart part)
                {
                    if (part == HardThreadCleanupPart.Directory || openErrors[(int)part] != 32)
                    {
                        return false;
                    }
                    try
                    {
                        string expectedHashResult = ExpectedHash(part);
                        if (expectedHashResult == null || !VerifyFile(PartPath(part), expectedHashResult))
                        {
                            return false;
                        }
                    }
                    catch (Exception error)
                    {
                        owner.RememberClosureFailure(error);
                        return false;
                    }
                    NoteSettledLeftovers();
                    Deferred = (Deferred == null ? "" : Deferred + "、") + PartName(part);
                    return true;
                }
            }

            internal readonly string ControlledRoot;

            internal Scope Setup;

            internal bool SpawnSucceeded;

            // WMI 超时不表示未创建。结果未知期间保留原控制器，阻止删除现场或再次派生。
            internal bool BrokerSpawnPending, BrokerSpawnRequested, BrokerAbsenceConfirmed;
            internal string BrokerSpawnFailure;

            internal bool DirectoryCreated;

            internal volatile bool controllerReleased;

            internal ActorPin Parent;

            internal HardThreadGuardianBrokerPin BrokerProof;

            internal string VerifyBrokerProof(HardThreadGuardianBrokerPin proof)
            {
                lock (lifetime)
                {
                    if (disposed || BrokerProof != null) throw new IOException("独立守护所有者已关闭或 WMI 证明已登记");
                    BrokerProof = proof;
                    return BrokerProof.Verify();
                }
            }

            internal string RecheckBrokerProof()
            {
                lock (lifetime)
                {
                    if (disposed) throw new IOException("独立守护所有者在 armed 前已关闭");
                    return BrokerProof == null ? null : BrokerProof.Verify();
                }
            }

            internal DirectoryPin SessionDirectory;

            internal DirectoryPin GuardsDirectory;

            internal IntPtr UnverifiedSpawn;

            internal IntPtr SpawnThread;

            internal bool ExecutableCreated;

            internal bool ConfigurationCreated;

            internal bool RegistrationCreated;

            internal Func<HardThreadGuardianActor, bool> Unregister;

            private readonly HardThreadCleanupState cleanupState = new HardThreadCleanupState();

            private bool cleanupComplete;

            private bool protectionReleased;

            private bool childGone;

            private bool closeFault;

            private bool spawnThreadCloseFault;

            private bool spawnProcessCloseFault;

            private Exception unassignedCloseFailure;

            private string directoryIdentity;

            private Timer cleanupTimer;

            private int cleanupDelay = 1000;
            private int cleanupAttempts;
            private bool cleanupExitPhase;
            // 仅记录文件清理与保护注销的阻塞阶段。
            private string cleanupBlocker, cleanupDeferred, protectionBlocker;

            private static string Brief(Exception error)
            {
                string text = (error.GetType().Name + " " + error.Message).Replace('\r', ' ').Replace('\n', ' ');
                return text.Length > 200 ? text.Substring(0, 200) : text;
            }

            internal Scope Scope;

            internal ActorPin Child;

            internal RootPin Root;

            internal FileStream Source;

            internal NamedPipeServerStream Pipe;

            private RegisteredWaitHandle wait;

            private ActorWait waitObject;

            private Action lost;

            private readonly object lifetime = new object();

            private volatile bool failed;

            private volatile bool disposed;

            private HardThreadSettledSource settledSource;

            internal Scope OriginalScope
            {
                get
                {
                    return Scope ?? Setup;
                }
            }

            private bool ControllerClosed
            {
                get
                {
                    if (!BrokerSpawnPending && BrokerProof == null && Child == null && Parent == null && Root == null && Source == null && Pipe == null && SessionDirectory == null && GuardsDirectory == null && wait == null && waitObject == null && UnverifiedSpawn == IntPtr.Zero)
                    {
                        return SpawnThread == IntPtr.Zero;
                    }
                    return false;
                }
            }

            private HardThreadRead ChildState
            {
                get
                {
                    if (Child != null)
                    {
                        return Child.State;
                    }
                    if (UnverifiedSpawn == IntPtr.Zero)
                    {
                        if (BrokerSpawnRequested && !BrokerSpawnPending && BrokerAbsenceConfirmed) return HardThreadRead.Gone;
                        return HardThreadRead.Unknown;
                    }
                    switch (WaitForSingleObject(UnverifiedSpawn, 0u))
                    {
                    default:
                        return HardThreadRead.Unknown;
                    case 258u:
                        return HardThreadRead.Known;
                    case 0u:
                        return HardThreadRead.Gone;
                    }
                }
            }

            public HardThreadGuardianActor Actor
            {
                get
                {
                    lock (lifetime)
                    {
                        return (Scope == null) ? null : Scope.Child;
                    }
                }
            }

            public bool ResourcesClosed
            {
                get
                {
                    lock (lifetime)
                    {
                        return cleanupComplete && protectionReleased && ControllerClosed && !closeFault;
                    }
                }
            }

            internal ParentGuardian(string root)
            {
                ControlledRoot = Canonical(root);
            }

            internal void RememberClosureFailure(Exception error)
            {
                if (!HardThreadClosureFailure.Contains(error))
                {
                    return;
                }
                lock (lifetime)
                {
                    closeFault = true;
                    if (unassignedCloseFailure == null)
                    {
                        unassignedCloseFailure = error;
                    }
                }
            }

            internal void ReleaseSpawnHandles(bool releaseProcess)
            {
                lock (lifetime)
                {
                    try
                    {
                        HardThreadSpawnHandleClosure.Close(ref SpawnThread, ref spawnThreadCloseFault, ref UnverifiedSpawn, ref spawnProcessCloseFault, releaseProcess, CloseHandle);
                    }
                    catch (Exception error)
                    {
                        RememberClosureFailure(error);
                        throw;
                    }
                }
            }

            internal void BeginBrokerSpawn()
            {
                lock (lifetime)
                {
                    if (disposed || BrokerSpawnRequested || Child != null || UnverifiedSpawn != IntPtr.Zero || SpawnThread != IntPtr.Zero)
                        throw new IOException("独立守护备用创建资格或旧句柄未结清");
                    BrokerSpawnRequested = BrokerSpawnPending = true;
                }
            }

            internal void CompleteBrokerSpawn(IntPtr process, bool absenceConfirmed, string failure)
            {
                bool cleanup;
                lock (lifetime)
                {
                    // 仅由本次创建请求的工作线程处理；超时方保留所有权，不另建请求。
                    BrokerSpawnFailure = failure;
                    if (process != IntPtr.Zero)
                    {
                        UnverifiedSpawn = process;
                        SpawnSucceeded = true;
                        BrokerSpawnPending = false;
                    }
                    else if (absenceConfirmed)
                    {
                        BrokerAbsenceConfirmed = true;
                        BrokerSpawnPending = false;
                    }
                    cleanup = disposed;
                    // 迟到结果不能被此前的清理失败预算挡住退出通知；观察本身不写游戏，也不补充清理次数。
                    if (cleanup && process != IntPtr.Zero) WatchLocked();
                }
                if (cleanup) Dispose();
            }

            private void ScheduleCleanupLocked()
            {
                if (cleanupAttempts >= 3) return;
                if (cleanupTimer == null)
                {
                    cleanupTimer = new Timer(delegate
                    {
                        try
                        {
                            Dispose();
                        }
                        catch
                        {
                            lock (lifetime)
                            {
                                ScheduleCleanupLocked();
                            }
                        }
                    }, null, cleanupDelay, -1);
                }
                else
                {
                    cleanupTimer.Change(cleanupDelay, -1);
                }
                cleanupDelay = Math.Min(30000, cleanupDelay * 2);
            }

            private void FinishProtectionRelease()
            {
                Func<HardThreadGuardianActor, bool> unregister;
                HardThreadGuardianActor arg;
                lock (lifetime)
                {
                    if (!cleanupComplete || !ControllerClosed || closeFault || protectionReleased)
                    {
                        return;
                    }
                    unregister = Unregister;
                    arg = ((Scope == null) ? null : Scope.Child);
                }
                try
                {
                    if (!CleanupReleasedGameThreadControl(OriginalScope, settledSource))
                    { lock (lifetime) { protectionBlocker = "统一线程账本未清理：与结清记录不一致或删除未完成"; ScheduleCleanupLocked(); } return; }
                }
                catch (Exception error)
                { lock (lifetime) { protectionBlocker = "统一线程账本清理未通过：" + Brief(error); ScheduleCleanupLocked(); } return; }
                bool registrationClosed = unregister == null;
                string registrationFault = null;
                if (unregister != null)
                {
                    try
                    {
                        registrationClosed = unregister(arg);
                    }
                    catch (Exception error)
                    {
                        registrationFault = Brief(error);
                    }
                }
                lock (lifetime)
                {
                    protectionBlocker = registrationClosed ? null : "后台保护登记未注销："
                        + (registrationFault ?? "调度门禁正被占用或守护身份缺失");
                    protectionReleased = registrationClosed;
                    if (registrationClosed)
                    {
                        controllerReleased = true;
                        RemoveController(this);
                    }
                    if (registrationClosed && cleanupTimer != null)
                    {
                        cleanupTimer.Dispose();
                        cleanupTimer = null;
                    }
                    else if (!registrationClosed)
                    {
                        ScheduleCleanupLocked();
                    }
                }
            }

            private void CloseControllerLocked()
            {
                if (wait != null)
                {
                    try
                    {
                        wait.Unregister(null);
                        wait = null;
                    }
                    catch
                    {
                    }
                }
                if (waitObject != null)
                {
                    try
                    {
                        waitObject.Dispose();
                        waitObject = null;
                    }
                    catch
                    {
                    }
                }
                if (Child != null)
                {
                    try
                    {
                        Child.Dispose();
                        Child = null;
                    }
                    catch
                    {
                        if (!Child.ClosureCertain)
                        {
                            closeFault = true;
                        }
                    }
                }
                try
                {
                    ReleaseSpawnHandles(true);
                }
                catch
                {
                    closeFault = true;
                }
                if (Parent != null)
                {
                    try
                    {
                        Parent.Dispose();
                        Parent = null;
                    }
                    catch
                    {
                        if (!Parent.ClosureCertain)
                        {
                            closeFault = true;
                        }
                    }
                }
                if (BrokerProof != null)
                {
                    try { BrokerProof.Dispose(); BrokerProof = null; }
                    catch { if (!BrokerProof.ClosureCertain) closeFault = true; }
                }
                if (Source != null)
                {
                    try
                    {
                        Source.Dispose();
                        Source = null;
                    }
                    catch
                    {
                    }
                }
                if (SessionDirectory != null)
                {
                    try
                    {
                        SessionDirectory.Dispose();
                        SessionDirectory = null;
                    }
                    catch
                    {
                        closeFault = true;
                    }
                }
                if (GuardsDirectory != null)
                {
                    try
                    {
                        GuardsDirectory.Dispose();
                        GuardsDirectory = null;
                    }
                    catch
                    {
                        closeFault = true;
                    }
                }
                if (Root != null)
                {
                    try
                    {
                        Root.Dispose();
                        Root = null;
                    }
                    catch
                    {
                        closeFault = true;
                    }
                }
            }

            private void TryCleanupLocked()
            {
                if (!childGone || OriginalScope == null || settledSource == null)
                {
                    cleanupBlocker = !childGone ? "守护子进程尚未退出" : OriginalScope == null ? "缺少原守护范围" : "尚未取得结清记录";
                    return;
                }
                try
                {
                    HardThreadFileStore store = new HardThreadFileStore(OriginalScope.Root);
                    HardThreadConstructionCleanup.AcquireAndRelease(delegate
                    {
                        CleanupFiles original = new CleanupFiles(this, store);
                        cleanupComplete = cleanupState.Step(original);
                        cleanupBlocker = cleanupComplete ? null : original.Blocker ?? "句柄关闭结果未确认";
                        if (original.Deferred != null) cleanupDeferred = original.Deferred;
                    }, delegate
                    {
                        HardThreadConstructionCleanup.CloseOwned(store);
                    });
                }
                catch (Exception error)
                {
                    cleanupBlocker = "守护文件清理异常：" + Brief(error);
                    RememberClosureFailure(error);
                }
            }

            // 调用方须持有 lifetime；结清返回 null，否则返回各清理阶段状态。
            private string PendingCleanupLocked()
            {
                if (cleanupComplete && protectionReleased && ControllerClosed && !closeFault) return null;
                return "文件清理=" + (cleanupComplete ? "完成" : "未完成（" + (cleanupBlocker ?? "原因未记录") + "）")
                    + (cleanupDeferred == null ? "" : "；被其他进程占用而留给后续清扫=" + cleanupDeferred)
                    + "；本方句柄=" + (ControllerClosed ? "已关闭" : "未全部关闭") + (closeFault ? "，存在关闭故障" : "")
                    + "；保护注销=" + (protectionReleased ? "完成" : "未完成（" + (protectionBlocker ?? "等待文件清理与句柄关闭") + "）");
            }

            public void NoteSettled(HardThreadSettledSource original)
            {
                lock (lifetime)
                {
                    if (OriginalScope == null || original == null || !OriginalScope.Target.Same(original.Target) || !string.Equals(OriginalScope.Root, Canonical(original.Root), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("settled source belongs to another original guardian");
                    }
                    settledSource = original;
                }
            }

            internal void Watch()
            {
                lock (lifetime)
                {
                    WatchLocked();
                }
            }

            private void WatchLocked()
            {
                if (wait != null || (Child == null && UnverifiedSpawn == IntPtr.Zero))
                {
                    return;
                }
                waitObject = new ActorWait((Child == null) ? UnverifiedSpawn : Child.Handle);
                wait = ThreadPool.RegisterWaitForSingleObject(waitObject, delegate
                {
                    Action action;
                    lock (lifetime)
                    {
                        failed = true;
                        action = (disposed ? null : lost);
                    }
                    if (action != null)
                    {
                        try
                        {
                            action();
                        }
                        catch
                        {
                        }
                    }
                    if (disposed)
                    {
                        try
                        {
                            Dispose();
                        }
                        catch
                        {
                        }
                    }
                }, null, -1, true);
            }

            public void OnLost(Action callback)
            {
                bool notifyLost;
                lock (lifetime)
                {
                    lost = callback;
                    notifyLost = failed && !disposed && callback != null;
                }
                if (!notifyLost)
                {
                    return;
                }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        callback();
                    }
                    catch
                    {
                    }
                });
            }

            public void RequireForward()
            {
                lock (lifetime)
                {
                    if (disposed || failed || settledSource != null || Child == null || Child.State != 0)
                    {
                        throw new InvalidOperationException("original guardian is not alive");
                    }
                }
            }

            public void Dispose()
            {
                string attemptLabel;
                bool cleanupRan;
                lock (lifetime)
                {
                    bool exited = childGone || ChildState == HardThreadRead.Gone;
                    if (exited && !cleanupExitPhase)
                    {
                        cleanupExitPhase = true;
                        cleanupAttempts = 0;
                    }
                    if (cleanupAttempts >= 3) return;
                    cleanupAttempts++;
                    // 每个阶段首次普通停止或清理不计为失败。
                    attemptLabel = "phase=" + (cleanupExitPhase ? "child-exit" : "live") + " 尝试=" + cleanupAttempts + "/3";
                    string cleanupLine = "独立守护清理：" + attemptLabel;
                    if (cleanupAttempts == 1) Logger.Info(cleanupLine); else Logger.Warn(cleanupLine);
                    disposed = true;
                    try
                    {
                        bool settled = false;
                        if (Scope != null && settledSource != null)
                        {
                            HardThreadFileStore store = new HardThreadFileStore(Scope.Root);
                            HardThreadConstructionCleanup.AcquireAndRelease(delegate
                            {
                                settled = store.MatchesSettled(settledSource, cleanupState.Removed(HardThreadCleanupPart.Ledger));
                            }, delegate
                            {
                                HardThreadConstructionCleanup.CloseOwned(store);
                            });
                        }
                        if (settled && !failed && Child != null && Child.State == HardThreadRead.Known && Pipe != null)
                        {
                            WritePacket(Pipe, Text("stop:" + Scope.Digest));
                        }
                    }
                    catch (Exception error)
                    {
                        RememberClosureFailure(error);
                    }
                    if (Pipe != null)
                    {
                        try
                        {
                            Pipe.Dispose();
                            Pipe = null;
                        }
                        catch
                        {
                        }
                    }
                    HardThreadRead childState = ChildState;
                    if (childGone || childState == HardThreadRead.Gone || (!BrokerSpawnPending && Child == null && UnverifiedSpawn == IntPtr.Zero))
                    {
                        childGone = true;
                        if (OriginalScope == null || (!DirectoryCreated && !SpawnSucceeded))
                        {
                            cleanupComplete = true;
                        }
                        else if (!cleanupComplete)
                        {
                            TryCleanupLocked();
                        }
                        if (cleanupComplete)
                        {
                            ReleaseLocked();
                        }
                        if (!cleanupComplete || !ControllerClosed)
                        {
                            ScheduleCleanupLocked();
                        }
                    }
                    else
                    {
                        WatchLocked();
                    }
                    cleanupRan = childGone;
                }
                FinishProtectionRelease();
                if (!cleanupRan) return;
                // 每次清理记录未完成阶段，日志数量随清理尝试次数封顶。
                string pending;
                lock (lifetime) pending = PendingCleanupLocked();
                if (pending != null) Logger.Warn("独立守护清理未完成：" + attemptLabel + "；" + pending);
            }

            private void ReleaseLocked()
            {
                CloseControllerLocked();
            }
        }

    }
}
