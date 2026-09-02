// 文件用途 Intel 全局低延迟需用户开启 作用范围限定在已核实的对局内
using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private volatile bool intelLowLatencyOn;
        private int intelGraphicsGeneration, intelGraphicsRestorePending;
        private int intelGraphicsAppliedGeneration = int.MinValue;
        private bool intelGraphicsWasWanted;
        private bool intelGraphicsSettledThisGeneration;
        private bool intelResidueHintLogged;
        private readonly object intelGraphicsGate = new object();

        private void InitializeIntelGraphics()
        {
            intelLowLatencyOn = Settings.Load(PolicyCatalog.KeyIntelLowLatency, false);
        }

        public bool IntelLowLatency
        {
            get { return intelLowLatencyOn; }
            set
            {
                lock (sync)
                {
                    if (stopping) return;
                    InvalidateIntelGraphicsWork();
                    bool saved = Settings.Save(PolicyCatalog.KeyIntelLowLatency, value);
                    intelLowLatencyOn = value && saved;
                    if (intelLowLatencyOn) ClearEnvFuse("intelll");
                }
                RequestPolicyApply();
            }
        }

        private void IntelGraphicsPolicyChanged(bool enabledValue)
        {
            if (enabledValue) ClearEnvFuse("intelll");
            RequestPolicyApply();
        }

        private void InvalidateIntelGraphicsWork()
        {
            Interlocked.Increment(ref intelGraphicsGeneration);
        }

        private void BeginIntelGraphicsRestore()
        {
            Interlocked.Increment(ref intelGraphicsRestorePending);
            InvalidateIntelGraphicsWork();
        }

        private void EndIntelGraphicsRestore()
        {
            if (Interlocked.Decrement(ref intelGraphicsRestorePending) < 0)
                Interlocked.Exchange(ref intelGraphicsRestorePending, 0);
        }

        private Func<bool> CaptureIntelGraphicsAdmission(out int generation)
        {
            bool wanted;
            string profileId;
            int captured;
            lock (sync)
            {
                generation = captured = Volatile.Read(ref intelGraphicsGeneration);
                GameDetection detection = activeDetection;
                profileId = detection != null && detection.Profile != null ? detection.Profile.Id : null;
                PolicySnapshot policy = sessionPolicy;
                wanted = LiveBoolPreferenceLocked(PolicyCatalog.KeyIntelLowLatency, intelLowLatencyOn);
                wanted = wanted && !envFused.Contains("intelll") && !string.IsNullOrEmpty(profileId)
                    && (policy == null || string.IsNullOrEmpty(policy.ProfileId)
                        || string.Equals(policy.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
            }
            return delegate
            {
                if (!wanted || captured != Volatile.Read(ref intelGraphicsGeneration)
                    || Volatile.Read(ref intelGraphicsRestorePending) != 0
                    || !Volatile.Read(ref active) || !enabled || stopping || panicReq || ProfileStoreSaveFailed
                    || Volatile.Read(ref stickyGraceOnly) || Volatile.Read(ref gameGoneSinceTicks) != 0) return false;
                GameDetection detection = Volatile.Read(ref activeDetection);
                return detection != null && detection.Profile != null
                    && string.Equals(profileId, detection.Profile.Id, StringComparison.OrdinalIgnoreCase);
            };
        }

        private void ApplyIntelGraphicsPolicy()
        {
            int generation;
            Func<bool> admitted = CaptureIntelGraphicsAdmission(out generation);
            lock (intelGraphicsGate)
            {
                bool wanted = admitted();
                if (!wanted)
                {
                    if (intelGraphicsWasWanted || IntelGraphicsTweaks.HasResidue)
                    {
                        if (RunIrqIsolatedMutation(new Func<bool>(IntelGraphicsTweaks.Restore)))
                        { intelGraphicsWasWanted = false; intelGraphicsSettledThisGeneration = false; }
                        else MaybeLogIntelResidueHint();
                    }
                    return;
                }
                if (intelGraphicsAppliedGeneration != generation)
                {
                    if ((intelGraphicsWasWanted || IntelGraphicsTweaks.HasResidue)
                        && !RunIrqIsolatedMutation(new Func<bool>(IntelGraphicsTweaks.Restore)))
                    { MaybeLogIntelResidueHint(); return; }
                    intelGraphicsAppliedGeneration = generation;
                    intelGraphicsSettledThisGeneration = false;
                    lock (sync) { envNextAttempt.Remove("intelll"); envFailures.Remove("intelll"); }
                }
                if (!admitted()) return;
                // 驱动偏好不是一个持续强制的控制器
                // 每一代只成功施加或跳过一次 就不用轮询驱动
                // 不用重启中断观测 也不会跟用户后来的手动修改顶牛
                if (intelGraphicsSettledThisGeneration) return;
                lock (sync)
                {
                    long next;
                    if (envNextAttempt.TryGetValue("intelll", out next) && DateTime.UtcNow.Ticks < next) return;
                }
                intelGraphicsWasWanted = true;
                bool ok = RunIrqIsolatedMutation(new Func<bool>(delegate { return IntelGraphicsTweaks.TryApply(admitted); }));
                if (!admitted())
                {
                    if (RunIrqIsolatedMutation(new Func<bool>(IntelGraphicsTweaks.Restore)))
                    { intelGraphicsWasWanted = false; intelGraphicsSettledThisGeneration = false; }
                    return;
                }
                bool fused = false;
                int failures = 0;
                lock (sync)
                {
                    // 一次慢的旧驱动调用 不能把更新的开启状态或档案给关掉
                    if (generation != Volatile.Read(ref intelGraphicsGeneration) || !admitted()) return;
                    if (ok)
                    {
                        intelGraphicsSettledThisGeneration = true;
                        envFailures.Remove("intelll"); envNextAttempt.Remove("intelll");
                    }
                    else
                    {
                        envFailures.TryGetValue("intelll", out failures);
                        failures = Math.Min(EnvFuseAttempts, failures + 1);
                        envFailures["intelll"] = failures;
                        envNextAttempt["intelll"] = DateTime.UtcNow.AddSeconds(EnvRetryBaseSeconds).Ticks;
                        if (failures >= EnvFuseAttempts && envFused.Add("intelll"))
                        {
                            Settings.Save("EnvFuse_intelll", true);
                            DisableEnvSwitch("intelll");
                            fused = true;
                        }
                    }
                }
                if (fused)
                {
                    RunIrqIsolatedMutation(new Func<bool>(IntelGraphicsTweaks.Restore));
                    Logger.Warn(Lang.T("log.gamemodeenv.9") + Lang.T("set.intel.lowlatency")
                        + Lang.T("log.gamemodeenv.10") + failures + Lang.T("log.gamemodeenv.11"));
                }
            }
        }

        private bool RestoreIntelGraphics()
        {
            InvalidateIntelGraphicsWork();
            lock (intelGraphicsGate)
            {
                bool ok = !intelGraphicsWasWanted && !IntelGraphicsTweaks.HasResidue
                    || RunIrqIsolatedMutation(new Func<bool>(IntelGraphicsTweaks.Restore));
                if (ok) { intelGraphicsWasWanted = false; intelGraphicsSettledThisGeneration = false; }
                else MaybeLogIntelResidueHint();
                return ok;
            }
        }

        // 无法认领的账本只能等外部关闭后自动结账 提示一次告诉用户出口 不刷屏
        private void MaybeLogIntelResidueHint()
        {
            if (intelResidueHintLogged || !IntelGraphicsTweaks.RestoreBlockedByOwnership) return;
            intelResidueHintLogged = true;
            Logger.Log(Lang.T("intel.lowlatency.residue"));
        }

        private bool DrainIntelGraphics(int timeoutMs)
        {
            if (intelGraphicsGate == null) return true;
            if (timeoutMs < 0 || Monitor.IsEntered(sync) || Monitor.IsEntered(intelGraphicsGate)
                || !Monitor.TryEnter(intelGraphicsGate, timeoutMs)) return false;
            Monitor.Exit(intelGraphicsGate);
            return true;
        }

#if PAVISE_SELFTEST
        internal Func<bool> CaptureIntelGraphicsAdmissionForTest()
        {
            int generation;
            return CaptureIntelGraphicsAdmission(out generation);
        }
#endif
    }
}
