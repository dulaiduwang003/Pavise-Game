// @author bdth 2074055628@qq.com
// 文件用途 NVAPI 驱动配置与数码振动封装 全部函数经 QueryInterface 动态解析 驱动缺失时整体降级

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class NvApi
    {
        private const uint IdInitialize = 0x0150E828;
        private const uint IdDrsCreateSession = 0x0694D52E;
        private const uint IdDrsDestroySession = 0xDAD9CFF8;
        private const uint IdDrsLoadSettings = 0x375DBD6B;
        private const uint IdDrsSaveSettings = 0xFCBC7E14;
        private const uint IdDrsCreateProfile = 0xCC176068;
        private const uint IdDrsCreateApplication = 0x4347A9DE;
        private const uint IdDrsFindApplicationByName = 0xEEE566B2;
        private const uint IdDrsSetSetting = 0x577DD202;
        private const uint IdDrsGetSetting = 0x73BF8338;
        private const uint IdDrsDeleteProfileSetting = 0xE4A26362;
        private const uint IdDrsGetBaseProfile = 0xDA8466A0;
        private const uint IdDrsGetSettingNameFromId = 0xD61CBE6E;
        private const uint IdDrsEnumAvailableSettingIds = 0xF020614A;
        private const uint IdEnumPhysicalGPUs = 0xE5AC921F;
        private const uint IdGpuGetPerfDecreaseInfo = 0x7F7F4600;
        private const uint IdSysGetDriverAndBranchVersion = 0x2926AAAD;

        public const uint SettingPreferredPState = 0x1057EB71;
        public const uint SettingFrlFps = 0x10835002;
        public const uint PStatePreferMax = 0x1;
        public const uint SettingPreRenderLimit = 0x007BA09E;
        public const uint SettingLowLatencyCpl = 0x0005F543;
        public const uint SettingFrlFpsBackground = 0x10835006;
        public const uint SettingAnselAllow = 0x1035DB89;
        public const uint SettingRebarFeature = 0x000F00BA;
        public const uint SettingRebarOptions = 0x000F00BB;
        public const uint SettingRebarSizeLimit = 0x000F00FF;
        public const uint SettingDlssSrOverride = 0x10E41E01;
        public const uint SettingDlssSrPreset = 0x10E41DF3;
        public const uint SettingBatteryBoostAppFps = 0x10115C8C;
        public const uint RebarSizeDefault = 0x40000000;
        public const uint DlssPresetJ = 0x0000000A;
        public const uint DlssPresetK = 0x0000000B;
        public const uint DlssPresetLatest = 0x00FFFFFF;
        public const uint BatteryFpsUncapped = 0x3FF;
        public const uint MinDriverForDlssOverride = 56614;

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnVoid();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnHandleOut(out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnHandle(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnFindApp(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string appName,
            out IntPtr profile, ref DrsApplication app);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnCreateProfile(IntPtr session, ref DrsProfile profile, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnCreateApp(IntPtr session, IntPtr profile, ref DrsApplication app);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnSetting(IntPtr session, IntPtr profile, ref DrsSetting setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnGetSetting(IntPtr session, IntPtr profile, uint id, ref DrsSetting setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnDeleteSetting(IntPtr session, IntPtr profile, uint id);

        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
        internal struct DrsProfile
        {
            public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)] public string ProfileName;
            public uint GpuSupport;
            public uint IsPredefined;
            public uint NumOfApps;
            public uint NumOfSettings;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
        internal struct DrsApplication
        {
            public uint Version;
            public uint IsPredefined;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)] public string UserFriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)] public string Launcher;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
        internal struct DrsSetting
        {
            public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)] public string SettingName;
            public uint SettingId;
            public uint SettingType;
            public uint SettingLocation;
            public uint IsCurrentPredefined;
            public uint IsPredefinedValid;
            public uint PredefinedValue;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)] public byte[] PredefinedPad;
            public uint CurrentValue;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)] public byte[] CurrentPad;
        }

        private static int state;
        private static FnHandleOut drsCreateSession;
        private static FnHandle drsDestroySession, drsLoadSettings, drsSaveSettings;
        private static FnFindApp drsFindApp;
        private static FnCreateProfile drsCreateProfile;
        private static FnCreateApp drsCreateApp;
        private static FnSetting drsSetSetting;
        private static FnGetSetting drsGetSetting;
        private static FnDeleteSetting drsDeleteSetting;

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

        private static T Resolve<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            return p == IntPtr.Zero ? null
                : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        private static bool Probe()
        {
            try
            {
                var init = Resolve<FnVoid>(IdInitialize);
                if (init == null || init() != 0) return false;
                drsCreateSession = Resolve<FnHandleOut>(IdDrsCreateSession);
                drsDestroySession = Resolve<FnHandle>(IdDrsDestroySession);
                drsLoadSettings = Resolve<FnHandle>(IdDrsLoadSettings);
                drsSaveSettings = Resolve<FnHandle>(IdDrsSaveSettings);
                drsFindApp = Resolve<FnFindApp>(IdDrsFindApplicationByName);
                drsCreateProfile = Resolve<FnCreateProfile>(IdDrsCreateProfile);
                drsCreateApp = Resolve<FnCreateApp>(IdDrsCreateApplication);
                drsSetSetting = Resolve<FnSetting>(IdDrsSetSetting);
                drsGetSetting = Resolve<FnGetSetting>(IdDrsGetSetting);
                drsDeleteSetting = Resolve<FnDeleteSetting>(IdDrsDeleteProfileSetting);
                return drsCreateSession != null && drsDestroySession != null
                    && drsLoadSettings != null && drsSaveSettings != null
                    && drsFindApp != null && drsCreateProfile != null
                    && drsCreateApp != null && drsSetSetting != null
                    && drsGetSetting != null && drsDeleteSetting != null;
            }
            catch (DllNotFoundException) { return false; }
            catch { return false; }
        }

        private static uint VersionOf<T>(int structVersion)
        {
            return (uint)Marshal.SizeOf(typeof(T)) | ((uint)structVersion << 16);
        }

        public static bool TryOpenSession(out IntPtr session)
        {
            session = IntPtr.Zero;
            if (!Available) return false;
            if (drsCreateSession(out session) != 0) return false;
            if (drsLoadSettings(session) != 0)
            {
                drsDestroySession(session);
                session = IntPtr.Zero;
                return false;
            }
            return true;
        }

        public static void CloseSession(IntPtr session)
        {
            if (session != IntPtr.Zero) try { drsDestroySession(session); } catch { }
        }

        public static bool SaveSession(IntPtr session)
        {
            try { return drsSaveSettings(session) == 0; } catch { return false; }
        }

        public static bool FindOrCreateAppProfile(IntPtr session, string exeName, out IntPtr profile)
        {
            profile = IntPtr.Zero;
            try
            {
                var app = new DrsApplication { Version = VersionOf<DrsApplication>(1) };
                if (drsFindApp(session, exeName, out profile, ref app) == 0 && profile != IntPtr.Zero)
                    return true;
                var prof = new DrsProfile
                {
                    Version = VersionOf<DrsProfile>(1),
                    ProfileName = "Pavise - " + exeName
                };
                int status = drsCreateProfile(session, ref prof, out profile);
                if (status != 0 || profile == IntPtr.Zero) return false;
                var newApp = new DrsApplication
                {
                    Version = VersionOf<DrsApplication>(1),
                    AppName = exeName,
                    UserFriendlyName = exeName,
                    Launcher = ""
                };
                return drsCreateApp(session, profile, ref newApp) == 0;
            }
            catch { profile = IntPtr.Zero; return false; }
        }

        public static int TryGetDword(IntPtr session, IntPtr profile, uint settingId, out uint value)
        {
            value = 0;
            try
            {
                var setting = new DrsSetting { Version = VersionOf<DrsSetting>(1) };
                int status = drsGetSetting(session, profile, settingId, ref setting);
                if (status != 0) return 0;
                value = setting.CurrentValue;
                return setting.SettingLocation == 0 ? 1 : 0;
            }
            catch { return -1; }
        }

        public static bool SetDword(IntPtr session, IntPtr profile, uint settingId, uint value)
        {
            int status;
            return SetDword(session, profile, settingId, value, out status);
        }

        public static bool SetDword(IntPtr session, IntPtr profile, uint settingId, uint value, out int status)
        {
            try
            {
                var setting = new DrsSetting
                {
                    Version = VersionOf<DrsSetting>(1),
                    SettingId = settingId,
                    SettingType = 0,
                    CurrentValue = value
                };
                status = drsSetSetting(session, profile, ref setting);
                return status == 0;
            }
            catch { status = int.MinValue; return false; }
        }

        public static bool DeleteSetting(IntPtr session, IntPtr profile, uint settingId)
        {
            try
            {
                int status = drsDeleteSetting(session, profile, settingId);
                return status == 0;
            }
            catch { return false; }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnGetBaseProfile(IntPtr session, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnSettingName(uint settingId, IntPtr nameBuffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnEnumSettingIds([Out] uint[] ids, ref uint maxCount);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnEnumGpus([Out] IntPtr[] handles, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnGpuDwordOut(IntPtr gpu, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnDriverVersion(out uint version, IntPtr branchBuffer);

        private static int extState;
        private static FnGetBaseProfile drsGetBaseProfile;
        private static FnSettingName drsSettingName;
        private static FnEnumSettingIds drsEnumSettingIds;
        private static FnEnumGpus enumGpus;
        private static FnGpuDwordOut gpuPerfDecrease;
        private static FnDriverVersion sysDriverVersion;
        private static int driverVersionCache = -1;

        private static void EnsureExtResolved()
        {
            if (Volatile.Read(ref extState) != 0 || !Available) return;
            try
            {
                drsGetBaseProfile = Resolve<FnGetBaseProfile>(IdDrsGetBaseProfile);
                drsSettingName = Resolve<FnSettingName>(IdDrsGetSettingNameFromId);
                drsEnumSettingIds = Resolve<FnEnumSettingIds>(IdDrsEnumAvailableSettingIds);
                enumGpus = Resolve<FnEnumGpus>(IdEnumPhysicalGPUs);
                gpuPerfDecrease = Resolve<FnGpuDwordOut>(IdGpuGetPerfDecreaseInfo);
                sysDriverVersion = Resolve<FnDriverVersion>(IdSysGetDriverAndBranchVersion);
            }
            catch { }
            Interlocked.CompareExchange(ref extState, 1, 0);
        }

        public static bool TryGetBaseProfile(IntPtr session, out IntPtr profile)
        {
            profile = IntPtr.Zero;
            EnsureExtResolved();
            if (drsGetBaseProfile == null) return false;
            try { return drsGetBaseProfile(session, out profile) == 0 && profile != IntPtr.Zero; }
            catch { profile = IntPtr.Zero; return false; }
        }

        public static bool TryGetSettingName(uint settingId, out string name)
        {
            name = null;
            EnsureExtResolved();
            if (drsSettingName == null) return false;
            IntPtr buffer = Marshal.AllocHGlobal(4096);
            try
            {
                Marshal.WriteInt16(buffer, 0, 0);
                if (drsSettingName(settingId, buffer) != 0) return false;
                name = Marshal.PtrToStringUni(buffer);
                return !string.IsNullOrEmpty(name);
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static uint[] EnumAvailableSettingIds()
        {
            EnsureExtResolved();
            if (drsEnumSettingIds == null) return null;
            try
            {
                uint count = 2048;
                var ids = new uint[2048];
                if (drsEnumSettingIds(ids, ref count) != 0 || count == 0 || count > 2048) return null;
                var result = new uint[count];
                Array.Copy(ids, result, (int)count);
                return result;
            }
            catch { return null; }
        }

        public static IntPtr[] EnumGpuHandles()
        {
            EnsureExtResolved();
            if (enumGpus == null) return null;
            try
            {
                var handles = new IntPtr[64];
                uint count;
                if (enumGpus(handles, out count) != 0 || count == 0 || count > 64) return null;
                var result = new IntPtr[count];
                Array.Copy(handles, result, (int)count);
                return result;
            }
            catch { return null; }
        }

        public const uint PerfDecreaseThermal = 0x00000001;
        public const uint PerfDecreasePower = 0x00000002;
        public const uint PerfDecreaseAcBatt = 0x00000004;
        public const uint PerfDecreaseApi = 0x00000008;
        public const uint PerfDecreaseInsufficientPower = 0x00000010;

        public static bool TryGetPerfDecrease(IntPtr gpu, out uint mask)
        {
            mask = 0;
            EnsureExtResolved();
            if (gpuPerfDecrease == null || gpu == IntPtr.Zero) return false;
            try { return gpuPerfDecrease(gpu, out mask) == 0; }
            catch { return false; }
        }

        public static uint DriverVersion()
        {
            int cached = Volatile.Read(ref driverVersionCache);
            if (cached >= 0) return (uint)cached;
            uint version = 0;
            EnsureExtResolved();
            if (sysDriverVersion != null)
            {
                IntPtr buffer = Marshal.AllocHGlobal(64);
                try
                {
                    uint raw;
                    if (sysDriverVersion(out raw, buffer) == 0) version = raw;
                }
                catch { }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            Interlocked.CompareExchange(ref driverVersionCache, (int)version, -1);
            return version;
        }
    }
}
