using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode : IModeLockGameHost
    {
        private ModeLockGameIdentity modeLockGame;

        // 正常由构造函数创建 这里的兜底只给测试里不走构造函数造出来的 GameMode 用
        internal ModeLockGameIdentity ModeLockGame
        {
            get { return LazyInitializer.EnsureInitialized(ref modeLockGame, () => new ModeLockGameIdentity(this)); }
        }

        bool IModeLockGameHost.ProfileListedLocked(string profileId) { return FindProfileLocked(profileId) != null; }

        private static void RetireSceneRulePreferences()
        {
            bool preferencesRemoved = false;
            string[] textEntries = new string[2] { "GmAggressive", "GmAdaptiveEscalateV1" };
            foreach (string name in textEntries)
            {
                bool value;
                string value2;
                if (Settings.TryLoadStoredBool(name, out value) || (Settings.TryLoadStr(name, out value2) && !string.IsNullOrEmpty(value2)))
                {
                    preferencesRemoved = true;
                }
                Settings.Remove(name);
            }
            if (preferencesRemoved)
            {
                Logger.Log(() => Logger.Text("“扩大后台压制范围”与“智能自适应升档”已并入场景规则，旧的独立选择不再生效：竞技在后台治理生效时固定宽范围，智能平时窄范围并在 CPU 持续满载时临时放宽", "Broader background suppression and Smart adaptive escalation are now scene rules; the old separate choices no longer apply: Competitive always uses the wide scope while background control is on, Smart uses the narrow scope and widens it temporarily under sustained CPU saturation"));
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
                    Logger.Log(() => PresetValue.MigrationNote(result));
                }
            }
        }
        private bool WritePreset()
        {
            List<string> list = new List<string>();
            list.Add(Lang.T("t.gamemode.26"));
            list.Add(Lang.T("t.gamemode.27"));
            list.Add(Lang.T("t.gamemode.28"));
            list.Add(Lang.T("t.gamemode.29"));
            list.Add(Lang.T("lib.family.whitelist.note"));
            list.Add("PAVISE_WHITELIST_V4");
            List<WhitelistRule> list2 = new List<WhitelistRule>();
            string[] presetWhitelist = SystemProcessCatalog.PresetWhitelist;
            foreach (string value in presetWhitelist)
            {
                WhitelistRule rule;
                if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, value, out rule))
                {
                    list2.Add(rule);
                    list.Add(rule.Serialize());
                }
            }
            list.Add(BuildWhitelistFooter(list2));
            return AtomicFile.WriteLines(whitePath, list.ToArray(), Lang.T("t.gamemode.31"));
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
        private bool ModeSwitchLockedLocked()
        {
            if (active || sessionPolicy != null || (activeDetection != null && activeDetection.RendererPid > 4))
            {
                return true;
            }
            return ModeLockGame.StillRunningLocked();
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
