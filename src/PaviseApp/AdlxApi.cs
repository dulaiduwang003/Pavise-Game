using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class AdlxApi
    {
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
        private delegate int FnQueryIface(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string interfaceId, out IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGpuOutByte(IntPtr self, IntPtr gpu, out byte value);

#pragma warning disable 0649
        private struct GpuLuid
        {
            internal uint Low;

            internal int High;
        }
#pragma warning restore 0649

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnOutLuid(IntPtr self, out GpuLuid value);

        public const int GpuTypeIntegrated = 1;

        public const int GpuTypeDiscrete = 2;

        private static readonly object lk = new object();

        private static int state;

        private static IntPtr system;

        private static FnTerminate terminate;

        public static bool Available
        {
            get
            {
                lock (lk)
                {
                    if (state != 0)
                    {
                        return state > 0;
                    }
                    state = (Probe() ? 1 : (-1));
                    return state > 0;
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string name, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        public static bool Succeeded(int result)
        {
            if (result >= 0)
            {
                return result <= 2;
            }
            return false;
        }

        private static bool Probe()
        {
            try
            {
                IntPtr libraryHandle = LoadLibraryExW("amdadlx64.dll", IntPtr.Zero, 7680u);
                if (libraryHandle == IntPtr.Zero)
                {
                    return false;
                }
                IntPtr procAddress = GetProcAddress(libraryHandle, "ADLXInitialize");
                IntPtr procAddress2 = GetProcAddress(libraryHandle, "ADLXTerminate");
                if (procAddress == IntPtr.Zero || procAddress2 == IntPtr.Zero)
                {
                    return false;
                }
                FnInitialize fnInitialize = (FnInitialize)Marshal.GetDelegateForFunctionPointer(procAddress, typeof(FnInitialize));
                terminate = (FnTerminate)Marshal.GetDelegateForFunctionPointer(procAddress2, typeof(FnTerminate));
                IntPtr nativePointer;
                int fnInitializeResult = fnInitialize(281496451547260uL, out nativePointer);
                if (!Succeeded(fnInitializeResult) || nativePointer == IntPtr.Zero)
                {
                    Logger.Warn(Lang.T("log.adlxapi.1", 0) + fnInitializeResult + Lang.T("log.adlxapi.2", 0));
                    return false;
                }
                system = nativePointer;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            try
            {
                if (terminate != null)
                {
                    terminate();
                }
            }
            catch
            {
            }
        }

        private static T VMethod<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr ptr = Marshal.ReadIntPtr(obj);
            IntPtr ptr2 = Marshal.ReadIntPtr(ptr, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(ptr2, typeof(T));
        }

        public static void Release(IntPtr iface)
        {
            if (iface == IntPtr.Zero)
            {
                return;
            }
            try
            {
                VMethod<FnIntProp>(iface, 1)(iface);
            }
            catch
            {
            }
        }

        public static IntPtr[] GetGpus()
        {
            if (!Available)
            {
                return null;
            }
            try
            {
                IntPtr nativePointer;
                if (!Succeeded(VMethod<FnOutPtr>(system, 1)(system, out nativePointer)) || nativePointer == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    uint firstGpuIndex = VMethod<FnUIntProp>(nativePointer, 5)(nativePointer);
                    uint gpuEndIndex = VMethod<FnUIntProp>(nativePointer, 6)(nativePointer);
                    if (gpuEndIndex <= firstGpuIndex || gpuEndIndex - firstGpuIndex > 16)
                    {
                        return new IntPtr[0];
                    }
                    FnAtList fnAtList = VMethod<FnAtList>(nativePointer, 11);
                    List<IntPtr> nativePointers = new List<IntPtr>();
                    for (uint iterationIndex = firstGpuIndex; iterationIndex < gpuEndIndex; iterationIndex++)
                    {
                        IntPtr currentNativePointer;
                        if (Succeeded(fnAtList(nativePointer, iterationIndex, out currentNativePointer)) && currentNativePointer != IntPtr.Zero)
                        {
                            nativePointers.Add(currentNativePointer);
                        }
                    }
                    return nativePointers.ToArray();
                }
                finally
                {
                    Release(nativePointer);
                }
            }
            catch
            {
                return null;
            }
        }

        public static void ReleaseAll(IntPtr[] interfaces)
        {
            if (interfaces != null)
            {
                foreach (IntPtr iface in interfaces)
                {
                    Release(iface);
                }
            }
        }

        public static string GpuName(IntPtr gpu)
        {
            try
            {
                IntPtr value;
                if (!Succeeded(VMethod<FnOutPtr>(gpu, 7)(gpu, out value)) || value == IntPtr.Zero)
                {
                    return null;
                }
                return Marshal.PtrToStringAnsi(value);
            }
            catch
            {
                return null;
            }
        }

        public static int GpuType(IntPtr gpu)
        {
            try
            {
                int value;
                return Succeeded(VMethod<FnOutInt>(gpu, 5)(gpu, out value)) ? value : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static string GpuPnpString(IntPtr gpu)
        {
            try
            {
                IntPtr value;
                if (!Succeeded(VMethod<FnOutPtr>(gpu, 9)(gpu, out value)) || value == IntPtr.Zero)
                {
                    return null;
                }
                return Marshal.PtrToStringAnsi(value);
            }
            catch
            {
                return null;
            }
        }

        public static string GpuVendor(IntPtr gpu)
        {
            try
            {
                IntPtr value;
                if (!Succeeded(VMethod<FnOutPtr>(gpu, 3)(gpu, out value)) || value == IntPtr.Zero)
                {
                    return null;
                }
                return Marshal.PtrToStringAnsi(value);
            }
            catch
            {
                return null;
            }
        }

        private static IntPtr Get3DServices()
        {
            try
            {
                IntPtr value;
                if (!Succeeded(VMethod<FnOutPtr>(system, 7)(system, out value)))
                {
                    return IntPtr.Zero;
                }
                return value;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static IntPtr GetFeature(int serviceSlot, IntPtr gpu)
        {
            if (!Available)
            {
                return IntPtr.Zero;
            }
            IntPtr nativePointerEntry = Get3DServices();
            if (nativePointerEntry == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            try
            {
                IntPtr nativePointer;
                if (!Succeeded(VMethod<FnGpuOutPtr>(nativePointerEntry, serviceSlot)(nativePointerEntry, gpu, out nativePointer)))
                {
                    return IntPtr.Zero;
                }
                return nativePointer;
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                Release(nativePointerEntry);
            }
        }

        private static bool FeatureFlags(IntPtr feature, out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            try
            {
                byte capabilityFlag;
                if (!Succeeded(VMethod<FnOutByte>(feature, 3)(feature, out capabilityFlag)))
                {
                    return false;
                }
                supported = capabilityFlag != 0;
                if (!supported)
                {
                    return true;
                }
                byte currentCapabilityFlag;
                if (!Succeeded(VMethod<FnOutByte>(feature, 4)(feature, out currentCapabilityFlag)))
                {
                    return false;
                }
                enabled = currentCapabilityFlag != 0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool AntiLagGet(IntPtr gpu, out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            IntPtr feature = GetFeature(3, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return FeatureFlags(feature, out supported, out enabled);
            }
            finally
            {
                Release(feature);
            }
        }

        public static bool AntiLagSet(IntPtr gpu, bool on)
        {
            IntPtr feature = GetFeature(3, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return Succeeded(VMethod<FnInByte>(feature, 5)(feature, (byte)(on ? 1 : 0)));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(feature);
            }
        }

        public static bool EnhancedSyncGet(IntPtr gpu, out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            IntPtr feature = GetFeature(7, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return FeatureFlags(feature, out supported, out enabled);
            }
            finally
            {
                Release(feature);
            }
        }

        public static bool EnhancedSyncSet(IntPtr gpu, bool on)
        {
            IntPtr feature = GetFeature(7, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return Succeeded(VMethod<FnInByte>(feature, 5)(feature, (byte)(on ? 1 : 0)));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(feature);
            }
        }

        public static bool ChillGet(IntPtr gpu, out bool supported, out bool enabled, out int minFps, out int maxFps, out AdlxIntRange range)
        {
            supported = false;
            enabled = false;
            minFps = 0;
            maxFps = 0;
            range = default(AdlxIntRange);
            IntPtr feature = GetFeature(4, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (!FeatureFlags(feature, out supported, out enabled))
                {
                    return false;
                }
                if (!supported)
                {
                    return true;
                }
                if (!Succeeded(VMethod<FnOutRange>(feature, 5)(feature, out range)))
                {
                    return false;
                }
                if (!Succeeded(VMethod<FnOutInt>(feature, 6)(feature, out minFps)))
                {
                    return false;
                }
                if (!Succeeded(VMethod<FnOutInt>(feature, 7)(feature, out maxFps)))
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(feature);
            }
        }

        public static bool ChillSet(IntPtr gpu, bool on, int minFps, int maxFps)
        {
            IntPtr feature = GetFeature(4, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (on)
                {
                    bool limitsApplied = Succeeded(VMethod<FnInInt>(feature, 9)(feature, minFps)) && Succeeded(VMethod<FnInInt>(feature, 10)(feature, maxFps));
                    if (!limitsApplied)
                    {
                        limitsApplied = Succeeded(VMethod<FnInInt>(feature, 10)(feature, maxFps)) && Succeeded(VMethod<FnInInt>(feature, 9)(feature, minFps));
                    }
                    if (!limitsApplied)
                    {
                        return false;
                    }
                }
                return Succeeded(VMethod<FnInByte>(feature, 8)(feature, (byte)(on ? 1 : 0)));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(feature);
            }
        }

        public static bool FrtcGet(IntPtr gpu, out bool supported, out bool enabled, out int fps, out AdlxIntRange range)
        {
            supported = false;
            enabled = false;
            fps = 0;
            range = default(AdlxIntRange);
            IntPtr feature = GetFeature(9, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (!FeatureFlags(feature, out supported, out enabled))
                {
                    return false;
                }
                if (!supported)
                {
                    return true;
                }
                if (!Succeeded(VMethod<FnOutRange>(feature, 5)(feature, out range)))
                {
                    return false;
                }
                if (!Succeeded(VMethod<FnOutInt>(feature, 6)(feature, out fps)))
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(feature);
            }
        }

        public static bool FrtcSet(IntPtr gpu, bool on, int fps)
        {
            IntPtr feature = GetFeature(9, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (on && !Succeeded(VMethod<FnInInt>(feature, 8)(feature, fps)))
                {
                    return false;
                }
                return Succeeded(VMethod<FnInByte>(feature, 7)(feature, (byte)(on ? 1 : 0)));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(feature);
            }
        }

        private static IntPtr GetAfmf()
        {
            if (!Available)
            {
                return IntPtr.Zero;
            }
            IntPtr nativePointerEntry = Get3DServices();
            if (nativePointerEntry == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            try
            {
                IntPtr nativePointer;
                if (!Succeeded(VMethod<FnQueryIface>(nativePointerEntry, 2)(nativePointerEntry, "IADLX3DSettingsServices1", out nativePointer)) || nativePointer == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
                try
                {
                    IntPtr currentNativePointer;
                    if (!Succeeded(VMethod<FnOutPtr>(nativePointer, 17)(nativePointer, out currentNativePointer)))
                    {
                        return IntPtr.Zero;
                    }
                    return currentNativePointer;
                }
                finally
                {
                    Release(nativePointer);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                Release(nativePointerEntry);
            }
        }

        public static bool AfmfGet(out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            IntPtr afmf = GetAfmf();
            if (afmf == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return FeatureFlags(afmf, out supported, out enabled);
            }
            finally
            {
                Release(afmf);
            }
        }

        public static bool AfmfSet(bool on)
        {
            IntPtr afmf = GetAfmf();
            if (afmf == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return Succeeded(VMethod<FnInByte>(afmf, 5)(afmf, (byte)(on ? 1 : 0)));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(afmf);
            }
        }

        private static IntPtr GetRsr()
        {
            if (!Available)
            {
                return IntPtr.Zero;
            }
            IntPtr nativePointerEntry = Get3DServices();
            if (nativePointerEntry == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            try
            {
                IntPtr nativePointer;
                if (!Succeeded(VMethod<FnOutPtr>(nativePointerEntry, 14)(nativePointerEntry, out nativePointer)))
                {
                    return IntPtr.Zero;
                }
                return nativePointer;
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                Release(nativePointerEntry);
            }
        }

        public static bool RsrGet(out bool supported, out bool enabled, out int sharpness)
        {
            supported = false;
            enabled = false;
            sharpness = 0;
            IntPtr rsr = GetRsr();
            if (rsr == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (!FeatureFlags(rsr, out supported, out enabled))
                {
                    return false;
                }
                if (!supported)
                {
                    return true;
                }
                int rsrSharpness;
                if (Succeeded(VMethod<FnOutInt>(rsr, 7)(rsr, out rsrSharpness)))
                {
                    sharpness = rsrSharpness;
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(rsr);
            }
        }

        public static bool RsrSet(bool on, int sharpness)
        {
            IntPtr rsr = GetRsr();
            if (rsr == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (on && sharpness >= 0 && !Succeeded(VMethod<FnInInt>(rsr, 8)(rsr, sharpness)))
                {
                    return false;
                }
                return Succeeded(VMethod<FnInByte>(rsr, 5)(rsr, (byte)(on ? 1 : 0)));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(rsr);
            }
        }

        private static IntPtr GetManualPowerTuning(IntPtr gpu, out bool supported)
        {
            supported = false;
            if (!Available)
            {
                return IntPtr.Zero;
            }
            IntPtr nativePointer;
            try
            {
                if (!Succeeded(VMethod<FnOutPtr>(system, 8)(system, out nativePointer)) || nativePointer == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
            try
            {
                byte capabilityFlag;
                if (!Succeeded(VMethod<FnGpuOutByte>(nativePointer, 11)(nativePointer, gpu, out capabilityFlag)) || capabilityFlag == 0)
                {
                    return IntPtr.Zero;
                }
                supported = true;
                IntPtr currentNativePointer;
                if (!Succeeded(VMethod<FnGpuOutPtr>(nativePointer, 17)(nativePointer, gpu, out currentNativePointer)))
                {
                    return IntPtr.Zero;
                }
                return currentNativePointer;
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                Release(nativePointer);
            }
        }

        private static IntPtr GetTuningServices()
        {
            if (!Available)
            {
                return IntPtr.Zero;
            }
            try
            {
                IntPtr nativePointer;
                if (!Succeeded(VMethod<FnOutPtr>(system, 8)(system, out nativePointer)) || nativePointer == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
                return nativePointer;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static IntPtr GetManualGfxTuning2(IntPtr gpu, out bool supported)
        {
            supported = false;
            IntPtr tuningServices = GetTuningServices();
            if (tuningServices == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            try
            {
                byte capabilityFlag;
                if (!Succeeded(VMethod<FnGpuOutByte>(tuningServices, 8)(tuningServices, gpu, out capabilityFlag)) || capabilityFlag == 0)
                {
                    return IntPtr.Zero;
                }
                IntPtr nativePointer;
                if (!Succeeded(VMethod<FnGpuOutPtr>(tuningServices, 14)(tuningServices, gpu, out nativePointer)) || nativePointer == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
                try
                {
                    IntPtr currentNativePointer;
                    if (!Succeeded(VMethod<FnQueryIface>(nativePointer, 2)(nativePointer, "IADLXManualGraphicsTuning2", out currentNativePointer)) || currentNativePointer == IntPtr.Zero)
                    {
                        return IntPtr.Zero;
                    }
                    supported = true;
                    return currentNativePointer;
                }
                finally
                {
                    Release(nativePointer);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                Release(tuningServices);
            }
        }

        public static bool GfxMinGet(IntPtr gpu, out bool supported, out int min, out int max, out AdlxIntRange minRange)
        {
            min = 0;
            max = 0;
            minRange = default(AdlxIntRange);
            IntPtr manualGfxTuning = GetManualGfxTuning2(gpu, out supported);
            if (manualGfxTuning == IntPtr.Zero)
            {
                return !supported;
            }
            try
            {
                if (!Succeeded(VMethod<FnOutRange>(manualGfxTuning, 3)(manualGfxTuning, out minRange)))
                {
                    return false;
                }
                if (!Succeeded(VMethod<FnOutInt>(manualGfxTuning, 4)(manualGfxTuning, out min)))
                {
                    return false;
                }
                if (!Succeeded(VMethod<FnOutInt>(manualGfxTuning, 7)(manualGfxTuning, out max)))
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(manualGfxTuning);
            }
        }

        public static bool GfxMinSet(IntPtr gpu, int min)
        {
            bool supported;
            IntPtr manualGfxTuning = GetManualGfxTuning2(gpu, out supported);
            if (manualGfxTuning == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return Succeeded(VMethod<FnInInt>(manualGfxTuning, 5)(manualGfxTuning, min));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(manualGfxTuning);
            }
        }

        private static IntPtr GetSam(IntPtr gpu)
        {
            IntPtr tuningServices = GetTuningServices();
            if (tuningServices == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            try
            {
                IntPtr nativePointer;
                if (!Succeeded(VMethod<FnQueryIface>(tuningServices, 2)(tuningServices, "IADLXGPUTuningServices1", out nativePointer)) || nativePointer == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
                try
                {
                    IntPtr currentNativePointer;
                    if (!Succeeded(VMethod<FnGpuOutPtr>(nativePointer, 18)(nativePointer, gpu, out currentNativePointer)))
                    {
                        return IntPtr.Zero;
                    }
                    return currentNativePointer;
                }
                finally
                {
                    Release(nativePointer);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                Release(tuningServices);
            }
        }

        public static bool SamGet(IntPtr gpu, out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            IntPtr sam = GetSam(gpu);
            if (sam == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return FeatureFlags(sam, out supported, out enabled);
            }
            finally
            {
                Release(sam);
            }
        }

        public static bool SamSet(IntPtr gpu, bool on)
        {
            IntPtr sam = GetSam(gpu);
            if (sam == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return Succeeded(VMethod<FnInByte>(sam, 5)(sam, (byte)(on ? 1 : 0)));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(sam);
            }
        }

        public static bool PowerLimitGet(IntPtr gpu, out bool supported, out int current, out int max)
        {
            supported = false;
            current = 0;
            max = 0;
            IntPtr manualPowerTuning = GetManualPowerTuning(gpu, out supported);
            if (manualPowerTuning == IntPtr.Zero)
            {
                return supported;
            }
            try
            {
                AdlxIntRange range;
                if (!Succeeded(VMethod<FnOutRange>(manualPowerTuning, 3)(manualPowerTuning, out range)))
                {
                    return false;
                }
                if (!Succeeded(VMethod<FnOutInt>(manualPowerTuning, 4)(manualPowerTuning, out current)))
                {
                    return false;
                }
                max = range.Max;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(manualPowerTuning);
            }
        }

        public static bool PowerLimitSet(IntPtr gpu, int value)
        {
            bool supported;
            IntPtr manualPowerTuning = GetManualPowerTuning(gpu, out supported);
            if (manualPowerTuning == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return Succeeded(VMethod<FnInInt>(manualPowerTuning, 5)(manualPowerTuning, value));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(manualPowerTuning);
            }
        }

        public static bool RisGet(IntPtr gpu, out bool supported, out bool enabled, out int sharpness, out AdlxIntRange range)
        {
            supported = false;
            enabled = false;
            sharpness = 0;
            range = default(AdlxIntRange);
            IntPtr feature = GetFeature(6, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (!FeatureFlags(feature, out supported, out enabled))
                {
                    return false;
                }
                if (!supported)
                {
                    return true;
                }
                if (!Succeeded(VMethod<FnOutRange>(feature, 5)(feature, out range)))
                {
                    return false;
                }
                if (!Succeeded(VMethod<FnOutInt>(feature, 6)(feature, out sharpness)))
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(feature);
            }
        }

        public static bool RisSet(IntPtr gpu, bool on, int sharpness)
        {
            IntPtr feature = GetFeature(6, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (on && !Succeeded(VMethod<FnInInt>(feature, 8)(feature, sharpness)))
                {
                    return false;
                }
                return Succeeded(VMethod<FnInByte>(feature, 7)(feature, (byte)(on ? 1 : 0)));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(feature);
            }
        }

        public static bool ResetShaderCache(IntPtr gpu)
        {
            IntPtr feature = GetFeature(15, gpu);
            if (feature == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                byte capabilityFlag;
                if (!Succeeded(VMethod<FnOutByte>(feature, 3)(feature, out capabilityFlag)) || capabilityFlag == 0)
                {
                    return false;
                }
                return Succeeded(VMethod<FnAction>(feature, 4)(feature));
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(feature);
            }
        }
    }
}
