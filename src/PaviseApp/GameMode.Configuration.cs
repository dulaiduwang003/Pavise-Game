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
        private string ResidueDetail()
        {
            List<string> list = new List<string>();
            if (SelfYield.HasResidue) list.Add(PerformancePolicy.Text("自身及辅助进程让路恢复", "Self/helper scheduling recovery"));
            if (GameNvDeliveryPending)
            {
                list.Add(PerformancePolicy.Text("游戏 NVIDIA 配置恢复", "Game NVIDIA configuration recovery"));
            }
            if (CriticalThreadHasRecoveryDebt(false))
            {
                list.Add(PerformancePolicy.Text("关键线程硬绑定恢复", "Hard thread affinity recovery"));
            }
            if (FrameCoordination.HasResidue)
            {
                list.Add(PerformancePolicy.Text("线程协同恢复", "Thread coordination recovery"));
            }
            if (GuardSchedulingNeedsRestore)
            {
                if (GameDvr.HasResidue)
                {
                    list.Add(Lang.T("set.dvr"));
                }
                if (Mmcss.HasResidue())
                {
                    list.Add(Lang.T("auto.mmcss"));
                }
            }
            bool activeSnapshot;
            int count;
            lock (sync)
            {
                activeSnapshot = active;
                count = gameBoost.Count;
            }
            if (activeSnapshot)
            {
                list.Add(Lang.T("t.gamemodeenv.29"));
            }
            if (count > 0)
            {
                list.Add(Lang.T("log.gamemodeboost.3") + count + Lang.T("t.gamemodeenv.30"));
            }
            if (core.AnyWith(SuppressReason.Background))
            {
                list.Add(Lang.T("cfg.group.bg"));
            }
            if (doActive || DoTweak.HasResidue)
            {
                list.Add(Lang.T("t.gamemodeenv.31"));
            }
            if (wuActive || UpdatePause.HasResidue)
            {
                list.Add(Lang.T("t.gamemodeenv.32"));
            }
            if (maintActive || MaintenancePause.HasResidue)
            {
                list.Add(Lang.T("gm.pausemaint"));
            }
            if (optionalServicesActive || OptionalServicePause.HasResidue)
            {
                list.Add(Lang.T("gm.pausesvc"));
            }
            if (standbyCleaner != null && standbyCleaner.HasInFlight)
            {
                list.Add(Lang.T("gm.standbycleaner"));
            }
            if (pqosActive || PresenceQos.HasResidue)
            {
                list.Add(Lang.T("t.gamemodeenv.33"));
            }
            if (awakeActive)
            {
                list.Add(Lang.T("t.gamemodeenv.34"));
            }
            if (dwmBoostActive)
            {
                list.Add(Lang.T("gm.dwmboost"));
            }
            if (RssSteer.HasResidue)
            {
                list.Add(Lang.T("gm.rsssteer"));
            }
            if (gpwActive || GpuPowerMax.HasResidue())
            {
                list.Add(EnvLabel("gpupower"));
            }
            if (GpuClockLock.HasResidue)
            {
                list.Add(Lang.T("set.gpuclock"));
            }
            if (nvVrrActive || NvVrrWindowed.HasResidue)
            {
                list.Add(EnvLabel("nvvrr"));
            }
            if (intelEndActive || IntelEndurance.HasResidue)
            {
                list.Add(EnvLabel("intelend"));
            }
            if (laptopPerfActive || LaptopPerfMode.HasResidue)
            {
                list.Add(Lang.T("gm.laptopperf"));
            }
            if (DisplaySolo.HasResidue())
            {
                list.Add(Lang.T("gm.solo"));
            }
            if (rsrActive || amdAlagActive || amdAfmfActive || AdlxTweaks.HasResidue())
            {
                list.Add("AMD");
            }
            if (planActive || PowerPlan.HasResidue || PowerPlan.HasParkResidue())
            {
                list.Add(Lang.T("t.gamemodeenv.35"));
            }
            if (NvGameResidueNeedsRestore)
            {
                list.Add("NVIDIA Profile");
            }
            if (GpuPrefStageResidueNeedsRestore)
            {
                list.Add(Lang.T("set.gpupref"));
            }
            if (timerRaised)
            {
                list.Add(Lang.T("t.gamemodeenv.36"));
            }
            if (list.Count <= 0)
            {
                return Lang.T("t.gamemodeenv.37");
            }
            return string.Join(" ", list.ToArray());
        }
        private static long ResidueBackoffTicks(int tries)
        {
            long minOffset = 4000L << Math.Min(Math.Max(tries - 1, 0), 7);
            if (minOffset > 300000)
            {
                minOffset = 300000L;
            }
            return minOffset * 10000;
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
            IntPtr intPtr = Native.OpenProcess(1052672, false, modeLockGamePid);
            if (intPtr == IntPtr.Zero)
            {
                return !Native.LastOpenProcessFailureWasNoSuchProcess();
            }
            try
            {
                if (Native.WaitForSingleObject(intPtr, 0u) != 258u)
                {
                    return false;
                }
                long creation;
                long cpu;
                ulong io;
                return !Native.QueryProcessSample(intPtr, out creation, out cpu, out io) || creation == modeLockGameCreation;
            }
            finally
            {
                Native.CloseHandle(intPtr);
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
