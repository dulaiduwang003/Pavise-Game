// @author bdth 2074055628@qq.com
// 文件用途 检测 ReBAR 是否开启 读取独显 PCI 已分配的最大显存直通窗口 无驱动依赖

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static class RebarProbe
    {
        private const uint DigcfPresent = 0x00000002;
        private const uint AllocLogConf = 0x00000002;
        private const uint ResTypeMem = 0x00000001;
        private const uint ResTypeMemLarge = 0x00000007;
        private const uint SpdrpDeviceDesc = 0x00000000;
        private const int CrSuccess = 0;
        private const ulong EnabledThreshold = 512UL * 1024 * 1024;

        private static Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr devInfo, uint index, ref SpDevinfoData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devInfo, ref SpDevinfoData data,
            StringBuilder instanceId, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr devInfo, ref SpDevinfoData data,
            uint property, out uint dataType, StringBuilder buffer, uint size, out uint required);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_First_Log_Conf(out IntPtr logConf, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Next_Res_Des(out IntPtr next, IntPtr current, uint forResource,
            IntPtr resourceIdOut, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Res_Des_Data_Size(out uint size, IntPtr resDes, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Res_Des_Data(IntPtr resDes, byte[] buffer, uint length, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Free_Res_Des_Handle(IntPtr resDes);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Free_Log_Conf_Handle(IntPtr logConf);

        public static bool TryDetect(out bool enabled, out ulong windowBytes, out string gpuName)
        {
            enabled = false; windowBytes = 0; gpuName = null;
            IntPtr devInfo = IntPtr.Zero;
            try
            {
                devInfo = SetupDiGetClassDevsW(ref DisplayClass, null, IntPtr.Zero, DigcfPresent);
                if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) return false;
                var data = new SpDevinfoData();
                data.Size = (uint)Marshal.SizeOf(typeof(SpDevinfoData));
                for (uint index = 0; SetupDiEnumDeviceInfo(devInfo, index, ref data); index++)
                {
                    var id = new StringBuilder(512);
                    uint required;
                    if (!SetupDiGetDeviceInstanceIdW(devInfo, ref data, id, 512, out required)) continue;
                    string instance = id.ToString();
                    if (!IsDiscretePciGpu(instance)) continue;
                    ulong window = LargestMemoryWindow(data.DevInst);
                    if (window <= windowBytes) continue;
                    windowBytes = window;
                    var desc = new StringBuilder(256);
                    uint dataType;
                    gpuName = SetupDiGetDeviceRegistryPropertyW(devInfo, ref data,
                        SpdrpDeviceDesc, out dataType, desc, 256, out required)
                        ? desc.ToString() : null;
                }
                enabled = EnabledFromWindow(windowBytes);
                return windowBytes > 0;
            }
            catch { return false; }
            finally
            {
                if (devInfo != IntPtr.Zero && devInfo != new IntPtr(-1))
                    try { SetupDiDestroyDeviceInfoList(devInfo); } catch { }
            }
        }

        internal static bool IsDiscretePciGpu(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            if (!instanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) return false;
            return instanceId.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0
                || instanceId.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool EnabledFromWindow(ulong bytes)
        {
            return bytes >= EnabledThreshold;
        }

        public static string WindowText(ulong bytes)
        {
            if (bytes >= 1024UL * 1024 * 1024)
                return (bytes / (1024.0 * 1024 * 1024)).ToString("0.#") + " GB";
            if (bytes >= 1024UL * 1024)
                return (bytes / (1024.0 * 1024)).ToString("0") + " MB";
            return bytes + " B";
        }

        private static ulong LargestMemoryWindow(uint devInst)
        {
            IntPtr logConf;
            if (CM_Get_First_Log_Conf(out logConf, devInst, AllocLogConf) != CrSuccess || logConf == IntPtr.Zero)
                return 0;
            try
            {
                ulong best = 0;
                foreach (uint resType in new[] { ResTypeMem, ResTypeMemLarge })
                {
                    IntPtr current = logConf;
                    bool currentIsLogConf = true;
                    while (true)
                    {
                        IntPtr next;
                        int status = CM_Get_Next_Res_Des(out next, current, resType, IntPtr.Zero, 0);
                        if (!currentIsLogConf) try { CM_Free_Res_Des_Handle(current); } catch { }
                        if (status != CrSuccess || next == IntPtr.Zero) break;
                        current = next;
                        currentIsLogConf = false;
                        ulong size = ReadRangeSize(next);
                        if (size > best) best = size;
                    }
                }
                return best;
            }
            finally { try { CM_Free_Log_Conf_Handle(logConf); } catch { } }
        }

        private static ulong ReadRangeSize(IntPtr resDes)
        {
            try
            {
                uint size;
                if (CM_Get_Res_Des_Data_Size(out size, resDes, 0) != CrSuccess || size < 24) return 0;
                var buffer = new byte[size];
                if (CM_Get_Res_Des_Data(resDes, buffer, size, 0) != CrSuccess) return 0;
                ulong start = BitConverter.ToUInt64(buffer, 8);
                ulong end = BitConverter.ToUInt64(buffer, 16);
                if (end <= start) return 0;
                return end - start + 1;
            }
            catch { return 0; }
        }
    }
}
