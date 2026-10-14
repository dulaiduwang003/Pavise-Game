using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private int automaticNvPlanKey = -1;
        private long nvFailureRetryGeneration = -1;
        private readonly TimerModeController timerMode = new TimerModeController();
        // 准备和正式执行均由驱动边界按游戏和动作持久隔离。
        internal List<string> ApplyNvGamePlan(string path, NvGamePlan plan, out bool retry, long now = -1,
            bool preStage = false)
        {
            List<string> failed = NvDrsTweaks.ApplyForGame(path, plan, out retry);
            if (failed == null && retry && plan != null)
            {
                if (plan.MaxPerf) RecordNvAutomation(PolicyCatalog.KeyNvMaxPerf, "failed", preStage);
                if (plan.ShaderCacheMax) RecordNvAutomation(PolicyCatalog.KeyNvShaderCache, "failed", preStage);
                if (plan.LowLatMode == "on") RecordNvAutomation(PolicyCatalog.KeyNvLowLat, "failed", preStage);
            }
            return failed;
        }

        internal void ResetNvApplyAttempts()
        {
            // 生命周期或模式变更不清除持久隔离或授予重试。
        }

        private void SyncNvFailureRetryGeneration()
        {
            long generation = ActionFailureStore.RetryGeneration;
            lock (sync)
            {
                if (generation == nvFailureRetryGeneration) return;
                nvFailureRetryGeneration = generation;
                tweakApplied.Clear();
                System.Threading.Interlocked.Exchange(ref nvTweakRetryAtTicks, 0);
            }
        }

        private void ApplyEnvironmentTimerOptions()
        {
            if (!AutomaticPolicy.Enabled) return;
            timerMode.SyncEnvironmentPreferences();
            RecordAutomation(PolicyCatalog.KeyTimerTick, timerMode.TickState);
            RecordAutomation(PolicyCatalog.KeyGlobalTimer, AutomaticExecutionState(PolicyCatalog.KeyGlobalTimer));
        }

        internal NvGamePlan RefreshAutomaticNvPlan(bool maxPerf, bool cache, bool onAc, string lowLatency = "off", bool windowedVrr = false, bool rebar = false)
        {
            NvGamePlan plan = NvGamePlan.ForAutomatic(maxPerf, cache, onAc, lowLatency, windowedVrr, rebar);
            int key = plan.AutomaticKey;
            lock (sync)
            {
                if (automaticNvPlanKey != key)
                {
                    automaticNvPlanKey = key;
                    tweakApplied.Clear();
                    System.Threading.Interlocked.Exchange(ref nvTweakRetryAtTicks, 0);
                }
            }
            return plan;
        }

        internal void InitializeAutomation()
        {
            if (!Settings.Save(AutomaticPolicy.VersionKey, true))
                throw new IOException("Automatic policy initialization failed.");
            Settings.Remove("AutoFrameCoordinationV1");
            SyncAutomationPolicy();
        }

        private void SyncAutomationPolicy()
        {
            if (!AutomaticPolicy.Enabled) return;
            PolicySnapshot policy = sessionPolicy ?? PolicyResolver.Global();
            bgSuppressOn = policy.SuppressBackground; boostOn = policy.BoostGame;
            pauseDlOn = policy.PauseDownloads; pauseUpdateOn = policy.PauseUpdate;
            pauseMaintOn = policy.PauseMaintenance; pauseServicesOn = policy.PauseServices;
            awakeOn = policy.Awake; planSwitch = policy.PowerPlanOn;
            aggressiveOn = policy.Aggressive; renderLaneOn = policy.RenderLane;
            gpuDemoteOn = policy.GpuDemote; adaptiveEscalateOn = policy.AdaptiveEscalate;
            cacheWarmOn = policy.CacheWarm;
            wsTrimOn = policy.WsTrim;
            // 旧设置和配置覆盖项不能重新启用已撤回功能。
            englishInputOn = false;
            nvMaxPerf = policy.NvMaxPerf; nvLowLatMode = policy.NvLowLatMode;
            nvSmoothMotion = false; nvShaderCacheMax = policy.NvShaderCacheMax;
            nvRebarOn = false; nvDlssMode = "off";
            amdAntiLag = policy.AmdAntiLag; amdAfmf = false;
            intelEnduranceOn = policy.IntelEnduranceOff;
            gpuPowerMaxOn = policy.GpuPowerMax; dwmBoostOn = policy.DwmBoost;
            nvVrrWindowedOn = Settings.LoadCached("NvVrrWindowed", false); rsrOn = false;
            gpuPrefStageOn = policy.Preset != PerformancePreset.Handheld && Native.OnAcPower();
            autoGpuOn = policy.Preset != PerformancePreset.Handheld;
            lock (sync)
                if (!autoAddPreferenceUnsaved) autoAddOn = Settings.LoadCached("GmAutoAdd", true);
            killGameDvr = Settings.LoadCached("GameDvrOff", true);
            mmcssOn = true;
            SuppressionCore.GpuDemoteEnabled = gpuDemoteOn;
        }

        internal PolicySnapshot AutomationPolicySnapshot { get { return sessionPolicy ?? PolicyResolver.Global(); } }
        internal bool SetEnvironmentPreference(string key, bool value)
        {
            if (!AutomaticPolicy.IsEnvironment(key)) return false;
            if (key == PolicyCatalog.KeyStandbyCleaner) return TrySetStandbyCleanerEnabled(value);
            lock (timerMode.Gate)
            {
                lock (sync)
                {
                    if (stopping || !Settings.Save(key, value)) return false;
                    if (key == PolicyCatalog.KeyCacheWarm)
                    {
                        cacheWarmOn = value;
                        InvalidateCacheWarm();
                    }
                }
            }
            RequestPolicyApply();
            return true;
        }

        internal bool SetGameLowLatency(bool value)
        {
            lock (sync)
            {
                if (stopping || !Settings.Save(AutomaticPolicy.GameLowLatencyKey, value)) return false;
                SyncAutomationPolicy();
                automaticNvPlanKey = -1;
            }
            RequestPolicyApply();
            return true;
        }

        internal void RefreshAutomaticPreferences()
        {
            SyncAutomationPolicy();
            RequestPolicyApply();
        }

        public string AutomationStatus
        {
            get
            {
                lock (sync)
                {
                    string state = !enabled ? AutomaticPolicy.Text("守护已暂停", "Guard paused")
                        : active ? AutomaticPolicy.Text("正在守护 · ", "Guarding · ") + activeGame
                        : AutomaticPolicy.Text("自动策略就绪 · 等待游戏", "Automatic policy ready · no active game");
                    return state + (envFused.Count == 0 ? "" : AutomaticPolicy.Text(" · 有受限动作，查看记录", " · actions blocked; see records"));
                }
            }
        }
    }
}
