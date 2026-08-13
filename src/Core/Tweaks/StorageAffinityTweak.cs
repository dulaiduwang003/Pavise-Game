// @author bdth 2074055628@qq.com
// 文件用途 硬盘控制器中断亲和 把读盘完成中断的 DPC/ISR 引离游戏核心

using System;
using System.Collections.Generic;
using System.Management;

namespace PaviseApp
{
    internal static class StorageAffinityTweak
    {
        private static readonly IrqAffinityEngine irqEngine =
            new IrqAffinityEngine("StorAffinityOnByPavise", "StorAff_", "硬盘控制器中断亲和");

        public static bool EnabledByPavise { get { return irqEngine.EnabledByPavise; } }

        internal static List<string> EnumerateStorageControllerIds()
        {
            var ids = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID, Status FROM Win32_PnPEntity WHERE PNPClass='SCSIAdapter' OR PNPClass='hdc'"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        using (mo)
                        {
                            string id = mo["PNPDeviceID"] as string;
                            string status = mo["Status"] as string;
                            if (string.IsNullOrEmpty(id)) continue;
                            if (!string.IsNullOrEmpty(status) && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!ids.Contains(id)) ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Log("枚举硬盘控制器失败 " + ex.Message); }
            return ids;
        }

        public static bool Enable()
        {
            return irqEngine.Enable(EnumerateStorageControllerIds(), CpuTopology.InterruptMask);
        }

        public static bool Disable()
        {
            return irqEngine.Disable(EnumerateStorageControllerIds());
        }
    }
}
