using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void StepAdaptiveGuard()
        {
            if (!AdaptiveEscalationWanted)
            {
                bool adaptiveEscalatedSnapshot = adaptiveEscalated;
                ResetAdaptiveGuard();
                if (adaptiveEscalatedSnapshot)
                {
                    Logger.Log(Lang.T("log.adaptive.3", 0));
                }
                return;
            }
            long now = AdaptiveNow();
            int stepResult = adaptiveGuard.Step(adaptivePressure.Update(AdaptiveSample(), now), now);
            if (stepResult > 0)
            {
                adaptiveEscalated = true;
                Logger.Log(Lang.T("log.adaptive.1", 0) + adaptiveGuard.Episodes + "/" + 3);
            }
            else if (stepResult < 0)
            {
                adaptiveEscalated = false;
                Logger.Log(Lang.T("log.adaptive.2", 0));
            }
        }
        private void ResetAdaptiveGuard()
        {
            adaptiveGuard.Reset();
            adaptivePressure.Reset();
            adaptiveEscalated = false;
        }
        private long AdaptiveNow()
        {
            return DateTime.UtcNow.Ticks;
        }
        private double AdaptiveSample()
        {
            return adaptivePressure.Sample();
        }
        private bool WideBackgroundScope(PolicySnapshot policy)
        {
            if (policy != null)
            {
                if (!policy.WideBackgroundScope)
                {
                    if (adaptiveEscalated)
                    {
                        return policy.AdaptiveEscalation;
                    }
                    return false;
                }
                return true;
            }
            return false;
        }
        private void ClearBackgroundSqueezes()
        {
            if (core.SqueezedCount(SuppressReason.Background, false) > 0)
            {
                core.ClearSqueezes(SuppressReason.Background);
            }
            bool jobReleased = core.ReleaseAllBackgroundAffinityJobs();
            coreBackgroundMoved = core.SqueezedCount(SuppressReason.Background);
            coreBackgroundPending = !jobReleased || core.SqueezedCount(SuppressReason.Background, false) > 0 || core.BackgroundAffinityPending;
            if (!coreBackgroundPending)
            {
                core.EnforceBackgroundAffinity = false;
            }
        }
        private bool ReleaseCriticalGuardianBackground()
        {
            HardThreadGuardianActor hardThreadGuardianActor = criticalGuardianActor;
            if (hardThreadGuardianActor == null)
            {
                return false;
            }
            BackgroundReleaseState state = core.ReleaseBackgroundForRenderer(hardThreadGuardianActor.Pid, hardThreadGuardianActor.Creation, Path.GetFileNameWithoutExtension(hardThreadGuardianActor.Path));
            return RuntimeBackgroundExempt(state);
        }
        private bool ReleaseJobGuardianBackground(int pid, long birth, string name)
        {
            BackgroundReleaseState state = core.ReleaseBackgroundForRenderer(pid, birth, name);
            return RuntimeBackgroundExempt(state);
        }
        private bool RunBackgroundPolicy(int epoch, Action action)
        {
            lock (familyPolicyGate)
            {
                if (!FamilyPolicyEpochCurrent(epoch))
                {
                    return false;
                }
                action();
                return true;
            }
        }
        internal static bool RuntimeBackgroundExempt(BackgroundReleaseState state)
        {
            if (state != 0 && state != BackgroundReleaseState.Gone)
            {
                return state == BackgroundReleaseState.OtherReasonActive;
            }
            return true;
        }
        internal bool SetGameDvrSuppression(bool value)
        {
            lock (sync)
            {
                if (stopping || !Settings.Save("GameDvrOff", value))
                {
                    return false;
                }
                killGameDvr = value;
                ClearEnvFuse("gamedvr");
            }
            SyncGameDvr();
            RequestPolicyApply("SetGameDvrSuppression");
            return true;
        }
        internal static SuppressionLevel BackgroundLevel()
        {
            return SuppressionLevel.Isolated;
        }
        private List<KeyValuePair<string, int>> BackgroundServiceHosts(ProcessSnapshot all)
        {
            if (!object.ReferenceEquals(servicePolicySnapshot, all))
            {
                servicePolicyHosts = null;
                bool hasServiceCandidate = false;
                if (all != null)
                {
                    foreach (ProcEntry procEntry in all.Entries)
                    {
                        if (procEntry.Pid > 4 && procEntry.Session == 0 && procEntry.Creation > 0 && BackgroundServicePolicy.CandidateName(procEntry.Name))
                        {
                            hasServiceCandidate = true;
                            break;
                        }
                    }
                }
                if (hasServiceCandidate)
                {
                    servicePolicyHosts = SvcQuery.RunningServices();
                }
                servicePolicySnapshot = all;
            }
            return servicePolicyHosts;
        }
        private void Sweep(ProcessSnapshot all, HashSet<int> gamePids)
        {
            lock (whiteEvalSync)
            {
                SweepWithStableWhitelist(all, gamePids);
            }
        }
    }
}
