// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed class NvGamePlan
    {
        public bool MaxPerf;
        public string LowLatMode;
        public bool SmoothMotion;
        public bool ShaderCacheMax;
        public bool Rebar;
        public string DlssMode;
        public bool WindowedVrr;
        public bool Automatic;

        internal int AutomaticKey
        {
            get { return (MaxPerf ? 1 : 0) | (ShaderCacheMax ? 2 : 0) | (LowLatMode == "on" ? 4 : 0)
                | (WindowedVrr ? 8 : 0) | (Rebar ? 16 : 0); }
        }

        internal static NvGamePlan ForAutomatic(bool maxPerf, bool shaderCache, bool onAc, string lowLatency = "off", bool windowedVrr = false, bool rebar = false)
        {
            return new NvGamePlan { Automatic = true, MaxPerf = maxPerf && onAc,
                ShaderCacheMax = shaderCache, LowLatMode = lowLatency == "on" ? "on" : "off", DlssMode = "off", WindowedVrr = windowedVrr, Rebar = rebar };
        }

        public bool Empty
        {
            get
            {
                return !MaxPerf && !Rebar && !WindowedVrr
                    && !SmoothMotion && !ShaderCacheMax
                    && (LowLatMode == null || LowLatMode == "off")
                    && (DlssMode == null || DlssMode == "off");
            }
        }
    }

    internal static class NvDrsTweaks
    {
#if PAVISE_SELFTEST
        internal static INvDrsDriver Driver = new NativeNvDrsDriver();
        internal static Func<uint> DriverVersionForTest;
#else
        private static readonly INvDrsDriver Driver = new NativeNvDrsDriver();
#endif
        internal const string ListKey = "NvDrsList";
        private const string SnapPrefix = "NvDrs_";
        public const string KeyPState = "pstate";
        // frl 仅用于清理旧驱动限帧记录。
        public const string KeyFrl = "frl";
        public const string KeyPreRender = "prerender";
        public const string KeyLowLatCpl = "lowlatcpl";
        public const string KeyUllEnable = "ullenable";
        public const string KeySmooth = "smooth";
        public const string KeyShaderCache = "shadercache";
        public const string KeyAnsel = "ansel";
        public const string KeyRebarFeat = "rebarfeat";
        public const string KeyRebarOpt = "rebaropt";
        public const string KeyRebarSize = "rebarsize";
        public const string KeyDlssOvr = "dlssovr";
        public const string KeyDlssPreset = "dlsspreset";
        public const string KeyForceP2 = "forcep2";
        public const string KeyShaderOn = "shaderon";
        public const string KeyVrrApp = "vrrapp";

        public static readonly string[] RebarKeys = { KeyRebarFeat, KeyRebarOpt, KeyRebarSize };
        public static readonly string[] DlssKeys = { KeyDlssOvr, KeyDlssPreset };
        public static readonly string[] UltraKeys = { KeyPreRender, KeyUllEnable, KeyLowLatCpl };

        private static readonly object sync = new object();
        internal static object SessionGate { get { return sync; } }
        private static bool dlssGateLogged;
        private static bool smoothGateLogged;
        private static uint CurrentDriverVersion()
        {
#if PAVISE_SELFTEST
            return DriverVersionForTest == null ? 0 : DriverVersionForTest();
#else
            return NvApi.DriverVersion();
#endif
        }

        internal static uint SettingIdOf(string key)
        {
            switch (key)
            {
                case KeyPState: return NvApi.SettingPreferredPState;
                case KeyFrl: return NvApi.SettingFrlFps;
                case KeyPreRender: return NvApi.SettingPreRenderLimit;
                case KeyLowLatCpl: return NvApi.SettingLowLatencyCpl;
                case KeyUllEnable: return NvApi.SettingUltraLowLatEnable;
                case KeySmooth: return NvApi.SettingSmoothMotion;
                case KeyShaderCache: return NvApi.SettingShaderCacheSize;
                case KeyAnsel: return NvApi.SettingAnselAllow;
                case KeyRebarFeat: return NvApi.SettingRebarFeature;
                case KeyRebarOpt: return NvApi.SettingRebarOptions;
                case KeyRebarSize: return NvApi.SettingRebarSizeLimit;
                case KeyDlssOvr: return NvApi.SettingDlssSrOverride;
                case KeyDlssPreset: return NvApi.SettingDlssSrPreset;
                case KeyForceP2: return NvApi.SettingCudaForceP2;
                case KeyShaderOn: return NvApi.SettingShaderCacheEnable;
                case KeyVrrApp: return NvApi.SettingVrrAppOverride;
                default: return 0;
            }
        }

        private static int TryGetDwordOf(IntPtr session, IntPtr profile, string key, out uint value)
        {
            uint id = SettingIdOf(key);
            if (id == 0) { value = 0; return -1; }
            return Driver.TryGetDword(session, profile, id, out value);
        }

        private static bool SetDwordOf(IntPtr session, IntPtr profile, string key, uint value, out int status)
        {
            uint id = SettingIdOf(key);
            if (id == 0) { status = 0; return false; }
            return Driver.SetDword(session, profile, id, value, out status);
        }

        private static bool DeleteSettingOf(IntPtr session, IntPtr profile, string key)
        {
            uint id = SettingIdOf(key);
            if (id == 0) return false;
            return Driver.DeleteSetting(session, profile, id);
        }

        internal static bool IsDlssCapableName(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                gpuName, @"\bRTX\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static bool DlssGpuCapable()
        {
            try
            {
                foreach (GpuAdapter a in GpuInventory.Adapters())
                    if (a.Vendor == GpuVendor.Nvidia && IsDlssCapableName(a.Name)) return true;
            }
            catch { }
            return false;
        }

        public static bool DlssDriverSupported()
        {
            return Driver.Available && CurrentDriverVersion() >= NvApi.MinDriverForDlssOverride;
        }

        public static bool DlssOverrideSupported()
        {
            return DlssDriverSupported() && DlssGpuCapable();
        }

        internal static bool IsSmoothMotionCapableName(string gpuName)
        {
            return SmoothMotionMinDriverForName(gpuName) > 0;
        }

        internal static uint SmoothMotionMinDriverForName(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName)) return 0;
            if (System.Text.RegularExpressions.Regex.IsMatch(gpuName, @"\bRTX\s*5\d{3}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return NvApi.MinDriverForSmoothMotion50;
            return System.Text.RegularExpressions.Regex.IsMatch(gpuName, @"\bRTX\s*4\d{3}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? NvApi.MinDriverForSmoothMotion40 : 0u;
        }

        public static bool SmoothMotionGpuCapable()
        {
            return SmoothMotionMinDriver() > 0;
        }

        internal static uint SmoothMotionMinDriver()
        {
            uint min = 0;
            try
            {
                foreach (GpuAdapter a in GpuInventory.Adapters())
                {
                    if (a.Vendor != GpuVendor.Nvidia || string.IsNullOrEmpty(a.Name)) continue;
                    uint candidate = SmoothMotionMinDriverForName(a.Name);
                    if (candidate > 0 && (min == 0 || candidate < min)) min = candidate;
                }
            }
            catch { }
            return min;
        }

        public static bool SmoothMotionSupported()
        {
            uint min = SmoothMotionMinDriver();
            if (min == 0 || !Driver.Available) return false;
            return IsSmoothMotionSupported(min, true, CurrentDriverVersion(), NvApi.SmoothMotionSettingSupported);
        }

        internal static bool IsSmoothMotionSupported(uint minDriver, bool available, uint driverVersion, Func<bool> settingSupported)
        {
            if (minDriver == 0 || !available || driverVersion < minDriver || settingSupported == null) return false;
            try { return settingSupported(); }
            catch { return false; }
        }

        internal static List<KeyValuePair<string, uint>> BuildDesired(NvGamePlan plan)
        {
            var desired = new List<KeyValuePair<string, uint>>();
            if (plan == null) return desired;
            if (plan.MaxPerf)
            {
                desired.Add(new KeyValuePair<string, uint>(KeyPState, NvApi.PStatePreferMax));
                if (!plan.Automatic)
                    desired.Add(new KeyValuePair<string, uint>(KeyForceP2, NvApi.CudaForceP2Off));
            }
            string lowLat = plan.LowLatMode;
            if (lowLat == "on" || lowLat == "ultra")
            {
                desired.Add(new KeyValuePair<string, uint>(KeyPreRender, 1u));
                if (lowLat == "ultra" && !UltraDriverGone())
                {
                    desired.Add(new KeyValuePair<string, uint>(KeyUllEnable, 1u));
                    desired.Add(new KeyValuePair<string, uint>(KeyLowLatCpl, NvApi.UltraCplUltra));
                }
            }
            if (plan.SmoothMotion)
            {
                if (SmoothMotionSupported())
                    desired.Add(new KeyValuePair<string, uint>(KeySmooth, 1u));
                else if (!smoothGateLogged)
                {
                    smoothGateLogged = true;
                    Logger.Log(!SmoothMotionGpuCapable()
                        ? Lang.T("log.nvdrstweaks.1")
                        : CurrentDriverVersion() >= SmoothMotionMinDriver()
                            ? Lang.T("set.nvsmooth.unavailable") + Lang.T("log.nvdrstweaks.4")
                            : Lang.T("log.nvdrstweaks.2") + FormatDriver(SmoothMotionMinDriver())
                            + Lang.T("log.nvdrstweaks.3") + FormatDriver(CurrentDriverVersion()) + Lang.T("log.nvdrstweaks.4"));
                }
            }
            if (plan.ShaderCacheMax)
            {
                desired.Add(new KeyValuePair<string, uint>(KeyShaderOn, NvApi.ShaderCacheOn));
                desired.Add(new KeyValuePair<string, uint>(KeyShaderCache, NvApi.ShaderCacheUnlimited));
            }
            if (plan.WindowedVrr) desired.Add(new KeyValuePair<string, uint>(KeyVrrApp, NvApi.VrrAppAllow));
            if (plan.Rebar)
            {
                desired.Add(new KeyValuePair<string, uint>(KeyRebarFeat, 1u));
                desired.Add(new KeyValuePair<string, uint>(KeyRebarOpt, 1u));
                desired.Add(new KeyValuePair<string, uint>(KeyRebarSize, NvApi.RebarSizeDefault));
            }
            string dlss = plan.DlssMode;
            if (dlss == "latest" || dlss == "j" || dlss == "k")
            {
                if (DlssOverrideSupported())
                {
                    desired.Add(new KeyValuePair<string, uint>(KeyDlssOvr, 1u));
                    desired.Add(new KeyValuePair<string, uint>(KeyDlssPreset,
                        dlss == "j" ? NvApi.DlssPresetJ : dlss == "k" ? NvApi.DlssPresetK : NvApi.DlssPresetLatest));
                }
                else if (!dlssGateLogged)
                {
                    dlssGateLogged = true;
                    Logger.Log(!DlssGpuCapable()
                        ? Lang.T("log.nvdrstweaks.5")
                        : Lang.T("log.nvdrstweaks.6")
                            + FormatDriver(CurrentDriverVersion()) + Lang.T("log.nvdrstweaks.4"));
                }
            }
            return desired;
        }

        internal static string FormatDriver(uint version)
        {
            if (version == 0) return Lang.T("t.nvdrstweaks.7");
            return (version / 100) + "." + (version % 100).ToString("00");
        }

        private static string SatKey(string exeName) { return "NvDrsSat_" + exeName; }

        internal const string UllUnsupKey = "NvUllUnsupDrv";

        internal static bool UltraDriverGone()
        {
            return Settings.LoadStr(UllUnsupKey, "") == "d" + CurrentDriverVersion();
        }

        private static void MarkUltraGone()
        {
            string tag = "d" + CurrentDriverVersion();
            if (Settings.LoadStr(UllUnsupKey, "") == tag) return;
            Settings.SaveStr(UllUnsupKey, tag);
            Logger.Log(Lang.T("log.nvdrstweaks.48"));
        }

        private const char AppliedSep = '~';

        internal static string SnapOrig(string stored)
        {
            int cut = stored.IndexOf(AppliedSep);
            return cut < 0 ? stored : stored.Substring(0, cut);
        }

        internal static bool TrySnapApplied(string stored, out uint applied)
        {
            applied = 0;
            int cut = stored.IndexOf(AppliedSep);
            if (cut < 0) return false;
            string expected = stored.Substring(cut + 1);
            int comma = expected.IndexOf(',');
            return uint.TryParse(comma < 0 ? expected : expected.Substring(0, comma), out applied);
        }

        private static bool MatchesApplied(string stored, uint current)
        {
            int cut = stored.IndexOf(AppliedSep);
            if (cut < 0) return false;
            foreach (string text in stored.Substring(cut + 1).Split(','))
            {
                uint expected;
                if (uint.TryParse(text, out expected) && expected == current) return true;
            }
            return false;
        }

        private static string PrepareWriteReceipt(string stored, uint desired)
        {
            uint previous;
            string expected = desired.ToString();
            if (TrySnapApplied(stored, out previous) && previous != desired) expected += "," + previous;
            return SnapOrig(stored) + AppliedSep + expected;
        }

        private static string SatSignature(List<KeyValuePair<string, uint>> desired)
        {
            var parts = new List<string>();
            foreach (var item in desired) parts.Add(item.Key + "=" + item.Value);
            parts.Sort(StringComparer.Ordinal);
            return "d" + CurrentDriverVersion() + "|" + string.Join(",", parts.ToArray());
        }

        public static List<string> ApplyForGame(string exePath, NvGamePlan plan)
        {
            bool ignore;
            return ApplyForGame(exePath, plan, out ignore);
        }

        internal const string DriverFailureAction = "NvDriverProfile";
        private static readonly Dictionary<string, long> restoreRetryAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> restoreFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
#if PAVISE_SELFTEST
        internal static Func<long> RestoreClockForTest;
        static NvDrsTweaks()
        {
            Settings.AfterTransientResetForTest += delegate
            {
                lock (sync) { restoreRetryAt.Clear(); restoreFailures.Clear(); RestoreClockForTest = null; DriverVersionForTest = null; }
            };
        }
#endif
        private static long RestoreClock()
        {
#if PAVISE_SELFTEST
            if (RestoreClockForTest != null) return RestoreClockForTest();
#endif
            return DateTime.UtcNow.Ticks;
        }

        internal static string FailureActionFor(string key)
        {
            switch (key)
            {
                case KeyPState: case KeyForceP2: return PolicyCatalog.KeyNvMaxPerf;
                case KeyPreRender: case KeyUllEnable: case KeyLowLatCpl: return PolicyCatalog.KeyNvLowLat;
                case KeyShaderCache: case KeyShaderOn: return PolicyCatalog.KeyNvShaderCache;
                case KeySmooth: return PolicyCatalog.KeyNvSmoothMotion;
                case KeyRebarFeat: case KeyRebarOpt: case KeyRebarSize: return PolicyCatalog.KeyNvRebar;
                case KeyDlssOvr: case KeyDlssPreset: return PolicyCatalog.KeyNvDlss;
                case KeyVrrApp: return "NvVrrWindowed";
                default: return "NvSetting_" + key;
            }
        }

        private static string RestoreKey(string exeName, string action) { return exeName + "|" + action; }
        private static void ScheduleRestore(string exeName, string action)
        {
            string key = RestoreKey(exeName, action);
            int count; restoreFailures.TryGetValue(key, out count); count = Math.Min(count + 1, 5);
            restoreFailures[key] = count;
            restoreRetryAt[key] = RestoreClock() + TimeSpan.FromSeconds(Math.Min(4 << (count - 1), 60)).Ticks;
        }
        private static bool RestoreDue(string exeName, string action)
        {
            if (ActionFailureStore.IsRetryPending(action, exeName)
                || ActionFailureStore.IsRetryPending(DriverFailureAction, exeName)) return true;
            long next;
            return !restoreRetryAt.TryGetValue(RestoreKey(exeName, action), out next) || RestoreClock() >= next;
        }

        private static bool SaveGameSnapshot(string exeName, Dictionary<string, string> snapshot)
        {
            if (!Settings.SaveStrDurable(SnapPrefix + exeName, SerializeSnapshot(snapshot))) return false;
            if (snapshot.Count > 0) return AddToList(exeName);
            var games = new List<string>(Settings.LoadStr(ListKey, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            games.RemoveAll(delegate(string game) { return string.Equals(game, exeName, StringComparison.OrdinalIgnoreCase); });
            return Settings.SaveStrDurable(ListKey, string.Join(";", games.ToArray()));
        }

        private static bool RestoreGameKeys(IntPtr session, IntPtr profile, string exeName,
            Dictionary<string, string> snapshot, List<string> keys, string action, bool force)
        {
            if (keys.Count == 0) return true;
            if (!force && !RestoreDue(exeName, action)) return false;
            var done = new List<string>();
            var yieldedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool wrote = false, ok = true;
            try
            {
                foreach (string key in keys)
                {
                    string stored;
                    if (!snapshot.TryGetValue(key, out stored)) continue;
                    uint current, applied;
                    int found = TryGetDwordOf(session, profile, key, out current);
                    if (found < 0) { ok = false; continue; }
                    if (!force && TrySnapApplied(stored, out applied) && found == 1 && !MatchesApplied(stored, current))
                    { done.Add(key); yieldedKeys.Add(key); continue; }
                    string original = SnapOrig(stored);
                    if (original == "absent" ? found == 0 : found == 1 && current == ParseUInt(original))
                    { done.Add(key); continue; }
                    bool restored = original == "absent" ? found == 0 || DeleteSettingOf(session, profile, key)
                        : RestoreDword(session, profile, key, original);
                    if (restored) { done.Add(key); wrote = true; } else ok = false;
                }
                if (wrote && !Driver.SaveSession(session)) { ScheduleRestore(exeName, action); return false; }
                var verified = new List<string>();
                foreach (string key in done)
                {
                    string stored = snapshot[key];
                    uint current;
                    int found = TryGetDwordOf(session, profile, key, out current);
                    string original = SnapOrig(stored);
                    if (yieldedKeys.Contains(key) || (original == "absent" ? found == 0 : found == 1 && current == ParseUInt(original))) verified.Add(key);
                    else ok = false;
                }
                var next = new Dictionary<string, string>(snapshot, StringComparer.OrdinalIgnoreCase);
                foreach (string key in verified) next.Remove(key);
                if (!SaveGameSnapshot(exeName, next)) { ScheduleRestore(exeName, action); return false; }
                foreach (string key in verified) snapshot.Remove(key);
                Settings.SaveStr(SatKey(exeName), "");
                if (ok)
                {
                    restoreFailures.Remove(RestoreKey(exeName, action));
                    restoreRetryAt.Remove(RestoreKey(exeName, action));
                }
                else ScheduleRestore(exeName, action);
                return ok;
            }
            catch { ScheduleRestore(exeName, action); return false; }
        }

        private static List<string> SnapshotKeysFor(Dictionary<string, string> snapshot, string action)
        {
            var keys = new List<string>();
            foreach (string key in snapshot.Keys) if (FailureActionFor(key) == action) keys.Add(key);
            return keys;
        }

        private static bool TryReadGameSnapshot(string exeName, out Dictionary<string, string> snapshot)
        {
            snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string raw;
            if (!Settings.TryLoadStr(SnapPrefix + exeName, out raw) || raw == null || raw.Length > 262144) return false;
            foreach (string pair in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equal = pair.IndexOf('=');
                if (equal <= 0 || equal == pair.Length - 1) return false;
                string key = pair.Substring(0, equal), stored = pair.Substring(equal + 1);
                uint value;
                if (snapshot.ContainsKey(key) || SettingIdOf(key) == 0
                    || (SnapOrig(stored) != "absent" && !uint.TryParse(SnapOrig(stored), out value))
                    || (stored.IndexOf(AppliedSep) >= 0 && !TrySnapApplied(stored, out value))) return false;
                if (stored.IndexOf(AppliedSep) >= 0)
                {
                    string[] expected = stored.Substring(stored.IndexOf(AppliedSep) + 1).Split(',');
                    if (expected.Length > 2) return false;
                    foreach (string text in expected) if (!uint.TryParse(text, out value)) return false;
                }
                snapshot[key] = stored;
            }
            return true;
        }

        public static List<string> ApplyForGame(string exePath, NvGamePlan plan, out bool retryLater)
        {
            retryLater = false;
            if (string.IsNullOrEmpty(exePath) || !Driver.Available) return null;
            string exeName = Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(exeName)) return null;
            lock (sync)
            {
                var desired = plan == null || plan.Empty ? new List<KeyValuePair<string, uint>>() : BuildDesired(plan);
                Dictionary<string, string> snapshot;
                if (!TryReadGameSnapshot(exeName, out snapshot))
                {
                    ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery journal is unreadable or invalid",
                        "NVIDIA DRS | GPU/driver context unavailable");
                    retryLater = true; return null;
                }
                if (desired.Count == 0 && snapshot.Count == 0) return null;
                var groups = new Dictionary<string, List<KeyValuePair<string, uint>>>(StringComparer.OrdinalIgnoreCase);
                foreach (var setting in desired)
                {
                    string action = FailureActionFor(setting.Key);
                    List<KeyValuePair<string, uint>> group;
                    if (!groups.TryGetValue(action, out group)) { group = new List<KeyValuePair<string, uint>>(); groups[action] = group; }
                    group.Add(setting);
                }
                uint driverVersion = CurrentDriverVersion();
                string context = "NVIDIA DRS | " + (driverVersion == 0 ? "GPU/driver context unavailable" : "driver " + FormatDriver(driverVersion));
                bool commonPending = ActionFailureStore.IsRetryPending(DriverFailureAction, exeName);
                bool commonAllowed = !ActionFailureStore.IsBlocked(DriverFailureAction, exeName);
                bool anyAllowed = false, pending = commonPending;
                foreach (string action in groups.Keys)
                {
                    anyAllowed |= !ActionFailureStore.IsBlocked(action, exeName);
                    pending |= ActionFailureStore.IsRetryPending(action, exeName);
                }
                string signature = SatSignature(desired);
                bool allAllowed = commonAllowed;
                foreach (string action in groups.Keys) allAllowed &= !ActionFailureStore.IsBlocked(action, exeName);
                if (desired.Count > 0 && allAllowed && !pending && Settings.LoadStr(SatKey(exeName), "") == signature)
                    return new List<string>();
                bool application = commonAllowed && anyAllowed;
                bool commonAdmitted = false;
                bool commonCleanupSafe = true;
                string reason;
                if (application && !(commonPending && snapshot.Count > 0))
                    commonAdmitted = ActionFailureStore.TryBeginApply(DriverFailureAction, exeName, out reason);
                if (application && !commonAdmitted && !(commonPending && snapshot.Count > 0)) application = false;
                if (!application && snapshot.Count == 0) return null;
                IntPtr session = IntPtr.Zero;
                try
                {
                    if (!Driver.TryOpenSession(out session))
                    {
                        if (commonAdmitted) ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA driver session could not be opened", context);
                        else if (commonPending) ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery before retry could not open the driver session", context);
                        else if (snapshot.Count > 0) ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery could not open the driver session", context);
                        retryLater = true; return null;
                    }
                    IntPtr profile;
                    if (!Driver.FindOrCreateAppProfile(session, exeName, out profile))
                    {
                        if (commonAdmitted || commonPending || snapshot.Count > 0) ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA game profile could not be opened", context);
                        retryLater = true; return null;
                    }
                    var failed = new List<string>();
                    if (commonPending && snapshot.Count > 0)
                    {
                        if (!RestoreGameKeys(session, profile, exeName, snapshot, new List<string>(snapshot.Keys), DriverFailureAction, false))
                        {
                            ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery before retry failed", context);
                            retryLater = true; return null;
                        }
                        commonAdmitted = ActionFailureStore.TryBeginApply(DriverFailureAction, exeName, out reason);
                        application = commonAdmitted && anyAllowed;
                    }
                    var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var setting in desired)
                        if (application && !ActionFailureStore.IsBlocked(FailureActionFor(setting.Key), exeName)) keep.Add(setting.Key);
                    var staleGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (string key in snapshot.Keys)
                        if (!keep.Contains(key))
                        {
                            string action = FailureActionFor(key);
                            List<string> stale;
                            if (!staleGroups.TryGetValue(action, out stale)) { stale = new List<string>(); staleGroups[action] = stale; }
                            stale.Add(key);
                        }
                    foreach (var stale in staleGroups)
                    {
                        bool due = RestoreDue(exeName, stale.Key);
                        if (!RestoreGameKeys(session, profile, exeName, snapshot, stale.Value, stale.Key, false))
                        {
                            if (due) ActionFailureStore.Trip(stale.Key, exeName, "NVIDIA stale setting recovery remains incomplete", context);
                            failed.AddRange(stale.Value); retryLater = true;
                        }
                        else ActionFailureStore.CancelApply(stale.Key, exeName);
                    }
                    foreach (var group in groups)
                    {
                        string action = group.Key;
                        if (!application || ActionFailureStore.IsBlocked(action, exeName))
                        { foreach (var setting in group.Value) if (!failed.Contains(setting.Key)) failed.Add(setting.Key); continue; }
                        if (ActionFailureStore.IsRetryPending(action, exeName)
                            && !RestoreGameKeys(session, profile, exeName, snapshot, SnapshotKeysFor(snapshot, action), action, false))
                        {
                            ActionFailureStore.Trip(action, exeName, "NVIDIA action recovery before retry failed", context);
                            foreach (var setting in group.Value) if (!failed.Contains(setting.Key)) failed.Add(setting.Key);
                            retryLater = true; continue;
                        }
                        if (!ActionFailureStore.TryBeginApply(action, exeName, out reason))
                        { foreach (var setting in group.Value) if (!failed.Contains(setting.Key)) failed.Add(setting.Key); continue; }
                        bool fault = false, wrote = false, skipped = false, attemptedWrite = false;
                        string faultReason = "";
                        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            foreach (var setting in group.Value)
                            {
                                if (snapshot.ContainsKey(setting.Key)) continue;
                                uint original; int found = TryGetDwordOf(session, profile, setting.Key, out original);
                                if (found < 0) { fault = true; faultReason = "NVIDIA original setting could not be read: " + setting.Key; break; }
                                snapshot[setting.Key] = found == 1 ? original.ToString() : "absent";
                            }
                            if (!fault && !SaveGameSnapshot(exeName, snapshot))
                            {
                                ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery journal could not be saved", context);
                                ActionFailureStore.CancelApply(action, exeName);
                                ActionFailureStore.WithdrawRetry(action, exeName);
                                RestoreGameKeys(session, profile, exeName, snapshot, SnapshotKeysFor(snapshot, action), action, false);
                                application = false; retryLater = true;
                                foreach (var setting in group.Value) if (!failed.Contains(setting.Key)) failed.Add(setting.Key);
                                continue;
                            }
                            if (!fault) foreach (var setting in group.Value)
                            {
                                uint current;
                                int currentState = TryGetDwordOf(session, profile, setting.Key, out current);
                                if (currentState < 0)
                                { fault = true; faultReason = "NVIDIA current setting became unreadable: " + setting.Key; break; }
                                if (currentState == 1 && current == setting.Value) continue;
                                int status;
                                snapshot[setting.Key] = PrepareWriteReceipt(snapshot[setting.Key], setting.Value);
                                if (!SaveGameSnapshot(exeName, snapshot))
                                    throw new IOException("NVIDIA write-ahead receipt could not be saved: " + setting.Key);
                                attemptedWrite = true;
                                if (SetDwordOf(session, profile, setting.Key, setting.Value, out status)) wrote = true;
                                else if (status == NvApi.StatusSettingNotFound)
                                {
                                    unsupported.Add(setting.Key);
                                    if (setting.Key == KeyUllEnable || setting.Key == KeyLowLatCpl) MarkUltraGone();
                                }
                                else { fault = true; faultReason = "NVIDIA setting write failed: " + setting.Key + " (" + status + ")"; break; }
                            }
                            if (!fault && wrote && !Driver.SaveSession(session))
                            {
                                ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA driver session could not be saved", context);
                                bool rolledBack = RestoreGameKeys(session, profile, exeName, snapshot, SnapshotKeysFor(snapshot, action), action, true);
                                if (rolledBack)
                                {
                                    ActionFailureStore.CancelApply(action, exeName);
                                    ActionFailureStore.WithdrawRetry(action, exeName);
                                }
                                else
                                {
                                    ActionFailureStore.Trip(action, exeName, "NVIDIA driver save failed and action rollback remains incomplete", context);
                                    commonCleanupSafe = false;
                                }
                                application = false; retryLater = true;
                                foreach (var setting in group.Value) if (!failed.Contains(setting.Key)) failed.Add(setting.Key);
                                continue;
                            }
                            if (!fault) foreach (var setting in group.Value)
                            {
                                if (unsupported.Contains(setting.Key)) continue;
                                uint readback;
                                if (TryGetDwordOf(session, profile, setting.Key, out readback) != 1 || readback != setting.Value)
                                { fault = true; faultReason = "NVIDIA setting verification failed: " + setting.Key; }
                            }
                            if (!fault && unsupported.Count == group.Value.Count) skipped = true;
                            if (!fault && !skipped)
                            {
                                foreach (var setting in group.Value)
                                {
                                    string stored;
                                    if (unsupported.Contains(setting.Key)) continue;
                                    if (snapshot.TryGetValue(setting.Key, out stored)) snapshot[setting.Key] = SnapOrig(stored) + AppliedSep + setting.Value;
                                }
                                if (!SaveGameSnapshot(exeName, snapshot))
                                { fault = true; faultReason = "NVIDIA verified setting receipt could not be saved"; }
                            }
                        }
                        catch (NotSupportedException) { skipped = true; }
                        catch (OperationCanceledException) { skipped = true; }
                        catch (Exception ex) { fault = true; faultReason = ex.GetType().Name + ": " + ex.Message; }
                        if (fault)
                        {
                            ActionFailureStore.Trip(action, exeName, faultReason, context);
                            bool rolledBack = RestoreGameKeys(session, profile, exeName, snapshot, SnapshotKeysFor(snapshot, action), action, attemptedWrite);
                            if (rolledBack) ActionFailureStore.CancelApply(action, exeName);
                            else ActionFailureStore.Trip(action, exeName, faultReason + "; rollback remains incomplete", context);
                            foreach (var setting in group.Value) if (!failed.Contains(setting.Key)) failed.Add(setting.Key);
                            Logger.Warn(action + " | " + exeName + " | " + faultReason);
                            retryLater = true;
                        }
                        else if (skipped)
                        {
                            bool restored = RestoreGameKeys(session, profile, exeName, snapshot, SnapshotKeysFor(snapshot, action), action, attemptedWrite);
                            if (restored) ActionFailureStore.CancelApply(action, exeName);
                            else ActionFailureStore.Trip(action, exeName, "NVIDIA recovery after skip or cancellation remains incomplete", context);
                            foreach (var setting in group.Value) if (!failed.Contains(setting.Key)) failed.Add(setting.Key);
                        }
                        else if (!ActionFailureStore.CompleteApply(action, exeName))
                        {
                            bool rolledBack = RestoreGameKeys(session, profile, exeName, snapshot, SnapshotKeysFor(snapshot, action), action, true);
                            if (rolledBack) ActionFailureStore.CancelApply(action, exeName);
                            else ActionFailureStore.Trip(action, exeName, "NVIDIA completion could not be persisted and rollback remains incomplete", context);
                            foreach (var setting in group.Value) if (!failed.Contains(setting.Key)) failed.Add(setting.Key);
                            retryLater = true;
                        }
                        foreach (string key in unsupported) if (!failed.Contains(key)) failed.Add(key);
                    }
                    if (commonAdmitted && application && !ActionFailureStore.CompleteApply(DriverFailureAction, exeName))
                    {
                        application = false; retryLater = true;
                        commonCleanupSafe = RestoreGameKeys(session, profile, exeName, snapshot, new List<string>(snapshot.Keys), DriverFailureAction, false);
                        if (!commonCleanupSafe) ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA session completion could not be persisted and rollback remains incomplete", context);
                    }
                    bool satisfied = failed.Count == 0 && desired.Count > 0 && application;
                    Settings.SaveStr(SatKey(exeName), satisfied ? signature : "");
                    return failed;
                }
                catch (NotSupportedException) { if (commonAdmitted) ActionFailureStore.CancelApply(DriverFailureAction, exeName); return null; }
                catch (OperationCanceledException) { if (commonAdmitted) ActionFailureStore.CancelApply(DriverFailureAction, exeName); return null; }
                catch (Exception ex)
                {
                    if (commonAdmitted) ActionFailureStore.Trip(DriverFailureAction, exeName, ex.GetType().Name + ": " + ex.Message, context);
                    retryLater = true; return null;
                }
                finally
                {
                    if (commonAdmitted && commonCleanupSafe) ActionFailureStore.CancelApply(DriverFailureAction, exeName);
                    if (session != IntPtr.Zero) try { Driver.CloseSession(session); } catch { }
                }
            }
        }

        private static bool DesiredHasDlss(List<KeyValuePair<string, uint>> desired)
        {
            foreach (var item in desired) if (item.Key == KeyDlssOvr) return true;
            return false;
        }

        internal static string DlssText(string mode)
        {
            if (mode == "latest") return Lang.T("t.nvdrstweaks.23");
            if (mode == "j") return Lang.T("t.nvdrstweaks.24");
            if (mode == "k") return Lang.T("t.nvdrstweaks.25");
            return Lang.T("t.nvdrstweaks.26");
        }

        internal static bool ContainsAny(List<string> list, string[] keys)
        {
            foreach (string key in keys) if (list.Contains(key)) return true;
            return false;
        }

        public static void RestoreKind(string key)
        {
            if (!Driver.Available) return;
            lock (sync)
            {
                string[] games = Settings.LoadStr(ListKey, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string exeName in games)
                {
                    Dictionary<string, string> snapshot;
                    if (!TryReadGameSnapshot(exeName, out snapshot))
                    { ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery journal is unreadable or invalid"); continue; }
                    if (!snapshot.ContainsKey(key)) continue;
                    string action = FailureActionFor(key);
                    bool due = RestoreDue(exeName, action);
                    if (!due) continue;
                    IntPtr session = IntPtr.Zero;
                    try
                    {
                        if (!Driver.TryOpenSession(out session))
                        { ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery could not open the driver session"); continue; }
                        IntPtr profile;
                        if (!Driver.FindOrCreateAppProfile(session, exeName, out profile))
                        { ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery could not open the game profile"); continue; }
                        if (!RestoreGameKeys(session, profile, exeName, snapshot, new List<string> { key }, action, false))
                            ActionFailureStore.Trip(action, exeName, "NVIDIA setting recovery remains incomplete");
                        else if (SnapshotKeysFor(snapshot, action).Count == 0) ActionFailureStore.CancelApply(action, exeName);
                    }
                    catch (NotSupportedException) { }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { ActionFailureStore.Trip(DriverFailureAction, exeName, ex.GetType().Name + ": " + ex.Message); }
                    finally { if (session != IntPtr.Zero) try { Driver.CloseSession(session); } catch { } }
                }
            }
        }

        public static void RestoreKinds(string[] keys)
        {
            foreach (string key in keys) RestoreKind(key);
        }

        public static bool HasSnapshotFor(string key)
        {
            lock (sync)
            {
                string[] games = Settings.LoadStr(ListKey, "")
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string exeName in games)
                    if (ParseSnapshot(Settings.LoadStr(SnapPrefix + exeName, "")).ContainsKey(key))
                        return true;
                return false;
            }
        }

        public static bool HasGameResidue
        {
            get { return Settings.LoadStr(ListKey, "").Length > 0; }
        }

        public static bool RestoreAllGames()
        {
            lock (sync)
            {
                string[] games = Settings.LoadStr(ListKey, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (games.Length == 0) return true;
                if (!Driver.Available)
                {
                    if (!NvidiaAbsent()) return false;
                    int dropped = DropAllSnapshots();
                    if (dropped > 0) Logger.Log(Lang.T("log.nvdrstweaks.34") + dropped + Lang.T("log.nvdrstweaks.35"));
                    return Settings.LoadStr(ListKey, "").Length == 0;
                }
                bool allOk = true;
                foreach (string exeName in games)
                {
                    Dictionary<string, string> snapshot;
                    if (!TryReadGameSnapshot(exeName, out snapshot))
                    {
                        ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery journal is unreadable or invalid");
                        allOk = false; continue;
                    }
                    if (snapshot.Count == 0)
                    {
                        if (!SaveGameSnapshot(exeName, snapshot)) allOk = false;
                        Settings.SaveStr(SatKey(exeName), ""); continue;
                    }
                    IntPtr session = IntPtr.Zero;
                    try
                    {
                        if (!Driver.TryOpenSession(out session))
                        { ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery could not open the driver session"); allOk = false; continue; }
                        IntPtr profile;
                        if (!Driver.FindOrCreateAppProfile(session, exeName, out profile))
                        { ActionFailureStore.Trip(DriverFailureAction, exeName, "NVIDIA recovery could not open the game profile"); allOk = false; continue; }
                        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (string key in snapshot.Keys)
                        {
                            string action = FailureActionFor(key);
                            List<string> keys;
                            if (!groups.TryGetValue(action, out keys)) { keys = new List<string>(); groups[action] = keys; }
                            keys.Add(key);
                        }
                        foreach (var group in groups)
                        {
                            bool due = RestoreDue(exeName, group.Key);
                            bool restored = RestoreGameKeys(session, profile, exeName, snapshot, group.Value, group.Key, false);
                            if (restored) ActionFailureStore.CancelApply(group.Key, exeName);
                            else
                            {
                                allOk = false;
                                if (due) ActionFailureStore.Trip(group.Key, exeName, "NVIDIA action recovery remains incomplete");
                            }
                        }
                        if (snapshot.Count == 0) ActionFailureStore.CancelApply(DriverFailureAction, exeName);
                    }
                    catch (NotSupportedException) { allOk = false; }
                    catch (OperationCanceledException) { allOk = false; }
                    catch (Exception ex) { ActionFailureStore.Trip(DriverFailureAction, exeName, ex.GetType().Name + ": " + ex.Message); allOk = false; }
                    finally { if (session != IntPtr.Zero) try { Driver.CloseSession(session); } catch { } }
                }
                return allOk;
            }
        }

        private static bool NvidiaAbsent()
        {
            GpuAdapter[] all = GpuInventory.Adapters();
            if (all == null || all.Length == 0) return false;
            foreach (GpuAdapter a in all) if (a.Vendor == GpuVendor.Nvidia) return false;
            return true;
        }

        private static int DropAllSnapshots()
        {
            lock (sync)
            {
                string[] games = Settings.LoadStr(ListKey, "")
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                int n = 0;
                foreach (string exeName in games)
                {
                    if (Settings.LoadStr(SnapPrefix + exeName, "").Length > 0) n++;
                    Settings.SaveStr(SnapPrefix + exeName, "");
                    Settings.SaveStr(SatKey(exeName), "");
                }
                Settings.SaveStr(ListKey, "");
                return n;
            }
        }

        private static bool RestoreDword(IntPtr session, IntPtr profile, string key, string orig)
        {
            int status;
            return SetDwordOf(session, profile, key, ParseUInt(orig), out status)
                || status == NvApi.StatusSettingNotFound;
        }

        private static uint ParseUInt(string value)
        {
            uint parsed;
            return uint.TryParse(value, out parsed) ? parsed : 0;
        }

        internal static Dictionary<string, string> ParseSnapshot(string raw)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw)) return map;
            foreach (string pair in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0) map[pair.Substring(0, eq)] = pair.Substring(eq + 1);
            }
            return map;
        }

        internal static string SerializeSnapshot(Dictionary<string, string> map)
        {
            var parts = new List<string>();
            foreach (var kv in map) parts.Add(kv.Key + "=" + kv.Value);
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(";", parts.ToArray());
        }

        private static bool AddToList(string exeName)
        {
            var games = new List<string>(Settings.LoadStr(ListKey, "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (string existing in games)
                if (string.Equals(existing, exeName, StringComparison.OrdinalIgnoreCase)) return true;
            games.Add(exeName);
            return Settings.SaveStrDurable(ListKey, string.Join(";", games.ToArray()));
        }

        private static void RemoveFromList(string exeName)
        {
            var games = new List<string>(Settings.LoadStr(ListKey, "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            games.RemoveAll(delegate(string g)
            {
                return string.Equals(g, exeName, StringComparison.OrdinalIgnoreCase);
            });
            Settings.SaveStr(ListKey, string.Join(";", games.ToArray()));
        }
    }
}
