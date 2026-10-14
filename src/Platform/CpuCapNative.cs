using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 只管理自行创建的 Job，避免接管外部限制。
    internal static class CpuCapNative
    {
        internal const string Prefix = @"Local\Pavise.CpuCap.V1.";
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinWorkingSet, MaxWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rate { public uint Flags, Value; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimits value, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int info, ref Rate value, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int info, out Rate value, uint size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inside);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        internal static IntPtr OpenTarget(int pid, long expectedCreation)
        {
            // 加入 Job 需要 SET_QUOTA 和 TERMINATE 权限。
            IntPtr p = Native.OpenProcess(0x100000 | 0x0100 | 0x0001 | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (p == IntPtr.Zero) return p;
            long creation, cpu; ulong io;
            if (IsAlive(p) && Native.QueryProcessSample(p, out creation, out cpu, out io)
                && expectedCreation > 0 && creation == expectedCreation)
            {
                string image = Native.ImagePath(p);
                if (!string.IsNullOrEmpty(image)
                    && !FamilyBoundary.CatalogProtected(pid, creation, Path.GetFileNameWithoutExtension(image), image)) return p;
            }
            Native.CloseHandle(p); return IntPtr.Zero;
        }
        internal static bool IsAlive(IntPtr process)
        { return process != IntPtr.Zero && Native.WaitForSingleObject(process, 0) == 258; }

        internal static IntPtr Create(string name)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, name);
            if (job == IntPtr.Zero) return job;
            if (Marshal.GetLastWin32Error() == 183) { Native.CloseHandle(job); return IntPtr.Zero; }
            // 允许子进程脱离限额，避免启动器连带限制游戏。
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x1000 } };
            if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits)))) return job;
            Native.CloseHandle(job); return IntPtr.Zero;
        }

        // CpuRate 单位为百分之一，最低值使用十进制计算。
        internal static bool ValidActivePercent(decimal percent)
        {
            return percent >= 0.01m && percent <= 50m
                && decimal.Truncate(percent * 100m) == percent * 100m;
        }

        internal static bool SetRate(IntPtr job, decimal percent)
        {
            if (percent != 0 && !ValidActivePercent(percent)) return false;
            var rate = new Rate { Flags = percent == 0 ? 0U : 5U, Value = (uint)(percent * 100) };
            if (!SetInformationJobObject(job, 15, ref rate, 8)) return false;
            Rate actual;
            return QueryInformationJobObject(job, 15, out actual, 8, IntPtr.Zero)
                && actual.Flags == rate.Flags && (percent == 0 || actual.Value == rate.Value);
        }

        internal static bool ReadRate(IntPtr job, out Rate rate)
        { return QueryInformationJobObject(job, 15, out rate, 8, IntPtr.Zero); }
    }
}
