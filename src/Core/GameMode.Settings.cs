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
            ReleaseBackground("核心域切换");
            if (!CpuTopology.SwapDomains()) return;
            throttleMask = CpuTopology.ThrottleMask;
            strictMask = CpuTopology.StrictBoostMask;
            core.RefreshTopologyMasks();
            Logger.Log("游戏核心范围 已切换 游戏 " + CpuTopology.DescribeMask(strictMask)
                + " 后台 " + CpuTopology.DescribeMask(throttleMask) + " 即刻生效");
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
                Logger.Log("自定义核心 存的核心集合在本机不可用 已回到默认分区");
                return;
            }
            Logger.Log("自定义核心 已启用 " + CpuTopology.DescribeMask(CpuTopology.CustomMask)
                + " 后台去 " + CpuTopology.DescribeMask(CpuTopology.CustomBackgroundMask));
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
                        ? "自定义核心 对局进行中 已记录 " + CpuTopology.DescribeMask(clean) + " 对局结束生效"
                        : "自定义核心 对局进行中 已记录关闭 对局结束生效");
                    return;
                }
                bool ok = CpuTopology.SetCustomMask(value);
                Settings.SaveStr(CoreMaskKey, ok ? CpuTopology.CustomMask.ToString("X") : "");
                Logger.Log(ok
                    ? "自定义核心 已设为 " + CpuTopology.DescribeMask(CpuTopology.CustomMask)
                        + " 后台去 " + CpuTopology.DescribeMask(CpuTopology.CustomBackgroundMask)
                    : "自定义核心 已关闭 回到默认分区策略");
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

        public bool UploadYieldOn
        {
            get { return uploadYieldOn; }
            set
            {
                uploadYieldOn = value; Settings.Save("GmUploadYield", value);
                if (!value)
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { UploadYield.Clear(); } catch { }
                    });
            }
        }

        public bool PurgeStandby
        {
            get { return standbySweepOn; }
            set { standbySweepOn = value; Settings.Save("GmStandbySweep", value); }
        }

        public bool SqueezeBackgroundOn
        {
            get { return squeezeBgOn; }
            set
            {
                squeezeBgOn = value; Settings.Save("GmSqueezeBg", value);
                SuppressionCore.SqueezeBackground = value;
                RequestPolicyApply();
            }
        }

        public bool SqueezeBackgroundAvailable
        {
            get { return CpuTopology.BackgroundSqueezeMask() != 0 && !CpuTopology.MultiGroup; }
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

        public bool PauseSvcIndex
        {
            get { return svcPauseOn; }
            set { svcPauseOn = value; Settings.Save("GmSvcPause", value); if (value) ClearEnvFuse("svc"); RequestPolicyApply(); }
        }

        public bool ServiceYield
        {
            get { return svcYieldOn; }
            set { svcYieldOn = value; Settings.Save("GmSvcYield", value); if (value) ClearEnvFuse("svcyield"); RequestPolicyApply(); }
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

        public string AmdFrlMode
        {
            get { return amdFrlMode; }
            set
            {
                string mode = PolicyCatalog.Canonical(PolicyCatalog.KeyAmdFrl, value);
                amdFrlMode = mode; Settings.SaveStr("AmdFrl", mode);
                if (mode == "off") AdlxTweaks.RestoreFrtc();
                else ClearEnvFuse("amdfrtc");
                RequestPolicyApply();
            }
        }

        public bool NvAnselOff
        {
            get { return nvAnselOff; }
            set
            {
                nvAnselOff = value; Settings.Save("NvAnselOff", value);
                if (!value) NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyAnsel);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyAnsel, 0);
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

        public bool NvBattFull
        {
            get { return nvBattFull; }
            set
            {
                nvBattFull = value; Settings.Save("NvBattFull", value);
                if (!value) NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyBattFps);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyBattFps, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
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

        public string NvFrlMode
        {
            get { return nvFrlMode; }
            set
            {
                string mode = PolicyCatalog.Canonical(PolicyCatalog.KeyNvFrl, value);
                nvFrlMode = mode; Settings.SaveStr("NvFrl", mode);
                if (mode == "off") NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyFrl);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyFrl, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool KillGameDvr
        {
            get { return killGameDvr; }
            set { killGameDvr = value; Settings.Save("GameDvrOff", value); if (value) ClearEnvFuse("dvr"); RequestPolicyApply(); }
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
