// @author bdth 2074055628@qq.com
// 文件用途 AMD ADLX 手写 vtable 互操作 只保留残留还原用到的写入接口 驱动缺失时整体降级

using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class AdlxApi
    {
        private const ulong FullVersion = 0x000100050000007C;
        private const uint LoadSearchFlags = 0x1E00;

        private const int SysSlotGetGpus = 1;
        private const int SysSlotGet3DServices = 7;

        private const int IfaceSlotRelease = 1;

        private const int ListSlotBegin = 5;
        private const int ListSlotEnd = 6;
        private const int GpuListSlotAt = 11;

        private const int SvcSlotAntiLag = 3;
        private const int SvcSlotChill = 4;
        private const int SvcSlotImageSharpening = 6;
        private const int SvcSlotEnhancedSync = 7;

        private const int ToggleSlotSetEnabled = 5;
        private const int ChillSlotSetEnabled = 8;
        private const int ChillSlotSetMinFps = 9;
        private const int ChillSlotSetMaxFps = 10;
        private const int RisSlotSetEnabled = 7;
        private const int RisSlotSetSharpness = 8;

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
        private delegate int FnInByte(IntPtr self, byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnInInt(IntPtr self, int value);

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
                    Logger.Log("ADLX 初始化失败 ADLX_RESULT " + result + " AMD 残留还原不可用");
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

        public static bool AntiLagSet(IntPtr gpu, bool on)
        {
            IntPtr feature = GetFeature(SvcSlotAntiLag, gpu);
            if (feature == IntPtr.Zero) return false;
            try { return Succeeded(VMethod<FnInByte>(feature, ToggleSlotSetEnabled)(feature, on ? (byte)1 : (byte)0)); }
            catch { return false; }
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
    }
}
