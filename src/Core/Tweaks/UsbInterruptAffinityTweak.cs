// @author bdth 2074055628@qq.com
// 文件用途 USB 控制器中断亲和 把高回报率鼠标等 HID 的 DPC/ISR 引离游戏核心

using System;
using System.Collections.Generic;
using System.Management;

namespace PaviseApp
{
    internal static class UsbInterruptAffinityTweak
    {
        private static readonly IrqAffinityEngine irqEngine =
            new IrqAffinityEngine("UsbAffinityOnByPavise", "UsbAff_", "USB 控制器中断亲和");

        public static bool EnabledByPavise { get { return irqEngine.EnabledByPavise; } }

        internal static List<string> EnumerateUsbControllerIds()
        {
            var ids = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID FROM Win32_USBController"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        using (mo)
                        {
                            string id = mo["PNPDeviceID"] as string;
                            if (string.IsNullOrEmpty(id)) continue;
                            if (!id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;
                            ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Log("枚举 USB 控制器失败：" + ex.Message); }
            return ids;
        }

        public static bool Enable()
        {
            return irqEngine.Enable(EnumerateUsbControllerIds(), CpuTopology.ThrottleMask);
        }

        public static bool Disable()
        {
            return irqEngine.Disable(EnumerateUsbControllerIds());
        }
    }
}
