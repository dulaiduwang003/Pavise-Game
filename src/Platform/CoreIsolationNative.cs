using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class IsolationCpuState
    {
        public ulong All, Allocated, Admitted;
        public ulong[] Physical;
    }

    internal interface ICoreIsolationPlatform
    {
        string BootIdentity();
        IsolationCpuState ReadSystem(IntPtr process);
        bool SetSystemAllowed(ulong mask);
        IntPtr Open(int pid, long creation);
        bool Exited(IntPtr handle);
        bool Gone(int pid, long creation);
        bool ReadAllowed(IntPtr handle, out ulong mask);
        bool SetAllowed(IntPtr handle, ulong mask);
        void Close(IntPtr handle);
    }

    // 168 takes one UINT64 mask per group no WorkloadClass prefix
    // 67 uses the same group masks CPU Set IDs are NOT bit positions
    // All operations are confined to one processor group and verified by readback
    internal sealed class CoreIsolationNative : ICoreIsolationPlatform
    {
        [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int c, byte[] b, int n);
        [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(IntPtr h, int c, byte[] b, int n);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int c, byte[] b, int n, out int r);
        [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int c, byte[] b, int n, out int r);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemCpuSetInformation(byte[] b, int n, out int r, IntPtr h, int flags);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);

        public string BootIdentity()
        {
            int n; var b = new byte[32];
            if (NtQuerySystemInformation(90, b, b.Length, out n) < 0 || n < 20 || n > b.Length) return null;
            var guid = new byte[16]; Array.Copy(b, guid, 16);
            Guid id = new Guid(guid);
            return id == Guid.Empty ? null : id.ToString("N");
        }

        public IsolationCpuState ReadSystem(IntPtr process)
        {
            int n;
            GetSystemCpuSetInformation(null, 0, out n, process, 0);
            if (n < 32 || n > 65536) return null;
            var b = new byte[n];
            if (!GetSystemCpuSetInformation(b, b.Length, out n, process, 0) || n > b.Length) return null;
            return Decode(b, n);
        }

        internal static IsolationCpuState Decode(byte[] b, int length)
        {
            if (b == null || length <= 0 || length > b.Length) return null;
            var state = new IsolationCpuState();
            var physical = new SortedDictionary<int, ulong>();
            var ids = new HashSet<uint>();
            for (int i = 0; i < length;)
            {
                if (length - i < 32) return null;
                uint size = BitConverter.ToUInt32(b, i);
                if (size < 32 || size > length - i || BitConverter.ToInt32(b, i + 4) != 0
                    || BitConverter.ToUInt16(b, i + 12) != 0 || b[i + 14] >= 64
                    || !ids.Add(BitConverter.ToUInt32(b, i + 8))) return null;
                ulong bit = 1UL << b[i + 14];
                if ((state.All & bit) != 0) return null;
                state.All |= bit;
                if ((b[i + 19] & 2) != 0) state.Allocated |= bit;
                if ((b[i + 19] & 4) != 0) state.Admitted |= bit;
                ulong siblings; physical.TryGetValue(b[i + 15], out siblings);
                physical[b[i + 15]] = siblings | bit;
                i += (int)size;
            }
            state.Physical = new List<ulong>(physical.Values).ToArray();
            return state.All == 0 ? null : state;
        }

        public bool SetSystemAllowed(ulong mask)
        {
            return Native.EnsureBoostPrivilege() && NtSetSystemInformation(168, BitConverter.GetBytes(mask), 8) == 0;
        }

        public IntPtr Open(int pid, long creation)
        {
            IntPtr h = Native.OpenProcess(0x100000 | Native.PROCESS_QUERY_LIMITED_INFORMATION
                | Native.PROCESS_SET_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return h;
            long found, cpu; ulong io;
            if (Native.QueryProcessSample(h, out found, out cpu, out io) && found == creation) return h;
            Native.CloseHandle(h); return IntPtr.Zero;
        }

        public bool Exited(IntPtr h) { return WaitForSingleObject(h, 0) == 0; }
        public bool Gone(int pid, long creation)
        {
            IntPtr q = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (q == IntPtr.Zero) return Native.LastOpenProcessFailureWasNoSuchProcess();
            try { long actual, cpu; ulong io;
                return Native.QueryProcessSample(q, out actual, out cpu, out io)
                    && (actual != creation || !Native.StillActive(q)); }
            finally { Native.CloseHandle(q); }
        }
        public bool ReadAllowed(IntPtr h, out ulong mask)
        {
            mask = 0; int n; var b = new byte[160];
            if (NtQueryInformationProcess(h, 67, b, b.Length, out n) != 0 || n != 8) return false;
            mask = BitConverter.ToUInt64(b, 0); return true;
        }
        public bool SetAllowed(IntPtr h, ulong mask)
        {
            return Native.EnsureBoostPrivilege() && NtSetInformationProcess(h, 67, BitConverter.GetBytes(mask), 8) == 0;
        }
        public void Close(IntPtr h) { if (h != IntPtr.Zero) Native.CloseHandle(h); }
    }
}
