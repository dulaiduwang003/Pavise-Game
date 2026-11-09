using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class RenderLane
    {
        private sealed class RecoveryAttempt { internal int Failures; internal long Due; }
        private sealed class RecoveryOwner
        {
            internal long Session, Birth;
            internal int Pid;
            internal string Configuration;
            internal bool Exited;
            internal string Key { get { return Session + "|" + Pid + "|" + Birth + "|" + Configuration + "|" + Exited; } }
        }
        private static long automaticSession;
        private static readonly Dictionary<string, string> configurations = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> workerPhases = new Dictionary<int, string>();
        private static readonly HashSet<string> stoppedPhases = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, RecoveryAttempt> recoveryAttempts = new Dictionary<string, RecoveryAttempt>(StringComparer.Ordinal);
        private static RecoveryOwner recoveryOwner;
        private static string TargetKey(int pid, long created) { return pid + "|" + created; }
        private static string ConfigurationFor(int pid, long created)
        { string value; return configurations.TryGetValue(TargetKey(pid, created), out value) ? value : "render-lane"; }
        private static string LanePhase(int pid, long created)
        { return automaticSession + "|" + TargetKey(pid, created) + "|" + ConfigurationFor(pid, created); }
        internal static void SyncAutomaticPhase(long gameSerial, int pid, long created, string materialConfig)
        {
            lock (sync)
            {
                if (gameSerial < automaticSession) return;
                if (gameSerial > automaticSession && recoveryOwner != null) recoveryOwner.Session = gameSerial;
                automaticSession = gameSerial;
                if (pid > 0 && created > 0) configurations[TargetKey(pid, created)] = materialConfig ?? "render-lane";
            }
        }
        private static long RecoveryClock() { return AutoCores.NowTicks; }
        private static int RecoveryProcessState(int pid, long created) { return NativeFrameThreads.ProcessState(pid, created); }
        private static void BindLaneRecoveryOwner()
        {
            lock (sync)
            {
                if (recoveryOwner != null) return;
                int ownerPid = lanePid, tid, original; long ownerBirth = laneCreation;
                string journal;
                if (!laneApplied && Settings.TryLoadStr("RenderLane", out journal)) ParseJournal(journal, out ownerPid, out ownerBirth, out tid, out original);
                recoveryOwner = new RecoveryOwner { Session = automaticSession, Pid = ownerPid, Birth = ownerBirth, Configuration = ConfigurationFor(ownerPid, ownerBirth) };
            }
        }
        private static void SettleLaneRecovery()
        {
            lock (sync)
            {
                if (recoveryOwner != null) recoveryAttempts.Remove(recoveryOwner.Key);
                recoveryOwner = null;
            }
        }
        private static bool RunLaneRecovery(Func<bool> work)
        {
            RecoveryAttempt attempt;
            RecoveryOwner owner;
            lock (sync)
            {
                owner = recoveryOwner;
                if (!owner.Exited && owner.Pid > 0 && owner.Birth > 0 && RecoveryProcessState(owner.Pid, owner.Birth) > 0) owner.Exited = true;
                if (!recoveryAttempts.TryGetValue(owner.Key, out attempt)) recoveryAttempts[owner.Key] = attempt = new RecoveryAttempt();
                if (attempt.Failures >= 3 || RecoveryClock() < attempt.Due) { ReportLaneRecovery(owner, attempt); return false; }
            }
            bool complete = false;
            try { complete = work(); return complete; }
            finally
            {
                lock (sync)
                {
                    if (complete)
                    {
                        attempt.Failures = 0; attempt.Due = 0;
                        if (object.ReferenceEquals(recoveryOwner, owner)) recoveryOwner = null;
                    }
                    else
                    {
                        attempt.Failures++;
                        attempt.Due = RecoveryClock() + TimeSpan.FromSeconds(30).Ticks;
                        ReportLaneRecovery(owner, attempt);
                        if (attempt.Failures == 3) Logger.Log("渲染通道恢复连续失败三次后暂停，保留原线程所有权");
                    }
                }
            }
        }
        private static void ReportLaneRecovery(RecoveryOwner owner, RecoveryAttempt attempt)
        {
            if (attempt.Failures < 3) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(Math.Max(0, attempt.Due - RecoveryClock()))); return; }
            string phase = owner.Key;
            RecoveryAttemptTrace.Exhausted(delegate
            {
                lock (sync)
                {
                    if (!object.ReferenceEquals(owner, recoveryOwner) || owner.Key != phase) return true;
                    return !owner.Exited && owner.Pid > 0 && owner.Birth > 0 && RecoveryProcessState(owner.Pid, owner.Birth) > 0;
                }
            });
        }
    }
}
