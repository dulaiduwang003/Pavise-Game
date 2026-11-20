using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 后台资源 Job 的系统调用
    // 每个后台进程一个命名 Job 往目标进程里放一个只读句柄当锚点 目标活着 Job 的名字就在 Pavise 崩溃后能按名字重开再解除限制
    // 子进程静默脱离 启动器拉起的游戏不会继承资源限制
    internal sealed class BackgroundResourceJobPlatform : IResourceJobPlatform
    {
        private const uint SilentBreakaway = 0x1000;
        private const uint PriorityLimit = 0x20;
        private const uint IdlePriorityClass = 0x40;
        private const uint CpuRateEnable = 0x1;
        private const uint CpuRateHardCap = 0x4;
        private const uint IoRateEnable = 0x1;
        private const int ExtendedLimitInformation = 9;
        private const int CpuRateControlInformation = 15;
        private const uint JobAssignProcess = 0x0001;
        private const uint JobSetAttributes = 0x0002;
        private const uint JobQuery = 0x0004;
        // 加入 Job 要 SET_QUOTA 和 TERMINATE 放锚点要 DUP_HANDLE 核对身份要 QUERY_LIMITED 和 SYNCHRONIZE 本类不终止目标
        private const uint TargetAccess = 0x100000 | 0x1000 | 0x0100 | 0x0040 | 0x0001;

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
        private struct CpuRate
        {
            internal uint ControlFlags;
            internal uint Value;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct IoRate
        {
            internal long MaxIops, MaxBandwidth, ReservationIops;
            [MarshalAs(UnmanagedType.LPWStr)] internal string VolumeName;
            internal uint BaseIoSize, ControlFlags;
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
        private static extern bool SetInformationJobObject(IntPtr job, int info, ref CpuRate value, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int info, out CpuRate value, uint size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetIoRateControlInformationJobObject(IntPtr job, ref IoRate info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inside);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessId(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        public IntPtr OpenTarget(int pid, long creation)
        {
            IntPtr process = Native.OpenProcess((int)TargetAccess, false, pid);
            if (process == IntPtr.Zero) return IntPtr.Zero;
            long actual, cpu; ulong io;
            if (Native.WaitForSingleObject(process, 0) == 258 && Native.QueryProcessSample(process, out actual, out cpu, out io) && actual == creation)
                return process;
            Native.CloseHandle(process);
            return IntPtr.Zero;
        }

        public bool SameSession(IntPtr process)
        {
            uint target, self;
            uint pid = GetProcessId(process);
            return pid != 0 && ProcessIdToSessionId(pid, out target) && ProcessIdToSessionId(GetCurrentProcessId(), out self) && target == self;
        }

        public IntPtr CreateAnchored(string name, IntPtr process, out string reason)
        {
            reason = null;
            IntPtr job = CreateJobObject(IntPtr.Zero, name);
            int error = Marshal.GetLastWin32Error();
            if (job == IntPtr.Zero) { reason = "create:" + error; return IntPtr.Zero; }
            if (error == 183) { Native.CloseHandle(job); reason = "name-collision"; return IntPtr.Zero; }
            var limits = new ExtendedLimits();
            limits.Basic.Flags = SilentBreakaway;
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            {
                reason = "initialize:" + Marshal.GetLastWin32Error();
                Native.CloseHandle(job);
                return IntPtr.Zero;
            }
            // 锚点只给查询权限 目标拿着它也改不了限制 本方从不去关它 免得误关目标自己复用的句柄值
            IntPtr remote;
            if (!DuplicateHandle(GetCurrentProcess(), job, process, out remote, JobQuery, false, 0))
            {
                reason = "anchor:" + Marshal.GetLastWin32Error();
                Native.CloseHandle(job);
                return IntPtr.Zero;
            }
            return job;
        }

        public IntPtr Open(string name, out int error)
        {
            IntPtr job = OpenJobObject(JobQuery | JobSetAttributes | JobAssignProcess, false, name);
            error = job == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            return job;
        }

        public bool Apply(IntPtr job, IntPtr process, ResourceJobLimits limits, out string reason)
        {
            reason = null;
            if (limits == null) { reason = "limits"; return false; }
            var basic = new ExtendedLimits();
            basic.Basic.Flags = SilentBreakaway | PriorityLimit;
            basic.Basic.Priority = IdlePriorityClass;
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref basic, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            { reason = "priority:" + Marshal.GetLastWin32Error(); return false; }
            var cpu = new CpuRate { ControlFlags = limits.CpuRate > 0 ? CpuRateEnable | CpuRateHardCap : 0, Value = limits.CpuRate };
            if (!SetInformationJobObject(job, CpuRateControlInformation, ref cpu, (uint)Marshal.SizeOf(typeof(CpuRate))))
            { reason = "cpu:" + Marshal.GetLastWin32Error(); return false; }
            bool member;
            if (!IsProcessInJob(process, job, out member)) { reason = "membership:" + Marshal.GetLastWin32Error(); return false; }
            if (!member && !AssignProcessToJobObject(job, process)) { reason = "assign:" + Marshal.GetLastWin32Error(); return false; }
            return Verify(job, process, limits, out reason);
        }

        private static bool Verify(IntPtr job, IntPtr process, ResourceJobLimits limits, out string reason)
        {
            reason = null;
            ExtendedLimits basic;
            if (!QueryInformationJobObject(job, ExtendedLimitInformation, out basic, (uint)Marshal.SizeOf(typeof(ExtendedLimits)), IntPtr.Zero))
            { reason = "verify-read:" + Marshal.GetLastWin32Error(); return false; }
            if (basic.Basic.Flags != (SilentBreakaway | PriorityLimit) || basic.Basic.Priority != IdlePriorityClass)
            { reason = "verify-priority-limit"; return false; }
            CpuRate cpu;
            if (!QueryInformationJobObject(job, CpuRateControlInformation, out cpu, (uint)Marshal.SizeOf(typeof(CpuRate)), IntPtr.Zero))
            { reason = "verify-cpu-read:" + Marshal.GetLastWin32Error(); return false; }
            uint expectedFlags = limits.CpuRate > 0 ? CpuRateEnable | CpuRateHardCap : 0;
            if (cpu.ControlFlags != expectedFlags || (limits.CpuRate > 0 && cpu.Value != limits.CpuRate))
            { reason = "verify-cpu"; return false; }
            bool member;
            if (!IsProcessInJob(process, job, out member) || !member) { reason = "verify-membership"; return false; }
            if (Native.GetPriorityClass(process) != IdlePriorityClass) { reason = "verify-process-priority"; return false; }
            return true;
        }

        public bool Clear(IntPtr job, out string reason)
        {
            reason = null;
            var basic = new ExtendedLimits();
            basic.Basic.Flags = SilentBreakaway;
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref basic, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            { reason = "clear-priority:" + Marshal.GetLastWin32Error(); return false; }
            var cpu = new CpuRate();
            if (!SetInformationJobObject(job, CpuRateControlInformation, ref cpu, (uint)Marshal.SizeOf(typeof(CpuRate))))
            { reason = "clear-cpu:" + Marshal.GetLastWin32Error(); return false; }
            ExtendedLimits check;
            CpuRate cpuCheck;
            if (!QueryInformationJobObject(job, ExtendedLimitInformation, out check, (uint)Marshal.SizeOf(typeof(ExtendedLimits)), IntPtr.Zero)
                || check.Basic.Flags != SilentBreakaway)
            { reason = "clear-unconfirmed"; return false; }
            if (!QueryInformationJobObject(job, CpuRateControlInformation, out cpuCheck, (uint)Marshal.SizeOf(typeof(CpuRate)), IntPtr.Zero)
                || (cpuCheck.ControlFlags & CpuRateEnable) != 0)
            { reason = "clear-cpu-unconfirmed"; return false; }
            return true;
        }

        public bool SetIoBandwidth(IntPtr job, long bandwidth, out string reason)
        {
            // 空卷名把同一限额用于各卷 返回值只说明系统接受了请求
            var io = new IoRate { MaxBandwidth = bandwidth, ControlFlags = bandwidth > 0 ? IoRateEnable : 0 };
            reason = null;
            try
            {
                if (SetIoRateControlInformationJobObject(job, ref io) != 0) return true;
                reason = "io:" + Marshal.GetLastWin32Error();
            }
            catch (EntryPointNotFoundException) { reason = "io:entry-point"; }
            return false;
        }

        public int Probe(int pid, long creation)
        {
            return GameAffinityJobNative.Probe(pid, creation);
        }

        public void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero) Native.CloseHandle(handle);
        }
    }
}
