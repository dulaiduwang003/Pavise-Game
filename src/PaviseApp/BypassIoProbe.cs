using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class BypassIoProbe
    {

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        public static BypassIoVerdict Query(string filePath)
        {
            BypassIoVerdict currentBypassIoVerdict = new BypassIoVerdict();
            currentBypassIoVerdict.Path = filePath ?? "";
            if (string.IsNullOrEmpty(filePath))
            {
                return currentBypassIoVerdict;
            }
            if (Native.OsBuild() > 0 && Native.OsBuild() < 22000)
            {
                return currentBypassIoVerdict;
            }
            IntPtr fileHandle = CreateFileW(filePath, 2147483648u, 7u, IntPtr.Zero, 3u, 536870912u, IntPtr.Zero);
            if (fileHandle == InvalidHandle)
            {
                return currentBypassIoVerdict;
            }
            IntPtr nativeBuffer = IntPtr.Zero;
            IntPtr currentNativeBuffer = IntPtr.Zero;
            try
            {
                nativeBuffer = Marshal.AllocHGlobal(24);
                currentNativeBuffer = Marshal.AllocHGlobal(352);
                for (int i = 0; i < 24; i += 4)
                {
                    Marshal.WriteInt32(nativeBuffer, i, 0);
                }
                for (int j = 0; j < 352; j += 4)
                {
                    Marshal.WriteInt32(currentNativeBuffer, j, 0);
                }
                Marshal.WriteInt32(nativeBuffer, 0, 3);
                uint returned;
                if (!DeviceIoControl(fileHandle, 590920u, nativeBuffer, 24, currentNativeBuffer, 352, out returned, IntPtr.Zero))
                {
                    currentBypassIoVerdict.Status = Marshal.GetLastWin32Error();
                    return currentBypassIoVerdict;
                }
                currentBypassIoVerdict.Supported = true;
                if ((currentBypassIoVerdict.Status = Marshal.ReadInt32(currentNativeBuffer, 24)) == 0)
                {
                    currentBypassIoVerdict.Enabled = true;
                    return currentBypassIoVerdict;
                }
                currentBypassIoVerdict.Blocker = ReadWide(currentNativeBuffer, 30, Marshal.ReadInt16(currentNativeBuffer, 28), 32);
                currentBypassIoVerdict.Reason = ReadWide(currentNativeBuffer, 96, Marshal.ReadInt16(currentNativeBuffer, 94), 128);
                return currentBypassIoVerdict;
            }
            catch
            {
                return currentBypassIoVerdict;
            }
            finally
            {
                if (nativeBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nativeBuffer);
                }
                if (currentNativeBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(currentNativeBuffer);
                }
                CloseHandle(fileHandle);
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
