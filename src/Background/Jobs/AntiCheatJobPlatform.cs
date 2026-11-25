using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 每个目标独立 Job 只锁优先级和可选核心
    internal sealed class AntiCheatJobPlatform : IAntiCheatJobPlatform
    {
        internal const string Prefix = "Local\\Pavise.AntiCheat.V1.";
        internal const uint AffinityLimit = 0x10;
        internal const uint PriorityLimit = 0x20;
        internal const uint SilentBreakaway = 0x1000;
        internal const uint IdlePriorityClass = 0x40;
        private const uint AllowedFlags = AffinityLimit | PriorityLimit | SilentBreakaway;
        private const int ExtendedLimitInformation = 9;

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimits value, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int info, out ExtendedLimits value, uint size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        internal static string Name(int pid, long birth) { return AntiCheatJobIdentity.Name(pid, birth); }

        public IntPtr OpenTarget(int pid, long birth) { return GameAffinityJobNative.OpenTarget(pid, birth); }
        public IntPtr Create(string name, out string reason) { return GameAffinityJobNative.Create(name, out reason); }
        public bool Anchor(IntPtr job, IntPtr process, out string reason) { return GameAffinityJobNative.Anchor(job, process, out reason); }
        public IntPtr Open(string name, out int error)
        {
            IntPtr job = GameAffinityJobNative.Open(name);
            error = job == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            return job;
        }
        public int Probe(int pid, long birth) { return GameAffinityJobNative.Probe(pid, birth); }
        public void Close(IntPtr handle) { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }

        private static bool Inspect(IntPtr job, out ExtendedLimits limits, out string reason)
        {
            reason = null;
            if (!QueryInformationJobObject(job, ExtendedLimitInformation, out limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits)), IntPtr.Zero))
            { reason = "job-read:" + Marshal.GetLastWin32Error(); return false; }
            if ((limits.Basic.Flags & ~AllowedFlags) != 0 || (limits.Basic.Flags & SilentBreakaway) == 0)
            { reason = "job-limit-conflict"; return false; }
            return true;
        }

        private static bool VerifyLimits(IntPtr job, ulong mask, out string reason)
        {
            ExtendedLimits limits;
            if (!Inspect(job, out limits, out reason)) return false;
            uint expected = SilentBreakaway | PriorityLimit | (mask == 0 ? 0 : AffinityLimit);
            if (limits.Basic.Flags != expected || limits.Basic.Priority != IdlePriorityClass
                || (mask != 0 && limits.Basic.Affinity.ToUInt64() != mask))
            { reason = "job-limit-mismatch"; return false; }
            return true;
        }

        public bool Apply(IntPtr job, IntPtr process, long birth, ulong mask, out string reason)
        {
            reason = null;
            if ((mask & (mask - 1)) != 0) { reason = "job-affinity-not-single"; return false; }
            if (!GameAffinityJobNative.Matches(process, birth)) { reason = "target-identity"; return false; }
            ExtendedLimits previous;
            if (!Inspect(job, out previous, out reason)) return false;
            var limits = new ExtendedLimits();
            limits.Basic.Flags = SilentBreakaway | PriorityLimit | (mask == 0 ? 0 : AffinityLimit);
            limits.Basic.Priority = IdlePriorityClass;
            limits.Basic.Affinity = (UIntPtr)mask;
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            { reason = "job-write:" + Marshal.GetLastWin32Error(); return false; }
            if (!VerifyLimits(job, mask, out reason)) return false;
            bool member;
            if (!GameAffinityJobNative.IsMember(process, job, out member))
            { reason = "job-membership-read:" + Marshal.GetLastWin32Error(); return false; }
            if (!GameAffinityJobNative.Matches(process, birth)) { reason = "target-identity"; return false; }
            if (!member && !AssignProcessToJobObject(job, process))
            { reason = "job-assign:" + Marshal.GetLastWin32Error(); return false; }
            if (!Verify(job, process, birth, mask, out reason)) return false;
            if (Native.GetPriorityClass(process) != IdlePriorityClass)
            { reason = "job-process-priority-mismatch"; return false; }
            ulong observed;
            if (mask != 0 && (!Native.TryQueryAffinity(process, out observed) || observed != mask))
            { reason = "job-process-affinity-mismatch"; return false; }
            return true;
        }

        public bool Verify(IntPtr job, IntPtr process, long birth, ulong mask, out string reason)
        {
            reason = null;
            if (!GameAffinityJobNative.Matches(process, birth)) { reason = "target-identity"; return false; }
            if (!VerifyLimits(job, mask, out reason)) return false;
            bool member;
            if (!GameAffinityJobNative.IsMember(process, job, out member) || !member)
            { reason = "job-membership-mismatch"; return false; }
            return true;
        }

        public bool Clear(IntPtr job, out string reason)
        {
            ExtendedLimits previous;
            if (!Inspect(job, out previous, out reason)) return false;
            var limits = new ExtendedLimits();
            limits.Basic.Flags = SilentBreakaway;
            if (previous.Basic.Flags != SilentBreakaway
                && !SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            { reason = "job-clear:" + Marshal.GetLastWin32Error(); return false; }
            if (!Inspect(job, out limits, out reason)) return false;
            if (limits.Basic.Flags != SilentBreakaway) { reason = "job-clear-unconfirmed"; return false; }
            return true;
        }
    }
}
