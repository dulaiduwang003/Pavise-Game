// @author bdth 2074055628@qq.com
// 文件用途 封装作业对象接口 用于给后台进程施加 CPU 硬配额

using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct JobCpuRateControl
        {
            public uint ControlFlags;
            public uint RateOrWeight;
        }

        public const uint JobCpuRateEnable = 0x1;
        public const uint JobCpuRateHardCap = 0x4;
        public const int JobObjectCpuRateControlInformation = 15;

        public const int ErrorAlreadyExists = 183;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr security, string name);

        public const uint JobObjectAllAccess = 0x1F001F;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenJobObject(uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass,
            ref JobCpuRateControl info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
            ref JobCpuRateControl info, int length, IntPtr returned);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsProcessInJob(IntPtr process, IntPtr job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);
    }
}
