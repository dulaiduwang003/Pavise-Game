using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 进程亲和性会改变已有线程掩码 因此拒绝游戏自行设置的线程掩码或 CPU Sets 这项有界准入检查不承诺全生命周期的线程状态恢复

    internal static class GameThreadAffinityAdmission
    {
        internal const string RefusalPrefix = "game-thread-affinity-";
        private const int MaxSnapshots = 3, MaxThreads = 4096, MaxSnapshotEntries = 32768;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadEntry
        {
            internal uint Size, Usage, Tid, OwnerPid;
            internal int BasePriority, DeltaPriority;
            internal uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity { internal UIntPtr Mask; internal ushort Group, R0, R1, R2; }
        private sealed class HeldThread
        {
            internal uint Tid;
            internal long Birth;
            internal IntPtr Handle;
        }
        private enum ThreadRead { Known, Gone, Unconfirmed }
        // 线程自设范围的原值 只给反作弊引擎的 SGuard64 用 进程范围写入会把它覆盖成进程范围 恢复进程范围后按这里写回
        internal struct ThreadMaskPin
        {
            internal uint Tid;
            internal long Birth;
            internal ulong Mask;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadTimes(IntPtr thread, out long birth, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadSelectedCpuSets(IntPtr thread, IntPtr ids, uint capacity, out uint required);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessIdOfThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long birth, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        internal static bool IsRefusal(string reason)
        { return reason != null && reason.IndexOf(RefusalPrefix, StringComparison.Ordinal) >= 0; }
        private static bool ProcessMatches(IntPtr process, int pid, long birth, ulong mask)
        {
            long observed, exit, kernel, user; uint exitCode; ulong observedMask;
            return process != IntPtr.Zero && pid > 4 && birth > 0 && mask != 0 && GetProcessId(process) == (uint)pid
                && GetExitCodeProcess(process, out exitCode) && exitCode == 259
                && GetProcessTimes(process, out observed, out exit, out kernel, out user) && observed == birth
                && Native.TryQueryAffinity(process, out observedMask) && observedMask == mask;
        }
        private static bool ThreadIds(int pid, out HashSet<uint> ids, out string reason)
        {
            ids = new HashSet<uint>(); reason = null;
            List<uint> own;
            // 优先只遍历目标进程自己的线程 拿不到查询权限时再用系统进程信息查询 最后回退到 Toolhelp 上限和拒绝原因三者一致
            if (ProcessThreadList.TryReadOwn(pid, MaxThreads, out own))
            {
                foreach (uint tid in own)
                    if (tid == 0 || !ids.Add(tid) || ids.Count > MaxThreads)
                    { reason = RefusalPrefix + "thread-set-invalid"; return false; }
                return true;
            }
            bool found; List<uint> listed; int systemThreads;
            if (ProcessThreadList.TryRead(pid, out found, out listed, out systemThreads))
            {
                if (systemThreads > MaxSnapshotEntries) { reason = RefusalPrefix + "snapshot-limit"; return false; }
                foreach (uint tid in listed)
                    if (tid == 0 || !ids.Add(tid) || ids.Count > MaxThreads)
                    { reason = RefusalPrefix + "thread-set-invalid"; return false; }
                if (ids.Count == 0) { reason = RefusalPrefix + "thread-set-unconfirmed"; return false; }
                return true;
            }
            IntPtr snapshot = CreateToolhelp32Snapshot(4, 0);
            if (snapshot == InvalidHandle || snapshot == IntPtr.Zero)
            { reason = RefusalPrefix + "snapshot-unavailable:" + Marshal.GetLastWin32Error(); return false; }
            try
            {
                var entry = new ThreadEntry(); entry.Size = (uint)Marshal.SizeOf(typeof(ThreadEntry));
                bool next = Thread32First(snapshot, ref entry); int entries = 0;
                while (next)
                {
                    if (++entries > MaxSnapshotEntries) { reason = RefusalPrefix + "snapshot-limit"; return false; }
                    if (entry.OwnerPid == (uint)pid && (entry.Tid == 0 || !ids.Add(entry.Tid) || ids.Count > MaxThreads))
                    { reason = RefusalPrefix + "thread-set-invalid"; return false; }
                    entry.Size = (uint)Marshal.SizeOf(typeof(ThreadEntry));
                    next = Thread32Next(snapshot, ref entry);
                }
                if (Marshal.GetLastWin32Error() != 18 || ids.Count == 0)
                { reason = RefusalPrefix + "thread-set-unconfirmed"; return false; }
                return true;
            }
            finally { Native.CloseHandle(snapshot); }
        }
        // capture 为空时自设掩码的线程直接拒绝 不为空时记下原掩码并继续检查 CPU Sets 等其余条件
        private static ThreadRead InspectThread(HeldThread thread, int pid, ulong processMask, Dictionary<uint, ThreadMaskPin> capture, out string reason)
        {
            reason = null; long birth, exit, kernel, user; GroupAffinity affinity;
            uint processPid = GetProcessIdOfThread(thread.Handle);
            if (processPid != (uint)pid)
            {
                int error = Marshal.GetLastWin32Error();
                if (processPid == 0 && thread.Birth > 0 && error != 5 && Native.WaitForSingleObject(thread.Handle, 0) == 0) return ThreadRead.Gone;
                reason = RefusalPrefix + "thread-unconfirmed:" + thread.Tid; return ThreadRead.Unconfirmed;
            }
            if (!GetThreadTimes(thread.Handle, out birth, out exit, out kernel, out user))
            {
                int error = Marshal.GetLastWin32Error();
                if (thread.Birth > 0 && error != 5 && Native.WaitForSingleObject(thread.Handle, 0) == 0) return ThreadRead.Gone;
                reason = RefusalPrefix + "thread-unconfirmed:" + thread.Tid; return ThreadRead.Unconfirmed;
            }
            if (birth <= 0 || (thread.Birth > 0 && thread.Birth != birth))
            { reason = RefusalPrefix + "thread-unconfirmed:" + thread.Tid; return ThreadRead.Unconfirmed; }
            thread.Birth = birth;
            uint state = Native.WaitForSingleObject(thread.Handle, 0);
            if (state == 0) return ThreadRead.Gone;
            if (state != 258)
            { reason = RefusalPrefix + "thread-unconfirmed:" + thread.Tid; return ThreadRead.Unconfirmed; }
            if (!GetThreadGroupAffinity(thread.Handle, out affinity))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 5 && Native.WaitForSingleObject(thread.Handle, 0) == 0) return ThreadRead.Gone;
                reason = RefusalPrefix + "thread-unconfirmed:" + thread.Tid; return ThreadRead.Unconfirmed;
            }
            if (affinity.Group != 0 || affinity.Mask == UIntPtr.Zero)
            { reason = RefusalPrefix + "group-unsupported:" + thread.Tid; return ThreadRead.Unconfirmed; }
            if (affinity.Mask.ToUInt64() != processMask)
            {
                if (capture == null) { reason = RefusalPrefix + "custom-mask:" + thread.Tid; return ThreadRead.Unconfirmed; }
                capture[thread.Tid] = new ThreadMaskPin { Tid = thread.Tid, Birth = birth, Mask = affinity.Mask.ToUInt64() };
            }
            uint required;
            bool queried = GetThreadSelectedCpuSets(thread.Handle, IntPtr.Zero, 0, out required);
            int queryError = Marshal.GetLastWin32Error();
            if (!queried && queryError != 122)
            {
                if (queryError != 5 && Native.WaitForSingleObject(thread.Handle, 0) == 0) return ThreadRead.Gone;
                reason = RefusalPrefix + "cpu-sets-unconfirmed:" + thread.Tid; return ThreadRead.Unconfirmed;
            }
            if (required > 0)
            { reason = RefusalPrefix + "custom-cpu-sets:" + thread.Tid; return ThreadRead.Unconfirmed; }
            if (!queried)
            { reason = RefusalPrefix + "cpu-sets-unconfirmed:" + thread.Tid; return ThreadRead.Unconfirmed; }
            state = Native.WaitForSingleObject(thread.Handle, 0);
            if (state == 0) return ThreadRead.Gone;
            if (state != 258)
            { reason = RefusalPrefix + "thread-unconfirmed:" + thread.Tid; return ThreadRead.Unconfirmed; }
            return ThreadRead.Known;
        }
        // 用户为游戏手动选定核心范围时使用 只确认进程身份和当前范围 不因线程状态拒绝
        // 进程范围写入后会接管全部线程 游戏自行设置的线程分配随之失效 调用方用 Survey 把数量记进日志
        internal static bool CheckProcessOnly(IntPtr process, int pid, long birth, ulong expectedMask, out string reason)
        {
            reason = null;
            try
            {
                if (ProcessMatches(process, pid, birth, expectedMask)) return true;
                reason = RefusalPrefix + "process-unconfirmed"; return false;
            }
            catch (Exception error) { reason = RefusalPrefix + "inspection-error:" + error.GetType().Name; return false; }
        }
        internal static bool CheckProcessOnly(IntPtr process, long birth, ulong expectedMask, out string reason)
        {
            try
            {
                uint pid = GetProcessId(process);
                if (pid > int.MaxValue) { reason = RefusalPrefix + "process-unconfirmed"; return false; }
                return CheckProcessOnly(process, (int)pid, birth, expectedMask, out reason);
            }
            catch (Exception error) { reason = RefusalPrefix + "inspection-error:" + error.GetType().Name; return false; }
        }
        // 只读清点一次 不重试也不影响是否写入 custom 是自设了掩码或 CPU Sets 的线程数 unreadable 是打不开或读不出的线程数
        internal static bool Survey(int pid, ulong processMask, out int custom, out int unreadable)
        {
            custom = 0; unreadable = 0;
            try
            {
                HashSet<uint> ids; string reason;
                if (pid <= 4 || processMask == 0 || !ThreadIds(pid, out ids, out reason)) return false;
                foreach (uint tid in ids)
                {
                    IntPtr handle = OpenThread(0x100040, false, tid);
                    if (handle == IntPtr.Zero)
                    {
                        if (Marshal.GetLastWin32Error() != 87) unreadable++;
                        continue;
                    }
                    try
                    {
                        ThreadRead read = InspectThread(new HeldThread { Tid = tid, Handle = handle }, pid, processMask, null, out reason);
                        if (read != ThreadRead.Unconfirmed) continue;
                        if (reason != null && (reason.StartsWith(RefusalPrefix + "custom-mask:", StringComparison.Ordinal) || reason.StartsWith(RefusalPrefix + "custom-cpu-sets:", StringComparison.Ordinal))) custom++;
                        else unreadable++;
                    }
                    finally { Native.CloseHandle(handle); }
                }
                return true;
            }
            catch { return false; }
        }
        internal static bool Check(IntPtr process, long birth, ulong expectedMask, out string reason)
        {
            try
            {
                uint pid = GetProcessId(process);
                if (pid > int.MaxValue) { reason = RefusalPrefix + "process-unconfirmed"; return false; }
                return Check(process, (int)pid, birth, expectedMask, out reason);
            }
            catch (Exception error) { reason = RefusalPrefix + "inspection-error:" + error.GetType().Name; return false; }
        }
        internal static bool Check(IntPtr process, int pid, long birth, ulong expectedMask, out string reason)
        {
            try { return CheckCore(process, pid, birth, expectedMask, null, out reason); }
            catch (Exception error) { reason = RefusalPrefix + "inspection-error:" + error.GetType().Name; return false; }
        }
        // 与 Check 相同的身份 线程集合稳定性和 CPU Sets 检查 只是自设掩码的线程不拒绝 把原掩码交给调用方
        // 只给反作弊引擎的 SGuard64 用
        internal static bool CheckCapturingThreadMasks(IntPtr process, int pid, long birth, ulong expectedMask, List<ThreadMaskPin> pins, out string reason)
        {
            var capture = new Dictionary<uint, ThreadMaskPin>();
            try
            {
                if (!CheckCore(process, pid, birth, expectedMask, capture, out reason)) return false;
                pins.AddRange(capture.Values);
                return true;
            }
            catch (Exception error) { reason = RefusalPrefix + "inspection-error:" + error.GetType().Name; return false; }
        }
        // 进程范围已恢复为 processMask 之后 把一个线程写回它自设的原值 每个线程只调用一次 不重试
        // 线程已退出 身份不符 或进程和线程当前范围不是刚恢复的范围（期间被别人改过）时跳过 保持现值
        internal static bool RestoreThreadMask(IntPtr process, ThreadMaskPin pin, ulong processMask)
        {
            if (pin.Tid == 0 || pin.Birth <= 0 || pin.Mask == 0 || processMask == 0 || (pin.Mask & ~processMask) != 0) return false;
            IntPtr handle = IntPtr.Zero;
            try
            {
                ulong currentProcess;
                uint pid = GetProcessId(process);
                if (pid == 0 || !Native.TryQueryAffinity(process, out currentProcess) || currentProcess != processMask) return false;
                // 只申请查询 设置和等待权限
                handle = OpenThread(0x100060, false, pin.Tid);
                if (handle == IntPtr.Zero) return false;
                long birth, exit, kernel, user; GroupAffinity affinity;
                if (GetProcessIdOfThread(handle) != pid || !GetThreadTimes(handle, out birth, out exit, out kernel, out user) || birth != pin.Birth
                    || Native.WaitForSingleObject(handle, 0) != 258
                    || !GetThreadGroupAffinity(handle, out affinity) || affinity.Group != 0 || affinity.Mask.ToUInt64() != processMask) return false;
                if (SetThreadAffinityMask(handle, (UIntPtr)pin.Mask) == UIntPtr.Zero) return false;
                return GetThreadGroupAffinity(handle, out affinity) && affinity.Group == 0 && affinity.Mask.ToUInt64() == pin.Mask;
            }
            catch { return false; }
            finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
        }
        private static bool CheckCore(IntPtr process, int pid, long birth, ulong expectedMask, Dictionary<uint, ThreadMaskPin> capture, out string reason)
        {
            reason = null;
            if (!ProcessMatches(process, pid, birth, expectedMask))
            { reason = RefusalPrefix + "process-unconfirmed"; return false; }
            for (int attempt = 0; attempt < MaxSnapshots; attempt++)
            {
                // 线程集合不稳定而重读时 上一轮记下的原值作废 只交出最终稳定那一轮的结果
                if (capture != null) capture.Clear();
                HashSet<uint> before, after;
                if (!ThreadIds(pid, out before, out reason)) return false;
                var held = new List<HeldThread>();
                try
                {
                    bool unstable = false;
                    foreach (uint tid in before)
                    {
                        // 只申请查询和等待权限 不申请线程写入权限
                        IntPtr handle = OpenThread(0x100040, false, tid);
                        if (handle == IntPtr.Zero)
                        {
                            int error = Marshal.GetLastWin32Error();
                            HashSet<uint> latest;
                            // 只有明确已消失的快照成员可以再次尝试 访问拒绝或身份未知不能重试

                            if (error == 87 && ThreadIds(pid, out latest, out reason) && !latest.Contains(tid))
                            { unstable = true; break; }
                            reason = RefusalPrefix + "thread-access:" + tid; return false;
                        }
                        var thread = new HeldThread { Tid = tid, Handle = handle }; held.Add(thread);
                        ThreadRead read = InspectThread(thread, pid, expectedMask, capture, out reason);
                        if (read == ThreadRead.Gone) { unstable = true; break; }
                        if (read != ThreadRead.Known) return false;
                    }
                    if (unstable) { reason = RefusalPrefix + "thread-set-unstable"; continue; }
                    if (!ThreadIds(pid, out after, out reason)) return false;
                    if (!before.SetEquals(after)) { reason = RefusalPrefix + "thread-set-unstable"; continue; }
                    foreach (HeldThread thread in held)
                    {
                        ThreadRead read = InspectThread(thread, pid, expectedMask, capture, out reason);
                        if (read == ThreadRead.Gone) { unstable = true; break; }
                        if (read != ThreadRead.Known) return false;
                    }
                    if (unstable) { reason = RefusalPrefix + "thread-set-unstable"; continue; }
                    if (!ProcessMatches(process, pid, birth, expectedMask))
                    { reason = RefusalPrefix + "process-changed"; return false; }
                    reason = null; return true;
                }
                finally { foreach (HeldThread thread in held) Native.CloseHandle(thread.Handle); }
            }
            reason = RefusalPrefix + "thread-set-unstable"; return false;
        }
    }
}
