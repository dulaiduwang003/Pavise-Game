// @author bdth 2074055628@qq.com
// 文件用途 游戏模式的设置开关属性
using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
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

        public bool CorePartitionEnabled
        {
            get { return corePartitionOn; }
            set { corePartitionOn = value; Settings.Save("GmStrictCores", value); RequestPolicyApply(); }
        }

        public bool CoreDomainAlt
        {
            get { return coreDomainAltOn; }
            set
            {
                if (coreDomainAltOn == value) return;
                coreDomainAltOn = value;
                Settings.Save("GmCoreDomainAlt", value);
                RequestPolicyApply();
            }
        }

        public bool CoreDomainSwitchPending
        {
            get { return CpuTopology.HasAltPartition() && coreDomainAltOn != CpuTopology.AltDomainActive; }
        }

        private void TryDomainSwap()
        {
            if (!CpuTopology.DomainPreferenceApplied || !CpuTopology.HasAltPartition()) return;
            if (coreDomainAltOn == CpuTopology.AltDomainActive) return;
            bool sessionActive;
            lock (sync) sessionActive = active;
            if (sessionActive) return;
            ReleaseBackground(Lang.T("t.gamemodesettings.1"));
            if (!CpuTopology.SwapDomains()) return;
            throttleMask = CpuTopology.ThrottleMask;
            strictMask = CpuTopology.StrictBoostMask;
            core.RefreshTopologyMasks();
            Logger.Log(Lang.T("log.gamemodesettings.2") + CpuTopology.DescribeMask(strictMask)
                + Lang.T("log.gamemodesettings.3") + CpuTopology.DescribeMask(throttleMask) + Lang.T("log.gamemodesettings.4"));
        }

        public bool AggressiveSuppression
        {
            get { return aggressiveOn; }
            set { aggressiveOn = value; Settings.Save("GmAggressive", value); RequestPolicyApply(); }
        }

        private static void LoadCustomCoreMask()
        {
            string raw = Settings.LoadStr(CoreMaskKey, "");
            if (raw.Length == 0) return;
            ulong parsed;
            if (!ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
            {
                Settings.SaveStr(CoreMaskKey, "");
                return;
            }
            if (!CpuTopology.SetCustomMask(parsed))
            {
                Settings.SaveStr(CoreMaskKey, "");
                Logger.Log(Lang.T("log.gamemodesettings.5"));
                return;
            }
            Logger.Log(Lang.T("log.gamemodesettings.6") + CpuTopology.DescribeMask(CpuTopology.CustomMask)
                + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(CpuTopology.CustomBackgroundMask));
        }

        public ulong CustomCoreMask
        {
            get { return CpuTopology.CustomMask; }
            set
            {
                bool sessionActive;
                lock (sync) sessionActive = active;
                if (sessionActive)
                {
                    ulong clean = CpuTopology.SanitizeCustomMask(value, CpuTopology.AllMask);
                    Settings.SaveStr(CoreMaskKey, clean != 0 ? clean.ToString("X") : "");
                    Logger.Log(clean != 0
                        ? Lang.T("log.gamemodesettings.8") + CpuTopology.DescribeMask(clean) + Lang.T("log.gamemodesettings.9")
                        : Lang.T("log.gamemodesettings.10"));
                    return;
                }
                bool ok = CpuTopology.SetCustomMask(value);
                Settings.SaveStr(CoreMaskKey, ok ? CpuTopology.CustomMask.ToString("X") : "");
                Logger.Log(ok
                    ? Lang.T("log.gamemodesettings.11") + CpuTopology.DescribeMask(CpuTopology.CustomMask)
                        + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(CpuTopology.CustomBackgroundMask)
                    : Lang.T("log.gamemodesettings.12"));
                RequestPolicyApply();
            }
        }

        public bool GpuDemote
        {
            get { return gpuDemoteOn; }
            set { gpuDemoteOn = value; SuppressionCore.GpuDemoteEnabled = value; Settings.Save("GmGpuDemote", value); RequestPolicyApply(); }
        }

        public bool IfeoBoostFallback
        {
            get { return ifeoOn; }
            set
            {
                ifeoOn = value; Settings.Save("GmIfeoBoost", value);
                if (!value) IfeoBoost.RestoreAll();
                RequestPolicyApply();
            }
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

        public bool PauseDownloads
        {
            get { return pauseDlOn; }
            set { pauseDlOn = value; Settings.Save("GmPauseDl", value); if (value) ClearEnvFuse("do"); RequestPolicyApply(); }
        }

        public bool GpuPowerLift
        {
            get { return gpuPowerMaxOn; }
            set { gpuPowerMaxOn = value; Settings.Save("GmGpuPowerMax", value); if (value) ClearEnvFuse("gpupower"); RequestPolicyApply(); }
        }

        public bool RsrUpscale
        {
            get { return rsrOn; }
            set { rsrOn = value; Settings.Save("GmRsr", value); if (value) ClearEnvFuse("rsr"); RequestPolicyApply(); }
        }

        public bool AmdAntiLag
        {
            get { return amdAntiLag; }
            set
            {
                amdAntiLag = value; Settings.Save("AmdAntiLag", value);
                if (!value) { AdlxTweaks.RestoreAntiLag(); AdlxTweaks.RestoreChill(); }
                else ClearEnvFuse("amdalag");
                RequestPolicyApply();
            }
        }

        public bool AmdAfmf
        {
            get { return amdAfmf; }
            set
            {
                amdAfmf = value; Settings.Save("AmdAfmf", value);
                if (!value) AdlxTweaks.RestoreAfmf();
                else ClearEnvFuse("amdafmf");
                RequestPolicyApply();
            }
        }

        public bool WlanScanGuard
        {
            get { return wlanGuardOn; }
            set { wlanGuardOn = value; Settings.Save("GmWlanGuard", value); if (value) ClearEnvFuse("wlanscan"); RequestPolicyApply(); }
        }

        public bool NvMaxPerf
        {
            get { return nvMaxPerf; }
            set
            {
                nvMaxPerf = value; Settings.Save("NvMaxPerf", value);
                if (!value) NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyPState);
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
                if (mode == "off") NvDrsTweaks.RestoreKinds(NvDrsTweaks.UltraKeys);
                else
                {
                    if (mode == "on")
                        NvDrsTweaks.RestoreKinds(new[] { NvDrsTweaks.KeyUllEnable, NvDrsTweaks.KeyLowLatCpl });
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
                if (!value) NvDrsTweaks.RestoreKind(NvDrsTweaks.KeySmooth);
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
                if (!value) NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyShaderCache);
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
                if (!value) NvDrsTweaks.RestoreKinds(NvDrsTweaks.RebarKeys);
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
                if (mode == "off") NvDrsTweaks.RestoreKinds(NvDrsTweaks.DlssKeys);
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
                if (!value) GpuPrefStage.Restore();
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
            set { killGameDvr = value; Settings.Save("GameDvrOff", value); SyncGameDvr(); RequestPolicyApply(); }
        }

        public bool MmcssOn
        {
            get { return mmcssOn; }
            set { mmcssOn = value; Settings.Save("GmMmcss", value); SyncMmcss(); }
        }

        public bool PowerPlanSwitch
        {
            get { return planSwitch; }
            set
            {
                planSwitch = value; Settings.Save("PowerPlanOn", value);
                if (value) { SaveCounter(PowerFailStreakKey, 0); ClearEnvFuse("overlay"); }
                RequestPolicyApply();
            }
        }
    }
}
