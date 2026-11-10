using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal sealed partial class CriticalThreadHardAffinitySession
    {
        private sealed class RecoveryGate : IHardThreadGate
        {
            private readonly CriticalThreadGuardian.StopProof original;

            public HardThreadTarget Target
            {
                get
                {
                    return original.Target;
                }
            }

            public bool AllowWorkers
            {
                get
                {
                    return false;
                }
            }

            internal RecoveryGate(CriticalThreadGuardian.StopProof original)
            {
                if (original == null)
                {
                    throw new ArgumentNullException("original");
                }
                this.original = original;
                original.RequireStopped();
            }

            public IDisposable EnterUse()
            {
                return original.EnterUse();
            }

            public void RequireActive()
            {
                throw new InvalidOperationException("original guardian recovery cannot authorize forward writes");
            }

            public void RequireRecovery()
            {
                original.RequireStopped();
            }
        }

        private sealed class NativeGate : IHardThreadGate
        {
            private readonly GameMode.CriticalThreadTargetLease original;

            private readonly HardThreadTarget target;

            public HardThreadTarget Target
            {
                get
                {
                    return target;
                }
            }

            public bool AllowWorkers
            {
                get
                {
                    return original.WorkerAvoidanceAllowed;
                }
            }

            internal NativeGate(GameMode.CriticalThreadTargetLease lease)
            {
                if (lease == null)
                {
                    throw new ArgumentNullException("original");
                }
                original = lease;
                target = new HardThreadTarget(lease.SessionId, lease.ProcessId, lease.ProcessCreation, lease.ExecutablePath, lease.AllowedMask);
            }

            public IDisposable EnterUse()
            {
                return original.EnterUse();
            }

            public void RequireActive()
            {
                original.RequireActive();
            }

            public void RequireRecovery()
            {
                original.RequireRecovery();
            }
        }

        private sealed class CriticalThreadHardAffinityNative : IHardThreadNative, IDisposable, IHardThreadTransientReads, IHardThreadCandidateReads, IHardThreadPlacementReads, IHardThreadPlacementBatchReads
        {
            private struct GroupAffinity
            {
                internal UIntPtr Mask;

                internal ushort Group;

                internal ushort Reserved0;

                internal ushort Reserved1;

                internal ushort Reserved2;
            }

            private readonly HardThreadTarget target;

            private IntPtr process;

            private PlacementBatch placementBatch;

            private sealed class PlacementBatch : IHardThreadPlacementBatch
            {
                private readonly CriticalThreadHardAffinityNative owner;
                private readonly IntPtr originalProcess;
                private readonly int ownerThread;
                private volatile bool closed;

                internal PlacementBatch(CriticalThreadHardAffinityNative owner)
                {
                    this.owner = owner;
                    originalProcess = owner.process;
                    ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                }

                private bool Current
                {
                    get { return !closed && ownerThread == System.Threading.Thread.CurrentThread.ManagedThreadId
                        && originalProcess != IntPtr.Zero && owner.process == originalProcess
                        && object.ReferenceEquals(System.Threading.Volatile.Read(ref owner.placementBatch), this); }
                }

                internal HardThreadRead Alive()
                {
                    if (!Current) return HardThreadRead.Unknown;
                    uint result = WaitForSingleObject(originalProcess, 0u);
                    return result == 258u ? HardThreadRead.Known : result == 0u ? HardThreadRead.Gone : HardThreadRead.Unknown;
                }

                internal void RequireIdentity()
                {
                    if (!Current || owner.ProcessIdentity() != HardThreadRead.Known)
                        throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original process identity unavailable during placement audit");
                }

                public HardThreadObserved ReadPlacement(HardThreadIdentity thread)
                {
                    return owner.ReadThreadCore(thread, false, this);
                }

                public void Complete()
                {
                    try { RequireIdentity(); }
                    finally { Dispose(); }
                }

                public void Dispose()
                {
                    closed = true;
                    System.Threading.Interlocked.CompareExchange(ref owner.placementBatch, null, this);
                }
            }

            public IHardThreadPlacementBatch BeginPlacementBatch()
            {
                var batch = new PlacementBatch(this);
                if (System.Threading.Interlocked.CompareExchange(ref placementBatch, batch, null) != null)
                    throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "placement audit already active");
                try { batch.RequireIdentity(); return batch; }
                catch { batch.Dispose(); throw; }
            }

            private bool closeFault;

            private readonly StringBuilder processPath = new StringBuilder(32768);

            private static readonly HardThreadOptionalEntry threadCpuSetMasks = new HardThreadOptionalEntry();

            private static readonly HardThreadOptionalEntry processCpuSetMasks = new HardThreadOptionalEntry();

            private readonly Dictionary<string, IntPtr> threads = new Dictionary<string, IntPtr>(StringComparer.Ordinal);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr OpenThread(uint access, bool inherit, int tid);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint GetProcessIdOfThread(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetProcessTimes(IntPtr handle, out long born, out long exited, out long kernel, out long user);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetThreadTimes(IntPtr handle, out long born, out long exited, out long kernel, out long user);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref int capacity);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetProcessAffinityMask(IntPtr handle, out UIntPtr processMask, out UIntPtr systemMask);

            [DllImport("kernel32.dll")]
            private static extern ushort GetActiveProcessorGroupCount();

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern int GetThreadPriority(IntPtr thread);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint GetPriorityClass(IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetThreadSelectedCpuSets(IntPtr thread, [Out] uint[] ids, uint capacity, out uint required);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetProcessDefaultCpuSets(IntPtr process, [Out] uint[] ids, uint capacity, out uint required);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetThreadSelectedCpuSetMasks(IntPtr thread, [Out] GroupAffinity[] masks, ushort capacity, out ushort required);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetProcessDefaultCpuSetMasks(IntPtr process, [Out] GroupAffinity[] masks, ushort capacity, out ushort required);

            private CriticalThreadHardAffinityNative(HardThreadTarget original, IntPtr retained)
            {
                if (IntPtr.Size != 8)
                {
                    throw new NotSupportedException("x64 hard affinity adapter required");
                }
                target = original;
                process = ((retained != IntPtr.Zero) ? retained : OpenProcess(1053696u, false, target.ProcessId));
                if (process == IntPtr.Zero)
                {
                    throw new InvalidOperationException("original process query denied: " + Marshal.GetLastWin32Error());
                }
                try
                {
                    if (ProcessIdentity() == HardThreadRead.Unknown)
                    {
                        throw new InvalidOperationException("original process identity mismatch");
                    }
                }
                catch
                {
                    CloseHandle(process);
                    process = IntPtr.Zero;
                    throw;
                }
            }

            internal static CriticalThreadHardAffinityNative Open(HardThreadTarget original)
            {
                return new CriticalThreadHardAffinityNative(original, IntPtr.Zero);
            }

            internal static CriticalThreadHardAffinityNative Open(CriticalThreadGuardian.StopProof original)
            {
                return new CriticalThreadHardAffinityNative(original.Target, original.DuplicateTarget());
            }

            private HardThreadRead ProcessIdentity()
            {
                if (process == IntPtr.Zero)
                {
                    return HardThreadRead.Unknown;
                }
                switch (WaitForSingleObject(process, 0u))
                {
                case 0u:
                    return HardThreadRead.Gone;
                default:
                    return HardThreadRead.Unknown;
                case 258u:
                {
                    processPath.Length = 0;
                    int capacity = processPath.Capacity;
                    long born;
                    long exited;
                    long kernel;
                    long user;
                    if (!GetProcessTimes(process, out born, out exited, out kernel, out user) || born != target.ProcessCreation || !QueryFullProcessImageName(process, 0u, processPath, ref capacity) || !string.Equals(Path.GetFullPath(processPath.ToString()), target.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return HardThreadRead.Unknown;
                    }
                    return HardThreadRead.Known;
                }
                }
            }

            public HardThreadMachine ReadMachine()
            {
                HardThreadMachine machine = ReadProcessScope();
                if (machine.Read != HardThreadRead.Known || machine.GroupCount != 1) return machine;
                try { machine.PhysicalCores = PhysicalCores(); }
                catch (Exception error) { machine.Read = HardThreadRead.Unknown; machine.Detail = error.GetType().Name; }
                return machine;
            }

            public HardThreadMachine ReadProcessScope()
            {
                HardThreadMachine currentHardThreadMachine = new HardThreadMachine();
                currentHardThreadMachine.Read = HardThreadRead.Unknown;
                currentHardThreadMachine.Detail = "native process or topology unavailable";
                currentHardThreadMachine.Read = ProcessIdentity();
                if (currentHardThreadMachine.Read != 0)
                {
                    return currentHardThreadMachine;
                }
                try
                {
                    currentHardThreadMachine.GroupCount = GetActiveProcessorGroupCount();
                    if (currentHardThreadMachine.GroupCount != 1)
                    {
                        currentHardThreadMachine.Detail = "multiple processor groups unsupported";
                        return currentHardThreadMachine;
                    }
                    UIntPtr processMask;
                    UIntPtr systemMask;
                    if (!GetProcessAffinityMask(process, out processMask, out systemMask) || processMask == UIntPtr.Zero)
                    {
                        currentHardThreadMachine.Read = HardThreadRead.Unknown;
                        return currentHardThreadMachine;
                    }
                    currentHardThreadMachine.ProcessMask = processMask.ToUInt64();
                    currentHardThreadMachine.Group = 0;
                    bool empty;
                    bool empty2;
                    if (!CpuSetIdsEmpty(process, false, out empty) || !CpuSetMasksEmpty(process, false, out empty2))
                    {
                        currentHardThreadMachine.Read = HardThreadRead.Unknown;
                        return currentHardThreadMachine;
                    }
                    currentHardThreadMachine.CpuSetsEmpty = empty && empty2;
                    try
                    {
                        uint priority = GetPriorityClass(process);
                        if (priority != 0) { currentHardThreadMachine.ProcessPriorityClass = priority; currentHardThreadMachine.ProcessPriorityClassKnown = true; }
                    }
                    catch { }
                    return currentHardThreadMachine;
                }
                catch (Exception ex)
                {
                    currentHardThreadMachine.Read = HardThreadRead.Unknown;
                    currentHardThreadMachine.Detail = ex.GetType().Name;
                    return currentHardThreadMachine;
                }
            }

            private static ulong[] PhysicalCores()
            {
                uint length = 0u;
                if (GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length) || Marshal.GetLastWin32Error() != 122 || length < 48 || length > 1048576)
                {
                    throw new InvalidDataException("bounded native physical topology required");
                }
                IntPtr nativeBuffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    uint length2 = length;
                    if (!GetLogicalProcessorInformationEx(0, nativeBuffer, ref length2) || length2 > length)
                    {
                        throw new InvalidDataException("physical topology changed");
                    }
                    int i = 0;
                    List<ulong> physicalCoreMasks = new List<ulong>();
                    int recordSize;
                    for (; i < length2; i += recordSize)
                    {
                        if (length2 - i < 8)
                        {
                            throw new InvalidDataException("truncated physical record");
                        }
                        IntPtr recordPointer = IntPtr.Add(nativeBuffer, i);
                        int relationship = Marshal.ReadInt32(recordPointer);
                        recordSize = Marshal.ReadInt32(recordPointer, 4);
                        if (relationship != 0 || recordSize < 48 || recordSize > length2 - i || Marshal.ReadInt16(recordPointer, 30) != 1)
                        {
                            throw new InvalidDataException("unsupported physical record");
                        }
                        GroupAffinity groupAffinity = (GroupAffinity)Marshal.PtrToStructure(IntPtr.Add(recordPointer, 32), typeof(GroupAffinity));
                        if (groupAffinity.Group != 0 || groupAffinity.Mask == UIntPtr.Zero)
                        {
                            throw new InvalidDataException("unsupported physical group");
                        }
                        physicalCoreMasks.Add(groupAffinity.Mask.ToUInt64());
                    }
                    if (i != length2 || physicalCoreMasks.Count == 0)
                    {
                        throw new InvalidDataException("empty physical topology");
                    }
                    return physicalCoreMasks.ToArray();
                }
                finally
                {
                    Marshal.FreeHGlobal(nativeBuffer);
                }
            }

            private static bool CpuSetIdsEmpty(IntPtr handle, bool thread, out bool empty)
            {
                empty = false;
                uint required;
                bool threadSelectedCpuSets = (thread ? GetThreadSelectedCpuSets(handle, null, 0u, out required) : GetProcessDefaultCpuSets(handle, null, 0u, out required));
                if ((!threadSelectedCpuSets && Marshal.GetLastWin32Error() != 122) || required > 4096)
                {
                    return false;
                }
                if (required == 0)
                {
                    empty = threadSelectedCpuSets;
                    return threadSelectedCpuSets;
                }
                uint[] ids = new uint[required];
                uint required2;
                if (!(thread ? GetThreadSelectedCpuSets(handle, ids, required, out required2) : GetProcessDefaultCpuSets(handle, ids, required, out required2)) || required2 > required)
                {
                    return false;
                }
                empty = required2 == 0;
                return true;
            }

            private static bool CpuSetMasksEmpty(IntPtr handle, bool thread, out bool empty)
            {
                bool observedEmpty = false, unsupported;
                HardThreadOptionalEntry entry = thread ? threadCpuSetMasks : processCpuSetMasks;
                bool read = entry.TryInvoke(delegate { return ReadCpuSetMasks(handle, thread, out observedEmpty); }, out unsupported);
                empty = unsupported || observedEmpty;
                return read;
            }

            private static bool ReadCpuSetMasks(IntPtr handle, bool thread, out bool empty)
            {
                empty = false;
                    ushort required;
                    bool threadSelectedCpuSetMasks = (thread ? GetThreadSelectedCpuSetMasks(handle, null, 0, out required) : GetProcessDefaultCpuSetMasks(handle, null, 0, out required));
                    if ((!threadSelectedCpuSetMasks && Marshal.GetLastWin32Error() != 122) || required > 64)
                    {
                        return false;
                    }
                    if (required == 0)
                    {
                        empty = threadSelectedCpuSetMasks;
                        return threadSelectedCpuSetMasks;
                    }
                    GroupAffinity[] masks = new GroupAffinity[required];
                    ushort required2;
                    if (!(thread ? GetThreadSelectedCpuSetMasks(handle, masks, required, out required2) : GetProcessDefaultCpuSetMasks(handle, masks, required, out required2)) || required2 > required)
                    {
                        return false;
                    }
                    empty = required2 == 0;
                    return true;
            }

            private IntPtr OriginalThread(HardThreadIdentity thread)
            {
                IntPtr threadHandle;
                if (threads.TryGetValue(thread.Key, out threadHandle))
                {
                    return threadHandle;
                }
                threadHandle = OpenThread(1050656u, false, thread.Tid);
                if (threadHandle == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
                long born;
                long exited;
                long kernel;
                long user;
                if (GetProcessIdOfThread(threadHandle) != target.ProcessId || !GetThreadTimes(threadHandle, out born, out exited, out kernel, out user) || born != thread.Creation)
                {
                    CloseHandle(threadHandle);
                    return IntPtr.Zero;
                }
                threads.Add(thread.Key, threadHandle);
                return threadHandle;
            }

            public HardThreadObserved ReadThread(HardThreadIdentity thread)
            {
                return ReadThreadCore(thread, true);
            }

            public HardThreadObserved ReadPlacement(HardThreadIdentity thread)
            {
                return ReadThreadCore(thread, false);
            }

            private HardThreadObserved ReadThreadCore(HardThreadIdentity thread, bool includePriority)
            {
                return ReadThreadCore(thread, includePriority, null);
            }

            private HardThreadObserved ReadThreadCore(HardThreadIdentity thread, bool includePriority, PlacementBatch batch)
            {
                HardThreadObserved currentHardThreadObserved = new HardThreadObserved();
                currentHardThreadObserved.Read = HardThreadRead.Unknown;
                currentHardThreadObserved.Detail = "original thread identity unknown";
                if ((batch == null ? ProcessIdentity() : batch.Alive()) != HardThreadRead.Known)
                {
                    return currentHardThreadObserved;
                }
                IntPtr originalThreadHandle = OriginalThread(thread);
                if (originalThreadHandle == IntPtr.Zero)
                {
                    return currentHardThreadObserved;
                }
                switch (WaitForSingleObject(originalThreadHandle, 0u))
                {
                case 0u:
                    currentHardThreadObserved.Read = HardThreadRead.Gone;
                    return currentHardThreadObserved;
                default:
                    return currentHardThreadObserved;
                case 258u:
                    try
                    {
                        GroupAffinity affinity;
                        bool empty;
                        bool empty2;
                        if (!GetThreadGroupAffinity(originalThreadHandle, out affinity) || !CpuSetIdsEmpty(originalThreadHandle, true, out empty) || !CpuSetMasksEmpty(originalThreadHandle, true, out empty2))
                        {
                            return currentHardThreadObserved;
                        }
                        currentHardThreadObserved.Read = HardThreadRead.Known;
                        currentHardThreadObserved.Group = affinity.Group;
                        currentHardThreadObserved.Mask = affinity.Mask.ToUInt64();
                        currentHardThreadObserved.CpuSetsEmpty = empty && empty2;
                        if (includePriority) try
                        {
                            int priority = GetThreadPriority(originalThreadHandle);
                            if (priority != int.MaxValue) { currentHardThreadObserved.ThreadPriorityLevel = priority; currentHardThreadObserved.ThreadPriorityKnown = true; }
                        }
                        catch { }
                        return currentHardThreadObserved;
                    }
                    catch (Exception ex)
                    {
                        currentHardThreadObserved.Detail = ex.GetType().Name;
                        return currentHardThreadObserved;
                    }
                }
            }

            public bool Set(HardThreadIdentity thread, ushort group, ulong mask, out ushort previousGroup, out ulong previousMask)
            {
                previousGroup = 0;
                previousMask = 0uL;
                if (ProcessIdentity() != 0 || group != 0 || mask == 0 || GetActiveProcessorGroupCount() != 1)
                {
                    return false;
                }
                IntPtr nativePointer;
                if (!threads.TryGetValue(thread.Key, out nativePointer) || WaitForSingleObject(nativePointer, 0u) != 258)
                {
                    return false;
                }
                GroupAffinity affinity = default(GroupAffinity);
                affinity.Mask = new UIntPtr(mask);
                affinity.Group = group;
                GroupAffinity previous;
                if (!SetThreadGroupAffinity(nativePointer, ref affinity, out previous))
                {
                    return false;
                }
                previousGroup = previous.Group;
                previousMask = previous.Mask.ToUInt64();
                return true;
            }

            public bool ReleaseUnowned(string[] retainedIdentities)
            {
                HashSet<string> retainedIdentitiesSnapshot = new HashSet<string>(retainedIdentities, StringComparer.Ordinal);
                List<string> keys = new List<string>();
                bool result = true;
                foreach (string key in threads.Keys)
                {
                    if (!retainedIdentitiesSnapshot.Contains(key))
                    {
                        keys.Add(key);
                    }
                }
                foreach (string currentKey in keys)
                {
                    IntPtr handle = threads[currentKey];
                    threads.Remove(currentKey);
                    if (!CloseHandle(handle))
                    {
                        result = false;
                        closeFault = true;
                    }
                }
                return result;
            }

            public void Dispose()
            {
                bool releaseFailed = false;
                foreach (IntPtr nativePointer in threads.Values)
                {
                    if (!CloseHandle(nativePointer))
                    {
                        releaseFailed = true;
                    }
                }
                threads.Clear();
                if (process != IntPtr.Zero)
                {
                    if (!CloseHandle(process))
                    {
                        releaseFailed = true;
                    }
                    process = IntPtr.Zero;
                }
                closeFault |= releaseFailed;
                if (closeFault)
                {
                    throw new InvalidOperationException("some retained hard-affinity handles did not close");
                }
            }
        }

    }
}
