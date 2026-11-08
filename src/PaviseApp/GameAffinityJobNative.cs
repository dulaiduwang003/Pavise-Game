using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class GameAffinityJobNative
    {
        internal const string Prefix = "Local\\Pavise.GameAffinity.V1.";
        internal const uint AffinityLimit = 0x10;
        internal const uint SilentBreakaway = 0x1000;
        private const uint AllowedFlags = AffinityLimit | SilentBreakaway;

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimits value, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int info, out ExtendedLimits value, uint size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inside);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        internal static bool IsInAnyJob(IntPtr process, out bool inside)
        { return IsProcessInJob(process, IntPtr.Zero, out inside); }

        internal static bool IsMember(IntPtr process, IntPtr job, out bool inside)
        { return IsProcessInJob(process, job, out inside); }

        internal static bool Alive(IntPtr process)
        { return process != IntPtr.Zero && Native.WaitForSingleObject(process, 0) == 258; }

        internal static bool Matches(IntPtr process, long birth)
        {
            long actual, cpu; ulong io;
            return Alive(process) && Native.QueryProcessSample(process, out actual, out cpu, out io) && actual == birth;
        }

        internal static IntPtr OpenTarget(int pid, long birth)
        {
            // AssignProcessToJobObject requires SET_QUOTA and TERMINATE access;
            // this module never terminates the target. SET_INFORMATION is for restoration.
            IntPtr process = Native.OpenProcess(0x101301, false, pid);
            if (process != IntPtr.Zero && Matches(process, birth)) return process;
            if (process != IntPtr.Zero) Native.CloseHandle(process);
            return IntPtr.Zero;
        }

        internal static IntPtr OpenIdentity(int pid, long birth)
        {
            IntPtr process = Native.OpenProcess(0x101000, false, pid);
            if (process != IntPtr.Zero && Matches(process, birth)) return process;
            if (process != IntPtr.Zero) Native.CloseHandle(process);
            return IntPtr.Zero;
        }

        internal static IntPtr OpenRestore(int pid, long birth)
        {
            IntPtr process = Native.OpenProcess(0x101200, false, pid);
            if (process != IntPtr.Zero && Matches(process, birth)) return process;
            if (process != IntPtr.Zero) Native.CloseHandle(process);
            return IntPtr.Zero;
        }

        // 0 = original live target, 1 = exited/reused PID, 2 = unreadable.
        internal static int Probe(int pid, long birth)
        {
            IntPtr process = Native.OpenProcess(0x101000, false, pid);
            if (process == IntPtr.Zero) return Native.LastOpenProcessFailureWasNoSuchProcess() ? 1 : 2;
            try
            {
                uint wait = Native.WaitForSingleObject(process, 0);
                if (wait == 0) return 1;
                if (wait != 258) return 2;
                long actual, cpu; ulong io;
                if (!Native.QueryProcessSample(process, out actual, out cpu, out io)) return 2;
                return actual == birth ? 0 : 1;
            }
            finally { Native.CloseHandle(process); }
        }

        internal static IntPtr Open(string name)
        { return OpenJobObject(7, false, name); }

        internal static IntPtr Create(string name, out string reason)
        {
            reason = null;
            IntPtr job = CreateJobObject(IntPtr.Zero, name);
            int error = Marshal.GetLastWin32Error();
            if (job == IntPtr.Zero) { reason = "create-job:" + error; return IntPtr.Zero; }
            if (error == 183) { Native.CloseHandle(job); reason = "job-name-collision"; return IntPtr.Zero; }
            var limits = new ExtendedLimits();
            limits.Basic.Flags = SilentBreakaway;
            if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            {
                uint flags; ulong mask;
                if (Inspect(job, out flags, out mask, out reason) && flags == SilentBreakaway) return job;
            }
            if (reason == null) reason = "initialize-job:" + Marshal.GetLastWin32Error();
            Native.CloseHandle(job);
            return IntPtr.Zero;
        }

        internal static bool Inspect(IntPtr job, out uint flags, out ulong mask, out string reason)
        {
            flags = 0; mask = 0; reason = null;
            ExtendedLimits limits;
            if (!QueryInformationJobObject(job, 9, out limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits)), IntPtr.Zero))
            { reason = "read-job:" + Marshal.GetLastWin32Error(); return false; }
            flags = limits.Basic.Flags; mask = limits.Basic.Affinity.ToUInt64();
            // Never accept a job containing kill-on-close, subset affinity, or any
            // unrelated resource limit. Only this module's two flags are owned.
            if ((flags & ~AllowedFlags) != 0 || (flags & SilentBreakaway) == 0)
            { reason = "job-limit-conflict"; return false; }
            return true;
        }

        internal static bool Apply(IntPtr job, IntPtr process, long birth, ulong desired, out string reason)
        {
            reason = null;
            if (desired == 0 || !Matches(process, birth)) { reason = "target-identity"; return false; }
            uint flags; ulong observed;
            if (!Inspect(job, out flags, out observed, out reason)) return false;
            bool member;
            if (!IsMember(process, job, out member)) { reason = "membership-unreadable"; return false; }
            ulong currentMask;
            if (!Native.TryQueryAffinity(process, out currentMask)
                || !GameThreadAffinityAdmission.Check(process, birth, currentMask, out reason))
            { if (reason == null) reason = GameThreadAffinityAdmission.RefusalPrefix + "process-mask-unreadable"; return false; }
            // Windows validates existing job hierarchies during assignment.
            // Confirm protection from assignment and effective affinity readback.
            var limits = new ExtendedLimits();
            limits.Basic.Flags = SilentBreakaway | AffinityLimit;
            limits.Basic.Affinity = (UIntPtr)desired;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            { reason = "set-job-affinity:" + Marshal.GetLastWin32Error(); return false; }
            if (!member && !AssignProcessToJobObject(job, process))
            { reason = "assign-job:" + Marshal.GetLastWin32Error(); return false; }
            return Verify(job, process, birth, desired, out reason);
        }

        internal static bool Verify(IntPtr job, IntPtr process, long birth, ulong desired, out string reason)
        {
            reason = null;
            if (!Matches(process, birth)) { reason = "target-identity"; return false; }
            uint flags; ulong mask; bool member;
            if (!Inspect(job, out flags, out mask, out reason)) return false;
            if (flags != (SilentBreakaway | AffinityLimit) || mask != desired)
            { reason = "job-affinity-mismatch"; return false; }
            if (!IsMember(process, job, out member) || !member)
            { reason = "job-membership-mismatch"; return false; }
            if (!Native.TryQueryAffinity(process, out mask) || mask != desired)
            { reason = "process-affinity-mismatch"; return false; }
            return true;
        }

        internal static bool Restore(IntPtr job, IntPtr process, long birth, ulong original, ulong desired, out string reason)
        {
            reason = null;
            if (process == IntPtr.Zero) { reason = "restore-process-unavailable"; return false; }
            uint wait = Native.WaitForSingleObject(process, 0);
            if (wait != 0 && wait != 258) { reason = "restore-process-unreadable"; return false; }
            uint flags; ulong mask;
            if (!Inspect(job, out flags, out mask, out reason)) return false;
            bool wasLimited = (flags & AffinityLimit) != 0;
            bool alive = wait == 258, member = false;
            if (alive && (!Matches(process, birth) || !IsMember(process, job, out member)))
            { reason = "restore-identity"; return false; }
            var limits = new ExtendedLimits();
            limits.Basic.Flags = SilentBreakaway;
            if (wasLimited && !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            { reason = "clear-job-affinity:" + Marshal.GetLastWin32Error(); return false; }
            if (!Inspect(job, out flags, out mask, out reason) || (flags & AffinityLimit) != 0)
            { if (reason == null) reason = "clear-job-unconfirmed"; return false; }
            if (!alive || !member) return true;
            if (!Native.TryQueryAffinity(process, out mask)) { reason = "restore-mask-unreadable"; return false; }
            if (mask == original) return true;
            // Clearing our limit can expose a changed ancestor-job limit. A distinct
            // current range is not our desired range and must not be overwritten.
            if (mask != desired) return true;
            if (original == 0 || !Native.SetProcessAffinityMask(process, (UIntPtr)original)
                || !Native.TryQueryAffinity(process, out mask) || mask != original)
            { reason = "restore-mask-unconfirmed"; return false; }
            return true;
        }

        internal static bool ClearForGoneTarget(IntPtr job, out string reason)
        {
            uint flags; ulong mask;
            if (!Inspect(job, out flags, out mask, out reason)) return false;
            var limits = new ExtendedLimits();
            limits.Basic.Flags = SilentBreakaway;
            if ((flags & AffinityLimit) != 0
                && !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            { reason = "clear-gone-job:" + Marshal.GetLastWin32Error(); return false; }
            return Inspect(job, out flags, out mask, out reason) && (flags & AffinityLimit) == 0;
        }

        internal static bool VerifyNeutral(IntPtr job, out string reason)
        {
            uint flags; ulong mask;
            if (!Inspect(job, out flags, out mask, out reason)) return false;
            if ((flags & AffinityLimit) != 0) { reason = "neutral-job-limit-conflict"; return false; }
            return true;
        }
    }
}
