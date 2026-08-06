// @author bdth 2074055628@qq.com
// 文件用途 AMD ADLX 手写 vtable 互操作 无 SDK 依赖 驱动缺失时整体降级 按 1.5.0.124 头文件布局

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdlxIntRange
    {
        public int Min;
        public int Max;
        public int Step;
    }

    internal static class AdlxApi
    {
        private const ulong FullVersion = 0x000100050000007C;
        private const uint LoadSearchFlags = 0x1E00;

        public const int GpuTypeIntegrated = 1;
        public const int GpuTypeDiscrete = 2;

        private const int SysSlotGetGpus = 1;
        private const int SysSlotGet3DServices = 7;
        private const int SysSlotGetPerfServices = 9;

        private const int IfaceSlotRelease = 1;

        private const int ListSlotBegin = 5;
        private const int ListSlotEnd = 6;
        private const int GpuListSlotAt = 11;

        private const int GpuSlotVendorId = 3;
        private const int GpuSlotType = 5;
        private const int GpuSlotName = 7;

        private const int SvcSlotAntiLag = 3;
        private const int SvcSlotChill = 4;
        private const int SvcSlotImageSharpening = 6;
        private const int SvcSlotEnhancedSync = 7;
        private const int SvcSlotResetShaderCache = 15;

        private const int FeatSlotIsSupported = 3;
        private const int FeatSlotIsEnabled = 4;
        private const int ToggleSlotSetEnabled = 5;
        private const int ChillSlotGetFpsRange = 5;
        private const int ChillSlotGetMinFps = 6;
        private const int ChillSlotGetMaxFps = 7;
        private const int ChillSlotSetEnabled = 8;
        private const int ChillSlotSetMinFps = 9;
        private const int ChillSlotSetMaxFps = 10;
        private const int RisSlotGetRange = 5;
        private const int RisSlotGetSharpness = 6;
        private const int RisSlotSetEnabled = 7;
        private const int RisSlotSetSharpness = 8;
        private const int CacheSlotReset = 4;

        private const int PerfSlotCurrentGpuMetrics = 19;
        private const int MetricsSlotUsage = 4;
        private const int MetricsSlotClock = 5;
        private const int MetricsSlotTemperature = 7;
        private const int MetricsSlotPower = 9;
        private const int MetricsSlotVram = 12;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string name, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnInitialize(ulong version, out IntPtr system);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnTerminate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnIntProp(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint FnUIntProp(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnOutPtr(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGpuOutPtr(IntPtr self, IntPtr gpu, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnAtList(IntPtr self, uint location, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnOutByte(IntPtr self, out byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnInByte(IntPtr self, byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnOutInt(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnInInt(IntPtr self, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnOutDouble(IntPtr self, out double value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnOutRange(IntPtr self, out AdlxIntRange range);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnAction(IntPtr self);

        private static readonly object lk = new object();
        private static int state;
        private static IntPtr system;
        private static FnTerminate terminate;

        public static bool Succeeded(int result)
        {
            return result >= 0 && result <= 2;
        }

        public static bool Available
        {
            get
            {
                lock (lk)
                {
                    if (state != 0) return state > 0;
                    state = Probe() ? 1 : -1;
                    return state > 0;
                }
            }
        }

        private static bool Probe()
        {
            try
            {
                IntPtr module = LoadLibraryExW("amdadlx64.dll", IntPtr.Zero, LoadSearchFlags);
                if (module == IntPtr.Zero) return false;
                IntPtr pInit = GetProcAddress(module, "ADLXInitialize");
                IntPtr pTerm = GetProcAddress(module, "ADLXTerminate");
                if (pInit == IntPtr.Zero || pTerm == IntPtr.Zero) return false;
                var init = (FnInitialize)Marshal.GetDelegateForFunctionPointer(pInit, typeof(FnInitialize));
                terminate = (FnTerminate)Marshal.GetDelegateForFunctionPointer(pTerm, typeof(FnTerminate));
                IntPtr sys;
                int result = init(FullVersion, out sys);
                if (!Succeeded(result) || sys == IntPtr.Zero)
                {
                    Logger.Log("ADLX 初始化失败 (ADLX_RESULT " + result + ")，AMD 调优不可用");
                    return false;
                }
                system = sys;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                return true;
            }
            catch { return false; }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            try { if (terminate != null) terminate(); } catch { }
        }

        private static T VMethod<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        public static void Release(IntPtr iface)
        {
            if (iface == IntPtr.Zero) return;
            try { VMethod<FnIntProp>(iface, IfaceSlotRelease)(iface); } catch { }
        }

        public static IntPtr[] GetGpus()
        {
            if (!Available) return null;
            try
            {
                IntPtr list;
                if (!Succeeded(VMethod<FnOutPtr>(system, SysSlotGetGpus)(system, out list)) || list == IntPtr.Zero)
                    return null;
                try
                {
                    uint begin = VMethod<FnUIntProp>(list, ListSlotBegin)(list);
                    uint end = VMethod<FnUIntProp>(list, ListSlotEnd)(list);
                    if (end <= begin || end - begin > 16) return new IntPtr[0];
                    var at = VMethod<FnAtList>(list, GpuListSlotAt);
                    var gpus = new System.Collections.Generic.List<IntPtr>();
                    for (uint i = begin; i < end; i++)
                    {
                        IntPtr gpu;
                        if (Succeeded(at(list, i, out gpu)) && gpu != IntPtr.Zero) gpus.Add(gpu);
                    }
                    return gpus.ToArray();
                }
                finally { Release(list); }
            }
            catch { return null; }
        }

        public static void ReleaseAll(IntPtr[] interfaces)
        {
            if (interfaces == null) return;
            foreach (IntPtr p in interfaces) Release(p);
        }

        public static string GpuName(IntPtr gpu)
        {
            try
            {
                IntPtr text;
                if (!Succeeded(VMethod<FnOutPtr>(gpu, GpuSlotName)(gpu, out text)) || text == IntPtr.Zero)
                    return null;
                return Marshal.PtrToStringAnsi(text);
            }
            catch { return null; }
        }

        public static int GpuType(IntPtr gpu)
        {
            try
            {
                int type;
                return Succeeded(VMethod<FnOutInt>(gpu, GpuSlotType)(gpu, out type)) ? type : 0;
            }
            catch { return 0; }
        }

        public static string GpuVendor(IntPtr gpu)
        {
            try
            {
                IntPtr text;
                if (!Succeeded(VMethod<FnOutPtr>(gpu, GpuSlotVendorId)(gpu, out text)) || text == IntPtr.Zero)
                    return null;
                return Marshal.PtrToStringAnsi(text);
            }
            catch { return null; }
        }

        private static IntPtr Get3DServices()
        {
            try
            {
                IntPtr services;
                if (!Succeeded(VMethod<FnOutPtr>(system, SysSlotGet3DServices)(system, out services)))
                    return IntPtr.Zero;
                return services;
            }
            catch { return IntPtr.Zero; }
        }

        private static IntPtr GetFeature(int serviceSlot, IntPtr gpu)
        {
            if (!Available) return IntPtr.Zero;
            IntPtr services = Get3DServices();
            if (services == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr feature;
                if (!Succeeded(VMethod<FnGpuOutPtr>(services, serviceSlot)(services, gpu, out feature)))
                    return IntPtr.Zero;
                return feature;
            }
            catch { return IntPtr.Zero; }
            finally { Release(services); }
        }

        private static bool FeatureFlags(IntPtr feature, out bool supported, out bool enabled)
        {
            supported = false; enabled = false;
            try
            {
                byte rawSupported, rawEnabled;
                if (!Succeeded(VMethod<FnOutByte>(feature, FeatSlotIsSupported)(feature, out rawSupported)))
                    return false;
                supported = rawSupported != 0;
                if (!supported) return true;
                if (!Succeeded(VMethod<FnOutByte>(feature, FeatSlotIsEnabled)(feature, out rawEnabled)))
                    return false;
                enabled = rawEnabled != 0;
                return true;
            }
            catch { return false; }
        }

        public static bool AntiLagGet(IntPtr gpu, out bool supported, out bool enabled)
        {
            supported = false; enabled = false;
            IntPtr feature = GetFeature(SvcSlotAntiLag, gpu);
            if (feature == IntPtr.Zero) return false;
            try { return FeatureFlags(feature, out supported, out enabled); }
            finally { Release(feature); }
        }

        public static bool AntiLagSet(IntPtr gpu, bool on)
        {
            IntPtr feature = GetFeature(SvcSlotAntiLag, gpu);
            if (feature == IntPtr.Zero) return false;
            try { return Succeeded(VMethod<FnInByte>(feature, ToggleSlotSetEnabled)(feature, on ? (byte)1 : (byte)0)); }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool EnhancedSyncGet(IntPtr gpu, out bool supported, out bool enabled)
        {
            supported = false; enabled = false;
            IntPtr feature = GetFeature(SvcSlotEnhancedSync, gpu);
            if (feature == IntPtr.Zero) return false;
            try { return FeatureFlags(feature, out supported, out enabled); }
            finally { Release(feature); }
        }

        public static bool EnhancedSyncSet(IntPtr gpu, bool on)
        {
            IntPtr feature = GetFeature(SvcSlotEnhancedSync, gpu);
            if (feature == IntPtr.Zero) return false;
            try { return Succeeded(VMethod<FnInByte>(feature, ToggleSlotSetEnabled)(feature, on ? (byte)1 : (byte)0)); }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool ChillGet(IntPtr gpu, out bool supported, out bool enabled,
            out int minFps, out int maxFps, out AdlxIntRange range)
        {
            supported = false; enabled = false; minFps = 0; maxFps = 0; range = new AdlxIntRange();
            IntPtr feature = GetFeature(SvcSlotChill, gpu);
            if (feature == IntPtr.Zero) return false;
            try
            {
                if (!FeatureFlags(feature, out supported, out enabled)) return false;
                if (!supported) return true;
                if (!Succeeded(VMethod<FnOutRange>(feature, ChillSlotGetFpsRange)(feature, out range))) return false;
                if (!Succeeded(VMethod<FnOutInt>(feature, ChillSlotGetMinFps)(feature, out minFps))) return false;
                if (!Succeeded(VMethod<FnOutInt>(feature, ChillSlotGetMaxFps)(feature, out maxFps))) return false;
                return true;
            }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool ChillSet(IntPtr gpu, bool on, int minFps, int maxFps)
        {
            IntPtr feature = GetFeature(SvcSlotChill, gpu);
            if (feature == IntPtr.Zero) return false;
            try
            {
                if (on)
                {
                    if (!Succeeded(VMethod<FnInInt>(feature, ChillSlotSetMinFps)(feature, minFps))) return false;
                    if (!Succeeded(VMethod<FnInInt>(feature, ChillSlotSetMaxFps)(feature, maxFps))) return false;
                }
                return Succeeded(VMethod<FnInByte>(feature, ChillSlotSetEnabled)(feature, on ? (byte)1 : (byte)0));
            }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool RisGet(IntPtr gpu, out bool supported, out bool enabled,
            out int sharpness, out AdlxIntRange range)
        {
            supported = false; enabled = false; sharpness = 0; range = new AdlxIntRange();
            IntPtr feature = GetFeature(SvcSlotImageSharpening, gpu);
            if (feature == IntPtr.Zero) return false;
            try
            {
                if (!FeatureFlags(feature, out supported, out enabled)) return false;
                if (!supported) return true;
                if (!Succeeded(VMethod<FnOutRange>(feature, RisSlotGetRange)(feature, out range))) return false;
                if (!Succeeded(VMethod<FnOutInt>(feature, RisSlotGetSharpness)(feature, out sharpness))) return false;
                return true;
            }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool RisSet(IntPtr gpu, bool on, int sharpness)
        {
            IntPtr feature = GetFeature(SvcSlotImageSharpening, gpu);
            if (feature == IntPtr.Zero) return false;
            try
            {
                if (on && !Succeeded(VMethod<FnInInt>(feature, RisSlotSetSharpness)(feature, sharpness)))
                    return false;
                return Succeeded(VMethod<FnInByte>(feature, RisSlotSetEnabled)(feature, on ? (byte)1 : (byte)0));
            }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool ResetShaderCache(IntPtr gpu)
        {
            IntPtr feature = GetFeature(SvcSlotResetShaderCache, gpu);
            if (feature == IntPtr.Zero) return false;
            try
            {
                byte supported;
                if (!Succeeded(VMethod<FnOutByte>(feature, FeatSlotIsSupported)(feature, out supported))
                    || supported == 0) return false;
                return Succeeded(VMethod<FnAction>(feature, CacheSlotReset)(feature));
            }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool TryReadMetrics(IntPtr gpu, out double usage, out int clockMhz,
            out double temperature, out double powerWatts, out int vramMb)
        {
            usage = 0; clockMhz = 0; temperature = 0; powerWatts = 0; vramMb = 0;
            if (!Available) return false;
            try
            {
                IntPtr services;
                if (!Succeeded(VMethod<FnOutPtr>(system, SysSlotGetPerfServices)(system, out services))
                    || services == IntPtr.Zero) return false;
                try
                {
                    IntPtr metrics;
                    if (!Succeeded(VMethod<FnGpuOutPtr>(services, PerfSlotCurrentGpuMetrics)(services, gpu, out metrics))
                        || metrics == IntPtr.Zero) return false;
                    try
                    {
                        VMethod<FnOutDouble>(metrics, MetricsSlotUsage)(metrics, out usage);
                        VMethod<FnOutInt>(metrics, MetricsSlotClock)(metrics, out clockMhz);
                        VMethod<FnOutDouble>(metrics, MetricsSlotTemperature)(metrics, out temperature);
                        VMethod<FnOutDouble>(metrics, MetricsSlotPower)(metrics, out powerWatts);
                        VMethod<FnOutInt>(metrics, MetricsSlotVram)(metrics, out vramMb);
                        return true;
                    }
                    finally { Release(metrics); }
                }
                finally { Release(services); }
            }
            catch { return false; }
        }
    }
}
