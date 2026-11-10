using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PaviseApp
{
    internal static class NvDrsTweaks
    {
        internal const string ListKey = "NvDrsList";

        private const string SnapPrefix = "NvDrs_";

        public const string KeyPState = "pstate";

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

        internal const string UllUnsupKey = "NvUllUnsupDrv";

        internal const string DriverFailureAction = "NvDriverProfile";

        private const string InvalidJournalReason = "NVIDIA recovery journal is unreadable or invalid";

        private static readonly INvDrsDriver Driver = new NativeNvDrsDriver();

        public static readonly string[] RebarKeys = new string[3] { "rebarfeat", "rebaropt", "rebarsize" };

        public static readonly string[] DlssKeys = new string[2] { "dlssovr", "dlsspreset" };

        public static readonly string[] UltraKeys = new string[3] { "prerender", "ullenable", "lowlatcpl" };

        private static readonly object sync = GameNvNativeGate.SyncRoot;

        private static bool dlssGateLogged;

        private static bool smoothGateLogged;

        private static readonly Dictionary<string, long> restoreRetryAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, int> restoreFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> automaticPhases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> automaticSessions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static long automaticSession;

        internal static void BeginAutomaticSession(long session)
        {
            lock (sync)
            {
                if (session <= automaticSession) return;
                automaticSession = session;
                foreach (string name in new List<string>(automaticPhases.Keys))
                {
                    string phase = automaticPhases[name];
                    automaticPhases[name] = session + phase.Substring(phase.IndexOf(':'));
                    automaticSessions[name] = session;
                }
            }
        }

        // 同一真实游戏会话中重新访问配置时，保留该配置原有预算。
        internal static void SyncAutomaticPhase(string executable, long session, int configuration)
        {
            if (string.IsNullOrEmpty(executable)) return;
            lock (sync)
            {
                if (session < automaticSession) return;
                BeginAutomaticSession(session);
                string name = Path.GetFileName(executable);
                long previous;
                if (automaticSessions.TryGetValue(name, out previous) && session < previous) return;
                automaticSessions[name] = session;
                automaticPhases[name] = session + ":" + configuration;
            }
        }

        internal static object SessionGate
        {
            get
            {
                return sync;
            }
        }

        private static void IsolateInvalidJournal(string executable)
        {
            // 隔离同一故障期间，保留最初失败及其恢复记录。
            if (ActionFailureStore.IsBlocked(DriverFailureAction, executable)
                && ActionFailureStore.FailureReason(DriverFailureAction, executable) == InvalidJournalReason) return;
            ActionFailureStore.Trip(DriverFailureAction, executable, InvalidJournalReason,
                "NVIDIA DRS | GPU/driver context unavailable");
        }

        public static bool HasGameResidue
        {
            get
            {
                return Settings.LoadStr("NvDrsList", "").Length > 0;
            }
        }

        internal static bool PlanSatisfied(string executable, NvGamePlan plan)
        {
            if (string.IsNullOrEmpty(executable) || plan == null || plan.Empty) return false;
            lock (sync)
            {
                string name = Path.GetFileName(executable);
                List<KeyValuePair<string, uint>> desired = BuildDesired(plan);
                if (desired.Count == 0 || ActionFailureStore.IsBlocked(DriverFailureAction, name)) return false;
                foreach (var setting in desired) if (ActionFailureStore.IsBlocked(FailureActionFor(setting.Key), name)) return false;
                return Settings.LoadStr(SatKey(name), "") == SatSignature(desired);
            }
        }

        private static uint CurrentDriverVersion()
        {
            return NvApi.DriverVersion();
        }

        internal static uint SettingIdOf(string key)
        {
            switch (key)
            {
            case "pstate":
                return 274197361u;
            case "frl":
                return 277041154u;
            case "prerender":
                return 8102046u;
            case "lowlatcpl":
                return 390467u;
            case "ullenable":
                return 277041152u;
            case "smooth":
                return 2966652096u;
            case "shadercache":
                return 11306135u;
            case "ansel":
                return 271965065u;
            case "rebarfeat":
                return 983226u;
            case "rebaropt":
                return 983227u;
            case "rebarsize":
                return 983295u;
            case "dlssovr":
                return 283385345u;
            case "dlsspreset":
                return 283385331u;
            case "forcep2":
                return 1343646814u;
            case "shaderon":
                return 1675263u;
            case "vrrapp":
                return 279476687u;
            default:
                return 0u;
            }
        }

        private static int TryGetDwordOf(IntPtr session, IntPtr profile, string key, out uint value)
        {
            uint settingId = SettingIdOf(key);
            if (settingId == 0)
            {
                value = 0u;
                return -1;
            }
            return Driver.TryGetDword(session, profile, settingId, out value);
        }

        private static bool SetDwordOf(IntPtr session, IntPtr profile, string key, uint value, out int status)
        {
            uint settingId = SettingIdOf(key);
            if (settingId == 0)
            {
                status = 0;
                return false;
            }
            return Driver.SetDword(session, profile, settingId, value, out status);
        }

        private static bool DeleteSettingOf(IntPtr session, IntPtr profile, string key)
        {
            uint settingId = SettingIdOf(key);
            if (settingId == 0)
            {
                return false;
            }
            return Driver.DeleteSetting(session, profile, settingId);
        }

        internal static bool IsDlssCapableName(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName))
            {
                return false;
            }
            return Regex.IsMatch(gpuName, "\\bRTX\\b", RegexOptions.IgnoreCase);
        }

        public static bool DlssGpuCapable()
        {
            try
            {
                foreach (GpuAdapter gpuAdapter in GpuInventory.Adapters())
                {
                    if (gpuAdapter.Vendor == GpuVendor.Nvidia && IsDlssCapableName(gpuAdapter.Name))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public static bool DlssDriverSupported()
        {
            if (Driver.Available)
            {
                return CurrentDriverVersion() >= 56614;
            }
            return false;
        }

        public static bool DlssOverrideSupported()
        {
            if (DlssDriverSupported())
            {
                return DlssGpuCapable();
            }
            return false;
        }

        internal static bool IsSmoothMotionCapableName(string gpuName)
        {
            return SmoothMotionMinDriverForName(gpuName) != 0;
        }

        internal static uint SmoothMotionMinDriverForName(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName))
            {
                return 0u;
            }
            if (Regex.IsMatch(gpuName, "\\bRTX\\s*5\\d{3}\\b", RegexOptions.IgnoreCase))
            {
                return 57186u;
            }
            if (!Regex.IsMatch(gpuName, "\\bRTX\\s*4\\d{3}\\b", RegexOptions.IgnoreCase))
            {
                return 0u;
            }
            return 58108u;
        }

        public static bool SmoothMotionGpuCapable()
        {
            return SmoothMotionMinDriver() != 0;
        }

        internal static uint SmoothMotionMinDriver()
        {
            uint minimumDriver = 0u;
            try
            {
                foreach (GpuAdapter gpuAdapter in GpuInventory.Adapters())
                {
                    if (gpuAdapter.Vendor == GpuVendor.Nvidia && !string.IsNullOrEmpty(gpuAdapter.Name))
                    {
                        uint adapterMinimumDriver = SmoothMotionMinDriverForName(gpuAdapter.Name);
                        if (adapterMinimumDriver != 0 && (minimumDriver == 0 || adapterMinimumDriver < minimumDriver))
                        {
                            minimumDriver = adapterMinimumDriver;
                        }
                    }
                }
            }
            catch
            {
            }
            return minimumDriver;
        }

        public static bool SmoothMotionSupported()
        {
            uint minimumDriver = SmoothMotionMinDriver();
            if (minimumDriver == 0 || !Driver.Available)
            {
                return false;
            }
            return IsSmoothMotionSupported(minimumDriver, true, CurrentDriverVersion(), NvApi.SmoothMotionSettingSupported);
        }

        internal static bool IsSmoothMotionSupported(uint minDriver, bool available, uint driverVersion, Func<bool> settingSupported)
        {
            if (minDriver == 0 || !available || driverVersion < minDriver || settingSupported == null)
            {
                return false;
            }
            try
            {
                return settingSupported();
            }
            catch
            {
                return false;
            }
        }

        internal static List<KeyValuePair<string, uint>> BuildDesired(NvGamePlan plan)
        {
            List<KeyValuePair<string, uint>> keyValuePairs = new List<KeyValuePair<string, uint>>();
            if (plan == null)
            {
                return keyValuePairs;
            }
            if (plan.MaxPerf)
            {
                keyValuePairs.Add(new KeyValuePair<string, uint>("pstate", 1u));
                if (!plan.Automatic)
                {
                    keyValuePairs.Add(new KeyValuePair<string, uint>("forcep2", 0u));
                }
            }
            string lowLatMode = plan.LowLatMode;
            if (lowLatMode == "on" || lowLatMode == "ultra")
            {
                keyValuePairs.Add(new KeyValuePair<string, uint>("prerender", 1u));
                if (lowLatMode == "ultra" && !UltraDriverGone())
                {
                    keyValuePairs.Add(new KeyValuePair<string, uint>("ullenable", 1u));
                    keyValuePairs.Add(new KeyValuePair<string, uint>("lowlatcpl", 2u));
                }
            }
            if (plan.SmoothMotion)
            {
                if (SmoothMotionSupported())
                {
                    keyValuePairs.Add(new KeyValuePair<string, uint>("smooth", 1u));
                }
                else if (!smoothGateLogged)
                {
                    smoothGateLogged = true;
                    Logger.Log((!SmoothMotionGpuCapable()) ? Lang.T("log.nvdrstweaks.1", 0) : ((CurrentDriverVersion() >= SmoothMotionMinDriver()) ? (Lang.T("set.nvsmooth.unavailable", 0) + Lang.T("log.nvdrstweaks.4", 0)) : (Lang.T("log.nvdrstweaks.2", 0) + FormatDriver(SmoothMotionMinDriver()) + Lang.T("log.nvdrstweaks.3", 0) + FormatDriver(CurrentDriverVersion()) + Lang.T("log.nvdrstweaks.4", 0))));
                }
            }
            if (plan.ShaderCacheMax)
            {
                keyValuePairs.Add(new KeyValuePair<string, uint>("shaderon", 1u));
                keyValuePairs.Add(new KeyValuePair<string, uint>("shadercache", uint.MaxValue));
            }
            if (plan.WindowedVrr)
            {
                keyValuePairs.Add(new KeyValuePair<string, uint>("vrrapp", 0u));
            }
            if (plan.Rebar)
            {
                keyValuePairs.Add(new KeyValuePair<string, uint>("rebarfeat", 1u));
                keyValuePairs.Add(new KeyValuePair<string, uint>("rebaropt", 1u));
                keyValuePairs.Add(new KeyValuePair<string, uint>("rebarsize", 1073741824u));
            }
            string dlssMode = plan.DlssMode;
            switch (dlssMode)
            {
            case "latest":
            case "j":
            case "k":
                if (DlssOverrideSupported())
                {
                    keyValuePairs.Add(new KeyValuePair<string, uint>("dlssovr", 1u));
                    keyValuePairs.Add(new KeyValuePair<string, uint>("dlsspreset", (dlssMode == "j") ? 10u : ((dlssMode == "k") ? 11u : 16777215u)));
                }
                else if (!dlssGateLogged)
                {
                    dlssGateLogged = true;
                    Logger.Log((!DlssGpuCapable()) ? Lang.T("log.nvdrstweaks.5", 0) : (Lang.T("log.nvdrstweaks.6", 0) + FormatDriver(CurrentDriverVersion()) + Lang.T("log.nvdrstweaks.4", 0)));
                }
                break;
            }
            return keyValuePairs;
        }

        internal static string FormatDriver(uint version)
        {
            if (version == 0)
            {
                return Lang.T("t.nvdrstweaks.7");
            }
            return version / 100 + "." + (version % 100).ToString("00");
        }

        private static string SatKey(string exeName)
        {
            return "NvDrsSat_" + exeName;
        }

        internal static bool UltraDriverGone()
        {
            return Settings.LoadStr("NvUllUnsupDrv", "") == "d" + CurrentDriverVersion();
        }

        private static void MarkUltraGone()
        {
            string currentDriverVersionOffset = "d" + CurrentDriverVersion();
            if (Settings.LoadStr("NvUllUnsupDrv", "") != currentDriverVersionOffset)
            {
                Settings.SaveStr("NvUllUnsupDrv", currentDriverVersionOffset);
                Logger.Log(Lang.T("log.nvdrstweaks.48", 0));
            }
        }

        internal static string SnapOrig(string stored)
        {
            int matchIndex = stored.IndexOf('~');
            if (matchIndex >= 0)
            {
                return stored.Substring(0, matchIndex);
            }
            return stored;
        }

        internal static bool TrySnapApplied(string stored, out uint applied)
        {
            applied = 0u;
            int matchIndex = stored.IndexOf('~');
            if (matchIndex < 0)
            {
                return false;
            }
            string substringResult = stored.Substring(matchIndex + 1);
            int matchIndex2 = substringResult.IndexOf(',');
            return uint.TryParse((matchIndex2 < 0) ? substringResult : substringResult.Substring(0, matchIndex2), out applied);
        }

        private static bool MatchesApplied(string stored, uint current)
        {
            int matchIndex = stored.IndexOf('~');
            if (matchIndex < 0)
            {
                return false;
            }
            foreach (string s in stored.Substring(matchIndex + 1).Split(','))
            {
                uint result;
                if (uint.TryParse(s, out result) && result == current)
                {
                    return true;
                }
            }
            return false;
        }

        private static string PrepareWriteReceipt(string stored, uint desired)
        {
            string desiredText = desired.ToString();
            uint applied;
            if (TrySnapApplied(stored, out applied) && applied != desired)
            {
                desiredText = desiredText + "," + applied;
            }
            return SnapOrig(stored) + '~' + desiredText;
        }

        private static string SatSignature(List<KeyValuePair<string, uint>> desired)
        {
            List<string> texts = new List<string>();
            foreach (KeyValuePair<string, uint> item in desired)
            {
                texts.Add(item.Key + "=" + item.Value);
            }
            texts.Sort(StringComparer.Ordinal);
            return "d" + CurrentDriverVersion() + "|" + string.Join(",", texts.ToArray());
        }

        public static List<string> ApplyForGame(string exePath, NvGamePlan plan)
        {
            bool retryLater;
            return ApplyForGame(exePath, plan, out retryLater);
        }

        private static long RestoreClock()
        {
            return AutoCores.NowTicks;
        }

        internal static string FailureActionFor(string key)
        {
            switch (key)
            {
            case "pstate":
            case "forcep2":
                return "NvMaxPerf";
            case "prerender":
            case "ullenable":
            case "lowlatcpl":
                return "NvLowLat";
            case "shadercache":
            case "shaderon":
                return "NvShaderCache";
            case "smooth":
                return "NvSmoothMotion";
            case "rebarfeat":
            case "rebaropt":
            case "rebarsize":
                return "NvRebar";
            case "dlssovr":
            case "dlsspreset":
                return "NvDlss";
            case "vrrapp":
                return "NvVrrWindowed";
            default:
                return "NvSetting_" + key;
            }
        }

        private static string RestoreKey(string exeName, string action)
        {
            string phase;
            if (!automaticPhases.TryGetValue(exeName, out phase))
            {
                phase = automaticSession + ":startup";
                automaticPhases[exeName] = phase;
                automaticSessions[exeName] = automaticSession;
            }
            return exeName + "|" + phase + "|" + action;
        }

        private static void ScheduleRestore(string exeName, string action)
        {
            string key = RestoreKey(exeName, action);
            int value;
            restoreFailures.TryGetValue(key, out value);
            value = Math.Min(value + 1, 3);
            restoreFailures[key] = value;
            restoreRetryAt[key] = RestoreClock() + TimeSpan.FromSeconds(4 << value - 1).Ticks;
            ReportRestoreSchedule(exeName, action);
            if (value == 3) Logger.Log("NVIDIA 恢复连续失败三次后暂停： " + exeName + " / " + action + "；保留恢复回执");
        }

        private static bool RestoreExhausted(string exeName, string action)
        {
            int failures;
            return restoreFailures.TryGetValue(RestoreKey(exeName, action), out failures) && failures >= 3;
        }

        private static bool RestoreDue(string exeName, string action)
        {
            if (RestoreExhausted(exeName, action)) { ReportRestoreSchedule(exeName, action); return false; }
            long value;
            if (restoreRetryAt.TryGetValue(RestoreKey(exeName, action), out value))
            {
                if (RestoreClock() < value) { ReportRestoreSchedule(exeName, action); return false; }
            }
            return true;
        }

        private static void ReportRestoreSchedule(string executable, string action)
        {
            if (RestoreExhausted(executable, action)) { RecoveryAttemptTrace.Exhausted(); return; }
            long due;
            if (restoreRetryAt.TryGetValue(RestoreKey(executable, action), out due))
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(Math.Max(0, due - RestoreClock())));
        }

        private static bool SaveGameSnapshot(string exeName, Dictionary<string, string> snapshot)
        {
            if (!Settings.SaveStrDurable("NvDrs_" + exeName, SerializeSnapshot(snapshot)))
            {
                return false;
            }
            if (snapshot.Count > 0)
            {
                return AddToList(exeName);
            }
            List<string> splitSnapshot = new List<string>(Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            splitSnapshot.RemoveAll((string game) => string.Equals(game, exeName, StringComparison.OrdinalIgnoreCase));
            return Settings.SaveStrDurable("NvDrsList", string.Join(";", splitSnapshot.ToArray()));
        }

        private static bool RestoreGameKeys(IntPtr session, IntPtr profile, string exeName, Dictionary<string, string> snapshot, List<string> keys, string action, bool force)
        {
            if (keys.Count == 0)
            {
                return true;
            }
            if (RestoreExhausted(exeName, action) || (!force && !RestoreDue(exeName, action)))
            {
                ReportRestoreSchedule(exeName, action);
                return false;
            }
            List<string> currentKeys = new List<string>();
            HashSet<string> candidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool sessionChanged = false;
            bool restoreGameKeysSucceeded = true;
            try
            {
                foreach (string key in keys)
                {
                    string currentSnapshot;
                    if (!snapshot.TryGetValue(key, out currentSnapshot))
                    {
                        continue;
                    }
                    uint originalSettingValue;
                    int dwordOf = TryGetDwordOf(session, profile, key, out originalSettingValue);
                    if (dwordOf < 0)
                    {
                        restoreGameKeysSucceeded = false;
                        continue;
                    }
                    uint applied;
                    if (!force && TrySnapApplied(currentSnapshot, out applied) && (dwordOf == 0 || (dwordOf == 1 && !MatchesApplied(currentSnapshot, originalSettingValue))))
                    {
                        currentKeys.Add(key);
                        candidateKeys.Add(key);
                        continue;
                    }
                    string originalSetting = SnapOrig(currentSnapshot);
                    bool alreadyRestored = originalSetting == "absent" ? dwordOf == 0
                        : dwordOf == 1 && originalSettingValue == ParseUInt(originalSetting);
                    if (alreadyRestored)
                    {
                        currentKeys.Add(key);
                        continue;
                    }
                    if (!force && (key == "pstate" || key == "forcep2") && !TrySnapApplied(currentSnapshot, out applied))
                    {
                        restoreGameKeysSucceeded = false;
                        continue;
                    }
                    bool restoreDwordSucceeded = originalSetting == "absent"
                        ? dwordOf == 0 || DeleteSettingOf(session, profile, key)
                        : RestoreDword(session, profile, key, originalSetting);
                    if (!restoreDwordSucceeded)
                    {
                        restoreGameKeysSucceeded = false;
                        continue;
                    }
                    currentKeys.Add(key);
                    sessionChanged = true;
                }
                if (sessionChanged && !Driver.SaveSession(session))
                {
                    ScheduleRestore(exeName, action);
                    return false;
                }
                List<string> texts = new List<string>();
                foreach (string currentKey in currentKeys)
                {
                    string stored = snapshot[currentKey];
                    uint readbackValue;
                    int dwordOf2 = TryGetDwordOf(session, profile, currentKey, out readbackValue);
                    string originalSetting = SnapOrig(stored);
                    if (!candidateKeys.Contains(currentKey))
                    {
                        bool restoreConfirmed = originalSetting == "absent" ? dwordOf2 == 0
                            : dwordOf2 == 1 && readbackValue == ParseUInt(originalSetting);
                        if (!restoreConfirmed)
                        {
                            restoreGameKeysSucceeded = false;
                            continue;
                        }
                    }
                    texts.Add(currentKey);
                }
                Dictionary<string, string> snapshotSnapshot = new Dictionary<string, string>(snapshot, StringComparer.OrdinalIgnoreCase);
                foreach (string text in texts)
                {
                    snapshotSnapshot.Remove(text);
                }
                if (!SaveGameSnapshot(exeName, snapshotSnapshot))
                {
                    ScheduleRestore(exeName, action);
                    return false;
                }
                foreach (string currentText in texts)
                {
                    snapshot.Remove(currentText);
                }
                Settings.SaveStr(SatKey(exeName), "");
                if (restoreGameKeysSucceeded)
                {
                    if (action == DriverFailureAction ? snapshot.Count == 0 : SnapshotKeysFor(snapshot, action).Count == 0)
                    {
                        restoreFailures.Remove(RestoreKey(exeName, action));
                        restoreRetryAt.Remove(RestoreKey(exeName, action));
                    }
                }
                else
                {
                    ScheduleRestore(exeName, action);
                }
                return restoreGameKeysSucceeded;
            }
            catch
            {
                ScheduleRestore(exeName, action);
                return false;
            }
        }

        private static List<string> SnapshotKeysFor(Dictionary<string, string> snapshot, string action)
        {
            List<string> keys = new List<string>();
            foreach (string key in snapshot.Keys)
            {
                if (FailureActionFor(key) == action)
                {
                    keys.Add(key);
                }
            }
            return keys;
        }

        private static bool TryReadGameSnapshot(string exeName, out Dictionary<string, string> snapshot)
        {
            snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string text;
            if (!Settings.TryLoadStr("NvDrs_" + exeName, out text) || text == null || text.Length > 262144)
            {
                return false;
            }
            foreach (string entryText in text.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int matchIndex = entryText.IndexOf('=');
                if (matchIndex <= 0 || matchIndex == entryText.Length - 1)
                {
                    return false;
                }
                string key = entryText.Substring(0, matchIndex);
                string substringResult = entryText.Substring(matchIndex + 1);
                uint result;
                if (snapshot.ContainsKey(key) || SettingIdOf(key) == 0 || (SnapOrig(substringResult) != "absent" && !uint.TryParse(SnapOrig(substringResult), out result)) || (substringResult.IndexOf('~') >= 0 && !TrySnapApplied(substringResult, out result)))
                {
                    return false;
                }
                if (substringResult.IndexOf('~') >= 0)
                {
                    string[] substringParts = substringResult.Substring(substringResult.IndexOf('~') + 1).Split(',');
                    if (substringParts.Length > 2)
                    {
                        return false;
                    }
                    foreach (string s in substringParts)
                    {
                        if (!uint.TryParse(s, out result))
                        {
                            return false;
                        }
                    }
                }
                snapshot[key] = substringResult;
            }
            return true;
        }

        public static List<string> ApplyForGame(string exePath, NvGamePlan plan, out bool retryLater)
        {
            retryLater = false;
            if (string.IsNullOrEmpty(exePath))
            {
                return null;
            }
            string fileName = Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }
            lock (sync)
            {
                if (!RestoreDue(fileName, DriverFailureAction)) { retryLater = !RestoreExhausted(fileName, DriverFailureAction); return null; }
                try
                {
                    if (!Driver.Available)
                    {
                        string raw;
                        bool ownsRecovery = !Settings.TryLoadStr(SnapPrefix + fileName, out raw) || raw.Length > 0;
                        if (ownsRecovery) ScheduleRestore(fileName, DriverFailureAction);
                        retryLater = ownsRecovery || !NvidiaAbsent();
                        return null;
                    }
                }
                catch
                {
                    string raw;
                    if (!Settings.TryLoadStr(SnapPrefix + fileName, out raw) || raw.Length > 0) ScheduleRestore(fileName, DriverFailureAction);
                    throw;
                }
                List<KeyValuePair<string, uint>> keyValuePairs = ((plan == null || plan.Empty) ? new List<KeyValuePair<string, uint>>() : BuildDesired(plan));
                Dictionary<string, string> snapshot;
                if (!TryReadGameSnapshot(fileName, out snapshot))
                {
                    IsolateInvalidJournal(fileName);
                    ScheduleRestore(fileName, DriverFailureAction);
                    retryLater = true;
                    return null;
                }
                if (keyValuePairs.Count == 0 && snapshot.Count == 0)
                {
                    return null;
                }
                Dictionary<string, List<KeyValuePair<string, uint>>> listsByKey = new Dictionary<string, List<KeyValuePair<string, uint>>>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, uint> keyValuePairEntry in keyValuePairs)
                {
                    string key = FailureActionFor(keyValuePairEntry.Key);
                    List<KeyValuePair<string, uint>> currentList;
                    if (!listsByKey.TryGetValue(key, out currentList))
                    {
                        currentList = (listsByKey[key] = new List<KeyValuePair<string, uint>>());
                    }
                    currentList.Add(keyValuePairEntry);
                }
                uint driverVersion = CurrentDriverVersion();
                string environmentContext = "NVIDIA DRS | " + ((driverVersion == 0) ? "GPU/driver context unavailable" : ("driver " + FormatDriver(driverVersion)));
                bool driverAllowed = !ActionFailureStore.IsBlocked("NvDriverProfile", fileName);
                bool hasEligibleSetting = false;
                foreach (string key4 in listsByKey.Keys)
                {
                    hasEligibleSetting |= !ActionFailureStore.IsBlocked(key4, fileName);
                }
                string configurationSignature = SatSignature(keyValuePairs);
                bool allSettingsEligible = driverAllowed;
                foreach (string key5 in listsByKey.Keys)
                {
                    allSettingsEligible &= !ActionFailureStore.IsBlocked(key5, fileName);
                }
                if (keyValuePairs.Count > 0 && allSettingsEligible && Settings.LoadStr(SatKey(fileName), "") == configurationSignature)
                {
                    return new List<string>();
                }
                bool canApply = driverAllowed && hasEligibleSetting;
                bool applyStarted = false;
                bool recoverySettled = true;
                string reason;
                if (canApply)
                {
                    applyStarted = ActionFailureStore.TryBeginApply("NvDriverProfile", fileName, out reason);
                }
                if (canApply && !applyStarted)
                {
                    canApply = false;
                }
                if (!canApply && snapshot.Count == 0)
                {
                    return null;
                }
                if (!canApply)
                {
                    bool recoveryDue = false;
                    foreach (string key in snapshot.Keys) recoveryDue |= RestoreDue(fileName, FailureActionFor(key));
                    if (!recoveryDue) return null;
                }
                IntPtr session = IntPtr.Zero;
                try
                {
                    if (!Driver.TryOpenSession(out session))
                    {
                        ScheduleRestore(fileName, DriverFailureAction);
                        if (applyStarted)
                        {
                            ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA driver session could not be opened", environmentContext);
                        }
                        else if (snapshot.Count > 0)
                        {
                            ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA recovery could not open the driver session", environmentContext);
                        }
                        retryLater = true;
                        return null;
                    }
                    IntPtr profile;
                    if (!Driver.FindOrCreateAppProfile(session, fileName, out profile))
                    {
                        ScheduleRestore(fileName, DriverFailureAction);
                        if (applyStarted || snapshot.Count > 0)
                        {
                            ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA game profile could not be opened", environmentContext);
                        }
                        retryLater = true;
                        return null;
                    }
                    List<string> failedSettings = new List<string>();
                    HashSet<string> textsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, uint> currentKeyValuePairEntry in keyValuePairs)
                    {
                        if (canApply && !ActionFailureStore.IsBlocked(FailureActionFor(currentKeyValuePairEntry.Key), fileName))
                        {
                            textsSet.Add(currentKeyValuePairEntry.Key);
                        }
                    }
                    Dictionary<string, List<string>> storedSettingGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (string key6 in snapshot.Keys)
                    {
                        if (!textsSet.Contains(key6))
                        {
                            string key2 = FailureActionFor(key6);
                            List<string> storedGroup;
                            if (!storedSettingGroups.TryGetValue(key2, out storedGroup))
                            {
                                storedGroup = (storedSettingGroups[key2] = new List<string>());
                            }
                            storedGroup.Add(key6);
                        }
                    }
                    foreach (KeyValuePair<string, List<string>> storedSettingGroupEntry in storedSettingGroups)
                    {
                        bool restoreDueSucceeded = RestoreDue(fileName, storedSettingGroupEntry.Key);
                        if (!RestoreGameKeys(session, profile, fileName, snapshot, storedSettingGroupEntry.Value, storedSettingGroupEntry.Key, false))
                        {
                            if (restoreDueSucceeded)
                            {
                                ActionFailureStore.Trip(storedSettingGroupEntry.Key, fileName, "NVIDIA stale setting recovery remains incomplete", environmentContext);
                            }
                            failedSettings.AddRange(storedSettingGroupEntry.Value);
                            retryLater = true;
                        }
                        else
                        {
                            ActionFailureStore.CancelApply(storedSettingGroupEntry.Key, fileName);
                        }
                    }
                    foreach (KeyValuePair<string, List<KeyValuePair<string, uint>>> listEntry in listsByKey)
                    {
                        string key3 = listEntry.Key;
                        if (!canApply || ActionFailureStore.IsBlocked(key3, fileName))
                        {
                            foreach (KeyValuePair<string, uint> collectionEntry in listEntry.Value)
                            {
                                if (!failedSettings.Contains(collectionEntry.Key))
                                {
                                    failedSettings.Add(collectionEntry.Key);
                                }
                            }
                            continue;
                        }
                        if (!ActionFailureStore.TryBeginApply(key3, fileName, out reason))
                        {
                            foreach (KeyValuePair<string, uint> currentCollectionEntry in listEntry.Value)
                            {
                                if (!failedSettings.Contains(currentCollectionEntry.Key))
                                {
                                    failedSettings.Add(currentCollectionEntry.Key);
                                }
                            }
                            continue;
                        }
                        bool settingFailed = false;
                        bool settingChanged = false;
                        bool settingApplied = false;
                        bool force = false;
                        string message = "";
                        HashSet<string> confirmedSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            foreach (KeyValuePair<string, uint> candidateCollectionEntry in listEntry.Value)
                            {
                                if (!snapshot.ContainsKey(candidateCollectionEntry.Key))
                                {
                                    uint previousSettingValue;
                                    int dwordOf = TryGetDwordOf(session, profile, candidateCollectionEntry.Key, out previousSettingValue);
                                    if (dwordOf < 0)
                                    {
                                        settingFailed = true;
                                        message = "NVIDIA original setting could not be read: " + candidateCollectionEntry.Key;
                                        break;
                                    }
                                    snapshot[candidateCollectionEntry.Key] = ((dwordOf == 1) ? previousSettingValue.ToString() : "absent");
                                }
                            }
                            if (!settingFailed && !SaveGameSnapshot(fileName, snapshot))
                            {
                                ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA recovery journal could not be saved", environmentContext);
                                ActionFailureStore.CancelApply(key3, fileName);
                                RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, false);
                                canApply = false;
                                retryLater = true;
                                foreach (KeyValuePair<string, uint> resolvedCollectionEntry in listEntry.Value)
                                {
                                    if (!failedSettings.Contains(resolvedCollectionEntry.Key))
                                    {
                                        failedSettings.Add(resolvedCollectionEntry.Key);
                                    }
                                }
                                continue;
                            }
                            if (!settingFailed)
                            {
                                foreach (KeyValuePair<string, uint> selectedCollectionEntry in listEntry.Value)
                                {
                                    uint readbackValue;
                                    int dwordOf2 = TryGetDwordOf(session, profile, selectedCollectionEntry.Key, out readbackValue);
                                    if (dwordOf2 < 0)
                                    {
                                        settingFailed = true;
                                        message = "NVIDIA current setting became unreadable: " + selectedCollectionEntry.Key;
                                        break;
                                    }
                                    if (dwordOf2 == 1 && readbackValue == selectedCollectionEntry.Value)
                                    {
                                        continue;
                                    }
                                    snapshot[selectedCollectionEntry.Key] = PrepareWriteReceipt(snapshot[selectedCollectionEntry.Key], selectedCollectionEntry.Value);
                                    if (!SaveGameSnapshot(fileName, snapshot))
                                    {
                                        throw new IOException("NVIDIA write-ahead receipt could not be saved: " + selectedCollectionEntry.Key);
                                    }
                                    force = true;
                                    int status;
                                    if (SetDwordOf(session, profile, selectedCollectionEntry.Key, selectedCollectionEntry.Value, out status))
                                    {
                                        settingChanged = true;
                                        continue;
                                    }
                                    if (status == -160)
                                    {
                                        confirmedSettings.Add(selectedCollectionEntry.Key);
                                        if (selectedCollectionEntry.Key == "ullenable" || selectedCollectionEntry.Key == "lowlatcpl")
                                        {
                                            MarkUltraGone();
                                        }
                                        continue;
                                    }
                                    settingFailed = true;
                                    message = "NVIDIA setting write failed: " + selectedCollectionEntry.Key + " (" + status + ")";
                                    break;
                                }
                            }
                            if (!settingFailed && settingChanged && !Driver.SaveSession(session))
                            {
                                ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA driver session could not be saved", environmentContext);
                                if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, true))
                                {
                                    ActionFailureStore.CancelApply(key3, fileName);
                                }
                                else
                                {
                                    ActionFailureStore.Trip(key3, fileName, "NVIDIA driver save failed and action rollback remains incomplete", environmentContext);
                                    recoverySettled = false;
                                }
                                canApply = false;
                                retryLater = true;
                                foreach (KeyValuePair<string, uint> workingCollectionEntry in listEntry.Value)
                                {
                                    if (!failedSettings.Contains(workingCollectionEntry.Key))
                                    {
                                        failedSettings.Add(workingCollectionEntry.Key);
                                    }
                                }
                                continue;
                            }
                            if (!settingFailed)
                            {
                                foreach (KeyValuePair<string, uint> pendingCollectionEntry in listEntry.Value)
                                {
                                    uint readbackValue;
                                    if (!confirmedSettings.Contains(pendingCollectionEntry.Key) && (TryGetDwordOf(session, profile, pendingCollectionEntry.Key, out readbackValue) != 1 || readbackValue != pendingCollectionEntry.Value))
                                    {
                                        settingFailed = true;
                                        message = "NVIDIA setting verification failed: " + pendingCollectionEntry.Key;
                                    }
                                }
                            }
                            if (!settingFailed && confirmedSettings.Count == listEntry.Value.Count)
                            {
                                settingApplied = true;
                            }
                            if (!settingFailed && !settingApplied)
                            {
                                foreach (KeyValuePair<string, uint> sourceCollectionEntry in listEntry.Value)
                                {
                                    string currentSnapshot;
                                    if (!confirmedSettings.Contains(sourceCollectionEntry.Key) && snapshot.TryGetValue(sourceCollectionEntry.Key, out currentSnapshot))
                                    {
                                        snapshot[sourceCollectionEntry.Key] = SnapOrig(currentSnapshot) + '~' + sourceCollectionEntry.Value;
                                    }
                                }
                                if (!SaveGameSnapshot(fileName, snapshot))
                                {
                                    settingFailed = true;
                                    message = "NVIDIA verified setting receipt could not be saved";
                                }
                            }
                        }
                        catch (NotSupportedException)
                        {
                            settingApplied = true;
                        }
                        catch (OperationCanceledException)
                        {
                            settingApplied = true;
                        }
                        catch (Exception ex3)
                        {
                            settingFailed = true;
                            message = ex3.GetType().Name + ": " + ex3.Message;
                        }
                        if (settingFailed)
                        {
                            ActionFailureStore.Trip(key3, fileName, message, environmentContext);
                            if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, force))
                            {
                                ActionFailureStore.CancelApply(key3, fileName);
                            }
                            else
                            {
                                ActionFailureStore.Trip(key3, fileName, message + "; rollback remains incomplete", environmentContext);
                            }
                            foreach (KeyValuePair<string, uint> targetCollectionEntry in listEntry.Value)
                            {
                                if (!failedSettings.Contains(targetCollectionEntry.Key))
                                {
                                    failedSettings.Add(targetCollectionEntry.Key);
                                }
                            }
                            Logger.Warn(key3 + " | " + fileName + " | " + message);
                            retryLater = true;
                        }
                        else if (settingApplied)
                        {
                            if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, force))
                            {
                                ActionFailureStore.CancelApply(key3, fileName);
                            }
                            else
                            {
                                ActionFailureStore.Trip(key3, fileName, "NVIDIA recovery after skip or cancellation remains incomplete", environmentContext);
                            }
                            foreach (KeyValuePair<string, uint> nextCollectionEntry in listEntry.Value)
                            {
                                if (!failedSettings.Contains(nextCollectionEntry.Key))
                                {
                                    failedSettings.Add(nextCollectionEntry.Key);
                                }
                            }
                        }
                        else if (!ActionFailureStore.CompleteApply(key3, fileName))
                        {
                            if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, true))
                            {
                                ActionFailureStore.CancelApply(key3, fileName);
                            }
                            else
                            {
                                ActionFailureStore.Trip(key3, fileName, "NVIDIA completion could not be persisted and rollback remains incomplete", environmentContext);
                            }
                            foreach (KeyValuePair<string, uint> previousCollectionEntry in listEntry.Value)
                            {
                                if (!failedSettings.Contains(previousCollectionEntry.Key))
                                {
                                    failedSettings.Add(previousCollectionEntry.Key);
                                }
                            }
                            retryLater = true;
                        }
                        foreach (string confirmedSetting in confirmedSettings)
                        {
                            if (!failedSettings.Contains(confirmedSetting))
                            {
                                failedSettings.Add(confirmedSetting);
                            }
                        }
                    }
                    if (applyStarted && canApply && !ActionFailureStore.CompleteApply("NvDriverProfile", fileName))
                    {
                        canApply = false;
                        retryLater = true;
                        recoverySettled = RestoreGameKeys(session, profile, fileName, snapshot, new List<string>(snapshot.Keys), "NvDriverProfile", false);
                        if (!recoverySettled)
                        {
                            ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA session completion could not be persisted and rollback remains incomplete", environmentContext);
                        }
                    }
                    bool configurationSatisfied = failedSettings.Count == 0 && keyValuePairs.Count > 0 && canApply;
                    Settings.SaveStr(SatKey(fileName), configurationSatisfied ? configurationSignature : "");
                    return failedSettings;
                }
                catch (NotSupportedException)
                {
                    ScheduleRestore(fileName, DriverFailureAction);
                    if (applyStarted)
                    {
                        ActionFailureStore.CancelApply("NvDriverProfile", fileName);
                    }
                    return null;
                }
                catch (OperationCanceledException)
                {
                    ScheduleRestore(fileName, DriverFailureAction);
                    if (applyStarted)
                    {
                        ActionFailureStore.CancelApply("NvDriverProfile", fileName);
                    }
                    return null;
                }
                catch (Exception ex6)
                {
                    ScheduleRestore(fileName, DriverFailureAction);
                    if (applyStarted)
                    {
                        ActionFailureStore.Trip("NvDriverProfile", fileName, ex6.GetType().Name + ": " + ex6.Message, environmentContext);
                    }
                    retryLater = true;
                    return null;
                }
                finally
                {
                    if (applyStarted && recoverySettled)
                    {
                        ActionFailureStore.CancelApply("NvDriverProfile", fileName);
                    }
                    if (session != IntPtr.Zero)
                    {
                        try
                        {
                            Driver.CloseSession(session);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        internal static string DlssText(string mode)
        {
            switch (mode)
            {
            case "latest":
                return Lang.T("t.nvdrstweaks.23");
            case "j":
                return Lang.T("t.nvdrstweaks.24");
            case "k":
                return Lang.T("t.nvdrstweaks.25");
            default:
                return Lang.T("t.nvdrstweaks.26");
            }
        }

        internal static bool ContainsAny(List<string> list, string[] keys)
        {
            foreach (string item in keys)
            {
                if (list.Contains(item))
                {
                    return true;
                }
            }
            return false;
        }

        public static void RestoreKind(string key)
        {
            lock (sync)
                foreach (string executable in Settings.LoadStr(ListKey, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    RestoreTarget(executable, key);
        }

        private static bool RestoreTarget(string executable, string onlyKey)
        {
            if (!RestoreDue(executable, DriverFailureAction)) return false;
            Dictionary<string, string> snapshot;
            if (!TryReadGameSnapshot(executable, out snapshot))
            {
                IsolateInvalidJournal(executable);
                ScheduleRestore(executable, DriverFailureAction);
                return false;
            }
            if (snapshot.Count == 0)
            {
                if (SaveGameSnapshot(executable, snapshot))
                {
                    restoreFailures.Remove(RestoreKey(executable, DriverFailureAction));
                    restoreRetryAt.Remove(RestoreKey(executable, DriverFailureAction));
                    return true;
                }
                ScheduleRestore(executable, DriverFailureAction);
                return false;
            }
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            bool due = false;
            foreach (string key in snapshot.Keys)
            {
                if (onlyKey != null && key != onlyKey) continue;
                string action = FailureActionFor(key);
                List<string> keys;
                if (!groups.TryGetValue(action, out keys)) groups[action] = keys = new List<string>();
                keys.Add(key);
                due |= RestoreDue(executable, action);
            }
            if (groups.Count == 0) return true;
            // 预算耗尽或延后的记录不能反复打开原生会话。
            if (!due) return false;
            IntPtr session = IntPtr.Zero;
            try
            {
                IntPtr profile;
                if (!Driver.Available || !Driver.TryOpenSession(out session)
                    || !Driver.FindOrCreateAppProfile(session, executable, out profile))
                {
                    ActionFailureStore.Trip(DriverFailureAction, executable, "NVIDIA recovery driver/profile unavailable");
                    ScheduleRestore(executable, DriverFailureAction);
                    return false;
                }
                bool restored = true;
                foreach (var group in groups)
                {
                    bool admitted = RestoreDue(executable, group.Key);
                    if (RestoreGameKeys(session, profile, executable, snapshot, group.Value, group.Key, false))
                    {
                        if (SnapshotKeysFor(snapshot, group.Key).Count == 0) ActionFailureStore.CancelApply(group.Key, executable);
                    }
                    else
                    {
                        restored = false;
                        if (admitted) ActionFailureStore.Trip(group.Key, executable, "NVIDIA action recovery remains incomplete");
                    }
                }
                if (snapshot.Count == 0)
                {
                    ActionFailureStore.CancelApply(DriverFailureAction, executable);
                    restoreFailures.Remove(RestoreKey(executable, DriverFailureAction));
                    restoreRetryAt.Remove(RestoreKey(executable, DriverFailureAction));
                }
                return restored;
            }
            catch (Exception error)
            {
                ActionFailureStore.Trip(DriverFailureAction, executable, error.GetType().Name + ": " + error.Message);
                ScheduleRestore(executable, DriverFailureAction);
                return false;
            }
            finally
            {
                if (session != IntPtr.Zero) try { Driver.CloseSession(session); } catch { }
            }
        }

        public static void RestoreKinds(string[] keys)
        {
            foreach (string key in keys)
            {
                RestoreKind(key);
            }
        }

        public static bool HasSnapshotFor(string key)
        {
            lock (sync)
            {
                string[] loadStrParts = Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string entryText in loadStrParts)
                {
                    if (ParseSnapshot(Settings.LoadStr("NvDrs_" + entryText, "")).ContainsKey(key))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        internal static bool PresetKindsRestored(string[] kinds)
        {
            lock (sync)
            {
                string raw; string[] names;
                if (!Settings.TryLoadStr(ListKey, out raw) || !Settings.TryLoadStoredNames("NvDrs_", out names)) return false;
                string[] indexed = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                HashSet<string> all = new HashSet<string>(indexed, StringComparer.OrdinalIgnoreCase);
                if (indexed.Length != all.Count || all.Count > 4096 || names.Length > 4096) return false;
                foreach (string name in names)
                {
                    string text;
                    if (!Settings.TryLoadStr(name, out text)) return false;
                    if (!string.IsNullOrEmpty(text)) all.Add(name.Substring("NvDrs_".Length));
                }
                foreach (string executable in all)
                {
                    Dictionary<string, string> snapshot;
                    if (Path.GetFileName(executable) != executable || !TryReadGameSnapshot(executable, out snapshot)) return false;
                    foreach (string kind in kinds) if (snapshot.ContainsKey(kind)) return false;
                }
                return true;
            }
        }

        public static bool RestoreAllGames()
        {
            lock (sync)
            {
                bool restored = true;
                foreach (string executable in Settings.LoadStr(ListKey, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    restored &= RestoreTarget(executable, null);
                return restored;
            }
        }

        private static bool NvidiaAbsent()
        {
            GpuAdapter[] adapters = GpuInventory.Adapters();
            if (adapters == null || adapters.Length == 0)
            {
                return false;
            }
            foreach (GpuAdapter gpuAdapter in adapters)
            {
                if (gpuAdapter.Vendor == GpuVendor.Nvidia)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool RestoreDword(IntPtr session, IntPtr profile, string key, string orig)
        {
            int status;
            if (!SetDwordOf(session, profile, key, ParseUInt(orig), out status))
            {
                return status == -160;
            }
            return true;
        }

        private static uint ParseUInt(string value)
        {
            uint result;
            if (!uint.TryParse(value, out result))
            {
                return 0u;
            }
            return result;
        }

        internal static Dictionary<string, string> ParseSnapshot(string raw)
        {
            Dictionary<string, string> substringsBySubstring = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw))
            {
                return substringsBySubstring;
            }
            foreach (string entryText in raw.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int matchIndex = entryText.IndexOf('=');
                if (matchIndex > 0)
                {
                    substringsBySubstring[entryText.Substring(0, matchIndex)] = entryText.Substring(matchIndex + 1);
                }
            }
            return substringsBySubstring;
        }

        internal static string SerializeSnapshot(Dictionary<string, string> map)
        {
            List<string> texts = new List<string>();
            foreach (KeyValuePair<string, string> item in map)
            {
                texts.Add(item.Key + "=" + item.Value);
            }
            texts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(";", texts.ToArray());
        }

        private static bool AddToList(string exeName)
        {
            List<string> splitSnapshot = new List<string>(Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (string item in splitSnapshot)
            {
                if (string.Equals(item, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            splitSnapshot.Add(exeName);
            return Settings.SaveStrDurable("NvDrsList", string.Join(";", splitSnapshot.ToArray()));
        }

        internal static bool TryGameNvDebt(out string[] debt)
        {
            lock (sync)
            {
                debt = new string[0];
                string[] names;
                string text;
                if (!Settings.TryLoadStoredNames("NvDrs_", out names) || !Settings.TryLoadStr("NvDrsList", out text))
                {
                    return false;
                }
                string[] valueParts = text.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                HashSet<string> valuePartsSnapshot = new HashSet<string>(valueParts, StringComparer.OrdinalIgnoreCase);
                if (valuePartsSnapshot.Count != valueParts.Length || valuePartsSnapshot.Count > 4096 || names.Length > 4096)
                {
                    return false;
                }
                foreach (string entryText in names)
                {
                    string substring = entryText.Substring("NvDrs_".Length);
                    string currentText;
                    if (!valuePartsSnapshot.Contains(substring) && (!Settings.TryLoadStr(entryText, out currentText) || currentText.Length != 0))
                    {
                        return false;
                    }
                }
                List<string> texts = new List<string>();
                foreach (string currentValuePartsSnapshot in valuePartsSnapshot)
                {
                    if (Path.GetFileName(currentValuePartsSnapshot) != currentValuePartsSnapshot || !currentValuePartsSnapshot.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    Dictionary<string, string> snapshot;
                    if (!TryReadGameSnapshot(currentValuePartsSnapshot, out snapshot))
                    {
                        return false;
                    }
                    if (snapshot.ContainsKey("pstate") || snapshot.ContainsKey("forcep2"))
                    {
                        texts.Add(currentValuePartsSnapshot);
                    }
                }
                debt = texts.ToArray();
                return true;
            }
        }
    }
}
