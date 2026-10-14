// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        public int ExitGraceSeconds { get { return exitGraceSeconds; } }

        internal static bool IsExitGraceChoice(int seconds)
        {
            return seconds == 0 || seconds == 2 || seconds == 5
                || seconds == 8 || seconds == 15 || seconds == 30;
        }

        public bool TrySetExitGraceSeconds(int seconds)
        {
            if (!IsExitGraceChoice(seconds)) return false;
            lock (sync)
            {
                if (stopping || !Settings.SaveStr(ExitGraceSettingKey,
                    seconds.ToString(System.Globalization.CultureInfo.InvariantCulture))) return false;
                exitGraceSeconds = seconds;
            }
            KickGameDetectionNow();
            return true;
        }

        internal static bool ExitGraceElapsed(long since, long now, int seconds)
        {
            return since > 0 && now >= since
                && now - since >= (long)seconds * TimeSpan.TicksPerSecond;
        }

        public bool SuppressBackground
        {
            get { return bgSuppressOn; }
            set { bgSuppressOn = value; Settings.Save("GmSuppress", value); RequestPolicyApply(); }
        }

        public bool BoostGame
        {
            get { return boostOn; }
            set { boostOn = value; Settings.Save("GmBoost", value); RequestPolicyApply(); }
        }

        public bool AggressiveSuppression
        {
            get { return aggressiveOn; }
            set { aggressiveOn = value; Settings.Save("GmAggressive", value); RequestPolicyApply(); }
        }

        public bool GpuDemote
        {
            get { return gpuDemoteOn; }
            set { gpuDemoteOn = value; SuppressionCore.GpuDemoteEnabled = value; Settings.Save("GmGpuDemote", value); RequestPolicyApply(); }
        }

        public bool RenderLaneOn
        {
            get { return renderLaneOn; }
            set
            {
                renderLaneOn = value; Settings.Save("GmRenderLane", value);
                if (!value) RenderLane.Release();
                RequestPolicyApply();
            }
        }

        public bool PauseWindowsUpdate
        {
            get { return pauseUpdateOn; }
            set { pauseUpdateOn = value; Settings.Save("GmPauseUpdate", value); if (value) ClearEnvFuse("wu"); RequestPolicyApply(); }
        }

        public bool PauseMaintenance
        {
            get { return pauseMaintOn; }
            set { pauseMaintOn = value; Settings.Save(PolicyCatalog.KeyPauseMaintenance, value); if (value) ClearEnvFuse("maint"); RequestPolicyApply(); }
        }

        public bool PauseDownloads
        {
            get { return pauseDlOn; }
            set { pauseDlOn = value; Settings.Save("GmPauseDl", value); if (value) ClearEnvFuse("do"); RequestPolicyApply(); }
        }

        public bool PauseServices
        {
            get { return pauseServicesOn; }
            set
            {
                pauseServicesOn = value;
                Settings.Save(PolicyCatalog.KeyPauseServices, value);
                PauseServicesPolicyChanged(value);
            }
        }

        private void PauseServicesPolicyChanged(bool enabledValue)
        {
            System.Threading.Interlocked.Increment(ref optionalServiceGeneration);
            if (enabledValue) ClearEnvFuse("services");
            else lock (sync)
            {
                envNextAttempt.Remove("services");
                envFailures.Remove("services");
            }
            RequestPolicyApply();
        }

        public bool GpuPowerLift
        {
            get { return false; }
            set { gpuPowerMaxOn = false; Settings.Save("GmGpuPowerMax", false); RequestPolicyApply(); }
        }

        public bool DwmBoostOn
        {
            get { return dwmBoostOn; }
            set { dwmBoostOn = value; Settings.Save("GmDwmBoost", value); if (value) ClearEnvFuse("dwmboost"); RequestPolicyApply(); }
        }

        public bool WsTrimOn
        {
            get { return wsTrimOn; }
            set { wsTrimOn = value; Settings.Save(PolicyCatalog.KeyWsTrim, value); RequestPolicyApply(); }
        }

        public bool RsrUpscale
        {
            get { return rsrOn; }
            set { rsrOn = value; Settings.Save("GmRsr", value); if (value) ClearEnvFuse("rsr"); RequestPolicyApply(); }
        }

        public bool AmdAntiLag
        {
            get { return false; }
            set
            {
                amdAntiLag = false; Settings.Save("AmdAntiLag", false);
                RequestPolicyApply();
            }
        }

        public bool AmdAfmf
        {
            get { return amdAfmf; }
            set
            {
                amdAfmf = value; Settings.Save("AmdAfmf", value);
                if (!value)
                    AdlxTweaks.RestoreAfmf();
                else ClearEnvFuse("amdafmf");
                RequestPolicyApply();
            }
        }

        public bool NvVrrWindowedEnabled
        {
            get { return nvVrrWindowedOn; }
            set
            {
                lock (sync)
                {
                    if (stopping || !Settings.Save("NvVrrWindowed", value)) return;
                    nvVrrWindowedOn = value;
                    if (value) ClearEnvFuse("nvvrr");
                    automaticNvPlanKey = -1;
                    tweakApplied.Clear();
                }
                RequestPolicyApply();
            }
        }

        public bool IntelEnduranceOff
        {
            get { return intelEnduranceOn; }
            set
            {
                intelEnduranceOn = value;
                Settings.Save(PolicyCatalog.KeyIntelEndurance, value);
                if (value) ClearEnvFuse("intelend");
                RequestPolicyApply();
            }
        }

        public bool NvMaxPerf
        {
            get { return nvMaxPerf; }
            set
            {
                nvMaxPerf = value; Settings.Save("NvMaxPerf", value);
                if (!value)
                {
                    NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyPState);
                    NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyForceP2);
                }
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPState, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public string NvLowLatMode
        {
            get { return nvLowLatMode; }
            set
            {
                string mode = value == "on" || value == "ultra" ? value : "off";
                nvLowLatMode = mode; Settings.SaveStr("NvLowLat", mode);
                if (mode == "off")
                    NvDrsTweaks.RestoreKinds(NvDrsTweaks.UltraKeys);
                else
                {
                    if (mode == "on")
                        NvDrsTweaks.RestoreKinds(new[]
                        {
                            NvDrsTweaks.KeyUllEnable,
                            NvDrsTweaks.KeyLowLatCpl
                        });
                    SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPreRender, 0);
                }
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool NvSmoothMotion
        {
            get { return nvSmoothMotion; }
            set
            {
                nvSmoothMotion = value; Settings.Save("NvSmoothMotion", value);
                if (!value)
                    NvDrsTweaks.RestoreKind(NvDrsTweaks.KeySmooth);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeySmooth, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool NvShaderCacheMax
        {
            get { return nvShaderCacheMax; }
            set
            {
                nvShaderCacheMax = value; Settings.Save("NvShaderCache", value);
                if (!value)
                {
                    NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyShaderCache);
                    NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyShaderOn);
                }
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyShaderCache, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool NvRebar
        {
            get { return nvRebarOn; }
            set
            {
                nvRebarOn = value; Settings.Save("NvRebar", value);
                if (!value)
                    NvDrsTweaks.RestoreKinds(NvDrsTweaks.RebarKeys);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyRebarFeat, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public string NvDlssMode
        {
            get { return nvDlssMode; }
            set
            {
                string mode = value == "latest" || value == "j" || value == "k" ? value : "off";
                nvDlssMode = mode; Settings.SaveStr("NvDlss", mode);
                if (mode == "off")
                    NvDrsTweaks.RestoreKinds(NvDrsTweaks.DlssKeys);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyDlssOvr, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool GpuPrefStageOn
        {
            get { return gpuPrefStageOn; }
            set
            {
                gpuPrefStageOn = value; Settings.Save("GpuPrefStageOn", value);
                if (!value)
                    GpuPrefStage.Restore();
            }
        }

        public bool KeepAwake
        {
            get { return awakeOn; }
            set
            {
                awakeOn = value; Settings.Save("GmAwake", value);
                if (value) ClearEnvFuse("awake");
                RequestPolicyApply();
            }
        }

        public bool KillGameDvr
        {
            get { return killGameDvr; }
            set { SetGameDvrSuppression(value); }
        }

        internal bool SetGameDvrSuppression(bool value)
        {
            lock (sync)
            {
                if (stopping || !Settings.Save("GameDvrOff", value)) return false;
                killGameDvr = value;
                ClearEnvFuse("gamedvr");
            }
            SyncGameDvr();
            RequestPolicyApply();
            return true;
        }

        public bool PowerPlanSwitch
        {
            get { return !PowerPlan.NoSwitchSelected && planSwitch; }
            set
            {
                lock (sync)
                {
                    planSwitch = value;
                }
                Settings.Save("PowerPlanOn", value);
                if (value) { SaveCounter(PowerFailStreakKey, 0); ClearEnvFuse("overlay"); }
                RequestPolicyApply();
            }
        }
    }
}
