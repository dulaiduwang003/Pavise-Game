// @author bdth 2074055628@qq.com
// 文件用途 引导 GPU 中断亲和策略靠近游戏所在核心 开启 关闭并恢复

using System;
using System.Collections.Generic;
using System.Management;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class InterruptAffinityTweak
    {
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("IrqAffinityOnByPavise", "IrqAff_", Lang.T("t.interruptaffinitytweak.1"));

        public static bool EnabledByPavise { get { return engine.EnabledByPavise; } }

        internal static byte[] MaskToBytes(ulong mask) { return IrqAffinityEngine.MaskToBytes(mask); }
        internal static ulong BytesToMask(byte[] b) { return IrqAffinityEngine.BytesToMask(b); }

        internal static List<string> EnumerateGpuDeviceIds()
        {
            var ids = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID, Status FROM Win32_VideoController"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        using (mo)
                        {
                            object idObj = mo["PNPDeviceID"];
                            object statusObj = mo["Status"];
                            string id = idObj as string;
                            string status = statusObj as string;
                            if (string.IsNullOrEmpty(id)) continue;
                            if (!string.IsNullOrEmpty(status) && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;
                            if (GpuInventory.IsIntegratedDevice(id)) continue;
                            ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Log(Lang.T("log.interruptaffinitytweak.2") + ex.Message); }
            return ids;
        }

        // 混合架构传 P 核顶端掩码 让"绑定游戏核心"名副其实 否则 BoostMask=全核时引擎只会退回就近处理
        public static bool Enable()
        {
            return engine.Enable(EnumerateGpuDeviceIds(), CpuTopology.GpuInterruptPreferredMask());
        }

        public static bool Disable() { return engine.Disable(EnumerateGpuDeviceIds()); }

        public static bool HealStaleMask() { return engine.HealStaleMask(); }

        public static bool ResyncMask()
        {
            return engine.EnabledByPavise
                && engine.ResyncMask(EnumerateGpuDeviceIds(), CpuTopology.GpuInterruptPreferredMask());
        }

#if PAVISE_SELFTEST
        internal static bool RestartDevice(string pnpDeviceId, out string error)
        {
            error = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID='"
                    + pnpDeviceId.Replace(@"\", @"\\").Replace("'", @"\'") + "'"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        using (mo)
                        {
                            ManagementBaseObject disableResult = mo.InvokeMethod("Disable", null, null);
                            uint disableCode = disableResult != null ? Convert.ToUInt32(disableResult["ReturnValue"]) : 999;
                            System.Threading.Thread.Sleep(800);
                            ManagementBaseObject enableResult = mo.InvokeMethod("Enable", null, null);
                            uint enableCode = enableResult != null ? Convert.ToUInt32(enableResult["ReturnValue"]) : 999;
                            if (disableCode != 0 || enableCode != 0)
                            {
                                error = "disable=" + disableCode + " enable=" + enableCode;
                                return false;
                            }
                            return true;
                        }
                    }
                }
                error = "device not found";
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
#endif
    }
}
