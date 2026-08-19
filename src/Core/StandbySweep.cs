// @author bdth 2074055628@qq.com
// 文件用途 对局中在内存压力到阈值时清理低优先级待机内存页 带冷却防抖
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class StandbySweep
    {
        private const int SystemMemoryListInformation = 0x50;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        private const double PressureFreeRatio = 0.15;
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

        public static void ResetCooldown() { nextAllowedTicks = 0; }

        public static bool MaybePurge()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now < nextAllowedTicks) return false;
            double freeRatio;
            if (!TryFreeRatio(out freeRatio) || freeRatio > PressureFreeRatio) return false;
            nextAllowedTicks = now + CooldownTicks;
            return PurgeOnce();
        }

        internal static bool FreeRatio(out double ratio) { return TryFreeRatio(out ratio); }

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
                    Logger.Log(Lang.T("log.standbysweep.1"));
                    return false;
                }
                int command = MemoryPurgeLowPriorityStandbyList;
                int status = NtSetSystemInformation(
                    SystemMemoryListInformation, ref command, sizeof(int));
                if (status != 0)
                {
                    Logger.Log(Lang.T("log.standbysweep.2") + status.ToString("X8"));
                    return false;
                }
                Logger.Log(Lang.T("log.standbysweep.3"));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log(Lang.T("log.standbysweep.4") + ex.Message);
                return false;
            }
        }
    }
}
