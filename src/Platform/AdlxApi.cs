// @author bdth 2074055628@qq.com
// File purpose AMD ADLX hand-written vtable interop, no SDK dependency, degrades as a whole when the driver is missing, follows the 1.5.0.124 header layout
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
        private const int IfaceSlotQuery = 2;

        private const int ListSlotBegin = 5;
        private const int ListSlotEnd = 6;
        private const int GpuListSlotAt = 11;

        private const int GpuSlotVendorId = 3;
        private const int GpuSlotType = 5;
        private const int GpuSlotName = 7;
        private const int GpuSlotPnp = 9;

        private const int SysSlotGetTuningServices = 8;

        private const int SvcSlotAntiLag = 3;
        private const int SvcSlotChill = 4;
        private const int SvcSlotImageSharpening = 6;
        private const int SvcSlotEnhancedSync = 7;
        private const int SvcSlotFrtc = 9;
        private const int SvcSlotRsr = 14;
        private const int SvcSlotResetShaderCache = 15;
        private const int Svc1SlotAfmf = 17;

        private const int RsrSlotSetEnabled = 5;
        private const int RsrSlotGetSharpness = 7;
        private const int RsrSlotSetSharpness = 8;

        private const int TuneSlotIsSupportedManualPower = 11;
        private const int TuneSlotGetManualPower = 17;
        // Manual core clock tuning entries of IADLXGPUTuningServices, same vtable as above
        private const int TuneSlotIsSupportedManualGfx = 8;
        private const int TuneSlotGetManualGfx = 14;
        // IADLXGPUTuningServices1 appends GetSmartAccessMemory after the parent interface's 18 slots
        private const int Tune1SlotGetSam = 18;
        private const string TuningServices1Iid = "IADLXGPUTuningServices1";
        // GetManualGFXTuning returns the base interface, from RDNA2 on it must be QueryInterface'd to gen 2 to get the minimum frequency
        private const string ManualGfx2Iid = "IADLXManualGraphicsTuning2";
        private const int Gfx2SlotGetMinRange = 3;
        private const int Gfx2SlotGetMin = 4;
        private const int Gfx2SlotSetMin = 5;
        private const int Gfx2SlotGetMax = 7;
        private const int PowerSlotGetRange = 3;
        private const int PowerSlotGetLimit = 4;
        private const int PowerSlotSetLimit = 5;
        private const string Services1Iid = "IADLX3DSettingsServices1";

        private const int FeatSlotIsSupported = 3;
        private const int FeatSlotIsEnabled = 4;
        private const int ToggleSlotSetEnabled = 5;
        private const int ChillSlotGetFpsRange = 5;
        private const int ChillSlotGetMinFps = 6;
        private const int ChillSlotGetMaxFps = 7;
        private const int ChillSlotSetEnabled = 8;
        private const int ChillSlotSetMinFps = 9;
        private const int ChillSlotSetMaxFps = 10;
        private const int FrtcSlotGetFpsRange = 5;
        private const int FrtcSlotGetFps = 6;
        private const int FrtcSlotSetEnabled = 7;
        private const int FrtcSlotSetFps = 8;
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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnQueryIface(IntPtr self,
            [MarshalAs(UnmanagedType.LPWStr)] string interfaceId, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGpuOutByte(IntPtr self, IntPtr gpu, out byte value);

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
                    Logger.Warn(Lang.T("log.adlxapi.1") + result + Lang.T("log.adlxapi.2"));
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

        public static string GpuPnpString(IntPtr gpu)
        {
            try
            {
                IntPtr text;
                if (!Succeeded(VMethod<FnOutPtr>(gpu, GpuSlotPnp)(gpu, out text)) || text == IntPtr.Zero)
                    return null;
                return Marshal.PtrToStringAnsi(text);
            }
            catch { return null; }
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
                    bool ok = Succeeded(VMethod<FnInInt>(feature, ChillSlotSetMinFps)(feature, minFps))
                        && Succeeded(VMethod<FnInInt>(feature, ChillSlotSetMaxFps)(feature, maxFps));
                    if (!ok)
                        ok = Succeeded(VMethod<FnInInt>(feature, ChillSlotSetMaxFps)(feature, maxFps))
                            && Succeeded(VMethod<FnInInt>(feature, ChillSlotSetMinFps)(feature, minFps));
                    if (!ok) return false;
                }
                return Succeeded(VMethod<FnInByte>(feature, ChillSlotSetEnabled)(feature, on ? (byte)1 : (byte)0));
            }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool FrtcGet(IntPtr gpu, out bool supported, out bool enabled,
            out int fps, out AdlxIntRange range)
        {
            supported = false; enabled = false; fps = 0; range = new AdlxIntRange();
            IntPtr feature = GetFeature(SvcSlotFrtc, gpu);
            if (feature == IntPtr.Zero) return false;
            try
            {
                if (!FeatureFlags(feature, out supported, out enabled)) return false;
                if (!supported) return true;
                if (!Succeeded(VMethod<FnOutRange>(feature, FrtcSlotGetFpsRange)(feature, out range))) return false;
                if (!Succeeded(VMethod<FnOutInt>(feature, FrtcSlotGetFps)(feature, out fps))) return false;
                return true;
            }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool FrtcSet(IntPtr gpu, bool on, int fps)
        {
            IntPtr feature = GetFeature(SvcSlotFrtc, gpu);
            if (feature == IntPtr.Zero) return false;
            try
            {
                if (on && !Succeeded(VMethod<FnInInt>(feature, FrtcSlotSetFps)(feature, fps)))
                    return false;
                return Succeeded(VMethod<FnInByte>(feature, FrtcSlotSetEnabled)(feature, on ? (byte)1 : (byte)0));
            }
            catch { return false; }
            finally { Release(feature); }
        }

        private static IntPtr GetAfmf()
        {
            if (!Available) return IntPtr.Zero;
            IntPtr services = Get3DServices();
            if (services == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr services1;
                if (!Succeeded(VMethod<FnQueryIface>(services, IfaceSlotQuery)(services, Services1Iid, out services1))
                    || services1 == IntPtr.Zero) return IntPtr.Zero;
                try
                {
                    IntPtr feature;
                    if (!Succeeded(VMethod<FnOutPtr>(services1, Svc1SlotAfmf)(services1, out feature)))
                        return IntPtr.Zero;
                    return feature;
                }
                finally { Release(services1); }
            }
            catch { return IntPtr.Zero; }
            finally { Release(services); }
        }

        public static bool AfmfGet(out bool supported, out bool enabled)
        {
            supported = false; enabled = false;
            IntPtr feature = GetAfmf();
            if (feature == IntPtr.Zero) return false;
            try { return FeatureFlags(feature, out supported, out enabled); }
            finally { Release(feature); }
        }

        public static bool AfmfSet(bool on)
        {
            IntPtr feature = GetAfmf();
            if (feature == IntPtr.Zero) return false;
            try { return Succeeded(VMethod<FnInByte>(feature, ToggleSlotSetEnabled)(feature, on ? (byte)1 : (byte)0)); }
            catch { return false; }
            finally { Release(feature); }
        }

        private static IntPtr GetRsr()
        {
            if (!Available) return IntPtr.Zero;
            IntPtr services = Get3DServices();
            if (services == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr feature;
                if (!Succeeded(VMethod<FnOutPtr>(services, SvcSlotRsr)(services, out feature)))
                    return IntPtr.Zero;
                return feature;
            }
            catch { return IntPtr.Zero; }
            finally { Release(services); }
        }

        public static bool RsrGet(out bool supported, out bool enabled, out int sharpness)
        {
            supported = false; enabled = false; sharpness = 0;
            IntPtr feature = GetRsr();
            if (feature == IntPtr.Zero) return false;
            try
            {
                if (!FeatureFlags(feature, out supported, out enabled)) return false;
                if (!supported) return true;
                int raw;
                if (Succeeded(VMethod<FnOutInt>(feature, RsrSlotGetSharpness)(feature, out raw)))
                    sharpness = raw;
                return true;
            }
            catch { return false; }
            finally { Release(feature); }
        }

        public static bool RsrSet(bool on, int sharpness)
        {
            IntPtr feature = GetRsr();
            if (feature == IntPtr.Zero) return false;
            try
            {
                if (on && sharpness >= 0
                    && !Succeeded(VMethod<FnInInt>(feature, RsrSlotSetSharpness)(feature, sharpness)))
                    return false;
                return Succeeded(VMethod<FnInByte>(feature, RsrSlotSetEnabled)(feature, on ? (byte)1 : (byte)0));
            }
            catch { return false; }
            finally { Release(feature); }
        }

        private static IntPtr GetManualPowerTuning(IntPtr gpu, out bool supported)
        {
            supported = false;
            if (!Available) return IntPtr.Zero;
            IntPtr services;
            try
            {
                if (!Succeeded(VMethod<FnOutPtr>(system, SysSlotGetTuningServices)(system, out services))
                    || services == IntPtr.Zero) return IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
            try
            {
                byte raw;
                if (!Succeeded(VMethod<FnGpuOutByte>(services, TuneSlotIsSupportedManualPower)(services, gpu, out raw))
                    || raw == 0) return IntPtr.Zero;
                supported = true;
                IntPtr tuning;
                if (!Succeeded(VMethod<FnGpuOutPtr>(services, TuneSlotGetManualPower)(services, gpu, out tuning)))
                    return IntPtr.Zero;
                return tuning;
            }
            catch { return IntPtr.Zero; }
            finally { Release(services); }
        }

        private static IntPtr GetTuningServices()
        {
            if (!Available) return IntPtr.Zero;
            try
            {
                IntPtr services;
                if (!Succeeded(VMethod<FnOutPtr>(system, SysSlotGetTuningServices)(system, out services))
                    || services == IntPtr.Zero) return IntPtr.Zero;
                return services;
            }
            catch { return IntPtr.Zero; }
        }

        private static IntPtr GetManualGfxTuning2(IntPtr gpu, out bool supported)
        {
            supported = false;
            IntPtr services = GetTuningServices();
            if (services == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                byte raw;
                if (!Succeeded(VMethod<FnGpuOutByte>(services, TuneSlotIsSupportedManualGfx)(services, gpu, out raw))
                    || raw == 0) return IntPtr.Zero;
                IntPtr baseTuning;
                if (!Succeeded(VMethod<FnGpuOutPtr>(services, TuneSlotGetManualGfx)(services, gpu, out baseTuning))
                    || baseTuning == IntPtr.Zero) return IntPtr.Zero;
                try
                {
                    IntPtr tuning2;
                    if (!Succeeded(VMethod<FnQueryIface>(baseTuning, IfaceSlotQuery)(baseTuning, ManualGfx2Iid, out tuning2))
                        || tuning2 == IntPtr.Zero) return IntPtr.Zero;
                    supported = true;
                    return tuning2;
                }
                finally { Release(baseTuning); }
            }
            catch { return IntPtr.Zero; }
            finally { Release(services); }
        }

        // Minimum core frequency in MHz, reads only the current min and max values plus the legal range of the minimum frequency
        public static bool GfxMinGet(IntPtr gpu, out bool supported, out int min, out int max, out AdlxIntRange minRange)
        {
            min = 0; max = 0; minRange = new AdlxIntRange();
            IntPtr tuning = GetManualGfxTuning2(gpu, out supported);
            if (tuning == IntPtr.Zero) return supported == false;
            try
            {
                if (!Succeeded(VMethod<FnOutRange>(tuning, Gfx2SlotGetMinRange)(tuning, out minRange))) return false;
                if (!Succeeded(VMethod<FnOutInt>(tuning, Gfx2SlotGetMin)(tuning, out min))) return false;
                if (!Succeeded(VMethod<FnOutInt>(tuning, Gfx2SlotGetMax)(tuning, out max))) return false;
                return true;
            }
            catch { return false; }
            finally { Release(tuning); }
        }

        public static bool GfxMinSet(IntPtr gpu, int min)
        {
            bool supported;
            IntPtr tuning = GetManualGfxTuning2(gpu, out supported);
            if (tuning == IntPtr.Zero) return false;
            try { return Succeeded(VMethod<FnInInt>(tuning, Gfx2SlotSetMin)(tuning, min)); }
            catch { return false; }
            finally { Release(tuning); }
        }

        private static IntPtr GetSam(IntPtr gpu)
        {
            IntPtr services = GetTuningServices();
            if (services == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr services1;
                if (!Succeeded(VMethod<FnQueryIface>(services, IfaceSlotQuery)(services, TuningServices1Iid, out services1))
                    || services1 == IntPtr.Zero) return IntPtr.Zero;
                try
                {
                    IntPtr sam;
                    if (!Succeeded(VMethod<FnGpuOutPtr>(services1, Tune1SlotGetSam)(services1, gpu, out sam)))
                        return IntPtr.Zero;
                    return sam;
                }
                finally { Release(services1); }
            }
            catch { return IntPtr.Zero; }
            finally { Release(services); }
        }

        public static bool SamGet(IntPtr gpu, out bool supported, out bool enabled)
        {
            supported = false; enabled = false;
            IntPtr sam = GetSam(gpu);
            if (sam == IntPtr.Zero) return false;
            try { return FeatureFlags(sam, out supported, out enabled); }
            finally { Release(sam); }
        }

        public static bool SamSet(IntPtr gpu, bool on)
        {
            IntPtr sam = GetSam(gpu);
            if (sam == IntPtr.Zero) return false;
            try { return Succeeded(VMethod<FnInByte>(sam, ToggleSlotSetEnabled)(sam, on ? (byte)1 : (byte)0)); }
            catch { return false; }
            finally { Release(sam); }
        }

        public static bool PowerLimitGet(IntPtr gpu, out bool supported, out int current, out int max)
        {
            supported = false; current = 0; max = 0;
            IntPtr tuning = GetManualPowerTuning(gpu, out supported);
            if (tuning == IntPtr.Zero) return supported;
            try
            {
                AdlxIntRange range;
                if (!Succeeded(VMethod<FnOutRange>(tuning, PowerSlotGetRange)(tuning, out range))) return false;
                if (!Succeeded(VMethod<FnOutInt>(tuning, PowerSlotGetLimit)(tuning, out current))) return false;
                max = range.Max;
                return true;
            }
            catch { return false; }
            finally { Release(tuning); }
        }

        public static bool PowerLimitSet(IntPtr gpu, int value)
        {
            bool supported;
            IntPtr tuning = GetManualPowerTuning(gpu, out supported);
            if (tuning == IntPtr.Zero) return false;
            try { return Succeeded(VMethod<FnInInt>(tuning, PowerSlotSetLimit)(tuning, value)); }
            catch { return false; }
            finally { Release(tuning); }
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
