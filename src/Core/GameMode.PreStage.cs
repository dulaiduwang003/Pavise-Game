// @author bdth 2074055628@qq.com
// File purpose Pre-staged writes for driver tuning, release and standby status broadcast
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private volatile string preStagedNvPath;
        private readonly object driverStageGate = new object();

        private bool RunPreStagedMutation(Action mutation)
        {
            lock (driverStageGate)
            {
                if (stopping || IsActive || mutation == null) return false;
                bool applied = false;
                IrqMutationBoundary.Run(delegate
                {
                    // Entering the interrupt boundary may take time, re-check before the first native write
                    // Stop drains this same gate
                    if (stopping || IsActive) return;
                    mutation();
                    applied = true;
                });
                return applied;
            }
        }

        private void PreStageDriverTuning(List<GameProfile> copy, string armedName)
        {
            if (stopping) return;
            GameProfile armed = null;
            foreach (GameProfile p in copy)
                if (string.Equals(p.Name, armedName, StringComparison.OrdinalIgnoreCase))
                { armed = p; break; }
            if (armed == null) return;
            string path = armed.PreferredExecutablePath;
            if (string.IsNullOrEmpty(path)) return;
            if (string.Equals(preStagedNvPath, path, StringComparison.OrdinalIgnoreCase)) return;
            PolicySnapshot sp;
            try { sp = PolicyResolver.For(armed); } catch { return; }
            var plan = new NvGamePlan
            {
                MaxPerf = sp.NvMaxPerf,
                LowLatMode = sp.NvLowLatMode,
                SmoothMotion = sp.NvSmoothMotion,
                ShaderCacheMax = sp.NvShaderCacheMax,
                Rebar = sp.NvRebar,
                DlssMode = sp.NvDlssMode,
                WindowedVrr = nvVrrWindowedOn
            };
            bool stageGpuPref = gpuPrefStageOn && GpuPrefStage.Supported;
            if (plan.Empty && !stageGpuPref) return;
            string previous = preStagedNvPath;
            preStagedNvPath = path;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    RunPreStagedMutation(delegate
                    {
                        if (!string.IsNullOrEmpty(previous)
                            && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
                            NvDrsTweaks.RestoreAllGames();
                        if (stageGpuPref) GpuPrefStage.Stage(path);
                        if (plan.Empty) return;
                        bool retry;
                        NvDrsTweaks.ApplyForGame(path, plan, out retry);
                        if (retry) preStagedNvPath = null;
                    });
                }
                catch { }
            });
        }

        private void ReleasePreStagedTuning()
        {
            if (stopping || preStagedNvPath == null) return;
            preStagedNvPath = null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    RunPreStagedMutation(delegate
                    {
                        NvDrsTweaks.RestoreAllGames();
                        GpuPrefStage.Restore();
                    });
                }
                catch { }
            });
        }

        private void UpdateArmedStatus(string name, string via, bool engaged)
        {
            armedGameName = name;
            if (string.Equals(name, lastArmedLogged, StringComparison.OrdinalIgnoreCase)) return;
            if (name != null)
                Logger.Log(Lang.T("log.gamemode.54") + name + Lang.T("log.gamemode.55")
                    + (string.IsNullOrEmpty(via) ? "" : Lang.T("log.gamemode.62") + via));
            else if (lastArmedLogged != null && !engaged)
            {
                Logger.Log(Lang.T("log.gamemode.56") + lastArmedLogged + Lang.T("log.gamemode.57"));
                ReleasePreStagedTuning();
            }
            lastArmedLogged = name;
        }
    }
}
