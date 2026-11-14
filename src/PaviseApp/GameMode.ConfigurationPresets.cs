using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal bool ApplyConfigurationPresetPreference(string key, string value, out string detail, out bool pendingVerification)
        {
            pendingVerification = false;
            detail = PerformancePolicy.Text("已保存，按适用条件执行", "Saved; runs when eligible");
            if (ConfigurationPresetCatalog.IsGameTuningKey(key))
            {
                detail = PerformancePolicy.Text("请在游戏卡片的深度调优中设置此项", "Configure this in the game card's Advanced tuning");
                return false;
            }
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
                // 偏好已记录不代表计时器已生效；等待启动核验不撤回偏好，也不计为一次应用成功。
                pendingVerification = state == "unverified" || state == "pending-reboot" || state == "restore-pending-reboot";
                bool ok = state != "failed" && state != "restore-failed" && state != "stopped" && state != "unsupported";
                if (!ok && on && SetEnvironmentPreference(key, false)) ApplyEnvironmentTimerOptions();
                return ok;
            }
            if (key == "AutoGameMitigationsV1" || key == "AutoNtfsShortNamesV1" || key == "AutoOpenGlThreadsV1")
            {
                if (!SetPerformanceActionSelected(key, on) || !performanceActions.DrainForNvHandoff(30000)) return false;
                if (!on)
                {
                    // 守护暂停时不执行持久游戏动作；显式关闭仍需处理其恢复记录。
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
            if (key == LaboratoryPower.IdleKey || key == LaboratoryPower.EppKey || key == LaboratoryGpu.ClockKey || key == LaboratoryGpu.PowerKey || key == LaboratoryAudio.Key)
            {
                if (!Settings.SaveStrDurable(key, value)) return false;
                if (key == LaboratoryGpu.ClockKey || key == LaboratoryGpu.PowerKey)
                {
                    // 局外只撤回已关闭的请求；开启请求留待会话内核验适用条件。
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
            if (item.Kind == PolicyValueKind.Bool)
            { if (!Settings.SaveDurable(key, on)) return false; }
            else if (!Settings.SaveStrDurable(key, value)) return false;
            RefreshAutomaticPreferences(); return true;
        }
    }
}
