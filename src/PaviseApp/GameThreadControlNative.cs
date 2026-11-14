using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal sealed class GameThreadControlNative : IGameThreadControlNative, IGameThreadPriorityReader, IHardThreadTransientReads
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Processor { internal ushort Group; internal byte Number, Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity { internal UIntPtr Mask; internal ushort Group, R0, R1, R2; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadIdealProcessorEx(IntPtr thread, out Processor processor);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadIdealProcessorEx(IntPtr thread, ref Processor processor, out Processor previous);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadSelectedCpuSets(IntPtr thread, IntPtr ids, uint count, out uint needed);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessDefaultCpuSets(IntPtr process, IntPtr ids, uint count, out uint needed);
        [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint count);

        private readonly HardThreadTarget target;
        private readonly bool readOnly;
        private readonly bool timeCriticalAllowed;
        private readonly Func<IntPtr, bool> closeThread;
        private readonly object imagePathGate = new object();
        private readonly StringBuilder imagePathBuffer = new StringBuilder(32768);
        private IntPtr process;
        private readonly Dictionary<string, IntPtr> threads = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        private bool disposed;
        private bool closeFault;

        internal GameThreadControlNative(HardThreadTarget target, bool readOnly = false, bool timeCriticalAllowed = false, Func<IntPtr, bool> closeThread = null)
        {
            this.target = target;
            this.readOnly = readOnly;
            this.timeCriticalAllowed = timeCriticalAllowed && target.AllowedMask != 0;
            this.closeThread = closeThread ?? Native.CloseHandle;
            process = Native.OpenProcess(0x101000, false, target.ProcessId);
            if (process == IntPtr.Zero) throw new IOException("统一线程控制无法保留目标进程句柄；错误码=" + Marshal.GetLastWin32Error());
            if (ProcessState() != HardThreadRead.Known)
            {
                Dispose();
                throw new IOException("统一线程控制的进程身份未确认");
            }
        }

        public bool TargetExited { get { return process != IntPtr.Zero && WaitForSingleObject(process, 0) == 0; } }

        private HardThreadRead ProcessState()
        {
            if (disposed || process == IntPtr.Zero) return HardThreadRead.Unknown;
            int state = Native.QueryProcessIdentityState(process, target.ProcessCreation);
            if (state > 0) return HardThreadRead.Gone;
            if (state < 0) return HardThreadRead.Unknown;
            // 每字段核验仍复读进程路径，但复用缓冲，避免控制周期反复分配大字符数组干扰帧尾延迟。
            lock (imagePathGate)
            {
                imagePathBuffer.Length = 0;
                uint count = (uint)imagePathBuffer.Capacity;
                if (!QueryFullProcessImageName(process, 0, imagePathBuffer, ref count)) return HardThreadRead.Unknown;
                return string.Equals(Path.GetFullPath(imagePathBuffer.ToString()), target.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                    ? HardThreadRead.Known : HardThreadRead.Gone;
            }
        }

        public GameThreadControlProcess ReadProcess()
        {
            HardThreadRead state = ProcessState();
            var result = new GameThreadControlProcess { Read = state };
            if (state != HardThreadRead.Known) return result;
            result.PriorityClass = Native.GetPriorityClass(process);
            try { result.MultipleGroups = GetActiveProcessorGroupCount() != 1; }
            catch (EntryPointNotFoundException) { result.MultipleGroups = true; }
            if (result.PriorityClass == 0) result.Read = HardThreadRead.Unknown;
            return result;
        }

        private IntPtr Thread(HardThreadIdentity identity, out HardThreadRead state)
        {
            state = ProcessState();
            if (state != HardThreadRead.Known) return IntPtr.Zero;
            IntPtr handle;
            if (!threads.TryGetValue(identity.Key, out handle))
            {
                if (closeFault || threads.Count >= 512) { state = HardThreadRead.Unknown; return IntPtr.Zero; }
                // 原始对照只读取基线，不申请或使用线程设置权限。
                handle = Native.OpenThread(readOnly ? 0x0800 : 0x0820, false, identity.Tid);
                if (handle == IntPtr.Zero)
                {
                    state = Marshal.GetLastWin32Error() == 87 ? HardThreadRead.Gone : HardThreadRead.Unknown;
                    return IntPtr.Zero;
                }
                threads.Add(identity.Key, handle);
            }
            int owner = Native.QueryThreadOwnerPid(handle);
            if (owner < 0) { state = HardThreadRead.Unknown; return IntPtr.Zero; }
            if (owner != target.ProcessId) { state = HardThreadRead.Gone; RequireThreadReleased(identity.Key); return IntPtr.Zero; }
            long born, exited, kernel, user;
            if (!Native.GetThreadTimes(handle, out born, out exited, out kernel, out user))
            { state = HardThreadRead.Unknown; return IntPtr.Zero; }
            if (born != identity.Creation || exited != 0) { state = HardThreadRead.Gone; RequireThreadReleased(identity.Key); return IntPtr.Zero; }
            state = HardThreadRead.Known;
            return handle;
        }

        private bool Placement(IntPtr thread, out GroupAffinity affinity)
        {
            affinity = new GroupAffinity();
            if (target.AllowedMask == 0) return false;
            try
            {
                uint count, processCount; ulong processMask;
                if (GetActiveProcessorGroupCount() != 1 || !GetThreadGroupAffinity(thread, out affinity) || affinity.Group != 0
                    || !Native.TryQueryAffinity(process, out processMask) || processMask == 0
                    || !GetThreadSelectedCpuSets(thread, IntPtr.Zero, 0, out count) || count != 0
                    || !GetProcessDefaultCpuSets(process, IntPtr.Zero, 0, out processCount) || processCount != 0) return false;
                affinity.Mask = new UIntPtr(affinity.Mask.ToUInt64() & processMask & target.AllowedMask);
                return affinity.Mask != UIntPtr.Zero;
            }
            catch (EntryPointNotFoundException) { return false; }
        }

        public GameThreadControlObserved ReadPriority(HardThreadIdentity identity)
        {
            try { return ReadPriorityCore(identity); }
            finally { if (readOnly) RequireThreadReleased(identity.Key); }
        }

        private GameThreadControlObserved ReadPriorityCore(HardThreadIdentity identity)
        {
            HardThreadRead state;
            IntPtr handle = Thread(identity, out state);
            var result = new GameThreadControlObserved { Read = state };
            if (handle == IntPtr.Zero) return result;
            result.Priority = Native.GetThreadPriority(handle);
            result.PriorityKnown = result.Priority != int.MaxValue;
            return result;
        }

        public GameThreadControlObserved Read(HardThreadIdentity identity)
        {
            try { return ReadCore(identity); }
            finally { if (readOnly) RequireThreadReleased(identity.Key); }
        }

        private GameThreadControlObserved ReadCore(HardThreadIdentity identity)
        {
            HardThreadRead state; IntPtr handle = Thread(identity, out state);
            var result = new GameThreadControlObserved { Read = state };
            if (handle == IntPtr.Zero) return result;
            result.Priority = Native.GetThreadPriority(handle);
            result.PriorityKnown = result.Priority != int.MaxValue;
            Processor ideal;
            result.IdealKnown = GetThreadIdealProcessorEx(handle, out ideal);
            if (result.IdealKnown) result.Ideal = new GameThreadControlProcessor(ideal.Group, ideal.Number);
            GroupAffinity affinity;
            result.PlacementKnown = Placement(handle, out affinity);
            if (result.PlacementKnown) { result.Group = affinity.Group; result.Allowed = affinity.Mask.ToUInt64(); }
            return result;
        }

        public bool SetPriority(HardThreadIdentity identity, int value)
        {
            if (readOnly) return false;
            if ((value < -2 || value > 2) && !(value == 15 && timeCriticalAllowed)) return false;
            HardThreadRead state; IntPtr thread = Thread(identity, out state);
            if (thread != IntPtr.Zero && value == 15 && !TimeCriticalPlacement(thread)) return false;
            return thread != IntPtr.Zero && Native.SetThreadPriority(thread, value);
        }

        private bool TimeCriticalPlacement(IntPtr thread)
        {
            try
            {
                GroupAffinity affinity; ulong processMask; uint threadSets, processSets;
                int desired;
                uint processClass = Native.GetPriorityClass(process);
                if (!GameThreadControlPolicy.PriorityTarget(processClass, 0, 15, out desired) || desired != 15
                    || GetActiveProcessorGroupCount() != 1 || !GetThreadGroupAffinity(thread, out affinity) || affinity.Group != 0
                    || !Native.TryQueryAffinity(process, out processMask) || processMask == 0
                    || !GetThreadSelectedCpuSets(thread, IntPtr.Zero, 0, out threadSets) || threadSets != 0
                    || !GetProcessDefaultCpuSets(process, IntPtr.Zero, 0, out processSets) || processSets != 0) return false;
                ulong mask = affinity.Mask.ToUInt64();
                // 检查原始线程掩码，不能先求交集把宽范围误认成已固定单核。
                return mask != 0 && (mask & (mask - 1)) == 0 && (mask & ~target.AllowedMask) == 0 && (mask & ~processMask) == 0
                    && ControllerCanRunOutside(mask);
            }
            catch (EntryPointNotFoundException) { return false; }
        }

        internal static bool ControllerCanRunOutside(ulong targetMask)
        {
            if (targetMask == 0 || (targetMask & (targetMask - 1)) != 0) return false;
            try
            {
                IntPtr selfProcess = new IntPtr(-1), controlThread = new IntPtr(-2);
                GroupAffinity affinity; ulong processMask; uint threadSets, processSets;
                if (GetActiveProcessorGroupCount() != 1 || !GetThreadGroupAffinity(controlThread, out affinity) || affinity.Group != 0
                    || !Native.TryQueryAffinity(selfProcess, out processMask) || processMask == 0
                    || !GetThreadSelectedCpuSets(controlThread, IntPtr.Zero, 0, out threadSets) || threadSets != 0
                    || !GetProcessDefaultCpuSets(selfProcess, IntPtr.Zero, 0, out processSets) || processSets != 0) return false;
                // 自身让路只降低优先级，不保证控制线程还能跑在目标核之外；同时核对线程与进程范围。
                // 这是当前调用线程的准入条件，不能据此承诺外部改绑后仍无饥饿或守护绝不会延迟。
                return (affinity.Mask.ToUInt64() & processMask & ~targetMask) != 0;
            }
            catch (EntryPointNotFoundException) { return false; }
        }

        public bool SetIdeal(HardThreadIdentity identity, GameThreadControlProcessor value, out GameThreadControlProcessor previous)
        {
            previous = new GameThreadControlProcessor();
            if (readOnly || target.AllowedMask == 0) return false;
            HardThreadRead state; IntPtr thread = Thread(identity, out state);
            if (thread == IntPtr.Zero) return false;
            // 正常写入必须属于线程、进程及既有 Job 读回范围；恢复旧值可以是系统的无优先核哨兵。
            GroupAffinity affinity;
            if (!Placement(thread, out affinity) || value.Group != affinity.Group
                || value.Number < 64 && (affinity.Mask.ToUInt64() & (1UL << value.Number)) == 0) return false;
            Processor request = new Processor { Group = value.Group, Number = value.Number }, actual;
            if (!SetThreadIdealProcessorEx(thread, ref request, out actual)) return false;
            previous = new GameThreadControlProcessor(actual.Group, actual.Number);
            return true;
        }

        private void RequireThreadReleased(string identity)
        {
            if (!ReleaseThread(identity)) throw new HardThreadClosureFailure("线程观察句柄关闭未确认，停止本次属性处理");
        }

        private bool ReleaseThread(string identity)
        {
            IntPtr handle;
            if (!threads.TryGetValue(identity, out handle)) return !closeFault;
            // 关闭结果未知时保留故障，不能再用可能被系统复用的句柄值重试关闭。
            threads.Remove(identity);
            try { if (!closeThread(handle)) closeFault = true; }
            catch { closeFault = true; }
            return !closeFault;
        }

        public bool ReleaseUnowned(string[] retainedIdentities)
        {
            var retained = new HashSet<string>(retainedIdentities, StringComparer.Ordinal);
            var released = new List<string>();
            foreach (string key in threads.Keys) if (!retained.Contains(key)) released.Add(key);
            foreach (string key in released) ReleaseThread(key);
            return !closeFault;
        }

        public void Dispose()
        {
            disposed = true;
            ReleaseUnowned(new string[0]);
            IntPtr handle = process;
            process = IntPtr.Zero;
            if (handle != IntPtr.Zero)
                try { if (!Native.CloseHandle(handle)) closeFault = true; }
                catch { closeFault = true; }
            if (closeFault) throw new IOException("统一线程控制句柄关闭未确认；保留故障与恢复归属");
        }
    }
}
