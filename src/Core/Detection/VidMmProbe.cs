// @author bdth 2074055628@qq.com
// 文件用途 只读查询游戏在实际渲染显卡上的显存预算与占用 走用户态 WDDM 接口
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

        // 占用贴着预算跑是动作的前提 预算读不到时一律当作不吃紧
        public double UsageShare
        {
            get { return Budget > 0 ? (double)CurrentUsage / Budget : 0; }
        }
    }

    // 适配器句柄有寿命 TDR 之后旧句柄失效 必须重开
    //   句柄只在需要时开 用完即关 不常驻 避免驱动重置时留下不可用的引用
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

        // hProcess 传目标进程句柄 传 IntPtr.Zero 则查询本进程
        //   句柄要 PROCESS_QUERY_LIMITED_INFORMATION 反作弊拒绝时调用方直接跳过
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

        // 预留是给显存管理器的提示 不是把显存锁死
        //   写 0 即撤销 返回 false 时调用方仍要当作"可能已经写进去了"走还原路径
        //   hProcess 在头文件里类型是 UINT64 但字段名带 h 前缀 按进程句柄传
        //     写完必须回读 CurrentReservation 核实 不能只看返回值
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
