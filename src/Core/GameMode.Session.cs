// @author bdth 2074055628@qq.com
// 文件用途 对局期中断观测的开关同步 布防与封存
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly Dictionary<int, long> repCpu = new Dictionary<int, long>();
        private readonly Dictionary<int, long> repCreation = new Dictionary<int, long>();
        private readonly Dictionary<int, string> repProc = new Dictionary<int, string>();
        private readonly Dictionary<int, long> repSealed = new Dictionary<int, long>();
        private long repStart;
        private string repGame;
        private long repPaviseCpuStart;
        private string repProfileId;
        private int repRendererPid;
        private bool repIrqRequested;

        public event Action<string> SessionEnded;

        // 概览页"最近一局"要的是一行能看完的短摘要 和托盘气泡那条长文不是一回事
        //   长文带 Pavise 自身占用 显卡受限 显存溢出 中断台账 塞不进一行
        //   这里只留游戏名 时长 压制进程数 三项 其余仍然只进日志和气泡
        internal const string LastSessionKey = "LastSessionBrief";

        public static string LastSessionBrief
        {
            get { return Settings.LoadStr(LastSessionKey, ""); }
        }

        public event Action<string> SessionBriefed;

        // 对局结束回顾式挪核建议 只在本局够长且观测开着时 基于已落盘的多局实测跑一次判定
        //   有 Worth 驱动就把数量抛给 UI 高亮提示 不自动改注册表 用户走手动流程
        public event Action<int> IrqSuggested;

        // 原始记录完成和有没有挪核建议是两回事 零建议和失败一样要刷页面
        public event Action IrqObservationUpdated;
        public string IrqObservationStatusText { get { return irqProbe.StatusText; } }
        public bool IrqObservationStatusWarning { get { return irqProbe.StatusWarning; } }
        private string irqNotifiedStatus = "";
        private bool irqNotifiedWarning;
        private bool presentProbeAttempted;
        private long presentProbeEpoch = -1;
        private int irqSettingChanged;

        public void RequestIrqObservationSettingChanged()
        {
            System.Threading.Interlocked.Exchange(ref irqSettingChanged, 1);
            try { kick.Set(); } catch { }
        }

        private void ApplyIrqObservationSettingChange(string game)
        {
            if (System.Threading.Interlocked.Exchange(ref irqSettingChanged, 0) == 0) return;
            // 用户局中显式动了观测开关 那是明确的本局观测请求 预算让路
            irqObserveThisSession = true;
            ArmIrqObservation(game);
            if (IrqSessionProbe.EnabledSetting)
                lock (sync) repIrqRequested = true;
            if (irqProbe.RequiresPlacementAudit)
            {
                // 局中显式开启时 让普通落核缓存重新经过严格 proof 初始化
                // 单纯一次采样失败不会走这里 不会每轮强制重试或改写亲和性
                lock (sync)
                {
                    int pid = activeDetection == null ? 0 : activeDetection.RendererPid;
                    gamePlacement.Remove(pid);
                    gamePlacementStrict.Remove(pid);
                    gameBoostNextAudit.Remove(pid);
                }
            }
        }

        private void NotifyIrqObservationChanged(bool force)
        {
            string text = irqProbe.StatusText;
            bool warning = irqProbe.StatusWarning;
            if (!force && text == irqNotifiedStatus && warning == irqNotifiedWarning) return;
            irqNotifiedStatus = text;
            irqNotifiedWarning = warning;
            var changed = IrqObservationUpdated;
            if (changed != null) { try { changed(); } catch { } }
        }

        internal static bool NeedsSystemIrqObservation(
            bool boostEnabled, bool writeDenied, bool multiGroup,
            ulong desiredMask, ulong availableMask)
        {
            return !boostEnabled || writeDenied || multiGroup
                || !IrqSessionProbe.CanConfirmMaskShape(desiredMask, availableMask);
        }

        private bool NeedsSystemIrqObservation()
        {
            bool strict;
            ulong desired = EffectiveGameMask(sessionPolicy, out strict);
            string rendererName;
            lock (sync) rendererName = activeDetection == null ? null : activeDetection.RendererName;
            return NeedsSystemIrqObservation(EffBoost,
                ProtectedGameRoster.Contains(rendererName), CpuTopology.MultiGroup, desired, allMask);
        }

        // 本局要不要观测 开局判一次 局内所有重挂点共用这个决定 不许中途改判
        //   宽限恢复和封存重开走的也是 ArmIrqObservation 跳过局重挂时同样跳过
        private bool irqObserveThisSession = true;
        private bool irqBudgetDecided;

        private void ArmIrqObservation(string game)
        {
            DiscardPresentProbe();
            if (!irqObserveThisSession)
            {
                NotifyIrqObservationChanged(false);
                return;
            }
            irqProbe.Arm(game, allMask, NeedsSystemIrqObservation());
            NotifyIrqObservationChanged(false);
        }

        private void ObserveSystemIrq(int rendererPid, long rendererCreation)
        {
            if (!IrqSessionProbe.EnabledSetting)
            {
                if (irqProbe.IsCapturing) irqProbe.InvalidateGameMask();
                return;
            }
            // 本局按观测预算跳过 逐 tick 的回退重挂也不许把它捡回来
            if (!irqObserveThisSession) return;
            bool fallback = false;
            if (!irqProbe.IsSystemObservation)
            {
                if (NeedsSystemIrqObservation())
                {
                    ArmIrqObservation(repGame ?? activeGame);
                    fallback = true;
                }
                else fallback = irqProbe.TryFallbackToSystemObservation(
                    repGame ?? activeGame, allMask);
            }
            if (fallback)
            {
                DiscardPresentProbe();
                // 退出专为严格 IRQ 证明设置的临时硬绑核 普通游戏调优保持原样
                // 还原失败的句柄仍由已有恢复路径跟进 不阻止只读系统观测
                RestoreAllIrqProofHardPins(false);
            }
            if (!irqProbe.CanObserveSystemNow) return;
            // 系统观测不申请 SET 权限 更不为出现测量值而改动游戏亲和性
            // pid+creation 读回失败时不拿过期身份继续采样
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION,
                false, rendererPid);
            if (handle == IntPtr.Zero)
            {
                if (irqProbe.IsCapturing)
                {
                    if (Native.LastOpenProcessFailureWasNoSuchProcess()) SealIrqObservation();
                    else irqProbe.InvalidateGameMask();
                }
                return;
            }
            try
            {
                long creation, cpu;
                ulong disk;
                if (rendererPid > 0 && rendererCreation > 0
                    && Native.QueryProcessSample(handle, out creation, out cpu, out disk)
                    && creation == rendererCreation)
                    irqProbe.ConfirmSystemObservation(rendererPid, rendererCreation);
                else if (irqProbe.IsCapturing) irqProbe.InvalidateGameMask();
            }
            finally { Native.CloseHandle(handle); }
        }

        private void DiscardPresentProbe()
        {
            PresentProbe old = presentProbe;
            presentProbe = null;
            presentProbeAttempted = false;
            presentProbeEpoch = -1;
            if (old != null) { try { old.Stop(); } catch { } }
        }

        private void UpdateIrqPresentProbe()
        {
            // 系统观测不出挪核建议 无需额外抓 PRESENT 严格观测也必须先
            // 有 DPC 窗口 换 epoch 时丢弃旧 present 不能跨窗口对齐
            if (irqProbe.HasSealedPending) return;
            if (!irqProbe.IsPlacementCapturing || !IrqSessionProbe.EnabledSetting)
            {
                DiscardPresentProbe();
                return;
            }
            long epoch = irqProbe.CaptureEpoch;
            if (presentProbeEpoch != epoch)
            {
                DiscardPresentProbe();
                presentProbeEpoch = epoch;
            }
            if (presentProbeAttempted) return;
            presentProbeAttempted = true;
            StartPresentProbe();
        }

        private void SealIrqObservation()
        {
            // 收口顺序与起采相反 先停 present 再停 DPC 保留 present
            // 实例供宽限结束后的 CollectLongFrames 取数据 不继续录桌面
            PresentProbe p = presentProbe;
            if (p != null) { try { p.RequestStop(); } catch { } }
            irqProbe.Seal();
        }
    }
}
