using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class NvApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnVoid();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnHandleOut(out IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnHandle(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnFindApp(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string appName, out IntPtr profile, ref DrsApplication app);

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        internal struct DrsProfile
        {
            public uint Version;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
            public string ProfileName;

            public uint GpuSupport;

            public uint IsPredefined;

            public uint NumOfApps;

            public uint NumOfSettings;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        internal struct DrsApplication
        {
            public uint Version;

            public uint IsPredefined;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
            public string AppName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
            public string UserFriendlyName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
            public string Launcher;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        internal struct DrsSetting
        {
            public uint Version;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
            public string SettingName;

            public uint SettingId;

            public uint SettingType;

            public uint SettingLocation;

            public uint IsCurrentPredefined;

            public uint IsPredefinedValid;

            public uint PredefinedValue;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)]
            public byte[] PredefinedPad;

            public uint CurrentValue;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)]
            public byte[] CurrentPad;
        }

        private sealed class DrsSessionPermit
        {
            internal IDisposable Gate;

            internal int Thread;
        }

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

        private const uint IdInitialize = 22079528u;

        private const uint IdDrsCreateSession = 110417198u;

        private const uint IdDrsDestroySession = 3671707640u;

        private const uint IdDrsLoadSettings = 928890219u;

        private const uint IdDrsSaveSettings = 4240211476u;

        private const uint IdDrsCreateProfile = 3424084072u;

        private const uint IdDrsCreateApplication = 1128770014u;

        private const uint IdDrsFindApplicationByName = 4008011442u;

        private const uint IdDrsSetSetting = 1467863554u;

        private const uint IdDrsGetSetting = 1941930808u;

        private const uint IdDrsDeleteProfileSetting = 3835847522u;

        private const uint IdDrsGetSettingNameFromId = 3592207982u;

        private const uint IdDrsEnumAvailableSettingIds = 4028653898u;

        private const uint IdEnumPhysicalGPUs = 3853292063u;

        private const uint IdGpuGetPerfDecreaseInfo = 2139047424u;

        private const uint IdSysGetDriverAndBranchVersion = 690399917u;

        public const uint SettingPreferredPState = 274197361u;

        public const uint SettingFrlFps = 277041154u;

        public const uint PStatePreferMax = 1u;

        public const uint SettingSmoothMotion = 2966652096u;

        public const uint SettingShaderCacheSize = 11306135u;

        public const uint ShaderCacheUnlimited = uint.MaxValue;

        public const uint ShaderCacheDefault = 16384u;

        public const uint SettingFrlFpsBackground = 277041174u;

        public const uint SettingAnselAllow = 271965065u;

        public const uint SettingRebarFeature = 983226u;

        public const uint SettingRebarOptions = 983227u;

        public const uint SettingRebarSizeLimit = 983295u;

        public const uint SettingDlssSrOverride = 283385345u;

        public const uint SettingDlssSrPreset = 283385331u;

        public const uint SettingCudaForceP2 = 1343646814u;

        public const uint CudaForceP2Off = 0u;

        public const uint SettingShaderCacheEnable = 1675263u;

        public const uint ShaderCacheOn = 1u;

        public const uint RebarSizeDefault = 1073741824u;

        public const uint DlssPresetJ = 10u;

        public const uint DlssPresetK = 11u;

        public const uint DlssPresetLatest = 16777215u;

        public const uint MinDriverForDlssOverride = 56614u;

        public const int StatusSettingNotFound = -160;

        public const uint PerfDecreaseThermal = 1u;

        public const uint PerfDecreasePower = 2u;

        public const uint PerfDecreaseAcBatt = 4u;

        public const uint PerfDecreaseApi = 8u;

        public const uint PerfDecreaseInsufficientPower = 16u;

        private static int state;

        private static FnHandleOut drsCreateSession;

        private static FnHandle drsDestroySession;

        private static FnHandle drsLoadSettings;

        private static FnHandle drsSaveSettings;

        private static FnFindApp drsFindApp;

        private static FnCreateProfile drsCreateProfile;

        private static FnCreateApp drsCreateApp;

        private static FnSetting drsSetSetting;

        private static FnGetSetting drsGetSetting;

        private static FnDeleteSetting drsDeleteSetting;

        private static readonly Dictionary<IntPtr, DrsSessionPermit> drsPermits = new Dictionary<IntPtr, DrsSessionPermit>();

        private static int extState;

        private static FnSettingName drsSettingName;

        private static FnEnumSettingIds drsEnumSettingIds;

        private static FnEnumGpus enumGpus;

        private static FnGpuDwordOut gpuPerfDecrease;

        private static FnDriverVersion sysDriverVersion;

        private static int driverVersionCache = -1;

        public static bool Available
        {
            get
            {
                int readResult = Volatile.Read(ref state);
                if (readResult != 0)
                {
                    return readResult > 0;
                }
                bool probeSucceeded = Probe();
                Interlocked.CompareExchange(ref state, probeSucceeded ? 1 : (-1), 0);
                return Volatile.Read(ref state) > 0;
            }
        }

        [DllImport("nvapi64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvapi_QueryInterface")]
        private static extern IntPtr QueryInterface(uint id);

        private static void RequireDrsPermit(IntPtr session)
        {
            lock (drsPermits)
            {
                DrsSessionPermit value;
                if (!drsPermits.TryGetValue(session, out value) || value.Thread != Thread.CurrentThread.ManagedThreadId)
                {
                    throw new InvalidOperationException("DRS session escaped its original serialized transaction");
                }
            }
        }

        private static T Resolve<T>(uint id) where T : class
        {
            IntPtr intPtr = QueryInterface(id);
            if (intPtr != IntPtr.Zero)
            {
                return (T)(object)Marshal.GetDelegateForFunctionPointer(intPtr, typeof(T));
            }
            return null;
        }

        private static bool Probe()
        {
            try
            {
                FnVoid fnVoid = Resolve<FnVoid>(IdInitialize);
                if (fnVoid == null || fnVoid() != 0)
                {
                    return false;
                }
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
                return drsCreateSession != null && drsDestroySession != null && drsLoadSettings != null && drsSaveSettings != null && drsFindApp != null && drsCreateProfile != null && drsCreateApp != null && drsSetSetting != null && drsGetSetting != null && drsDeleteSetting != null;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static uint VersionOf<T>(int structVersion)
        {
            return (uint)(Marshal.SizeOf(typeof(T)) | (structVersion << 16));
        }

        public static bool TryOpenSession(out IntPtr session)
        {
            session = IntPtr.Zero;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return false;
#endif
            IDisposable disposable = GameNvNativeGate.Enter();
            try
            {
                if (!Available || drsCreateSession(out session) != 0 || session == IntPtr.Zero)
                {
                    return false;
                }
                if (drsLoadSettings(session) != 0)
                {
                    return false;
                }
                lock (drsPermits)
                {
                    drsPermits.Add(session, new DrsSessionPermit
                    {
                        Gate = disposable,
                        Thread = Thread.CurrentThread.ManagedThreadId
                    });
                }
                disposable = null;
                return true;
            }
            finally
            {
                if (disposable != null)
                {
                    IntPtr intPtr = session;
                    session = IntPtr.Zero;
                    try
                    {
                        if (intPtr != IntPtr.Zero && drsDestroySession != null)
                        {
                            drsDestroySession(intPtr);
                        }
                    }
                    finally
                    {
                        disposable.Dispose();
                    }
                }
            }
        }

        public static void CloseSession(IntPtr session)
        {
            if (session == IntPtr.Zero)
            {
                return;
            }
            RequireDrsPermit(session);
            DrsSessionPermit drsSessionPermit;
            lock (drsPermits)
            {
                drsSessionPermit = drsPermits[session];
                drsPermits.Remove(session);
            }
            try
            {
                if (drsDestroySession(session) != 0)
                {
                    throw new InvalidOperationException("DRS session release failed");
                }
            }
            finally
            {
                drsSessionPermit.Gate.Dispose();
            }
        }

        public static bool SaveSession(IntPtr session)
        {
            try
            {
                RequireDrsPermit(session);
                return drsSaveSettings(session) == 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool FindOrCreateAppProfile(IntPtr session, string exeName, out IntPtr profile)
        {
            profile = IntPtr.Zero;
            try
            {
                RequireDrsPermit(session);
                DrsApplication drsApplication = default(DrsApplication);
                drsApplication.Version = VersionOf<DrsApplication>(1);
                DrsApplication app = drsApplication;
                if (drsFindApp(session, exeName, out profile, ref app) == 0 && profile != IntPtr.Zero)
                {
                    return true;
                }
                DrsProfile drsProfile = default(DrsProfile);
                drsProfile.Version = VersionOf<DrsProfile>(1);
                drsProfile.ProfileName = "Pavise - " + exeName;
                DrsProfile profile2 = drsProfile;
                if (drsCreateProfile(session, ref profile2, out profile) != 0 || profile == IntPtr.Zero)
                {
                    return false;
                }
                DrsApplication drsApplication2 = default(DrsApplication);
                drsApplication2.Version = VersionOf<DrsApplication>(1);
                drsApplication2.AppName = exeName;
                drsApplication2.UserFriendlyName = exeName;
                drsApplication2.Launcher = "";
                DrsApplication app2 = drsApplication2;
                return drsCreateApp(session, profile, ref app2) == 0;
            }
            catch
            {
                profile = IntPtr.Zero;
                return false;
            }
        }

        public static int TryGetDword(IntPtr session, IntPtr profile, uint settingId, out uint value)
        {
            value = 0u;
            try
            {
                RequireDrsPermit(session);
                DrsSetting drsSetting = default(DrsSetting);
                drsSetting.Version = VersionOf<DrsSetting>(1);
                DrsSetting setting = drsSetting;
                switch (drsGetSetting(session, profile, settingId, ref setting))
                {
                case -160:
                    return 0;
                case 0:
                    if (setting.SettingId == settingId && setting.SettingType == 0 && setting.Version == VersionOf<DrsSetting>(1) && setting.SettingLocation <= 3)
                    {
                        value = setting.CurrentValue;
                        return (setting.SettingLocation == 0) ? 1 : 0;
                    }
                    goto default;
                default:
                    return -1;
                }
            }
            catch
            {
                return -1;
            }
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
                RequireDrsPermit(session);
                DrsSetting drsSetting = default(DrsSetting);
                drsSetting.Version = VersionOf<DrsSetting>(1);
                drsSetting.SettingId = settingId;
                drsSetting.SettingType = 0u;
                drsSetting.CurrentValue = value;
                DrsSetting setting = drsSetting;
                status = drsSetSetting(session, profile, ref setting);
                return status == 0;
            }
            catch
            {
                status = int.MinValue;
                return false;
            }
        }

        public static bool DeleteSetting(IntPtr session, IntPtr profile, uint settingId)
        {
            try
            {
                RequireDrsPermit(session);
                int drsDeleteSettingResult = drsDeleteSetting(session, profile, settingId);
                return drsDeleteSettingResult == 0;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureExtResolved()
        {
            if (Volatile.Read(ref extState) == 0 && Available)
            {
                try
                {
                    drsSettingName = Resolve<FnSettingName>(IdDrsGetSettingNameFromId);
                    drsEnumSettingIds = Resolve<FnEnumSettingIds>(IdDrsEnumAvailableSettingIds);
                    enumGpus = Resolve<FnEnumGpus>(IdEnumPhysicalGPUs);
                    gpuPerfDecrease = Resolve<FnGpuDwordOut>(IdGpuGetPerfDecreaseInfo);
                    sysDriverVersion = Resolve<FnDriverVersion>(IdSysGetDriverAndBranchVersion);
                }
                catch
                {
                }
                Interlocked.CompareExchange(ref extState, 1, 0);
            }
        }

        public static IntPtr[] EnumGpuHandles()
        {
            EnsureExtResolved();
            if (enumGpus == null)
            {
                return null;
            }
            try
            {
                IntPtr[] nativeHandleItems = new IntPtr[64];
                uint count;
                if (enumGpus(nativeHandleItems, out count) != 0 || count == 0 || count > 64)
                {
                    return null;
                }
                IntPtr[] nativeHandleItems2 = new IntPtr[count];
                Array.Copy(nativeHandleItems, nativeHandleItems2, (int)count);
                return nativeHandleItems2;
            }
            catch
            {
                return null;
            }
        }

        public static bool TryGetPerfDecrease(IntPtr gpu, out uint mask)
        {
            mask = 0u;
            EnsureExtResolved();
            if (gpuPerfDecrease == null || gpu == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return gpuPerfDecrease(gpu, out mask) == 0;
            }
            catch
            {
                return false;
            }
        }

        public static uint DriverVersion()
        {
            int readResult = Volatile.Read(ref driverVersionCache);
            if (readResult >= 0)
            {
                return (uint)readResult;
            }
            uint driverVersionResult = 0u;
            EnsureExtResolved();
            if (sysDriverVersion != null)
            {
                IntPtr intPtr = Marshal.AllocHGlobal(64);
                try
                {
                    uint version;
                    if (sysDriverVersion(out version, intPtr) == 0)
                    {
                        driverVersionResult = version;
                    }
                }
                catch
                {
                }
                finally
                {
                    Marshal.FreeHGlobal(intPtr);
                }
            }
            Interlocked.CompareExchange(ref driverVersionCache, (int)driverVersionResult, -1);
            return driverVersionResult;
        }
    }
}
