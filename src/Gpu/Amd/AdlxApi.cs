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
        private delegate int FnOutRange(IntPtr self, out AdlxIntRange range);

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

        private const ulong FullVersion = 281496451547260uL;

        private const uint LoadSearchFlags = 7680u;

        public const int GpuTypeIntegrated = 1;

        public const int GpuTypeDiscrete = 2;

        private const int SysSlotGetGpus = 1;

        private const int SysSlotGet3DServices = 7;

        private const int IfaceSlotRelease = 1;

        private const int IfaceSlotQuery = 2;

        private const int ListSlotBegin = 5;

        private const int ListSlotEnd = 6;

        private const int GpuListSlotAt = 11;

        private const int GpuSlotVendorId = 3;

        private const int GpuSlotType = 5;

        private const int GpuSlotName = 7;

        private const int GpuSlotPnp = 9;

        private const int Gpu3SlotMicroArchitecture = 35;

        private const int SysSlotGetTuningServices = 8;

        private const int SvcSlotRsr = 14;


        private const int Svc1SlotAfmf = 17;

        private const int RsrSlotSetEnabled = 5;

        private const int RsrSlotGetSharpness = 7;

        private const int RsrSlotSetSharpness = 8;

        private const int TuneSlotIsSupportedManualPower = 11;

        private const int TuneSlotGetManualPower = 17;

        private const string ManualPowerIid = "IADLXManualPowerTuning";

        private const int TuneSlotIsSupportedManualGfx = 8;

        private const int TuneSlotGetManualGfx = 14;

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
                IntPtr intPtr = LoadLibraryExW("amdadlx64.dll", IntPtr.Zero, LoadSearchFlags);
                if (intPtr == IntPtr.Zero)
                {
                    return false;
                }
                IntPtr procAddress = GetProcAddress(intPtr, "ADLXInitialize");
                IntPtr procAddress2 = GetProcAddress(intPtr, "ADLXTerminate");
                if (procAddress == IntPtr.Zero || procAddress2 == IntPtr.Zero)
                {
                    return false;
                }
                FnInitialize fnInitialize = (FnInitialize)Marshal.GetDelegateForFunctionPointer(procAddress, typeof(FnInitialize));
                terminate = (FnTerminate)Marshal.GetDelegateForFunctionPointer(procAddress2, typeof(FnTerminate));
                IntPtr intPtr2;
                int fnInitializeResult = fnInitialize(FullVersion, out intPtr2);
                if (!Succeeded(fnInitializeResult) || intPtr2 == IntPtr.Zero)
                {
                    Logger.Warn(Lang.T("log.adlxapi.1") + fnInitializeResult + Lang.T("log.adlxapi.2"));
                    return false;
                }
                system = intPtr2;
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
                VMethod<FnIntProp>(iface, IfaceSlotRelease)(iface);
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
                IntPtr value;
                if (!Succeeded(VMethod<FnOutPtr>(system, SysSlotGetGpus)(system, out value)) || value == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    uint numericValue = VMethod<FnUIntProp>(value, ListSlotBegin)(value);
                    uint numericValue2 = VMethod<FnUIntProp>(value, ListSlotEnd)(value);
                    if (numericValue2 <= numericValue || numericValue2 - numericValue > 16)
                    {
                        return new IntPtr[0];
                    }
                    FnAtList fnAtList = VMethod<FnAtList>(value, GpuListSlotAt);
                    List<IntPtr> list = new List<IntPtr>();
                    for (uint iterationIndex = numericValue; iterationIndex < numericValue2; iterationIndex++)
                    {
                        IntPtr item;
                        if (Succeeded(fnAtList(value, iterationIndex, out item)) && item != IntPtr.Zero)
                        {
                            list.Add(item);
                        }
                    }
                    return list.ToArray();
                }
                finally
                {
                    Release(value);
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
                if (!Succeeded(VMethod<FnOutPtr>(gpu, GpuSlotName)(gpu, out value)) || value == IntPtr.Zero)
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
                return Succeeded(VMethod<FnOutInt>(gpu, GpuSlotType)(gpu, out value)) ? value : 0;
            }
            catch
            {
                return 0;
            }
        }

        internal static string GpuMicroArchitecture(IntPtr gpu)
        {
            IntPtr extended = IntPtr.Zero;
            try
            {
                if (!Succeeded(VMethod<FnQueryIface>(gpu, IfaceSlotQuery)(gpu, "IADLXGPU3", out extended))
                    || extended == IntPtr.Zero) return null;
                IntPtr name;
                if (!Succeeded(VMethod<FnOutPtr>(extended, Gpu3SlotMicroArchitecture)(extended, out name))
                    || name == IntPtr.Zero) return null;
                return Marshal.PtrToStringAnsi(name);
            }
            catch { return null; }
            finally { Release(extended); }
        }

        public static string GpuPnpString(IntPtr gpu)
        {
            try
            {
                IntPtr value;
                if (!Succeeded(VMethod<FnOutPtr>(gpu, GpuSlotPnp)(gpu, out value)) || value == IntPtr.Zero)
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
                if (!Succeeded(VMethod<FnOutPtr>(gpu, GpuSlotVendorId)(gpu, out value)) || value == IntPtr.Zero)
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
                if (!Succeeded(VMethod<FnOutPtr>(system, SysSlotGet3DServices)(system, out value)))
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

        private static bool FeatureFlags(IntPtr feature, out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            try
            {
                byte value;
                if (!Succeeded(VMethod<FnOutByte>(feature, FeatSlotIsSupported)(feature, out value)))
                {
                    return false;
                }
                supported = value != 0;
                if (!supported)
                {
                    return true;
                }
                byte value2;
                if (!Succeeded(VMethod<FnOutByte>(feature, FeatSlotIsEnabled)(feature, out value2)))
                {
                    return false;
                }
                enabled = value2 != 0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr GetAfmf()
        {
            if (!Available)
            {
                return IntPtr.Zero;
            }
            IntPtr intPtr = Get3DServices();
            if (intPtr == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            try
            {
                IntPtr value;
                if (!Succeeded(VMethod<FnQueryIface>(intPtr, IfaceSlotQuery)(intPtr, Services1Iid, out value)) || value == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
                try
                {
                    IntPtr value2;
                    if (!Succeeded(VMethod<FnOutPtr>(value, Svc1SlotAfmf)(value, out value2)))
                    {
                        return IntPtr.Zero;
                    }
                    return value2;
                }
                finally
                {
                    Release(value);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                Release(intPtr);
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
                return Succeeded(VMethod<FnInByte>(afmf, ToggleSlotSetEnabled)(afmf, (byte)(on ? 1 : 0)));
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
            IntPtr intPtr = Get3DServices();
            if (intPtr == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            try
            {
                IntPtr value;
                if (!Succeeded(VMethod<FnOutPtr>(intPtr, SvcSlotRsr)(intPtr, out value)))
                {
                    return IntPtr.Zero;
                }
                return value;
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                Release(intPtr);
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
                int value;
                if (Succeeded(VMethod<FnOutInt>(rsr, RsrSlotGetSharpness)(rsr, out value)))
                {
                    sharpness = value;
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
                if (on && sharpness >= 0 && !Succeeded(VMethod<FnInInt>(rsr, RsrSlotSetSharpness)(rsr, sharpness)))
                {
                    return false;
                }
                return Succeeded(VMethod<FnInByte>(rsr, RsrSlotSetEnabled)(rsr, (byte)(on ? 1 : 0)));
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

        private static IntPtr GetTuningServices()
        {
            if (!Available)
            {
                return IntPtr.Zero;
            }
            try
            {
                IntPtr value;
                if (!Succeeded(VMethod<FnOutPtr>(system, SysSlotGetTuningServices)(system, out value)) || value == IntPtr.Zero)
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

        private static bool TryGetManualTuning(IntPtr gpu, int supportSlot, int getSlot, string iid,
            out bool supported, out IntPtr tuning)
        {
            supported = false;
            tuning = IntPtr.Zero;
            IntPtr tuningServices = GetTuningServices();
            if (tuningServices == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                byte available;
                if (!Succeeded(VMethod<FnGpuOutByte>(tuningServices, supportSlot)(tuningServices, gpu, out available))) return false;
                if (available == 0) return true;
                IntPtr baseInterface;
                if (!Succeeded(VMethod<FnGpuOutPtr>(tuningServices, getSlot)(tuningServices, gpu, out baseInterface))
                    || baseInterface == IntPtr.Zero) return false;
                try
                {
                    if (!Succeeded(VMethod<FnQueryIface>(baseInterface, IfaceSlotQuery)(baseInterface, iid, out tuning))
                        || tuning == IntPtr.Zero) return false;
                    supported = true;
                    return true;
                }
                finally
                {
                    Release(baseInterface);
                }
            }
            catch
            {
                return false;
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
            IntPtr manualGfxTuning;
            if (!TryGetManualTuning(gpu, TuneSlotIsSupportedManualGfx, TuneSlotGetManualGfx, ManualGfx2Iid,
                out supported, out manualGfxTuning)) return false;
            if (!supported) return true;
            try
            {
                if (!Succeeded(VMethod<FnOutRange>(manualGfxTuning, Gfx2SlotGetMinRange)(manualGfxTuning, out minRange)))
                {
                    return false;
                }
                if (!Succeeded(VMethod<FnOutInt>(manualGfxTuning, Gfx2SlotGetMin)(manualGfxTuning, out min)))
                {
                    return false;
                }
                if (!Succeeded(VMethod<FnOutInt>(manualGfxTuning, Gfx2SlotGetMax)(manualGfxTuning, out max)))
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
            IntPtr manualGfxTuning;
            if (!TryGetManualTuning(gpu, TuneSlotIsSupportedManualGfx, TuneSlotGetManualGfx, ManualGfx2Iid,
                out supported, out manualGfxTuning) || !supported) return false;
            try
            {
                return Succeeded(VMethod<FnInInt>(manualGfxTuning, Gfx2SlotSetMin)(manualGfxTuning, min));
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

        private static bool ReadPowerLimit(IntPtr tuning, out int current, out AdlxIntRange range)
        {
            current = 0;
            return Succeeded(VMethod<FnOutRange>(tuning, PowerSlotGetRange)(tuning, out range))
                && Succeeded(VMethod<FnOutInt>(tuning, PowerSlotGetLimit)(tuning, out current))
                && range.Contains(current) && range.Contains(range.Max);
        }

        public static bool PowerLimitGet(IntPtr gpu, out bool supported, out int current, out int max)
        {
            current = 0;
            max = 0;
            IntPtr manualPowerTuning;
            if (!TryGetManualTuning(gpu, TuneSlotIsSupportedManualPower, TuneSlotGetManualPower, ManualPowerIid,
                out supported, out manualPowerTuning)) return false;
            if (!supported) return true;
            try
            {
                AdlxIntRange range;
                if (!ReadPowerLimit(manualPowerTuning, out current, out range)) return false;
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

        public static bool PowerLimitSet(IntPtr gpu, long expectedCurrent, long expectedMaximum, int value)
        {
            bool supported;
            IntPtr manualPowerTuning;
            if (!TryGetManualTuning(gpu, TuneSlotIsSupportedManualPower, TuneSlotGetManualPower, ManualPowerIid,
                out supported, out manualPowerTuning) || !supported) return false;
            try
            {
                int current;
                AdlxIntRange range;
                if (!ReadPowerLimit(manualPowerTuning, out current, out range) || current != expectedCurrent
                    || range.Max != expectedMaximum || !range.Contains(value)) return false;
                return Succeeded(VMethod<FnInInt>(manualPowerTuning, PowerSlotSetLimit)(manualPowerTuning, value));
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

    }
}
