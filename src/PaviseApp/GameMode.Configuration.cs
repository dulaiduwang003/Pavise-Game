using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private static void RetireSceneRulePreferences()
        {
            bool preferencesRemoved = false;
            foreach (string name in new string[2] { "GmAggressive", "GmAdaptiveEscalateV1" })
            {
                bool retiredEnabled;
                string text;
                if (Settings.TryLoadStoredBool(name, out retiredEnabled) || (Settings.TryLoadStr(name, out text) && !string.IsNullOrEmpty(text)))
                {
                    preferencesRemoved = true;
                }
                Settings.Remove(name);
            }
            if (preferencesRemoved)
            {
                Logger.Log(PerformancePolicy.Text("“扩大后台压制范围”与“智能自适应升档”已并入场景规则，旧的独立选择不再生效：竞技在后台治理生效时固定宽范围，智能平时窄范围并在 CPU 持续满载时临时放宽", "后台扩大范围及智能动态扩围已并入场景规则，旧独立选项不再使用：竞技在后台治理开启时保持宽范围，智能平时使用窄范围，并在持续高负载时临时扩大"));
            }
        }
        private void MigrateLegacyPreset()
        {
            int result;
            if (!int.TryParse(Settings.LoadStr("PerformancePreset", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || !PresetValue.IsLegacyRaw(result))
            {
                return;
            }
            PerformancePreset performancePreset = PresetValue.From(result);
            int performancePresetSnapshot = (int)performancePreset;
            if (Settings.SaveStr("PerformancePreset", performancePresetSnapshot.ToString(CultureInfo.InvariantCulture)))
            {
                lock (sync)
                {
                    preset = performancePreset;
                }
                string migrationNoteResult = PresetValue.MigrationNote(result);
                if (migrationNoteResult != null)
                {
                    Logger.Log(migrationNoteResult);
                }
            }
        }
        private bool WritePreset()
        {
            List<string> ts = new List<string>();
            ts.Add(Lang.T("t.gamemode.26"));
            ts.Add(Lang.T("t.gamemode.27"));
            ts.Add(Lang.T("t.gamemode.28"));
            ts.Add(Lang.T("t.gamemode.29"));
            ts.Add(Lang.T("lib.family.whitelist.note"));
            ts.Add("PAVISE_WHITELIST_V4");
            List<WhitelistRule> rules = new List<WhitelistRule>();
            foreach (string currentPresetWhitelist in SystemProcessCatalog.PresetWhitelist)
            {
                WhitelistRule rule;
                if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, currentPresetWhitelist, out rule))
                {
                    rules.Add(rule);
                    ts.Add(rule.Serialize());
                }
            }
            ts.Add(BuildWhitelistFooter(rules));
            return AtomicFile.WriteLines(whitePath, ts.ToArray(), Lang.T("t.gamemode.31"));
        }
        private static int LoadCounter(string key)
        {
            int result;
            if (!int.TryParse(Settings.LoadStr(key, "0"), out result) || result <= 0)
            {
                return 0;
            }
            return result;
        }
        private static void SaveCounter(string key, int value)
        {
            Settings.SaveStr(key, value.ToString());
        }
        private bool LiveBoolPreferenceLocked(string policyKey, bool globalOn)
        {
            return (sessionPolicy ?? PolicyResolver.Global()).ValueOf(policyKey) == "1";
        }
        private void RememberModeSwitchLockLocked(GameDetection detection)
        {
            if (detection != null && detection.RendererCreation > 0)
            {
                string id = ((detection.Profile != null) ? detection.Profile.Id : null);
                if (string.IsNullOrEmpty(id) || FindProfileLocked(id) != null)
                {
                    modeLockGamePid = detection.RendererPid;
                    modeLockGameCreation = detection.RendererCreation;
                    modeLockGameProfileId = id;
                }
            }
        }
        private bool ModeSwitchLockedLocked()
        {
            if (active || sessionPolicy != null || (activeDetection != null && activeDetection.RendererPid > 4))
            {
                return true;
            }
            if (modeLockGamePid <= 4 || modeLockGameCreation <= 0)
            {
                return false;
            }
            IntPtr processHandle = Native.OpenProcess(1052672, false, modeLockGamePid);
            if (processHandle == IntPtr.Zero)
            {
                return !Native.LastOpenProcessFailureWasNoSuchProcess();
            }
            try
            {
                if (Native.WaitForSingleObject(processHandle, 0u) != 258u)
                {
                    return false;
                }
                long creation;
                long cpu;
                ulong io;
                return !Native.QueryProcessSample(processHandle, out creation, out cpu, out io) || creation == modeLockGameCreation;
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }
        public bool TrySetPreset(PerformancePreset value)
        {
            if (!PresetValue.IsValid((int)value))
            {
                return false;
            }
            lock (sync)
            {
                if (stopping || ModeSwitchLockedLocked())
                {
                    return false;
                }
                int valueSnapshot = (int)value;
                if (!Settings.SaveStr("PerformancePreset", valueSnapshot.ToString()))
                {
                    return false;
                }
                preset = value;
            }
            RequestPolicyApply("TrySetPreset");
            return true;
        }
    }
}
