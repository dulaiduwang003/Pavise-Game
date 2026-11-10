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
                            if (!owner.childGone || !HardThreadCleanupPolicy.ChildSettled(owner.SpawnSucceeded, owner.ChildState) || owner.Parent == null || owner.Parent.State != 0 || !owner.Parent.Actor.Same(Original.Parent) || !owner.Parent.VerifyImage() || owner.Root == null || !owner.Root.Valid || owner.Root.Identity != Original.RootIdentity || owner.GuardsDirectory == null || !owner.GuardsDirectory.Valid)
                            {
                                return false;
                            }
                            RequireAcl(Original.Root, Original.Sid);
                            if (!store.MatchesSettled(owner.settledSource, owner.cleanupState.Removed(HardThreadCleanupPart.Ledger)))
                            {
                                return false;
                            }
                            foreach (HardThreadCleanupPart cleanupPart in Enum.GetValues(typeof(HardThreadCleanupPart)))
                            {
                                string path = PartPath(cleanupPart);
                                if (owner.cleanupState.Removed(cleanupPart))
                                {
                                    if (!Missing(path))
                                    {
                                        return false;
                                    }
                                }
                                else
                                {
                                    if (owner.cleanupState.Deferred(cleanupPart))
                                    {
                                        continue;
                                    }
                                    if (cleanupPart == HardThreadCleanupPart.Directory)
                                    {
                                        if (owner.SessionDirectory != null)
                                        {
                                            if (!owner.SessionDirectory.Valid)
                                            {
                                                return false;
                                            }
                                            owner.directoryIdentity = owner.SessionDirectory.Identity;
                                        }
                                        if (string.IsNullOrEmpty(owner.directoryIdentity))
                                        {
                                            return false;
                                        }
                                    }
                                    else if (!VerifyFile(path, ExpectedHash(cleanupPart)))
                                    {
                                        return false;
                                    }
                                }
                            }
                            return true;
                        }
                        catch (Exception error)
                        {
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
                            return Missing(path);
                        }
                        NoReparse(Path.GetDirectoryName(path));
                        IntPtr handle = CreateFile(path, 2147549184u, 5u, IntPtr.Zero, 3u, (part == HardThreadCleanupPart.Directory) ? 35651584u : 2097152u, IntPtr.Zero);
                        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                        {
                            openErrors[(int)part] = Marshal.GetLastWin32Error();
                            return false;
                        }
                        bool marked = false;
                        HardThreadConstructionCleanup.AcquireAndRelease(delegate
                        {
                            FileIdentity info;
                            if (GetFileInformationByHandle(handle, out info) && (info.Attributes & 0x400) == 0)
                            {
                                if (part == HardThreadCleanupPart.Directory)
                                {
                                    if ((info.Attributes & 0x10) == 0 || FileKey(info) != owner.directoryIdentity)
                                    {
                                        return;
                                    }
                                }
                                else
                                {
                                    if ((info.Attributes & 0x10u) != 0)
                                    {
                                        return;
                                    }
                                    using (SafeFileHandle handle2 = new SafeFileHandle(handle, false))
                                    {
                                        using (FileStream fileStream = new FileStream(handle2, FileAccess.Read))
                                        {
                                            if (fileStream.Length > 67108864 || Hash(fileStream) != expected)
                                            {
                                                return;
                                            }
                                        }
                                    }
                                }
                                DeleteDisposition deleteDisposition = default(DeleteDisposition);
                                deleteDisposition.Delete = 1;
                                DeleteDisposition information = deleteDisposition;
                                marked = SetFileInformationByHandle(handle, 4, ref information, 1u);
                            }
                        }, delegate
                        {
                            bool uncertain = false;
                            HardThreadRawHandleClosure.Close(ref handle, ref uncertain, CloseHandle);
                        });
                        return marked;
                    }
                    catch (Exception error)
                    {
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
                    return true;
                }
            }

            internal readonly string ControlledRoot;

            internal Scope Setup;

            internal bool SpawnSucceeded;

            internal bool DirectoryCreated;

            internal volatile bool controllerReleased;

            internal ActorPin Parent;

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
                    if (Child == null && Parent == null && Root == null && Source == null && Pipe == null && SessionDirectory == null && GuardsDirectory == null && wait == null && waitObject == null && UnverifiedSpawn == IntPtr.Zero)
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
                bool registrationClosed = unregister == null;
                if (unregister != null)
                {
                    try
                    {
                        registrationClosed = unregister(arg);
                    }
                    catch
                    {
                    }
                }
                lock (lifetime)
                {
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
                    return;
                }
                try
                {
                    HardThreadFileStore store = new HardThreadFileStore(OriginalScope.Root);
                    HardThreadConstructionCleanup.AcquireAndRelease(delegate
                    {
                        CleanupFiles original = new CleanupFiles(this, store);
                        cleanupComplete = cleanupState.Step(original);
                    }, delegate
                    {
                        HardThreadConstructionCleanup.CloseOwned(store);
                    });
                }
                catch (Exception error)
                {
                    RememberClosureFailure(error);
                }
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
                    if (disposed || failed || Child == null || Child.State != 0)
                    {
                        throw new InvalidOperationException("original guardian is not alive");
                    }
                }
            }

            public void Dispose()
            {
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
                    string cleanupLine = "Critical guardian cleanup: phase=" + (cleanupExitPhase ? "child-exit" : "live") + " 尝试=" + cleanupAttempts + "/3";
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
                    if (childGone || childState == HardThreadRead.Gone || (Child == null && UnverifiedSpawn == IntPtr.Zero))
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
                }
                FinishProtectionRelease();
            }

            private void ReleaseLocked()
            {
                CloseControllerLocked();
            }
        }

    }
}
