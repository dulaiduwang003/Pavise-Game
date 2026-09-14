// @author bdth 2074055628@qq.com
// File purpose Native calls specific to the League of Legends extension: find the TCP listener process by port
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class LolNative
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
            bool order, int addressFamily, int tableClass, int reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct TcpRowOwnerPid
        {
            public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
        }

        public static bool TryGetTcpListenerOwner(int port, out int pid)
        {
            const int AfInet = 2;
            const int TcpTableOwnerPidListener = 3;
            pid = 0;
            if (port <= 0 || port > 65535) return false;
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (size <= 0) return false;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
                    return false;
                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf(typeof(TcpRowOwnerPid));
                long cursor = buffer.ToInt64() + 4;
                for (int i = 0; i < count; i++, cursor += rowSize)
                {
                    var row = (TcpRowOwnerPid)Marshal.PtrToStructure(
                        new IntPtr(cursor), typeof(TcpRowOwnerPid));
                    int local = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                    if (local != port) continue;
                    pid = (int)row.OwningPid;
                    return pid > 0;
                }
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buffer); }
            return false;
        }
    }
}
