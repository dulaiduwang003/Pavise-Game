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

        public const uint SettingPreferredPState = 0x1057EB71;
        public const uint SettingFrlFps = 0x10835002;
        public const uint PStatePreferMax = 0x1;
        public const uint SettingPreRenderLimit = 0x007BA09E;
        public const uint SettingLowLatencyCpl = 0x0005F543;

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
            try
            {
                var setting = new DrsSetting
                {
                    Version = VersionOf<DrsSetting>(1),
                    SettingId = settingId,
                    SettingType = 0,
                    CurrentValue = value
                };
                return drsSetSetting(session, profile, ref setting) == 0;
            }
            catch { return false; }
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

    }
}
