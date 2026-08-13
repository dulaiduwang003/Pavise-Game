// @author bdth 2074055628@qq.com
// 文件用途 USB 控制器中断亲和 把高回报率鼠标等 HID 的 DPC/ISR 引离游戏核心
// 保留理由 1000Hz 鼠标每秒上千次中断 直接压在输入延迟路径上 频率与位置都比读盘中断重要得多
// 同机制的硬盘中断避让已在 1.7.0.1 移除 因为读盘中断不在出帧与输入路径上 两者不可类比
// 默认关闭 开启前必须弹窗说明风险 同构机器上掩码就是尾部两个逻辑核 与后台压制落点重合
// 这一个物理核空闲时会进深度睡眠 真机上出现过放着不动一会儿后第一下点击发木

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

        public static bool HasResidue { get { return irqEngine.HasResidue; } }

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
            catch (Exception ex) { Logger.Log("枚举 USB 控制器失败 " + ex.Message); }
            return ids;
        }

        public static bool Enable()
        {
            return irqEngine.Enable(EnumerateUsbControllerIds(), CpuTopology.InterruptMask);
        }

        public static bool Disable()
        {
            return irqEngine.Disable(EnumerateUsbControllerIds());
        }

        public static bool HealStaleMask()
        {
            return irqEngine.HealStaleMask();
        }
    }
}
