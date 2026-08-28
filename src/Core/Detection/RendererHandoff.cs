// @author bdth 2074055628@qq.com
// 文件用途 渲染交接的有界候选状态 不启动线程 不修改进程 不读写配置
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class RendererProbeTicket
    {
        public readonly long Generation;
        public readonly long StartedMs;
        public readonly GameDetection Detection;
        public volatile bool Canceled;

        internal RendererProbeTicket(long generation, long startedMs, GameDetection detection)
        {
            Generation = generation;
            StartedMs = startedMs;
            Detection = RendererHandoffTracker.Copy(detection);
        }
    }

    // 探测预算和保护生命期分开：预算耗尽不等于“不是游戏”。
    // 仍处于前台且身份/关联有效的候选只保这一份 PID，失焦后短宽限退出。
    internal sealed class RendererHandoffTracker
    {
        internal const int ProbeWindowMs = 10000;
        internal const int ProbeCooldownMs = 8000;
        internal const int MaxProbeAttempts = 2;
        internal const int UnresolvedProbeCooldownMs = 30000;
        internal const int MaxUnresolvedProbeCooldownMs = 120000;
        internal const int ForegroundGraceMs = 1500;
        internal const int ProofLifetimeMs = 3000;
        internal const int ProbeResultMaxAgeMs = 5000;

        private sealed class Candidate
        {
            public GameDetection Detection;
            public long Generation;
            public long FirstSeenMs;
            public long LastForegroundMs;
            public bool Foreground;
            public bool AllowBackground;
            public bool RecoveryReady;
            public int Attempts;
            public long NextAttemptMs;
            public bool Verified;
            public double Utilization;
            public long ProofMs;
            public bool UncertainLogged;
        }

        private readonly object gate = new object();
        private Candidate current;
        private RendererProbeTicket inFlight;
        private long generation;
        private long nextProbeMs;

        internal GameDetection Current
        {
            get { lock (gate) return current == null ? null : Copy(current.Detection); }
        }

        internal bool HasCandidate { get { lock (gate) return current != null; } }
        internal bool HasProbe { get { lock (gate) return inFlight != null; } }
        internal int Attempts { get { lock (gate) return current == null ? 0 : current.Attempts; } }

        internal void Offer(GameDetection detection, long nowMs)
        {
            if (detection == null || detection.Profile == null || detection.RendererPid <= 0
                || detection.RendererCreation <= 0 || string.IsNullOrEmpty(detection.RendererPath)) return;
            lock (gate)
            {
                if (current == null || !SameIdentity(current.Detection, detection)
                    || !SameProfile(current.Detection.Profile, detection.Profile)
                    || current.Detection.RendererSafetyOnly != detection.RendererSafetyOnly)
                {
                    CancelLocked();
                    current = new Candidate
                    {
                        Generation = ++generation,
                        FirstSeenMs = nowMs,
                        LastForegroundMs = nowMs,
                        Detection = Copy(detection)
                    };
                }
                else current.Detection = Copy(detection);
                current.Foreground = detection.RendererForeground;
                if (current.Foreground) current.LastForegroundMs = nowMs;
                // 保持已有 learned/Force 可见或后台目标的选举语义；
                // 陌生窗口化候选和仅安全保护的目标永远不能借此获得后台长驻豁免。
                current.AllowBackground = !detection.RendererForeground
                    && detection.RendererCandidateSelected && detection.RendererUserSelected
                    && !detection.RequiresGpuConfirm && !detection.RendererSafetyOnly;
            }
        }

        internal void Refresh(bool identityValid, bool foreground, long nowMs)
        {
            lock (gate)
            {
                if (current == null) return;
                if (!identityValid) { ClearLocked(); return; }
                ExpireProofLocked(nowMs);
                current.Foreground = foreground;
                if (foreground) current.LastForegroundMs = nowMs;
                else
                {
                    current.Verified = false;
                    CancelLocked();
                    if (!current.AllowBackground && nowMs - current.LastForegroundMs > ForegroundGraceMs)
                        ClearLocked();
                }
            }
        }

        internal void Recovery(GameDetection identity, bool ready)
        {
            lock (gate)
            {
                if (current == null || !SameIdentity(current.Detection, identity)) return;
                current.RecoveryReady = ready;
                if (!ready) { current.Verified = false; CancelLocked(); }
            }
        }

        internal bool Protects(int pid, long creation, string path, long nowMs)
        {
            lock (gate)
            {
                return current != null && current.Detection.RendererPid == pid
                    && creation > 0 && current.Detection.RendererCreation == creation
                    && SamePath(current.Detection.RendererPath, path)
                    && (current.Foreground || current.AllowBackground
                        || nowMs - current.LastForegroundMs <= ForegroundGraceMs);
            }
        }

        internal RendererProbeTicket BeginProbe(long nowMs)
        {
            lock (gate)
            {
                ExpireProofLocked(nowMs);
                if (current == null || inFlight != null || !current.Foreground || !current.RecoveryReady
                    || current.Detection.RendererSafetyOnly || !current.Detection.RequiresGpuConfirm
                    || current.Verified || nowMs < current.NextAttemptMs || nowMs < nextProbeMs) return null;
                current.Attempts++;
                nextProbeMs = nowMs + ProbeCooldownMs;
                int delay = ProbeCooldownMs;
                if (current.Attempts >= MaxProbeAttempts || nowMs - current.FirstSeenMs >= ProbeWindowMs)
                {
                    delay = UnresolvedProbeCooldownMs;
                    for (int i = MaxProbeAttempts; i < current.Attempts && delay < MaxUnresolvedProbeCooldownMs; i++)
                        delay = Math.Min(MaxUnresolvedProbeCooldownMs, delay * 2);
                }
                current.NextAttemptMs = nowMs + delay;
                inFlight = new RendererProbeTicket(current.Generation, nowMs, current.Detection);
                return inFlight;
            }
        }

        internal void Complete(RendererProbeTicket ticket, IDictionary<int, double> utilization,
            bool identityValid, long nowMs)
        {
            lock (gate)
            {
                if (!ReferenceEquals(inFlight, ticket)) return;
                inFlight = null;
                if (ticket == null || ticket.Canceled || !identityValid || current == null
                    || current.Generation != ticket.Generation || !current.Foreground || !current.RecoveryReady
                    || nowMs < ticket.StartedMs || nowMs - ticket.StartedMs > ProbeResultMaxAgeMs) return;
                double value;
                current.Verified = HasGpuEvidence(ticket.Detection, utilization, out value);
                if (current.Verified) { current.Utilization = value; current.ProofMs = nowMs; }
            }
        }

        internal GameDetection Confirmed(long nowMs)
        {
            lock (gate)
            {
                ExpireProofLocked(nowMs);
                if (current == null || !current.RecoveryReady || current.Detection.RendererSafetyOnly
                    || (!current.Foreground && !current.AllowBackground)) return null;
                bool hard = current.Detection.RendererCandidateSelected && !current.Detection.RequiresGpuConfirm;
                if (!hard && (!current.Verified || nowMs < current.ProofMs
                    || nowMs - current.ProofMs > ProofLifetimeMs)) return null;
                GameDetection result = Copy(current.Detection);
                result.RendererForeground = current.Foreground;
                result.RendererCandidateSelected = true;
                result.RequiresGpuConfirm = false;
                if (!hard)
                {
                    result.Evidence = Lang.F("detect.gpu", (int)current.Utilization);
                    result.RendererGpuProofExpiresMs = current.ProofMs + ProofLifetimeMs;
                }
                return result;
            }
        }

        internal bool TakeUncertainNotice(long nowMs)
        {
            lock (gate)
            {
                if (current == null || current.UncertainLogged || current.Verified
                    || current.Detection.RendererSafetyOnly || !current.Detection.RequiresGpuConfirm
                    || inFlight != null || nowMs - current.FirstSeenMs < ProbeWindowMs) return false;
                current.UncertainLogged = true;
                return true;
            }
        }

        internal void Clear()
        {
            lock (gate) ClearLocked();
        }

        private void ClearLocked()
        {
            CancelLocked();
            generation++;
            current = null;
            // 不重置全局冷却，也不假装尚未退出的采样任务已完成。
        }

        private void CancelLocked() { if (inFlight != null) inFlight.Canceled = true; }

        private void ExpireProofLocked(long nowMs)
        {
            if (current != null && current.Verified
                && (nowMs < current.ProofMs || nowMs - current.ProofMs > ProofLifetimeMs))
                current.Verified = false;
        }

        internal static bool HasGpuEvidence(GameDetection candidate, IDictionary<int, double> utilization, out double value)
        {
            value = 0;
            if (candidate == null || utilization == null
                || !utilization.TryGetValue(candidate.RendererPid, out value)
                || double.IsNaN(value) || double.IsInfinity(value) || value < GpuEvidence.MinElectUtilization) return false;
            foreach (int pid in candidate.FamilyPids)
            {
                double other;
                if (pid != candidate.RendererPid && utilization.TryGetValue(pid, out other)
                    && !double.IsNaN(other) && !double.IsInfinity(other) && other > value) return false;
            }
            return true;
        }

        internal static bool SameIdentity(GameDetection a, GameDetection b)
        {
            return a != null && b != null && a.RendererPid > 0 && a.RendererPid == b.RendererPid
                && a.RendererCreation > 0 && a.RendererCreation == b.RendererCreation
                && string.Equals(a.RendererName, b.RendererName, StringComparison.OrdinalIgnoreCase)
                && SamePath(a.RendererPath, b.RendererPath);
        }

        internal static bool SameProfile(GameProfile a, GameProfile b)
        {
            return a != null && b != null && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
                && a.ForceTrigger == b.ForceTrigger && SamePath(a.Root, b.Root)
                && SamePath(a.ExecutablePath, b.ExecutablePath)
                && SamePath(a.LearnedExecutablePath, b.LearnedExecutablePath);
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        internal static GameDetection Copy(GameDetection source)
        {
            if (source == null) return null;
            var result = new GameDetection
            {
                Profile = source.Profile == null ? null : source.Profile.Clone(),
                RendererPid = source.RendererPid, RendererCreation = source.RendererCreation,
                RendererName = source.RendererName, RendererPath = source.RendererPath,
                RendererForeground = source.RendererForeground, RendererCandidateSelected = source.RendererCandidateSelected,
                RendererUserSelected = source.RendererUserSelected, RendererLearnable = source.RendererLearnable,
                RequiresGpuConfirm = source.RequiresGpuConfirm, RendererSafetyOnly = source.RendererSafetyOnly,
                RendererGpuProofExpiresMs = source.RendererGpuProofExpiresMs,
                RendererMatchRank = source.RendererMatchRank, Evidence = source.Evidence
            };
            result.FamilyPids.UnionWith(source.FamilyPids);
            result.FamilyNames.UnionWith(source.FamilyNames);
            return result;
        }
    }
}
