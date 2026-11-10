using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static class RebarProbe
    {
        private struct SpDevinfoData
        {
            public uint Size;

            public Guid ClassGuid;

            public uint DevInst;

            public IntPtr Reserved;
        }

        private static Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr devInfo, uint index, ref SpDevinfoData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devInfo, ref SpDevinfoData data, StringBuilder instanceId, uint size, out uint required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr devInfo, ref SpDevinfoData data, uint property, out uint dataType, StringBuilder buffer, uint size, out uint required);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_First_Log_Conf(out IntPtr logConf, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Next_Res_Des(out IntPtr next, IntPtr current, uint forResource, IntPtr resourceIdOut, uint flags);

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
            bool nvidia;
            return TryDetect(out enabled, out windowBytes, out gpuName, out nvidia);
        }

        public static bool TryDetect(out bool enabled, out ulong windowBytes, out string gpuName, out bool nvidia)
        {
            return TryDetect(false, out enabled, out windowBytes, out gpuName, out nvidia);
        }

        internal static string NvidiaEnableBlockReason()
        {
            try
            {
                if (!NvApi.Available)
                {
                    return "lib.rebar.driver";
                }
                bool enabled;
                ulong windowBytes;
                string gpuName;
                bool nvidia;
                bool read = TryDetect(true, out enabled, out windowBytes, out gpuName, out nvidia);
                return EnableBlockReason(true, read, nvidia, enabled);
            }
            catch
            {
                return "lib.rebar.unknown";
            }
        }

        internal static string EnableBlockReason(bool driver, bool read, bool nvidia, bool enabled)
        {
            if (!driver)
            {
                return "lib.rebar.driver";
            }
            if (!read || !nvidia)
            {
                return "lib.rebar.unknown";
            }
            if (!enabled)
            {
                return "lib.rebar.bios";
            }
            return null;
        }

        private static bool TryDetect(bool nvidiaOnly, out bool enabled, out ulong windowBytes, out string gpuName, out bool nvidia)
        {
            enabled = false;
            windowBytes = 0uL;
            gpuName = null;
            nvidia = false;
            IntPtr deviceInfoSet = IntPtr.Zero;
            try
            {
                deviceInfoSet = SetupDiGetClassDevsW(ref DisplayClass, null, IntPtr.Zero, 2u);
                if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
                {
                    return false;
                }
                SpDevinfoData data = default(SpDevinfoData);
                data.Size = (uint)Marshal.SizeOf(typeof(SpDevinfoData));
                for (uint iterationIndex = 0u; SetupDiEnumDeviceInfo(deviceInfoSet, iterationIndex, ref data); iterationIndex++)
                {
                    StringBuilder stringBuilder = new StringBuilder(512);
                    uint required;
                    if (!SetupDiGetDeviceInstanceIdW(deviceInfoSet, ref data, stringBuilder, 512u, out required))
                    {
                        continue;
                    }
                    string stringBuilderText = stringBuilder.ToString();
                    if (IsDiscretePciGpu(stringBuilderText) && (!nvidiaOnly || stringBuilderText.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0) && !GpuInventory.IsIntegratedDevice(stringBuilderText))
                    {
                        ulong largestMemoryWindowResult = LargestMemoryWindow(data.DevInst);
                        if (largestMemoryWindowResult > windowBytes)
                        {
                            windowBytes = largestMemoryWindowResult;
                            nvidia = stringBuilderText.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0;
                            StringBuilder currentStringBuilder = new StringBuilder(256);
                            uint dataType;
                            gpuName = (SetupDiGetDeviceRegistryPropertyW(deviceInfoSet, ref data, 0u, out dataType, currentStringBuilder, 256u, out required) ? currentStringBuilder.ToString() : null);
                        }
                    }
                }
                enabled = EnabledFromWindow(windowBytes);
                return windowBytes != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (deviceInfoSet != IntPtr.Zero && deviceInfoSet != new IntPtr(-1))
                {
                    try
                    {
                        SetupDiDestroyDeviceInfoList(deviceInfoSet);
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal static bool IsDiscretePciGpu(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return false;
            }
            if (!instanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (instanceId.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return instanceId.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return true;
        }

        internal static bool EnabledFromWindow(ulong bytes)
        {
            return bytes >= 536870912;
        }

        public static string WindowText(ulong bytes)
        {
            if (bytes >= 1073741824)
            {
                return ((double)bytes / 1073741824.0).ToString("0.#") + " GB";
            }
            if (bytes >= 1048576)
            {
                return ((double)bytes / 1048576.0).ToString("0") + " MB";
            }
            return bytes + " B";
        }

        private static ulong LargestMemoryWindow(uint devInst)
        {
            IntPtr logConf;
            if (CM_Get_First_Log_Conf(out logConf, devInst, 2u) != 0 || logConf == IntPtr.Zero)
            {
                return 0uL;
            }
            try
            {
                ulong largestMemoryWindowResult = 0uL;
                foreach (uint forResource in new uint[2] { 1u, 7u })
                {
                    IntPtr logConfHandle = logConf;
                    bool firstWindow = true;
                    while (true)
                    {
                        IntPtr next;
                        int cMGetNextResDesResult = CM_Get_Next_Res_Des(out next, logConfHandle, forResource, IntPtr.Zero, 0u);
                        if (!firstWindow)
                        {
                            try
                            {
                                CM_Free_Res_Des_Handle(logConfHandle);
                            }
                            catch
                            {
                            }
                        }
                        if (cMGetNextResDesResult != 0 || next == IntPtr.Zero)
                        {
                            break;
                        }
                        logConfHandle = next;
                        firstWindow = false;
                        ulong rangeSize = ReadRangeSize(next);
                        if (rangeSize > largestMemoryWindowResult)
                        {
                            largestMemoryWindowResult = rangeSize;
                        }
                    }
                }
                return largestMemoryWindowResult;
            }
            finally
            {
                try
                {
                    CM_Free_Log_Conf_Handle(logConf);
                }
                catch
                {
                }
            }
        }

        private static ulong ReadRangeSize(IntPtr resDes)
        {
            try
            {
                uint size;
                if (CM_Get_Res_Des_Data_Size(out size, resDes, 0u) != 0 || size < 24)
                {
                    return 0uL;
                }
                byte[] rawBytes = new byte[size];
                if (CM_Get_Res_Des_Data(resDes, rawBytes, size, 0u) != 0)
                {
                    return 0uL;
                }
                ulong toUInt64Result = BitConverter.ToUInt64(rawBytes, 8);
                ulong toUInt64Result2 = BitConverter.ToUInt64(rawBytes, 16);
                if (toUInt64Result2 <= toUInt64Result)
                {
                    return 0uL;
                }
                return toUInt64Result2 - toUInt64Result + 1;
            }
            catch
            {
                return 0uL;
            }
        }
    }
}
