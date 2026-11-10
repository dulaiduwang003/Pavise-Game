using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 共用原有 Job 回执及守护；资源限制只用于已核实的微软后台名单。
    internal static class WindowsBackgroundJobNative
    {
        private const uint PriorityLimit = 0x20;
        private const uint AllowedFlags = 0x1030;

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            internal long ProcessTime, JobTime;
            internal uint Flags;
            internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcesses;
            internal UIntPtr Affinity;
            internal uint Priority, Scheduling;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CpuLimits { internal uint Flags, Rate; }

        [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
        private static extern bool SetExtended(IntPtr job, int info, ref ExtendedLimits value, uint size);
        [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
        private static extern bool QueryExtended(IntPtr job, int info, out ExtendedLimits value, uint size, IntPtr returned);
        [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
        private static extern bool SetCpu(IntPtr job, int info, ref CpuLimits value, uint size);
        [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
        private static extern bool QueryCpu(IntPtr job, int info, out CpuLimits value, uint size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        internal static IntPtr OpenTarget(GameAffinityJobRecord record)
        { return OpenProcess(record, record.Resources == null ? 0x101301u : 0x101701u); }

        internal static IntPtr OpenRestore(GameAffinityJobRecord record)
        { return OpenProcess(record, record.Resources == null ? 0x101200u : 0x101600u); }

        private static IntPtr OpenProcess(GameAffinityJobRecord record, uint access)
        {
            if (record.Resources != null) Native.TryEnableDebugPrivilege();
            IntPtr process = Native.OpenProcess((int)access, false, record.Pid);
            if (process != IntPtr.Zero && GameAffinityJobNative.Matches(process, record.Birth)) return process;
            if (process != IntPtr.Zero) Native.CloseHandle(process);
            return IntPtr.Zero;
        }

        internal static bool Capture(IntPtr process, int mode, out WindowsBackgroundLimits limits, out string reason)
        {
            uint priority = Native.GetPriorityClass(process);
            int io = Native.QueryIoPriority(process);
            limits = new WindowsBackgroundLimits { OriginalPriority = priority, OriginalIo = io };
            limits = limits.WithMode(mode);
            if ((priority == 32768 || priority == 128 || priority == 256) && !Native.EnsureBoostPrivilege())
            { reason = "微软后台原优先级恢复权限不足"; return false; }
            reason = limits.Valid ? null : "微软后台原始优先级读取失败";
            return limits.Valid;
        }

        private static bool Read(IntPtr job, out ExtendedLimits extended, out CpuLimits cpu, out string reason)
        {
            reason = null;
            extended = new ExtendedLimits(); cpu = new CpuLimits();
            if (!QueryExtended(job, 9, out extended, (uint)Marshal.SizeOf(typeof(ExtendedLimits)), IntPtr.Zero)
                || !QueryCpu(job, 15, out cpu, 8, IntPtr.Zero))
            { reason = "微软后台 Job 读取失败：" + Marshal.GetLastWin32Error(); return false; }
            if ((extended.Basic.Flags & ~AllowedFlags) != 0 || (extended.Basic.Flags & 0x1000) == 0
                || (cpu.Flags != 0 && cpu.Flags != 5))
            { reason = "微软后台 Job 含有不属于本功能的限制"; return false; }
            return true;
        }

        internal static bool Inspect(GameAffinityJobRecord record, IntPtr job, out string reason)
        {
            if (record.Resources == null)
            { uint flags; ulong mask; return GameAffinityJobNative.Inspect(job, out flags, out mask, out reason); }
            ExtendedLimits extended; CpuLimits cpu;
            return Read(job, out extended, out cpu, out reason);
        }

        internal static bool Apply(GameAffinityJobRecord record, IntPtr job, IntPtr process, out string reason)
        {
            if (record.Resources == null) return GameAffinityJobNative.Apply(job, process, record.Birth, record.Desired, out reason);
            reason = null;
            if (!record.Resources.Valid || !GameAffinityJobNative.Matches(process, record.Birth))
            { reason = "微软后台目标身份无效"; return false; }
            ExtendedLimits previous; CpuLimits previousCpu;
            if (!Read(job, out previous, out previousCpu, out reason)) return false;
            ulong current;
            if (!Native.TryQueryAffinity(process, out current)
                || !GameThreadAffinityAdmission.Check(process, record.Pid, record.Birth, current, out reason))
            { if (reason == null) reason = "微软后台原始核心范围不可读"; return false; }
            var limits = new ExtendedLimits();
            limits.Basic.Flags = AllowedFlags;
            limits.Basic.Affinity = (UIntPtr)record.Desired;
            limits.Basic.Priority = record.Resources.DesiredPriority;
            if (!SetExtended(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            { reason = "微软后台核心或优先级限制失败：" + Marshal.GetLastWin32Error(); return false; }
            var cpu = new CpuLimits { Flags = 5, Rate = record.Resources.CpuRate };
            if (!SetCpu(job, 15, ref cpu, 8))
            { reason = "微软后台 CPU 限额失败：" + Marshal.GetLastWin32Error(); return false; }
            bool member;
            if (!GameAffinityJobNative.IsMember(process, job, out member))
            { reason = "微软后台 Job 成员不可读"; return false; }
            if (!member && !AssignProcessToJobObject(job, process))
            { reason = "微软后台 Job 接管失败：" + Marshal.GetLastWin32Error(); return false; }
            int io = Native.QueryIoPriority(process);
            if (io < 0 || (io > record.Resources.DesiredIo && !Native.TrySetIoPriority(process, record.Resources.DesiredIo)))
            { reason = "微软后台 I/O 限制失败"; return false; }
            return Verify(record, job, process, out reason);
        }

        internal static bool Verify(GameAffinityJobRecord record, IntPtr job, IntPtr process, out string reason)
        {
            if (record.Resources == null) return GameAffinityJobNative.Verify(job, process, record.Birth, record.Desired, out reason);
            reason = null;
            ExtendedLimits limits; CpuLimits cpu; bool member; ulong mask;
            if (!GameAffinityJobNative.Matches(process, record.Birth) || !Read(job, out limits, out cpu, out reason))
            { if (reason == null) reason = "微软后台目标身份已变化"; return false; }
            if (limits.Basic.Flags != AllowedFlags || limits.Basic.Affinity.ToUInt64() != record.Desired
                || limits.Basic.Priority != record.Resources.DesiredPriority || cpu.Flags != 5 || cpu.Rate != record.Resources.CpuRate
                || !GameAffinityJobNative.IsMember(process, job, out member) || !member
                || !Native.TryQueryAffinity(process, out mask) || mask != record.Desired
                || Native.GetPriorityClass(process) != record.Resources.DesiredPriority)
            { reason = "微软后台有效限制与回执不一致"; return false; }
            int io = Native.QueryIoPriority(process);
            if (io < 0 || io > record.Resources.DesiredIo) { reason = "微软后台 I/O 限制已变化"; return false; }
            return true;
        }

        private static bool ClearResources(GameAffinityJobRecord record, IntPtr job, out string reason)
        {
            ExtendedLimits limits; CpuLimits cpu;
            if (!Read(job, out limits, out cpu, out reason)) return false;
            if (cpu.Flags != 0)
            {
                if (cpu.Rate != record.Resources.CpuRate) { reason = "微软后台 CPU 配额被外部修改"; return false; }
                cpu = new CpuLimits();
                if (!SetCpu(job, 15, ref cpu, 8)) { reason = "微软后台 CPU 限额清除失败：" + Marshal.GetLastWin32Error(); return false; }
            }
            if ((limits.Basic.Flags & PriorityLimit) != 0)
            {
                if (limits.Basic.Priority != record.Resources.DesiredPriority) { reason = "微软后台 Job 优先级被外部修改"; return false; }
                limits.Basic.Flags &= ~PriorityLimit;
                if (!SetExtended(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                { reason = "微软后台 Job 优先级清除失败：" + Marshal.GetLastWin32Error(); return false; }
            }
            if (!Read(job, out limits, out cpu, out reason) || cpu.Flags != 0 || (limits.Basic.Flags & PriorityLimit) != 0)
            { if (reason == null) reason = "微软后台额外限制未清除"; return false; }
            return true;
        }

        internal static bool Restore(GameAffinityJobRecord record, IntPtr job, IntPtr process, out string reason)
        {
            if (record.Resources == null) return GameAffinityJobNative.Restore(job, process, record.Birth, record.Original, record.Desired, out reason);
            bool live = GameAffinityJobNative.Alive(process), member = false;
            if (live && (!GameAffinityJobNative.Matches(process, record.Birth) || !GameAffinityJobNative.IsMember(process, job, out member)))
            { reason = "微软后台恢复身份不可确认"; return false; }
            if (!ClearResources(record, job, out reason)
                || !GameAffinityJobNative.Restore(job, process, record.Birth, record.Original, record.Desired, out reason)) return false;
            if (!live || !member) return true;
            uint currentPriority = Native.GetPriorityClass(process);
            if (currentPriority == 0) { reason = "微软后台恢复优先级不可读"; return false; }
            if ((record.Resources.OriginalPriority == 32768 || record.Resources.OriginalPriority == 128 || record.Resources.OriginalPriority == 256)
                && !Native.EnsureBoostPrivilege()) { reason = "微软后台原优先级恢复权限不足"; return false; }
            if (currentPriority == record.Resources.DesiredPriority && currentPriority != record.Resources.OriginalPriority
                && (!Native.SetPriorityClass(process, record.Resources.OriginalPriority)
                || Native.GetPriorityClass(process) != record.Resources.OriginalPriority))
            { reason = "微软后台原始优先级恢复失败"; return false; }
            int currentIo = Native.QueryIoPriority(process);
            if (currentIo < 0) { reason = "微软后台恢复 I/O 不可读"; return false; }
            if (currentIo == record.Resources.DesiredIo && currentIo != record.Resources.OriginalIo
                && (!Native.TrySetIoPriority(process, record.Resources.OriginalIo) || Native.QueryIoPriority(process) != record.Resources.OriginalIo))
            { reason = "微软后台原始 I/O 恢复失败"; return false; }
            // 与本方写入不同的值属于后来的外部设置，保留它们，不强制覆盖。
            return Neutral(record, job, out reason);
        }

        internal static bool ClearGone(GameAffinityJobRecord record, IntPtr job, out string reason)
        {
            if (record.Resources != null && !ClearResources(record, job, out reason)) return false;
            return GameAffinityJobNative.ClearForGoneTarget(job, out reason);
        }

        internal static bool Neutral(GameAffinityJobRecord record, IntPtr job, out string reason)
        {
            if (record.Resources != null)
            {
                ExtendedLimits limits; CpuLimits cpu;
                if (!Read(job, out limits, out cpu, out reason)) return false;
                if (cpu.Flags != 0 || (limits.Basic.Flags & PriorityLimit) != 0)
                { reason = "微软后台资源限制尚未解除"; return false; }
            }
            return GameAffinityJobNative.VerifyNeutral(job, out reason);
        }
    }
}
