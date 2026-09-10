// @author bdth 2074055628@qq.com
// 文件用途 驱动调优的预置写入 释放与待命状态播报
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
                    // 起中断边界可能要花时间 第一次原生写入之前再查一遍
                    // Stop 排的也是这同一道闸
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
