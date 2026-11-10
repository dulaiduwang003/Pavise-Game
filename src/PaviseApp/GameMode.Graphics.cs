using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool GpuPrefResidueGate(bool hasResidue)
        {
            if (hasResidue)
            {
                if (enabled && gpuPrefStageOn)
                {
                    return preStagedNvPath == null;
                }
                return true;
            }
            return false;
        }
        private void PreStageDriverTuning(List<GameProfile> copy, string armedName)
        {
            if (stopping || (sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete))
            {
                return;
            }
            GameProfile armed = null;
            foreach (GameProfile currentCopy in copy)
            {
                if (string.Equals(currentCopy.Name, armedName, StringComparison.OrdinalIgnoreCase))
                {
                    armed = currentCopy;
                    break;
                }
            }
            if (armed == null)
            {
                return;
            }
            PrewarmAutomatic(armed);
            string path = armed.PreferredExecutablePath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            PolicySnapshot policySnapshot;
            try
            {
                policySnapshot = PolicyResolver.For(armed);
            }
            catch
            {
                return;
            }
            NvGamePlan plan = new NvGamePlan
            {
                MaxPerf = policySnapshot.NvMaxPerf,
                LowLatMode = policySnapshot.NvLowLatMode,
                SmoothMotion = policySnapshot.NvSmoothMotion,
                ShaderCacheMax = policySnapshot.NvShaderCacheMax,
                Rebar = policySnapshot.NvRebar,
                DlssMode = policySnapshot.NvDlssMode,
                WindowedVrr = nvVrrWindowedOn
            };
            plan = AutomaticPreStagePlan(policySnapshot, Native.OnAcPower(), nvVrrWindowedOn);
            bool stageGpuPref = gpuPrefStageOn && GpuPrefStage.Supported;
            QueuePreStagedNvPlan(armed, path, plan, stageGpuPref);
        }
    }
}
