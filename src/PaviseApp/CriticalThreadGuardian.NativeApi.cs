using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static partial class CriticalThreadGuardian
    {
        private struct DeleteDisposition
        {
            internal byte Delete;
        }

        private struct FileIdentity
        {
            internal uint Attributes;

            internal uint CreatedLow;

            internal uint CreatedHigh;

            internal uint AccessLow;

            internal uint AccessHigh;

            internal uint WrittenLow;

            internal uint WrittenHigh;

            internal uint Volume;

            internal uint SizeHigh;

            internal uint SizeLow;

            internal uint Links;

            internal uint IndexHigh;

            internal uint IndexLow;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            internal int Size;

            internal IntPtr Reserved;

            internal IntPtr Desktop;

            internal IntPtr Title;

            internal uint X;

            internal uint Y;

            internal uint Width;

            internal uint Height;

            internal uint XChars;

            internal uint YChars;

            internal uint Fill;

            internal uint Flags;

            internal ushort Show;

            internal ushort ReservedSize;

            internal IntPtr ReservedBytes;

            internal IntPtr Input;

            internal IntPtr Output;

            internal IntPtr Error;
        }

        private struct ProcessInfo
        {
            internal IntPtr Process;

            internal IntPtr Thread;

            internal uint Pid;

            internal uint Tid;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(IntPtr handle, int informationClass, ref DeleteDisposition information, uint bytes);

        private static string FileKey(FileIdentity value)
        {
            return value.Volume + ":" + value.IndexHigh + ":" + value.IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle, out long born, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessId(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref int capacity);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr attributes, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectory(string path, IntPtr attributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr handle, out FileIdentity info);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inJob);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr duplicate, uint access, bool inherit, uint options);

    }
}
