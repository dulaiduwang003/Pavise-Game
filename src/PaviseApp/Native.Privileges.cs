using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class Native
    {

        private struct Luid
        {
            public uint LowPart;

            public int HighPart;
        }

        private struct TokenPrivileges
        {
            public uint PrivilegeCount;

            public Luid Luid;

            public uint Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges privileges, int bufferLength, IntPtr previous, IntPtr returnLength);

        public static bool EnsureBoostPrivilege()
        {
            int readResult = Volatile.Read(ref boostPrivilegeState);
            if (readResult != 0)
            {
                return readResult > 0;
            }
            bool enablePrivilegeSucceeded = EnablePrivilege("SeIncreaseBasePriorityPrivilege");
            Interlocked.CompareExchange(ref boostPrivilegeState, enablePrivilegeSucceeded ? 1 : (-1), 0);
            return Volatile.Read(ref boostPrivilegeState) > 0;
        }

        public static bool TryEnableDebugPrivilege()
        {
            int readResult = Volatile.Read(ref debugPrivilegeState);
            if (readResult != 0)
            {
                return readResult > 0;
            }
            bool enablePrivilegeSucceeded = EnablePrivilege("SeDebugPrivilege");
            Interlocked.CompareExchange(ref debugPrivilegeState, enablePrivilegeSucceeded ? 1 : (-1), 0);
            return Volatile.Read(ref debugPrivilegeState) > 0;
        }

        private static bool EnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 40u, out token))
            {
                return false;
            }
            try
            {
                Luid luid;
                if (!LookupPrivilegeValue(null, name, out luid))
                {
                    return false;
                }
                TokenPrivileges tokenPrivileges = default(TokenPrivileges);
                tokenPrivileges.PrivilegeCount = 1u;
                tokenPrivileges.Luid = luid;
                tokenPrivileges.Attributes = 2u;
                TokenPrivileges privileges = tokenPrivileges;
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    return false;
                }
                return Marshal.GetLastWin32Error() == 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }

    }
}
