// @author bdth 2074055628@qq.com
// 文件用途 对局中给无线网卡开媒体流模式 抑制周期性后台信道扫描的延迟突刺
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class WlanGuard
    {
        private const uint ClientVersion = 2;
        private const int OpcodeMediaStreamingMode = 3;

        private static readonly object lk = new object();
        private static IntPtr session;
        private static int guardedCount;
        private static bool noWifiLogged;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanInterfaceInfo
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Description;
            public int State;
        }

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved,
            out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);
        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanSetInterface(IntPtr handle, ref Guid interfaceGuid,
            int opcode, uint dataSize, ref uint data, IntPtr reserved);

        private static Guid[] EnumInterfaces(IntPtr handle)
        {
            IntPtr list = IntPtr.Zero;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0 || list == IntPtr.Zero)
                return new Guid[0];
            try
            {
                int count = Marshal.ReadInt32(list);
                if (count <= 0 || count > 32) return new Guid[0];
                var guids = new Guid[count];
                int stride = Marshal.SizeOf(typeof(WlanInterfaceInfo));
                for (int i = 0; i < count; i++)
                {
                    var info = (WlanInterfaceInfo)Marshal.PtrToStructure(
                        new IntPtr(list.ToInt64() + 8 + (long)i * stride), typeof(WlanInterfaceInfo));
                    guids[i] = info.InterfaceGuid;
                }
                return guids;
            }
            finally { WlanFreeMemory(list); }
        }

        private static int hasWifiCached = -1;

        public static bool HasWirelessInterface()
        {
            if (hasWifiCached < 0)
            {
                int result = 0;
                try
                {
                    IntPtr handle;
                    uint negotiated;
                    if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out negotiated, out handle) == 0)
                    {
                        try { if (EnumInterfaces(handle).Length > 0) result = 1; }
                        finally { WlanCloseHandle(handle, IntPtr.Zero); }
                    }
                }
                catch { }
                hasWifiCached = result;
            }
            return hasWifiCached == 1;
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (session != IntPtr.Zero) return true;
                IntPtr handle = IntPtr.Zero;
                uint negotiated;
                if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out negotiated, out handle) != 0)
                {
                    if (!noWifiLogged)
                    {
                        noWifiLogged = true;
                        Logger.Warn(Lang.T("log.wlanguard.1"));
                    }
                    return true;
                }
                Guid[] guids;
                try { guids = EnumInterfaces(handle); }
                catch { guids = new Guid[0]; }
                if (guids.Length == 0)
                {
                    WlanCloseHandle(handle, IntPtr.Zero);
                    if (!noWifiLogged)
                    {
                        noWifiLogged = true;
                        Logger.Log(Lang.T("log.wlanguard.2"));
                    }
                    return true;
                }
                int ok = 0;
                uint on = 1;
                for (int i = 0; i < guids.Length; i++)
                    if (WlanSetInterface(handle, ref guids[i], OpcodeMediaStreamingMode, 4, ref on, IntPtr.Zero) == 0)
                        ok++;
                if (ok == 0)
                {
                    WlanCloseHandle(handle, IntPtr.Zero);
                    Logger.Log(Lang.T("log.wlanguard.3"));
                    return false;
                }
                session = handle;
                guardedCount = ok;
                Logger.Log(Lang.T("log.wlanguard.4") + ok + Lang.T("log.wlanguard.5"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (session == IntPtr.Zero) return true;
                try
                {
                    Guid[] guids = EnumInterfaces(session);
                    uint off = 0;
                    for (int i = 0; i < guids.Length; i++)
                        WlanSetInterface(session, ref guids[i], OpcodeMediaStreamingMode, 4, ref off, IntPtr.Zero);
                }
                catch { }
                WlanCloseHandle(session, IntPtr.Zero);
                session = IntPtr.Zero;
                if (guardedCount > 0) Logger.Log(Lang.T("log.wlanguard.6") + guardedCount + Lang.T("log.wlanguard.7"));
                guardedCount = 0;
                return true;
            }
        }
    }
}
