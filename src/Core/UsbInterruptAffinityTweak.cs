// @author bdth 2074055628@qq.com
// 文件用途 USB 控制器中断亲和 把高回报率鼠标等 HID 的 DPC/ISR 引离游戏核心

using System;
using System.Collections.Generic;
using System.Management;

namespace AegisApp
{
    // 4K/8K 回报率鼠标每秒产生数千次中断，DPC 堆积在游戏主线程所在核心会造成
    // 甩枪掉帧与爆音（社区大量实证）。把 XHCI 控制器的中断亲和引到后台核心分区，
    // 与显卡（贴近游戏核）方向相反。
    internal static class UsbInterruptAffinityTweak
    {
        private static readonly IrqAffinityEngine irqEngine =
            new IrqAffinityEngine("UsbAffinityOnByAegis", "UsbAff_", "USB 控制器中断亲和");

        public static bool EnabledByAegis { get { return irqEngine.EnabledByAegis; } }

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
                            // 只收 PCI 真实控制器；ROOT\ 前缀的虚拟设备（如远程工具）排除
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
