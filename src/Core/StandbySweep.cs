// @author bdth 2074055628@qq.com
// 文件用途 对局中在内存压力到阈值时清理低优先级待机内存页 带冷却防抖
// 只清低优先级页(不清整个待机列表)代价小;可用物理内存充裕时完全不动作 高内存机几乎从不触发
// 触发条件=可用内存低于阈值 且距上次清理已过冷却期 冷却是关键:没有它会形成"清空→读盘→再堆积"的反复抖动 比微卡更糟
// 收益面:8~16G 内存、吃紧时的帧时间一致性(社区 ISLC 类工具的验证场景)32G+ 且占用不高的机器无感

using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class StandbySweep
    {
        private const int SystemMemoryListInformation = 0x50;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        // 可用物理内存低于总量的这个比例才视为有压力 才清
        private const double PressureFreeRatio = 0.15;
        // 两次清理最小间隔 防止反复触发拖累
        private static readonly long CooldownTicks = TimeSpan.FromSeconds(45).Ticks;

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile,
                TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        private static long nextAllowedTicks;

        // 新对局开始时调用:允许开局若已吃紧立刻清一次 不受上一局冷却牵连
        public static void ResetCooldown() { nextAllowedTicks = 0; }

        // 每 tick 可调:自带阈值与冷却判定 内存充裕或冷却未到则直接返回 极廉价(一次 GlobalMemoryStatusEx)
        public static bool MaybePurge()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now < nextAllowedTicks) return false;
            double freeRatio;
            if (!TryFreeRatio(out freeRatio) || freeRatio > PressureFreeRatio) return false;
            nextAllowedTicks = now + CooldownTicks;
            return PurgeOnce();
        }

        private static bool TryFreeRatio(out double ratio)
        {
            ratio = 1.0;
            var status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return false;
            ratio = status.AvailPhys / (double)status.TotalPhys;
            return true;
        }

        private static bool PurgeOnce()
        {
            try
            {
                if (!Native.EnsureProfilePrivilege())
                {
                    Logger.Log("待机内存清理 SeProfileSingleProcessPrivilege 不可用 已跳过");
                    return false;
                }
                int command = MemoryPurgeLowPriorityStandbyList;
                int status = NtSetSystemInformation(
                    SystemMemoryListInformation, ref command, sizeof(int));
                if (status != 0)
                {
                    Logger.Log("低优先级待机内存清理失败 NTSTATUS 0x" + status.ToString("X8"));
                    return false;
                }
                Logger.Log("内存吃紧 已清理低优先级待机内存页 缓解帧时间抖动");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("待机内存清理异常 " + ex.Message);
                return false;
            }
        }
    }
}
