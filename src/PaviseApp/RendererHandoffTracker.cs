using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal enum RendererProof
    {
        None,
        Gpu,
        Present,
        Rejected
    }

    internal sealed class RendererHandoffTracker
    {
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

            public int SlowAttempts;

            public int Rejections;

            public long NextAttemptMs;

            public bool Verified;

            public double Utilization;

            public double PresentFps;

            public RendererProof Proof;

            public long ProofMs;

            public bool UncertainLogged;
        }

        internal const int ProbeWindowMs = 10000;

        internal const int ProbeCooldownMs = 8000;

        internal const int QuickProbeWindowMs = 60000;

        internal const int MaxProbeAttempts = 2;

        internal const int UnresolvedProbeCooldownMs = 30000;

        internal const int MaxUnresolvedProbeCooldownMs = 120000;

        internal const int ForegroundGraceMs = 1500;

        internal const int ProofLifetimeMs = 3000;

        // 一次探测包含两个连续的观测窗口，约三秒；留出采集启停的余量，但必须短于两次探测的间隔。
        internal const int ProbeResultMaxAgeMs = 7000;

        private readonly object gate = new object();

        private Candidate current;

        private RendererProbeTicket inFlight;

        private long generation;

        private long nextProbeMs;

        internal GameDetection Current
        {
            get
            {
                lock (gate)
                {
                    return (current == null) ? null : Copy(current.Detection);
                }
            }
        }

        internal bool HasCandidate
        {
            get
            {
                lock (gate)
                {
                    return current != null;
                }
            }
        }

        internal bool HasProbe
        {
            get
            {
                lock (gate)
                {
                    return inFlight != null;
                }
            }
        }

        internal int Attempts
        {
            get
            {
                lock (gate)
                {
                    return (current != null) ? current.Attempts : 0;
                }
            }
        }

        internal void Offer(GameDetection detection, long nowMs)
        {
            if (detection == null || detection.Profile == null || detection.RendererPid <= 0 || detection.RendererCreation <= 0 || string.IsNullOrEmpty(detection.RendererPath))
            {
                return;
            }
            lock (gate)
            {
                if (current == null || !SameIdentity(current.Detection, detection) || !SameProfile(current.Detection.Profile, detection.Profile) || current.Detection.RendererSafetyOnly != detection.RendererSafetyOnly)
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
                else
                {
                    current.Detection = Copy(detection);
                }
                current.Foreground = detection.RendererForeground;
                if (current.Foreground)
                {
                    current.LastForegroundMs = nowMs;
                }
                current.AllowBackground = !detection.RendererForeground && detection.RendererCandidateSelected && detection.RendererUserSelected && !detection.RequiresGpuConfirm && !detection.RendererSafetyOnly;
            }
        }

        internal void Refresh(bool identityValid, bool foreground, long nowMs)
        {
            lock (gate)
            {
                if (current == null)
                {
                    return;
                }
                if (!identityValid)
                {
                    ClearLocked();
                    return;
                }
                ExpireProofLocked(nowMs);
                current.Foreground = foreground;
                if (foreground)
                {
                    current.LastForegroundMs = nowMs;
                    return;
                }
                current.Verified = false;
                CancelLocked();
                if (!current.AllowBackground && nowMs - current.LastForegroundMs > 1500)
                {
                    ClearLocked();
                }
            }
        }

        internal void Recovery(GameDetection identity, bool ready)
        {
            lock (gate)
            {
                if (current != null && SameIdentity(current.Detection, identity))
                {
                    current.RecoveryReady = ready;
                    if (!ready)
                    {
                        current.Verified = false;
                        CancelLocked();
                    }
                }
            }
        }

        internal bool Protects(int pid, long creation, string path, long nowMs)
        {
            lock (gate)
            {
                return current != null && current.Detection.RendererPid == pid && creation > 0 && current.Detection.RendererCreation == creation && SamePath(current.Detection.RendererPath, path) && (current.Foreground || current.AllowBackground || nowMs - current.LastForegroundMs <= 1500);
            }
        }

        internal RendererProbeTicket BeginProbe(long nowMs)
        {
            lock (gate)
            {
                ExpireProofLocked(nowMs);
                if (current == null || inFlight != null || !current.Foreground || !current.RecoveryReady || current.Detection.RendererSafetyOnly || !current.Detection.RequiresGpuConfirm || current.Verified || nowMs < current.NextAttemptMs || nowMs < nextProbeMs)
                {
                    return null;
                }
                current.Attempts++;
                nextProbeMs = nowMs + 8000;
        // 首分钟保持 8 秒探测间隔，覆盖加载期；之后按 30、60、120 秒退避。
                int beginProbeValue = 8000;
                if (nowMs - current.FirstSeenMs >= QuickProbeWindowMs)
                {
                    current.SlowAttempts++;
                    beginProbeValue = 30000;
                    for (int i = 1; i < current.SlowAttempts; i++)
                    {
                        if (beginProbeValue >= 120000)
                        {
                            break;
                        }
                        beginProbeValue = Math.Min(120000, beginProbeValue * 2);
                    }
                }
                current.NextAttemptMs = nowMs + beginProbeValue;
                inFlight = new RendererProbeTicket(current.Generation, nowMs, current.Detection);
                return inFlight;
            }
        }

        internal void Complete(RendererProbeTicket ticket, IDictionary<int, double> utilization, bool identityValid, long nowMs)
        {
            Complete(ticket, utilization, null, identityValid, nowMs);
        }

        internal RendererProof Complete(RendererProbeTicket ticket, IDictionary<int, double> utilization, PresentActivitySample presents, bool identityValid, long nowMs)
        {
            return Complete(ticket, utilization, presents, identityValid, nowMs, false);
        }

        internal RendererProof Complete(RendererProbeTicket ticket, IDictionary<int, double> utilization, PresentActivitySample presents, bool identityValid, long nowMs, bool gpuRequired)
        {
            lock (gate)
            {
                if (!object.ReferenceEquals(inFlight, ticket))
                {
                    return RendererProof.None;
                }
                inFlight = null;
                if (ticket != null && !ticket.Canceled && identityValid && current != null && current.Generation == ticket.Generation && current.Foreground && current.RecoveryReady && nowMs >= ticket.StartedMs && nowMs - ticket.StartedMs <= ProbeResultMaxAgeMs)
                {
                    double gpu, fps;
                    RendererProof proof = ResolveEvidence(ticket.Detection, utilization, presents, gpuRequired, out gpu, out fps);
                    if (proof == RendererProof.Rejected)
                    {
                        // 同家族另一进程在提交画面、本进程为零（如启动器窗口与独立渲染子进程），短时间内不会改变：
                        // 按 30、60、120 秒退避，不再沿用首分钟的 8 秒间隔反复启动画面采集。
                        current.Rejections++;
                        long cooldown = UnresolvedProbeCooldownMs;
                        for (int i = 1; i < current.Rejections && cooldown < MaxUnresolvedProbeCooldownMs; i++)
                        {
                            cooldown = Math.Min(MaxUnresolvedProbeCooldownMs, cooldown * 2);
                        }
                        current.NextAttemptMs = Math.Max(current.NextAttemptMs, nowMs + cooldown);
                    }
                    else
                    {
                        current.Rejections = 0;
                    }
                    current.Verified = proof == RendererProof.Present || proof == RendererProof.Gpu;
                    if (current.Verified)
                    {
                        current.Proof = proof;
                        current.Utilization = gpu;
                        current.PresentFps = fps;
                        current.ProofMs = nowMs;
                    }
                    return proof;
                }
                return RendererProof.None;
            }
        }

        internal const int MinPresentFrames = 8;

        // GPU 数据可用时，以 3D 负载阈值辅助区分启动器与游戏；缺少数据时仍可使用提交证据。
        internal const double MinPresentGpu = 3.0;

        // 优先使用画面提交证据，帧数无法区分时才参考 GPU 使用率。
        internal static RendererProof ResolveEvidence(GameDetection candidate, IDictionary<int, double> utilization, PresentActivitySample presents, out double gpu, out double fps)
        {
            return ResolveEvidence(candidate, utilization, presents, false, out gpu, out fps);
        }

        // 替换已确认渲染进程时要求 GPU 3D 证据，缺失数据不能仅凭提交数量放行。
        internal static RendererProof ResolveEvidence(GameDetection candidate, IDictionary<int, double> utilization, PresentActivitySample presents, bool gpuRequired, out double gpu, out double fps)
        {
            gpu = 0.0;
            fps = 0.0;
            if (candidate == null || candidate.RendererPid <= 0)
            {
                return RendererProof.None;
            }
            if (presents != null && presents.Valid)
            {
                int own = presents.FramesOf(candidate.RendererPid);
                int other = 0;
                foreach (int familyPid in candidate.FamilyPids)
                {
                    if (familyPid != candidate.RendererPid)
                    {
                        other = Math.Max(other, presents.FramesOf(familyPid));
                    }
                }
                // 要求明确的提交帧数领先，因为网页视图也可能按屏幕刷新率提交。
                if (own >= MinPresentFrames && (long)own * 2 >= (long)other * 3)
                {
                    if (gpuRequired && !HasGpuEvidence(candidate, utilization, out gpu))
                    {
                        return RendererProof.None;
                    }
            // 采样失败或缺少目标均按无 GPU 证据处理。
                    double observed;
                    if (utilization == null || !utilization.TryGetValue(candidate.RendererPid, out observed) || double.IsNaN(observed) || double.IsInfinity(observed))
                    {
                        return RendererProof.None;
                    }
                    gpu = observed;
                    if (observed < MinPresentGpu)
                    {
                        return RendererProof.None;
                    }
                    fps = presents.FpsOf(candidate.RendererPid);
                    return RendererProof.Present;
                }
                if (own == 0 && other >= MinPresentFrames)
                {
                    // 另一游戏进程已提交画面时，前台辅助进程或覆盖层不再具备候选资格。
                    return RendererProof.Rejected;
                }
            }
            return HasGpuEvidence(candidate, utilization, out gpu) ? RendererProof.Gpu : RendererProof.None;
        }

        internal GameDetection Confirmed(long nowMs)
        {
            lock (gate)
            {
                ExpireProofLocked(nowMs);
                if (current == null || !current.RecoveryReady || current.Detection.RendererSafetyOnly || (!current.Foreground && !current.AllowBackground))
                {
                    return null;
                }
                bool rendererObserved = current.Detection.RendererCandidateSelected && !current.Detection.RequiresGpuConfirm;
                if (!rendererObserved && (!current.Verified || nowMs < current.ProofMs || nowMs - current.ProofMs > 3000))
                {
                    return null;
                }
                GameDetection gameDetection = Copy(current.Detection);
                gameDetection.RendererForeground = current.Foreground;
                gameDetection.RendererCandidateSelected = true;
                gameDetection.RequiresGpuConfirm = false;
                if (!rendererObserved)
                {
                    gameDetection.Evidence = current.Proof == RendererProof.Present
                        ? Lang.F("detect.present", (int)Math.Round(current.PresentFps))
                        : Lang.F("detect.gpu", (int)current.Utilization);
                    gameDetection.RendererGpuProofExpiresMs = current.ProofMs + 3000;
                }
                return gameDetection;
            }
        }

        internal bool TakeUncertainNotice(long nowMs)
        {
            lock (gate)
            {
                if (current == null || current.UncertainLogged || current.Verified || current.Detection.RendererSafetyOnly || !current.Detection.RequiresGpuConfirm || inFlight != null || nowMs - current.FirstSeenMs < 10000)
                {
                    return false;
                }
                current.UncertainLogged = true;
                return true;
            }
        }

        // 当前候选的只读摘要，给待命迟迟不入场时的诊断用。
        internal string Describe(long nowMs)
        {
            lock (gate)
            {
                if (current == null) return "无";
                return current.Detection.RendererName + "(" + current.Detection.RendererPid + ") 在前台=" + (current.Foreground ? "是" : "否")
                    + " 恢复就绪=" + (current.RecoveryReady ? "是" : "否") + " 需探测=" + (current.Detection.RequiresGpuConfirm ? "是" : "否")
                    + " 已探测=" + current.Attempts + " 距下次探测毫秒=" + Math.Max(0L, Math.Max(current.NextAttemptMs, nextProbeMs) - nowMs)
                    + " 探测在途=" + (inFlight != null ? "是" : "否");
            }
        }

        internal void Clear()
        {
            lock (gate)
            {
                ClearLocked();
            }
        }

        private void ClearLocked()
        {
            CancelLocked();
            generation++;
            current = null;
        }

        private void CancelLocked()
        {
            if (inFlight != null)
            {
                inFlight.Canceled = true;
            }
        }

        private void ExpireProofLocked(long nowMs)
        {
            if (current != null && current.Verified && (nowMs < current.ProofMs || nowMs - current.ProofMs > 3000))
            {
                current.Verified = false;
            }
        }

        internal static bool HasGpuEvidence(GameDetection candidate, IDictionary<int, double> utilization, out double value)
        {
            value = 0.0;
            if (candidate == null || utilization == null || !utilization.TryGetValue(candidate.RendererPid, out value) || double.IsNaN(value) || double.IsInfinity(value) || value < 10.0)
            {
                return false;
            }
            foreach (int familyPid in candidate.FamilyPids)
            {
                double value2;
                if (familyPid != candidate.RendererPid && utilization.TryGetValue(familyPid, out value2) && !double.IsNaN(value2) && !double.IsInfinity(value2) && value2 > value)
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool SameIdentity(GameDetection a, GameDetection b)
        {
            if (a != null && b != null && a.RendererPid > 0 && a.RendererPid == b.RendererPid && a.RendererCreation > 0 && a.RendererCreation == b.RendererCreation && string.Equals(a.RendererName, b.RendererName, StringComparison.OrdinalIgnoreCase))
            {
                return SamePath(a.RendererPath, b.RendererPath);
            }
            return false;
        }

        internal static bool SameProfile(GameProfile a, GameProfile b)
        {
            if (a != null && b != null && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase) && a.ForceTrigger == b.ForceTrigger && SamePath(a.Root, b.Root) && SamePath(a.ExecutablePath, b.ExecutablePath))
            {
                return SamePath(a.LearnedExecutablePath, b.LearnedExecutablePath);
            }
            return false;
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        internal static GameDetection Copy(GameDetection source)
        {
            if (source == null)
            {
                return null;
            }
            GameDetection gameDetection = new GameDetection();
            gameDetection.Profile = ((source.Profile == null) ? null : source.Profile.Clone());
            gameDetection.RendererPid = source.RendererPid;
            gameDetection.RendererCreation = source.RendererCreation;
            gameDetection.RendererName = source.RendererName;
            gameDetection.RendererPath = source.RendererPath;
            gameDetection.RendererForeground = source.RendererForeground;
            gameDetection.RendererCandidateSelected = source.RendererCandidateSelected;
            gameDetection.RendererUserSelected = source.RendererUserSelected;
            gameDetection.RendererLearnable = source.RendererLearnable;
            gameDetection.RequiresGpuConfirm = source.RequiresGpuConfirm;
            gameDetection.RendererSafetyOnly = source.RendererSafetyOnly;
            gameDetection.RendererGpuProofExpiresMs = source.RendererGpuProofExpiresMs;
            gameDetection.RendererMatchRank = source.RendererMatchRank;
            gameDetection.Evidence = source.Evidence;
            GameDetection gameDetection2 = gameDetection;
            gameDetection2.FamilyPids.UnionWith(source.FamilyPids);
            gameDetection2.FamilyNames.UnionWith(source.FamilyNames);
            return gameDetection2;
        }
    }
}
