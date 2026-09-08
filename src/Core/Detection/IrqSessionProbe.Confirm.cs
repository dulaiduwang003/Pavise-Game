// @author bdth 2074055628@qq.com
// 文件用途 中断采样的确认 失效与外部变更窗口
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed partial class IrqSessionProbe : IDisposable
    {
        internal static bool CanConfirmMask(
            ulong verifiedMask, ulong availableSystemMask,
            int verifiedRendererPid, long verifiedRendererCreation)
        {
            return CanConfirmMaskShape(verifiedMask, availableSystemMask)
                && verifiedRendererPid > 0
                && verifiedRendererCreation > 0;
        }

        internal static bool CanConfirmMaskShape(
            ulong verifiedMask, ulong availableSystemMask)
        {
            return verifiedMask != 0
                && availableSystemMask != 0
                && verifiedMask != availableSystemMask
                && (verifiedMask & ~availableSystemMask) == 0;
        }

        public bool ConfirmGameMask(
            ulong verifiedMask, int verifiedRendererPid,
            long verifiedRendererCreation)
        {
            return ConfirmCapture(verifiedMask, verifiedRendererPid, verifiedRendererCreation, false);
        }

        public bool ConfirmSystemObservation(int verifiedRendererPid, long verifiedRendererCreation)
        {
            // 0 表示只观测系统中断 没有证明游戏的实际核域
            return ConfirmCapture(0, verifiedRendererPid, verifiedRendererCreation, true);
        }

        internal static bool CanObserveSystem(ulong availableSystemMask, int pid, long creation)
        {
            return availableSystemMask != 0 && pid > 0 && creation > 0;
        }

        private bool ConfirmCapture(
            ulong verifiedMask, int verifiedRendererPid,
            long verifiedRendererCreation, bool observeSystem)
        {
            bool enabled = platform.Enabled;
            bool accepted = false;
            bool logStarted = false;
            bool logStartFailed = false;
            bool logNoAdmin = false;
            IIrqSessionCapture discard = null;
            IIrqSessionCapture failedStart = null;
            lock (gate)
            {
                if (disposed || !armed || gameMaskInvalid || systemObservation != observeSystem) return false;
                // RenderLane 等异步调优若正在写 renderer 起采必须
                // 等它离开写区 该计数和开采在同一 gate 下 没有
                // 回调刚查完 ETW 就开了 setter 才落下 堵的是这个窗口
                if (!observeSystem && externalMutations > 0) return false;
                bool identityValid = observeSystem
                    ? verifiedMask == 0 && CanObserveSystem(systemMask,
                        verifiedRendererPid, verifiedRendererCreation)
                    : CanConfirmMask(verifiedMask, systemMask,
                        verifiedRendererPid, verifiedRendererCreation);
                if (!enabled || !identityValid)
                {
                    discard = InvalidateLocked();
                    SetStatusLocked(enabled ? "unavailable" : "disabled", "", 0);
                }
                else if (live != null)
                {
                    if (!completed
                        && gameMask == verifiedMask
                        && rendererPid == verifiedRendererPid
                        && rendererCreation == verifiedRendererCreation)
                    {
                        long now = platform.UtcTicks;
                        bool continuous = lastProofTicks > 0
                            && now >= lastProofTicks
                            && now - lastProofTicks <= MaxProofAgeTicks;
                        if (continuous)
                        {
                            lastProofTicks = now;
                            if (coreLoads != null) coreLoads.Poll(now);
                            placementWaitScans = 0;
                            accepted = true;
                        }
                        else
                        {
                            // 超过 proof 新鲜度的空窗无法在事后补证
                            // 丢弃旧 ETW 但保持本局 armed 下轮从当前
                            // 已验证落核点重新开一个干净 epoch
                            string resumeGame = gameName;
                            ulong resumeSystem = systemMask;
                            discard = InvalidateLocked();
                            gameName = resumeGame;
                            systemMask = resumeSystem;
                            systemObservation = observeSystem;
                            gameMaskInvalid = false;
                            armed = enabled && resumeSystem != 0;
                            completed = true;
                            SetStatusLocked("waiting", "", 0);
                        }
                    }
                    else
                    {
                        discard = InvalidateLocked();
                        SetStatusLocked("invalidated", "", 0);
                    }
                }
                else if (!platform.IsElevated)
                {
                    if (!warnedNoAdmin) { warnedNoAdmin = true; logNoAdmin = true; }
                    discard = InvalidateLocked();
                    SetStatusLocked("needadmin", "", 0);
                }
                else
                {
                    IIrqSessionCapture ia = platform.CreateCapture(!observeSystem);
                    bool started = false;
                    try { started = ia.Start(); } catch { }
                    if (!started)
                    {
                        failedStart = ia;
                        logStartFailed = !ia.Busy;
                        discard = InvalidateLocked();
                        SetStatusLocked("startfailed", ia.Busy
                            ? Lang.T("irq.probe.busy") : ia.FailDetail, 0);
                    }
                    else
                    {
                        live = ia;
                        gameMask = verifiedMask;
                        rendererPid = verifiedRendererPid;
                        rendererCreation = verifiedRendererCreation;
                        startTicks = platform.UtcTicks;
                        lastProofTicks = startTicks;
                        bootStamp = platform.BootStamp;
                        topologyStamp = platform.TopologyStamp;
                        try { coreLoads = new IrqCoreLoadCapture(platform.OpenCoreLoadSource(), startTicks, systemMask); }
                        catch { coreLoads = null; }
                        completed = false;
                        placementWaitScans = 0;
                        accepted = true;
                        logStarted = true;
                        SetStatusLocked(observeSystem ? "system" : "placed", "", 0);
                    }
                }
            }
            StopAndDiscard(discard);
            StopAndDiscard(failedStart);
            if (logNoAdmin) platform.Log(Logger.WarnTag + Lang.T("log.irqsession.4"));
            if (logStartFailed) platform.Log(Logger.WarnTag + Lang.T("log.irqsession.1"));
            if (logStarted) platform.Log(Lang.T("log.irqsession.2"));
            return accepted;
        }

        public void InvalidateGameMask()
        {
            IIrqSessionCapture discard;
            lock (gate)
            {
                bool hadCapture = armed || live != null || pendingRecord != null;
                discard = InvalidateLocked();
                if (hadCapture) SetStatusLocked(platform.Enabled ? "invalidated" : "disabled", "", 0);
            }
            StopAndDiscard(discard);
        }

        // 本局调优状态需要重写时 丢弃 live 但保留 armed
        // 调用方必须先等这个方法返回 旧 ETW 已停 再写入
        // 写完后下一个 Confirm 从新证明点起采
        public void RestartCurrentEpoch()
        {
            IIrqSessionCapture discard = null;
            lock (gate)
            {
                if (disposed || !armed || gameMaskInvalid
                    || live == null || completed) return;
                string resumeGame = gameName;
                ulong resumeSystem = systemMask;
                discard = InvalidateLocked();
                gameName = resumeGame;
                systemMask = resumeSystem;
                gameMaskInvalid = false;
                armed = platform.Enabled && resumeSystem != 0;
                completed = true;
                SetStatusLocked("waiting", "", 0);
            }
            StopAndDiscard(discard);
        }

        // 供 RenderLane 这类独立 worker 在真正 setter 前后标记
        // 若已采集 先作废旧 epoch 并保留本局 armed 写入结束后
        // 下轮才能从新 proof 开始 不把 Pavise 自己的写入算入对局
        public void BeginExternalMutation()
        {
            IIrqSessionCapture discard = null;
            lock (gate)
            {
                if (disposed) return;
                externalMutations++;
                // 系统观测记录真实整机 DPC 本来就包括正常后台活动
                // 不宣称游戏核归因 因此新进程压制等写入不应把整局反复打碎
                // 严格核域证据仍必须排除这些写入造成的观测污染
                if (!systemObservation && (stopInProgress
                    || (armed && !gameMaskInvalid && live != null && !completed)))
                {
                    string resumeGame = gameName;
                    ulong resumeSystem = systemMask;
                    discard = InvalidateLocked();
                    gameName = resumeGame;
                    systemMask = resumeSystem;
                    gameMaskInvalid = false;
                    armed = platform.Enabled && resumeSystem != 0;
                    completed = true;
                    SetStatusLocked("waiting", "", 0);
                }
            }
            StopAndDiscard(discard);
        }

        public void EndExternalMutation()
        {
            lock (gate)
                if (externalMutations > 0) externalMutations--;
        }

        private IIrqSessionCapture InvalidateLocked()
        {
            if (coreLoads != null) coreLoads.Dispose();
            coreLoads = null;
            generation++;
            IIrqSessionCapture discard = live;
            live = null;
            startTicks = 0;
            lastProofTicks = 0;
            gameMask = 0;
            systemMask = 0;
            rendererPid = 0;
            rendererCreation = 0;
            bootStamp = "";
            topologyStamp = "";
            armed = false;
            gameMaskInvalid = true;
            completed = true;
            sealedPending = false;
            pendingRecord = null;
            pendingSummary = null;
            pendingTimeline = null;
            pendingTimelineTruncated = false;
            return discard;
        }

        private static void StopAndDiscard(IIrqSessionCapture ia)
        {
            if (ia == null) return;
            try { ia.Stop(); } catch { }
        }

        public string TakeSummary()
        {
            lock (takeGate)
            {
                Run(true);
                lock (gate)
                {
                    string held = pendingSummary;
                    pendingSummary = null;
                    return held;
                }
            }
        }

        // 首次检测到游戏消失时立刻封存 避免退出宽限期里的系统 DPC 混入
        // 封存不消费 pending 8 秒后的 ReportFinish 仍可照常取摘要和时间线
        public void Seal()
        {
            lock (takeGate) Run(false);
        }

        // 取走本局逐事件 DPC 时间线(取走即清) 未采到或非管理员返回 null 调用方据此优雅退回
        //   与 TakeSummary 同源 Run() 已完成后再调是幂等的(completed 后 Run 直接返回不动 pending)
        public System.Collections.Generic.List<InterruptAttribution.DpcTimelineEntry> TakeDpcTimeline(out bool truncated)
        {
            lock (takeGate)
            {
                Run(true);
                lock (gate)
                {
                    var held = pendingTimeline;
                    truncated = pendingTimelineTruncated;
                    pendingTimeline = null;
                    pendingTimelineTruncated = false;
                    return held;
                }
            }
        }
    }
}
