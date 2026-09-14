// @author bdth 2074055628@qq.com
// File purpose Game mode settings switch properties
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
            if (CoreScheduling.HasGlobalRecord())
            {
                CoreSchedulingPlan plan = CoreScheduling.LoadGlobal();
                ulong mask;
                if (ulong.TryParse(CoreScheduling.Value(plan, PolicyCatalog.KeyCoreMask),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out mask))
                    CpuTopology.SetCustomMask(mask);
                else CpuTopology.SetCustomMask(0);
                return;
            }
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
            // Only wakes the existing worker thread, never queries or stops services on the UI thread
            RequestPolicyApply();
        }

        public bool DisableCpuIdle
        {
            get { return disableCpuIdleOn; }
            set
            {
                // Invalidate first, then write to disk, the write may block or fail
                lock (sync)
                {
                    System.Threading.Interlocked.Increment(ref cpuIdleGeneration);
                    disableCpuIdleOn = value;
                }
                Settings.Save(PolicyCatalog.KeyDisableCpuIdle, value);
                CpuIdlePolicyChanged(value);
            }
        }

        private void CpuIdlePolicyChanged(bool enabledValue)
        {
            if (enabledValue) ClearEnvFuse("cpuidle");
            else lock (sync)
            {
                envNextAttempt.Remove("cpuidle");
                envFailures.Remove("cpuidle");
            }
            // The UI only records intent, all power writes go through the worker thread gate
            RequestPolicyApply();
        }

        public bool GpuPowerLift
        {
            get { return gpuPowerMaxOn; }
            set { gpuPowerMaxOn = value; Settings.Save("GmGpuPowerMax", value); if (value) ClearEnvFuse("gpupower"); RequestPolicyApply(); }
        }

        // The three items below could only be enabled on the Extreme tier before 2.2.2, after Extreme was retired each has its own switch, default off
        //   putting the DWM composition thread into MMCSS is a single dwmapi call, lives as long as this process's DWM connection, DWM deregisters itself in exclusive fullscreen
        // Hard affinity defaults off, when on it writes affinity on suppressed background processes to keep them out of the exclusive range
        public bool HardAffinityOn
        {
            get { return hardAffinityOn; }
            set
            {
                hardAffinityOn = value;
                SuppressionCore.BackgroundPinsAllowed = value;
                Settings.Save("GmHardAffinityV1", value);
                RequestPolicyApply();
            }
        }

        // Affinity guard defaults off, when off another program's change to the game affinity is kept, when on every scan round checks and writes it back if changed
        public bool AffinityGuardOn
        {
            get { return affinityGuardOn; }
            set
            {
                affinityGuardOn = value;
                Settings.Save("GmAffinityGuardV1", value);
            }
        }

        public bool DwmBoostOn
        {
            get { return dwmBoostOn; }
            set { dwmBoostOn = value; Settings.Save("GmDwmBoost", value); if (value) ClearEnvFuse("dwmboost"); RequestPolicyApply(); }
        }

        // The idle policy knobs are written on the managed power scheme, any switch change requires rewriting the scheme
        //   when turned off PrepareExtremeKnobs restores both knobs from the snapshot, without the rewrite they stay in the scheme forever
        //   this bit goes into powerKey, any change makes it differ from lastPowerPolicyKey so the next round rewrites naturally, no separate invalidation needed
        public bool IdlePolicyOn
        {
            get { return idlePolicyOn; }
            set
            {
                idlePolicyOn = value;
                Settings.Save(PolicyCatalog.KeyIdlePolicy, value);
                RequestPolicyApply();
            }
        }

        // Working set trim has its own memory pressure gate, acts only when available is below 4 GiB and below 1/8 of total, the switch only expresses intent
        public bool WsTrimOn
        {
            get { return wsTrimOn; }
            set { wsTrimOn = value; Settings.Save(PolicyCatalog.KeyWsTrim, value); RequestPolicyApply(); }
        }

        // A silent stream pulls down the shared engine period, no registry write, closing the stream restores it
        public bool AudioLowLatOn
        {
            get { return audioLatOn; }
            set { audioLatOn = value; Settings.Save(PolicyCatalog.KeyAudioLowLat, value); if (value) ClearEnvFuse("audiolat"); RequestPolicyApply(); }
        }

        // Off by default, turning it off immediately revokes any reservation still attached
        //   turning it back on counts as the user wanting another try, and clears the breaker left by the last failed verification
        public bool VramShieldOn
        {
            get { return vramShieldOn; }
            set
            {
                if (value)
                {
                    if (!Settings.Save(VramShield.EnabledKey, true)) return;
                    vramShieldOn = true;
                    VramShield.ClearFuse();
                }
                else
                {
                    // A failed preference write must not prevent an immediate best-effort release
                    // but it cannot guarantee the opt-out choice reached disk
                    vramShieldOn = false;
                    Settings.Save(VramShield.EnabledKey, false);
                    VramShield.Release();
                }
                RequestPolicyApply();
            }
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
                if (!value)
                    IrqMutationBoundary.Run(delegate
                    {
                        AdlxTweaks.RestoreAntiLag();
                        AdlxTweaks.RestoreChill();
                    });
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
                if (!value)
                    IrqMutationBoundary.Run(delegate { AdlxTweaks.RestoreAfmf(); });
                else ClearEnvFuse("amdafmf");
                RequestPolicyApply();
            }
        }

        // The property name must not match the NvVrrWindowed static class, otherwise references to the class inside GameMode get shadowed by the property
        public bool NvVrrWindowedEnabled
        {
            get { return nvVrrWindowedOn; }
            set
            {
                nvVrrWindowedOn = value;
                Settings.Save("NvVrrWindowed", value);
                if (!value)
                    IrqMutationBoundary.Run(delegate { NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyVrrApp); });
                else ClearEnvFuse("nvvrr");
                lock (sync) tweakApplied.Clear();
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
                    IrqMutationBoundary.Run(delegate
                    {
                        NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyPState);
                        NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyForceP2);
                    });
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
                    IrqMutationBoundary.Run(delegate
                    {
                        NvDrsTweaks.RestoreKinds(NvDrsTweaks.UltraKeys);
                    });
                else
                {
                    if (mode == "on")
                        IrqMutationBoundary.Run(delegate
                        {
                            NvDrsTweaks.RestoreKinds(new[]
                            {
                                NvDrsTweaks.KeyUllEnable,
                                NvDrsTweaks.KeyLowLatCpl
                            });
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
                    IrqMutationBoundary.Run(delegate
                    {
                        NvDrsTweaks.RestoreKind(NvDrsTweaks.KeySmooth);
                    });
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
                    IrqMutationBoundary.Run(delegate
                    {
                        NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyShaderCache);
                        NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyShaderOn);
                    });
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
                    IrqMutationBoundary.Run(delegate
                    {
                        NvDrsTweaks.RestoreKinds(NvDrsTweaks.RebarKeys);
                    });
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
                    IrqMutationBoundary.Run(delegate
                    {
                        NvDrsTweaks.RestoreKinds(NvDrsTweaks.DlssKeys);
                    });
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
                    IrqMutationBoundary.Run(delegate { GpuPrefStage.Restore(); });
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
            set
            {
                killGameDvr = value;
                Settings.Save("GameDvrOff", value);
                IrqMutationBoundary.Run(SyncGameDvr);
                RequestPolicyApply();
            }
        }

        public bool MmcssOn
        {
            get { return mmcssOn; }
            set
            {
                mmcssOn = value;
                Settings.Save("GmMmcss", value);
                IrqMutationBoundary.Run(SyncMmcss);
            }
        }

        public bool PowerPlanSwitch
        {
            get { return planSwitch; }
            set
            {
                lock (sync)
                {
                    System.Threading.Interlocked.Increment(ref cpuIdleGeneration);
                    planSwitch = value;
                }
                Settings.Save("PowerPlanOn", value);
                if (value) { SaveCounter(PowerFailStreakKey, 0); ClearEnvFuse("overlay"); }
                RequestPolicyApply();
            }
        }
    }
}
