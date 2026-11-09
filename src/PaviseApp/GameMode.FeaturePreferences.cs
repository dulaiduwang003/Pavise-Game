namespace PaviseApp
{
    internal partial class GameMode
    {

        public bool CacheWarmOn
        {
            get
            {
                return cacheWarmOn;
            }
            set
            {
                lock (sync)
                {
                    if (stopping)
                    {
                        return;
                    }
                    InvalidateCacheWarm();
                    cacheWarmOn = Settings.Save("GmCacheWarmV2", value) && value;
                }
                RequestPolicyApply("CacheWarmOn");
            }
        }

        public string CacheWarmStatus
        {
            get
            {
                return Lang.T((cacheWarm == null) ? "cachewarm.idle" : cacheWarm.Status);
            }
        }

        public bool EnglishInputEnabled
        {
            get
            {
                return englishInputOn;
            }
            set
            {
                lock (sync)
                {
                    if (stopping)
                    {
                        return;
                    }
                    InvalidateEnglishInputWork();
                    bool gmEnglishInputV1Enabled = Settings.Save("GmEnglishInputV1", value);
                    englishInputOn = value && gmEnglishInputV1Enabled;
                }
                RequestPolicyApply("EnglishInputEnabled");
            }
        }

        public bool FrameCoordinationEnabled
        {
            get
            {
                return AutomationPolicySnapshot.FrameCoordinationOn;
            }
        }

        public bool SuppressBackground
        {
            get
            {
                return bgSuppressOn;
            }
            set
            {
                lock (familyPolicyGate)
                {
                    if (!Settings.Save("GmSuppress", value)) return;
                    bgSuppressOn = value;
                    InvalidateFamilyPolicy();
                }
                RequestPolicyApply("SuppressBackground");
            }
        }

        public bool BoostGame
        {
            get
            {
                return boostOn;
            }
            set
            {
                boostOn = value;
                Settings.Save("GmBoost", value);
                RequestPolicyApply("BoostGame");
            }
        }

        public bool RenderLaneOn
        {
            get
            {
                return renderLaneOn;
            }
            set
            {
                if (ThreadBoostSelection.Save(value ? "on" : "off"))
                {
                    renderLaneOn = value;
                    if (!value)
                    {
                        RenderLane.Release();
                    }
                    RequestPolicyApply("RenderLaneOn");
                }
            }
        }

        public bool PauseWindowsUpdate
        {
            get
            {
                return pauseUpdateOn;
            }
            set
            {
                pauseUpdateOn = value;
                Settings.Save("GmPauseUpdate", value);
                if (value)
                {
                    ClearEnvFuse("wu");
                }
                RequestPolicyApply("PauseWindowsUpdate");
            }
        }

        public bool PauseMaintenance
        {
            get
            {
                return pauseMaintOn;
            }
            set
            {
                pauseMaintOn = value;
                Settings.Save("GmPauseMaintenanceV1", value);
                if (value)
                {
                    ClearEnvFuse("maint");
                }
                RequestPolicyApply("PauseMaintenance");
            }
        }

        public bool PauseServices
        {
            get
            {
                return false;
            }
            set
            {
                pauseServicesOn = false;
                Settings.Remove("GmPauseServices");
                PauseServicesPolicyChanged(false);
            }
        }

        public bool GpuPowerLift
        {
            get
            {
                return false;
            }
            set
            {
                Settings.Save("GmGpuPowerMax", false);
                RequestPolicyApply("GpuPowerLift");
            }
        }

        public bool DwmBoostOn
        {
            get
            {
                return dwmBoostOn;
            }
            set
            {
                dwmBoostOn = value;
                Settings.Save("GmDwmBoost", value);
                if (value)
                {
                    ClearEnvFuse("dwmboost");
                }
                RequestPolicyApply("DwmBoostOn");
            }
        }

        public bool RsrUpscale
        {
            get
            {
                return rsrOn;
            }
            set
            {
                rsrOn = value;
                Settings.Save("GmRsr", value);
                if (value)
                {
                    ClearEnvFuse("rsr");
                }
                RequestPolicyApply("RsrUpscale");
            }
        }

        public bool AmdAntiLag
        {
            get
            {
                return false;
            }
            set
            {
                Settings.Save("AmdAntiLag", false);
                RequestPolicyApply("AmdAntiLag");
            }
        }

        public bool AmdAfmf
        {
            get
            {
                return amdAfmf;
            }
            set
            {
                amdAfmf = value;
                Settings.Save("AmdAfmf", value);
                if (!value)
                {
                    AdlxTweaks.RestoreAfmf();
                }
                else
                {
                    ClearEnvFuse("amdafmf");
                }
                RequestPolicyApply("AmdAfmf");
            }
        }

        public bool NvVrrWindowedEnabled
        {
            get
            {
                return nvVrrWindowedOn;
            }
            set
            {
                lock (sync)
                {
                    if (stopping || !Settings.Save("NvVrrWindowed", value))
                    {
                        return;
                    }
                    nvVrrWindowedOn = value;
                    if (value)
                    {
                        ClearEnvFuse("nvvrr");
                    }
                    automaticNvPlanKey = -1;
                    ForgetNvTweaksLocked();
                }
                RequestPolicyApply("NvVrrWindowedEnabled");
            }
        }

        public bool IntelEnduranceOff
        {
            get
            {
                return intelEnduranceOn;
            }
            set
            {
                intelEnduranceOn = value;
                Settings.Save("GmIntelEnduranceV1", value);
                if (value)
                {
                    ClearEnvFuse("intelend");
                }
                RequestPolicyApply("IntelEnduranceOff");
            }
        }

        public bool NvMaxPerf
        {
            get
            {
                return nvMaxPerf;
            }
            set
            {
                nvMaxPerf = value;
                Settings.Save("NvMaxPerf", value);
                if (!value)
                {
                    NvDrsTweaks.RestoreKind("pstate");
                    NvDrsTweaks.RestoreKind("forcep2");
                }
                else
                {
                    SaveCounter("NvFailStreak_pstate", 0);
                }
                lock (sync)
                {
                    ForgetNvTweaksLocked();
                }
                RequestPolicyApply("NvMaxPerf");
            }
        }

        public string NvLowLatMode
        {
            get
            {
                return nvLowLatMode;
            }
            set
            {
                SetGameLowLatency(value == "on" || value == "ultra");
            }
        }

        public bool NvSmoothMotion
        {
            get
            {
                return nvSmoothMotion;
            }
            set
            {
                nvSmoothMotion = value;
                Settings.Save("NvSmoothMotion", value);
                if (!value)
                {
                    NvDrsTweaks.RestoreKind("smooth");
                }
                else
                {
                    SaveCounter("NvFailStreak_smooth", 0);
                }
                lock (sync)
                {
                    ForgetNvTweaksLocked();
                }
                RequestPolicyApply("NvSmoothMotion");
            }
        }

        public bool NvShaderCacheMax
        {
            get
            {
                return nvShaderCacheMax;
            }
            set
            {
                nvShaderCacheMax = value;
                Settings.Save("NvShaderCache", value);
                if (!value)
                {
                    NvDrsTweaks.RestoreKind("shadercache");
                    NvDrsTweaks.RestoreKind("shaderon");
                }
                else
                {
                    SaveCounter("NvFailStreak_shadercache", 0);
                }
                lock (sync)
                {
                    ForgetNvTweaksLocked();
                }
                RequestPolicyApply("NvShaderCacheMax");
            }
        }

        public bool NvRebar
        {
            get
            {
                return nvRebarOn;
            }
            set
            {
                nvRebarOn = value;
                Settings.Save("NvRebar", value);
                if (!value)
                {
                    NvDrsTweaks.RestoreKinds(NvDrsTweaks.RebarKeys);
                }
                else
                {
                    SaveCounter("NvFailStreak_rebarfeat", 0);
                }
                lock (sync)
                {
                    ForgetNvTweaksLocked();
                }
                RequestPolicyApply("NvRebar");
            }
        }

        public string NvDlssMode
        {
            get
            {
                return nvDlssMode;
            }
            set
            {
                object instance;
                switch (value)
                {
                default:
                    instance = "off";
                    break;
                case "latest":
                case "j":
                case "k":
                    instance = value;
                    break;
                }
                string message = (nvDlssMode = (string)instance);
                Settings.SaveStr("NvDlss", message);
                if (message == "off")
                {
                    NvDrsTweaks.RestoreKinds(NvDrsTweaks.DlssKeys);
                }
                else
                {
                    SaveCounter("NvFailStreak_dlssovr", 0);
                }
                lock (sync)
                {
                    ForgetNvTweaksLocked();
                }
                RequestPolicyApply("NvDlssMode");
            }
        }

        public bool GpuPrefStageOn
        {
            get
            {
                return gpuPrefStageOn;
            }
            set
            {
                gpuPrefStageOn = value;
                Settings.Save("GpuPrefStageOn", value);
                if (!value)
                {
                    GpuPrefStage.Restore();
                }
            }
        }

        public bool KillGameDvr
        {
            get
            {
                return killGameDvr;
            }
            set
            {
                SetGameDvrSuppression(value);
            }
        }

        public bool PowerPlanSwitch
        {
            get
            {
                if (!PowerPlan.NoSwitchSelected)
                {
                    return planSwitch;
                }
                return false;
            }
            set
            {
                if (!stopping && PowerPlan.SelectPlan((!value) ? "off" : (PowerPlan.NoSwitchSelected ? "managed" : PowerPlan.EffectivePlanId)))
                {
                    lock (sync)
                    {
                        planSwitch = value;
                    }
                    if (value)
                    {
                        SaveCounter("PowerPlanFailStreak", 0);
                        ClearEnvFuse("overlay");
                    }
                    RequestPolicyApply("PowerPlanSwitch");
                }
            }
        }

        public bool StandbyCleanerEnabled
        {
            get
            {
                return standbyCleanerOn;
            }
            set
            {
                TrySetStandbyCleanerEnabled(value);
            }
        }

        public StandbyCleanerOptions StandbyCleaningOptions
        {
            get
            {
                return standbyCleanerOptions;
            }
        }

        public bool StandbyCleaningOptionsValid
        {
            get
            {
                return standbyCleanerOptionsValid;
            }
        }

    }
}
