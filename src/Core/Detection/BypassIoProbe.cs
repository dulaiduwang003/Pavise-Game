// @author bdth 2074055628@qq.com
// 文件用途 查询 BypassIO 直通读是否被过滤驱动阻断 纯只读 供体检页点名阻断者
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class BypassIoVerdict
    {
        public string Path = "";
        public bool Supported;
        public bool Enabled;
        public string Blocker = "";
        public string Reason = "";
        public int Status;
    }

    internal static class BypassIoProbe
    {
        private const uint FsctlManageBypassIo = 0x90448;
        private const int OpQuery = 3;
        private const int InputSize = 24;
        private const int OutputSize = 352;
        private const int OffOpStatus = 24;
        private const int OffDriverLen = 28;
        private const int OffDriverName = 30;
        private const int OffReasonLen = 94;
        private const int OffReason = 96;

        public static BypassIoVerdict Query(string filePath)
        {
            var v = new BypassIoVerdict { Path = filePath ?? "" };
            if (string.IsNullOrEmpty(filePath)) return v;
            if (Native.OsBuild() > 0 && Native.OsBuild() < 22000) return v;
            IntPtr h = CreateFileW(filePath, GenericRead,
                ShareAll, IntPtr.Zero, OpenExisting, FileFlagNoBuffering, IntPtr.Zero);
            if (h == InvalidHandle) return v;
            IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
            try
            {
                inBuf = Marshal.AllocHGlobal(InputSize);
                outBuf = Marshal.AllocHGlobal(OutputSize);
                for (int i = 0; i < InputSize; i += 4) Marshal.WriteInt32(inBuf, i, 0);
                for (int i = 0; i < OutputSize; i += 4) Marshal.WriteInt32(outBuf, i, 0);
                Marshal.WriteInt32(inBuf, 0, OpQuery);
                uint got;
                bool ok = DeviceIoControl(h, FsctlManageBypassIo, inBuf, InputSize,
                    outBuf, OutputSize, out got, IntPtr.Zero);
                if (!ok)
                {
                    v.Status = Marshal.GetLastWin32Error();
                    return v;
                }
                v.Supported = true;
                int opStatus = Marshal.ReadInt32(outBuf, OffOpStatus);
                v.Status = opStatus;
                if (opStatus == 0) { v.Enabled = true; return v; }
                v.Blocker = ReadWide(outBuf, OffDriverName, Marshal.ReadInt16(outBuf, OffDriverLen), 32);
                v.Reason = ReadWide(outBuf, OffReason, Marshal.ReadInt16(outBuf, OffReasonLen), 128);
                return v;
            }
            catch { return v; }
            finally
            {
                if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
                if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
                CloseHandle(h);
            }
        }

        private static string ReadWide(IntPtr buf, int offset, short lenChars, int maxChars)
        {
            int len = lenChars;
            if (len < 0 || len > maxChars) len = maxChars;
            if (len == 0) return "";
            try
            {
                string s = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + offset), len);
                return (s ?? "").TrimEnd('\0').Trim();
            }
            catch { return ""; }
        }

        private const uint GenericRead = 0x80000000;
        private const uint ShareAll = 0x1 | 0x2 | 0x4;
        private const uint OpenExisting = 3;
        private const uint FileFlagNoBuffering = 0x20000000;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string fileName, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint code,
            IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize,
            out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
