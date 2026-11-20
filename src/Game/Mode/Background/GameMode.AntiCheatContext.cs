namespace PaviseApp
{
    internal partial class GameMode
    {
        private int antiCheatStablePid;
        private long antiCheatStableBirth, antiCheatStableSerial, antiCheatStableSince;

        internal AntiCheatAffinityGameContext GetAntiCheatAffinityContext()
        {
            lock (sync)
            {
                GameDetection detection = activeDetection;
                if (!active || !enabled || stopping || panicReq || ProfileStoreSaveFailed || gameGoneSinceTicks != 0
                    || detection == null || !detection.RendererCandidateSelected || detection.RendererSafetyOnly
                    || detection.RequiresGpuConfirm || detection.RendererPid <= 0 || detection.RendererCreation <= 0)
                {
                    AntiCheatContextStable(null, 0);
                    return null;
                }
                if (!AntiCheatContextStable(detection, AutoCores.NowTicks)) return null;
                ulong reserved = 0, domain = 0, avoid = 0;
                if (!CpuTopology.MultiGroup)
                {
                    // 反作弊优先避让用户配置的游戏范围 不依赖本局是否强制执行该范围
                    if (coreManual != null) avoid = coreManual.GameMask & allMask;
                    if (corePlan != null) reserved |= corePlan.Allocated;
                    HardPin pin;
                    if (coreEngaged && hardPins.TryGetValue(detection.RendererPid, out pin) && pin.Creation == detection.RendererCreation)
                    {
                        if (pin.AppliedAffinity != allMask) domain = pin.AppliedAffinity;
                        if (coreManual != null && coreManual.Isolation && CoreIsolationClient.State == "schedule.isolation.active") reserved |= pin.AppliedAffinity;
                    }
                    CriticalThreadUiState threads = criticalThreadUi;
                    if (threads.TargetPid == detection.RendererPid && threads.TargetBirth == detection.RendererCreation && (threads.Applied || threads.Pending))
                        foreach (ulong mask in threads.CandidateMasks) reserved |= mask;
                }
                ProcessSnapshot processes = latestProcessSnapshot;
                System.Collections.Generic.HashSet<int> ancestors = VerifiedAntiCheatAncestors(processes, detection);
                if (ancestors == null) return null;
                return new AntiCheatAffinityGameContext(detection.RendererPid, detection.RendererCreation, gameSessionStarts,
                    CpuTopology.MultiGroup ? -1 : 0, reserved, domain, ancestors, processes, avoid);
            }
        }

        // 反作弊引擎每轮读取 为空表示不在对局 游戏退出宽限开始即视为退局
        // 读取游戏库里的最新设置 对局中改动立即生效 没有单独设置过的游戏按关处理
        internal bool? GetAntiCheatSessionChoice()
        {
            lock (sync)
            {
                GameDetection detection = activeDetection;
                if (!active || !enabled || stopping || panicReq || ProfileStoreSaveFailed || gameGoneSinceTicks != 0
                    || detection == null || detection.Profile == null)
                {
                    return null;
                }
                return AntiCheatSuppressionPolicy.Enabled(FindProfileLocked(detection.Profile.Id) ?? detection.Profile);
            }
        }

        // 对局开始 退局和逐游戏开关变化时唤醒反作弊引擎 其余时间引擎不轮询
        internal System.Action AntiCheatSessionChanged;

        private void NotifyAntiCheatSession()
        {
            System.Action changed = AntiCheatSessionChanged;
            if (changed == null) return;
            try { changed(); }
            catch { }
        }

        private static System.Collections.Generic.HashSet<int> VerifiedAntiCheatAncestors(ProcessSnapshot snapshot, GameDetection detection)
        {
            if (snapshot == null || detection == null) return null;
            ProcEntry renderer = snapshot.Find(detection.RendererPid);
            if (renderer == null || renderer.Creation != detection.RendererCreation || renderer.Creation <= 0) return null;
            var seeds = new System.Collections.Generic.HashSet<int>(detection.FamilyPids) { detection.RendererPid };
            var result = new System.Collections.Generic.HashSet<int>();
            foreach (int seed in seeds)
            {
                if (seed <= 4) continue;
                result.Add(seed);
                ProcEntry child = snapshot.Find(seed);
                var chain = new System.Collections.Generic.HashSet<int>();
                while (child != null && child.Pid > 4)
                {
                    if (child.Creation <= 0 || !chain.Add(child.Pid)) return null;
                    result.Add(child.Pid);
                    if (child.ParentPid <= 4) break;
                    ProcEntry parent = snapshot.Find(child.ParentPid);
                    // 完整快照中缺失的父进程视为已退出 更晚的创建时间证明 PID 已复用
                    if (parent == null || parent.Creation > child.Creation) break;
                    child = parent;
                }
            }
            return result;
        }

        // 会话时钟独立于首次提优和 GPU 自动化 前台变化期间 调用方需持续持有 sync
        private bool AntiCheatContextStable(GameDetection detection, long now)
        {
            if (detection == null || now <= 0)
            {
                antiCheatStablePid = 0;
                antiCheatStableSince = 0;
                return false;
            }
            if (antiCheatStablePid != detection.RendererPid || antiCheatStableBirth != detection.RendererCreation
                || antiCheatStableSerial != gameSessionStarts || antiCheatStableSince == 0 || now < antiCheatStableSince)
            {
                antiCheatStablePid = detection.RendererPid;
                antiCheatStableBirth = detection.RendererCreation;
                antiCheatStableSerial = gameSessionStarts;
                antiCheatStableSince = now;
                return false;
            }
            return now - antiCheatStableSince >= 3 * System.TimeSpan.TicksPerSecond;
        }
    }
}
