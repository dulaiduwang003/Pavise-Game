namespace PaviseApp
{
    internal partial class GameMode
    {

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
