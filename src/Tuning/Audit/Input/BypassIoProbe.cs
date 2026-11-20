using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class BypassIoProbe
    {
        private const uint FsctlManageBypassIo = 590920u;

        private const int OpQuery = 3;

        private const int InputSize = 24;

        private const int OutputSize = 352;

        private const int OffOpStatus = 24;

        private const int OffDriverLen = 28;

        private const int OffDriverName = 30;

        private const int OffReasonLen = 94;

        private const int OffReason = 96;

        private const uint GenericRead = 2147483648u;

        private const uint ShareAll = 7u;

        private const uint OpenExisting = 3u;

        private const uint FileFlagNoBuffering = 536870912u;

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        public static BypassIoVerdict Query(string filePath)
        {
            BypassIoVerdict bypassIoVerdict = new BypassIoVerdict();
            bypassIoVerdict.Path = filePath ?? "";
            BypassIoVerdict bypassIoVerdict2 = bypassIoVerdict;
            if (string.IsNullOrEmpty(filePath))
            {
                return bypassIoVerdict2;
            }
            if (Native.OsBuild() > 0 && Native.OsBuild() < 22000)
            {
                return bypassIoVerdict2;
            }
            IntPtr intPtr = CreateFileW(filePath, GenericRead, ShareAll, IntPtr.Zero, 3u, FileFlagNoBuffering, IntPtr.Zero);
            if (intPtr == InvalidHandle)
            {
                return bypassIoVerdict2;
            }
            IntPtr intPtr2 = IntPtr.Zero;
            IntPtr intPtr3 = IntPtr.Zero;
            try
            {
                intPtr2 = Marshal.AllocHGlobal(InputSize);
                intPtr3 = Marshal.AllocHGlobal(OutputSize);
                for (int i = 0; i < InputSize; i += 4)
                {
                    Marshal.WriteInt32(intPtr2, i, 0);
                }
                for (int j = 0; j < OutputSize; j += 4)
                {
                    Marshal.WriteInt32(intPtr3, j, 0);
                }
                Marshal.WriteInt32(intPtr2, 0, OpQuery);
                uint returned;
                if (!DeviceIoControl(intPtr, FsctlManageBypassIo, intPtr2, InputSize, intPtr3, OutputSize, out returned, IntPtr.Zero))
                {
                    bypassIoVerdict2.Status = Marshal.GetLastWin32Error();
                    return bypassIoVerdict2;
                }
                bypassIoVerdict2.Supported = true;
                if ((bypassIoVerdict2.Status = Marshal.ReadInt32(intPtr3, OffOpStatus)) == 0)
                {
                    bypassIoVerdict2.Enabled = true;
                    return bypassIoVerdict2;
                }
                bypassIoVerdict2.Blocker = ReadWide(intPtr3, OffDriverName, Marshal.ReadInt16(intPtr3, OffDriverLen), 32);
                bypassIoVerdict2.Reason = ReadWide(intPtr3, OffReason, Marshal.ReadInt16(intPtr3, OffReasonLen), 128);
                return bypassIoVerdict2;
            }
            catch
            {
                return bypassIoVerdict2;
            }
            finally
            {
                if (intPtr2 != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(intPtr2);
                }
                if (intPtr3 != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(intPtr3);
                }
                CloseHandle(intPtr);
            }
        }

        private static string ReadWide(IntPtr buf, int offset, short lenChars, int maxChars)
        {
            int lenCharsSnapshot = lenChars;
            if (lenCharsSnapshot < 0 || lenCharsSnapshot > maxChars)
            {
                lenCharsSnapshot = maxChars;
            }
            if (lenCharsSnapshot == 0)
            {
                return "";
            }
            try
            {
                string ptrToStringUniResult = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + offset), lenCharsSnapshot);
                string ptrToStringUniResultOffset = ptrToStringUniResult ?? "";
                char[] trimChars = new char[1];
                return ptrToStringUniResultOffset.TrimEnd(trimChars).Trim();
            }
            catch
            {
                return "";
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
