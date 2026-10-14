// 作者：bdth 2074055628@qq.com
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
        private int preStagedAutomaticNvKey = -1;
        private long preStagedRetryGeneration = -1;
        private readonly object driverStageGate = new object();

        internal static NvGamePlan AutomaticPreStagePlan(PolicySnapshot policy, bool onAc, bool windowedVrr)
        {
            return NvGamePlan.ForAutomatic(policy.NvMaxPerf, policy.NvShaderCacheMax,
                onAc, policy.NvLowLatMode, windowedVrr, policy.NvRebar);
        }

        private bool RunPreStagedMutation(Action mutation)
        {
            lock (driverStageGate)
            {
                if (stopping || IsActive || mutation == null) return false;
                mutation();
                return true;
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
            PrewarmAutomatic(armed);
            string path = armed.PreferredExecutablePath;
            if (string.IsNullOrEmpty(path)) return;
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
            if (AutomaticPolicy.Enabled)
                plan = AutomaticPreStagePlan(sp, Native.OnAcPower(), nvVrrWindowedOn);
            bool stageGpuPref = gpuPrefStageOn && GpuPrefStage.Supported;
            if (AutomaticPolicy.Enabled) stageGpuPref = sp.Preset != PerformancePreset.Handheld
                && Native.OnAcPower() && GpuPrefStage.Supported;
            int automaticKey = plan.AutomaticKey | (stageGpuPref ? 32 : 0);
            long retryGeneration = ActionFailureStore.RetryGeneration;
            if (string.Equals(preStagedNvPath, path, StringComparison.OrdinalIgnoreCase)
                && (!AutomaticPolicy.Enabled || preStagedAutomaticNvKey == automaticKey)
                && Interlocked.Read(ref preStagedRetryGeneration) == retryGeneration) return;
            if (plan.Empty && !stageGpuPref) return;
            string previous = preStagedNvPath;
            preStagedNvPath = path;
            preStagedAutomaticNvKey = automaticKey;
            Interlocked.Exchange(ref preStagedRetryGeneration, retryGeneration);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    RunPreStagedMutation(delegate
                    {
                        if (retryGeneration != ActionFailureStore.RetryGeneration
                            || Interlocked.Read(ref preStagedRetryGeneration) != retryGeneration) return;
                        if (!enabled || panicReq || ProfileStoreSaveFailed) { preStagedNvPath = null; return; }
                        lock (sync)
                        {
                            GameProfile current = FindProfileLocked(armed.Id);
                            if (current == null || PolicyResolver.For(current).NvRebar != plan.Rebar)
                            { preStagedNvPath = null; return; }
                            if (AutomaticPolicy.Enabled && AutomaticPreStagePlan(PolicyResolver.For(current),
                                Native.OnAcPower(), nvVrrWindowedOn).AutomaticKey != plan.AutomaticKey)
                            { preStagedNvPath = null; return; }
                        }
                        if (AutomaticPolicy.Enabled && (!string.Equals(preStagedNvPath, path, StringComparison.OrdinalIgnoreCase)
                            || preStagedAutomaticNvKey != automaticKey)) return;
                        if (!string.IsNullOrEmpty(previous)
                            && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
                            NvDrsTweaks.RestoreAllGames();
                        if (stageGpuPref) GpuPrefStage.Stage(path);
                        if (plan.Empty) return;
                        bool retry;
                        List<string> failed = ApplyNvGamePlan(path, plan, out retry, preStage: true);
                        HandleNvTweakOutcome(failed, plan, true);
                        if (retry || (failed != null && failed.Count > 0)) preStagedNvPath = null;
                    });
                }
                catch { }
            });
        }

        private void ReleasePreStagedTuning()
        {
            if (AutomaticPolicy.Enabled) InvalidateCacheWarm();
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
