// @author bdth 2074055628@qq.com
// 文件用途 NVIDIA NVML 只读遥测封装 驱动缺失时整体降级

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AegisApp
{
    internal static class Nvml
    {
        // nvmlClocksThrottleReasons 位掩码（NVML 公开头文件定义，跨版本稳定）
        public const ulong ReasonSwPowerCap = 0x4;
        public const ulong ReasonHwSlowdown = 0x8;
        public const ulong ReasonSwThermal = 0x20;
        public const ulong ReasonHwThermal = 0x40;
        public const ulong ReasonHwPowerBrake = 0x80;

        private static int state; // 0 未探测 1 可用 -1 不可用
        private static IntPtr device;

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        private static extern int NvmlInit();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
        private static extern int NvmlDeviceGetCount(out uint count);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr handle);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
        private static extern int NvmlDeviceGetUtilizationRates(IntPtr handle, out Utilization util);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
        private static extern int NvmlDeviceGetTemperature(IntPtr handle, int sensor, out uint value);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")]
        private static extern int NvmlDeviceGetCurrentClocksThrottleReasons(IntPtr handle, out ulong reasons);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization { public uint Gpu; public uint Memory; }

        public static bool Available
        {
            get
            {
                int known = Volatile.Read(ref state);
                if (known != 0) return known > 0;
                bool ok = Probe();
                Interlocked.CompareExchange(ref state, ok ? 1 : -1, 0);
                return Volatile.Read(ref state) > 0;
            }
        }

        private static bool Probe()
        {
            // 新驱动把 nvml.dll 装进 System32，老驱动在 NVSMI 目录；先按默认搜索，失败再显式加载
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (NvmlInit() != 0) return false;
                    uint count;
                    if (NvmlDeviceGetCount(out count) != 0 || count == 0) return false;
                    IntPtr h;
                    if (NvmlDeviceGetHandleByIndex(0, out h) != 0) return false;
                    device = h;
                    return true;
                }
                catch (DllNotFoundException)
                {
                    if (attempt > 0) return false;
                    try
                    {
                        string nvsmi = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                            @"NVIDIA Corporation\NVSMI\nvml.dll");
                        if (LoadLibrary(nvsmi) == IntPtr.Zero) return false;
                    }
                    catch { return false; }
                }
                catch { return false; }
            }
            return false;
        }

        public static bool TrySample(out int gpuUtil, out int tempC, out ulong throttleReasons)
        {
            gpuUtil = 0; tempC = 0; throttleReasons = 0;
            if (!Available) return false;
            try
            {
                Utilization util;
                if (NvmlDeviceGetUtilizationRates(device, out util) != 0) return false;
                uint temp;
                if (NvmlDeviceGetTemperature(device, 0, out temp) != 0) return false;
                ulong reasons;
                if (NvmlDeviceGetCurrentClocksThrottleReasons(device, out reasons) != 0) reasons = 0;
                gpuUtil = (int)util.Gpu;
                tempC = (int)temp;
                throttleReasons = reasons;
                return true;
            }
            catch { return false; }
        }
    }
}
