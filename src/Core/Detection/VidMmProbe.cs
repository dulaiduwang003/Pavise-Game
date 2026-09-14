// @author bdth 2074055628@qq.com
// File purpose Read-only query of the game's VRAM budget and usage on the GPU actually rendering, via the user-mode WDDM interface
using System;

namespace PaviseApp
{
    internal sealed class VramStatus
    {
        public bool Ok;
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong CurrentReservation;
        public ulong AvailableForReservation;

        // Usage running right up against the budget is the precondition for action; when the budget is unreadable, always treat it as not tight
        public double UsageShare
        {
            get { return Budget > 0 ? (double)CurrentUsage / Budget : 0; }
        }
    }

    // Adapter handles have a lifetime; after a TDR the old handle is invalid and must be reopened
    //   Open the handle only when needed and close it right after, never resident, so a driver reset leaves no unusable reference behind
    internal static class VidMmProbe
    {
        public static bool TryOpenAdapter(int luidHigh, uint luidLow, out uint hAdapter)
        {
            hAdapter = 0;
            try
            {
                var open = new Native.D3dkmtOpenAdapterFromLuid();
                open.AdapterLuid.HighPart = luidHigh;
                open.AdapterLuid.LowPart = luidLow;
                if (Native.D3DKMTOpenAdapterFromLuid(ref open) != 0) return false;
                if (open.hAdapter == 0) return false;
                hAdapter = open.hAdapter;
                return true;
            }
            catch { return false; }
        }

        public static void CloseAdapter(uint hAdapter)
        {
            if (hAdapter == 0) return;
            try
            {
                var close = new Native.D3dkmtCloseAdapter { hAdapter = hAdapter };
                Native.D3DKMTCloseAdapter(ref close);
            }
            catch { }
        }

        // hProcess takes the target process handle; IntPtr.Zero queries this process
        //   The handle needs PROCESS_QUERY_LIMITED_INFORMATION; when anti-cheat denies it the caller just skips
        public static VramStatus Query(IntPtr hProcess, uint hAdapter, uint phys)
        {
            var status = new VramStatus();
            if (hAdapter == 0) return status;
            try
            {
                var q = new Native.D3dkmtQueryVideoMemoryInfo
                {
                    hProcess = hProcess,
                    hAdapter = hAdapter,
                    MemorySegmentGroup = Native.D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL,
                    PhysicalAdapterIndex = phys
                };
                if (Native.D3DKMTQueryVideoMemoryInfo(ref q) != 0) return status;
                status.Ok = true;
                status.Budget = q.Budget;
                status.CurrentUsage = q.CurrentUsage;
                status.CurrentReservation = q.CurrentReservation;
                status.AvailableForReservation = q.AvailableForReservation;
                return status;
            }
            catch { return status; }
        }

        // A reservation is a hint to the VRAM manager, not a hard lock on VRAM
        //   Writing 0 revokes it; when it returns false the caller must still assume it may have been written and go through the restore path
        //   hProcess is typed UINT64 in the header but the field name has the h prefix, so pass it as a process handle
        //     Written value must be verified by reading back CurrentReservation; the return value alone is not enough
        public static bool SetReservation(IntPtr hProcess, uint hAdapter, uint phys, ulong bytes)
        {
            if (hAdapter == 0 || hProcess == IntPtr.Zero) return false;
            try
            {
                var c = new Native.D3dkmtChangeVideoMemoryReservation
                {
                    hProcess = unchecked((ulong)hProcess.ToInt64()),
                    hAdapter = hAdapter,
                    MemorySegmentGroup = Native.D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL,
                    Reservation = bytes,
                    PhysicalAdapterIndex = phys
                };
                return Native.D3DKMTChangeVideoMemoryReservation(ref c) == 0;
            }
            catch { return false; }
        }

        public static string Gb(ulong bytes)
        {
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00");
        }
    }
}
