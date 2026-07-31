// @author bdth 2074055628@qq.com
// 文件用途 引导 GPU 中断亲和策略靠近游戏所在核心 开启 关闭并恢复

using System;
using System.Collections.Generic;
using System.Management;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class InterruptAffinityTweak
    {
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("IrqAffinityOnByAegis", "IrqAff_", "中断亲和优化");

        public static bool EnabledByAegis { get { return engine.EnabledByAegis; } }

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
                            ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Log("枚举显卡设备失败：" + ex.Message); }
            return ids;
        }

        public static bool Enable() { return engine.Enable(EnumerateGpuDeviceIds()); }

        public static bool Disable() { return engine.Disable(EnumerateGpuDeviceIds()); }

#if AEGIS_SELFTEST
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
