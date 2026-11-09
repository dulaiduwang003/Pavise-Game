using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PaviseApp
{
    internal static partial class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public int Size;

            public int Major;

            public int Minor;

            public int Build;

            public int Platform;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string CSD;
        }

        private struct SystemPowerStatusNative
        {
            public byte AcLineStatus;

            public byte BatteryFlag;

            public byte BatteryLifePercent;

            public byte SystemStatusFlag;

            public int BatteryLifeTime;

            public int BatteryFullLifeTime;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OsVersionInfo v);

        public static int OsBuild()
        {
            if (osBuild < 0)
            {
                try
                {
                    OsVersionInfo v = default(OsVersionInfo);
                    v.Size = Marshal.SizeOf(typeof(OsVersionInfo));
                    osBuild = ((RtlGetVersion(ref v) == 0) ? v.Build : 0);
                }
                catch
                {
                    osBuild = 0;
                }
            }
            return osBuild;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatusNative status);

        [DllImport("netapi32.dll")]
        private static extern int NetGetJoinInformation(string server, out IntPtr name, out int status);

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        public static bool IsDomainJoined()
        {
            if (domainJoined < 0)
            {
                try
                {
                    IntPtr name;
                    int status;
                    if (NetGetJoinInformation(null, out name, out status) != 0)
                    {
                        domainJoined = 0;
                    }
                    else
                    {
                        if (name != IntPtr.Zero)
                        {
                            NetApiBufferFree(name);
                        }
                        domainJoined = ((status == 3) ? 1 : 0);
                    }
                }
                catch
                {
                    domainJoined = 0;
                }
            }
            return domainJoined == 1;
        }

        internal static bool ConfirmedAcPower()
        {
            try
            {
                SystemPowerStatusNative status;
                return GetSystemPowerStatus(out status) && status.AcLineStatus == 1;
            }
            catch
            {
                return false;
            }
        }

        public static bool OnAcPower()
        {
            try
            {
                SystemPowerStatusNative status;
                if (!GetSystemPowerStatus(out status))
                {
                    return true;
                }
                return status.AcLineStatus != 0;
            }
            catch
            {
                return true;
            }
        }

        public static bool HasSystemBattery()
        {
            if (hasBattery < 0)
            {
                try
                {
                    SystemPowerStatusNative status;
                    hasBattery = ((GetSystemPowerStatus(out status) && status.BatteryFlag != 128 && status.BatteryFlag != byte.MaxValue && status.AcLineStatus != byte.MaxValue) ? 1 : 0);
                }
                catch
                {
                    hasBattery = 0;
                }
            }
            return hasBattery == 1;
        }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

    }
}
