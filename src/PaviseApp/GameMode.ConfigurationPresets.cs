using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal bool ApplyConfigurationPresetPreference(string key, string value, out string detail)
        {
            detail = PerformancePolicy.Text("已保存，按适用条件执行", "Saved; runs when eligible");
            if (stopping || active) return false;
            bool on = ConfigurationPresetEntry.IsEnabled(value);
            if (key == "NvLowLat")
            {
                bool configured = SetGameLowLatency(on) && NvDrsTweaks.PresetKindsRestored(on ? new[] { "ullenable", "lowlatcpl" } : NvDrsTweaks.UltraKeys);
                if (!configured && on) SetGameLowLatency(false);
                return configured;
            }
            if (key == "NvMaxPerf" || key == "NvShaderCache")
            {
                if (!Settings.SaveDurable(key, on)) return false;
                if (key == "NvMaxPerf") NvMaxPerf = on; else NvShaderCacheMax = on;
                bool restored = on || NvDrsTweaks.PresetKindsRestored(key == "NvMaxPerf" ? new[] { "pstate", "forcep2" } : new[] { "shadercache", "shaderon" });
                if (!restored) detail = PerformancePolicy.Text("原驱动配置恢复未完成，已保留恢复记录", "Original driver configuration not restored; recovery records retained");
                return restored;
            }
            if (key == "GameDvrOff") return SetGameDvrSuppression(on);
            if (key == "GmCacheWarmV2") return SetEnvironmentPreference(key, on);
            if (key == "AutoTimerTick" || key == "AutoGlobalTimer")
            {
                if (!SetEnvironmentPreference(key, on)) return false;
                ApplyEnvironmentTimerOptions();
                string state = key == "AutoTimerTick" ? timerMode.TickState : timerMode.GlobalState;
                detail = GameMode.AutomationStateLabel(state);
                bool ok = state != "failed" && state != "restore-failed" && state != "stopped" && state != "unsupported" && (!on || state != "unverified");
                if (!ok && on && SetEnvironmentPreference(key, false)) ApplyEnvironmentTimerOptions();
                return ok;
            }
            if (key == "AutoGameMitigationsV1" || key == "AutoNtfsShortNamesV1" || key == "AutoOpenGlThreadsV1")
            {
                if (!SetPerformanceActionSelected(key, on) || !performanceActions.DrainForNvHandoff(30000)) return false;
                if (!on)
                {
                    // A paused guard does not process persistent game actions. Explicit OFF still repays their receipts.
                    bool restored;
                    lock (SystemConfigurationGate.Sync) restored = PerformanceTuning.RestoreWhere(receipt => receipt.Id == key);
                    if (!restored) { detail = PerformancePolicy.Text("原配置恢复未完成，已保留恢复记录", "Original configuration not restored; recovery records retained"); return false; }
                    foreach (PerformanceActionStore.Receipt receipt in PerformanceActionStore.All()) if (receipt.Id == key) return false;
                    detail = PerformancePolicy.Text("已关闭，原配置已恢复", "Off; original configuration restored"); return true;
                }
                string state = PerformanceExecutionState(key); detail = AutomationStateLabel(state);
                bool ok = state != "failed" && state != "restore-failed" && state != "stopped" && state != "unsupported";
                if (!ok)
                {
                    SetPerformanceActionSelected(key, false);
                    bool drained = performanceActions.DrainForNvHandoff(30000), restored = false;
                    if (drained) lock (SystemConfigurationGate.Sync) restored = PerformanceTuning.RestoreWhere(receipt => receipt.Id == key);
                    detail += PerformancePolicy.Text("；已撤回启用请求", "; enable request withdrawn");
                    if (!drained || !restored) detail += PerformancePolicy.Text("，恢复待处理", ", recovery remains pending");
                }
                return ok;
            }
            if (key == "GmMmcss")
            {
                if (!Settings.SaveDurable(key, on)) return false;
                mmcssOn = on; SyncGameDvr();
                string state = AutomaticExecutionState(key); detail = AutomationStateLabel(state);
                bool ok = state != "failed" && state != "restore-failed" && state != "stopped";
                if (!ok && on && Settings.SaveDurable(key, false)) { mmcssOn = false; SyncGameDvr(); }
                return ok;
            }
            if (key == "GpuPrefStageOn")
            {
                if (!Settings.SaveDurable(key, on)) return false;
                gpuPrefStageOn = on;
                if (!on && !GpuPrefStage.Restore()) return false;
                RefreshAutomaticPreferences(); return true;
            }
            if (key == LaboratoryPower.IdleKey || key == LaboratoryPower.EppKey || key == LaboratoryGpu.ClockKey || key == LaboratoryGpu.PowerKey || key == LaboratoryAudio.Key)
            {
                if (!Settings.SaveStrDurable(key, value)) return false;
                if (key == LaboratoryGpu.ClockKey || key == LaboratoryGpu.PowerKey)
                {
                    // Outside a match only withdraw disabled requests; enabled requests are session-qualified later.
                    LaboratoryGpu.Update(false, false);
                    if (LaboratoryGpu.HasResidue) { if (on) Settings.SaveStrDurable(key, "0"); return false; }
                }
                if ((key == LaboratoryPower.IdleKey || key == LaboratoryPower.EppKey) && !LaboratoryPower.Restore())
                { if (on) Settings.SaveStrDurable(key, key == LaboratoryPower.IdleKey ? "default" : "0"); return false; }
                if (key == LaboratoryAudio.Key && !LaboratoryAudio.Restore()) return false;
                Poke(); return true;
            }
            PolicyItem item = PolicyCatalog.ItemOf(key);
            if (item == null || key == PolicyCatalog.KeyPreset || PerformancePolicy.IsIntent(key) || key == PolicyCatalog.KeyPowerPlan) return false;
            if (key == ThreadBoostSelection.Key)
            { if (!ThreadBoostSelection.Save(value)) return false; }
            else if (item.Kind == PolicyValueKind.Bool)
            { if (!Settings.SaveDurable(key, on)) return false; }
            else if (!Settings.SaveStrDurable(key, value)) return false;
            RefreshAutomaticPreferences(); return true;
        }
    }
}
