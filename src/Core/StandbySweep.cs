// @author bdth 2074055628@qq.com
// 文件用途 对局建立时清理一次低优先级待机内存页
// 不清整个待机列表 两者的代价对比见 README 内存清理一节

using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class StandbySweep
    {
        private const int SystemMemoryListInformation = 0x50;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        public static bool PurgeOnce()
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
                Logger.Log("低优先级待机内存页已清理 对局前一次性");
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
