// @author bdth 2074055628@qq.com
// 文件用途 游戏进程的中断归属证明 线程放置校验与硬钉还原
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool AuditActiveIrqCapture(ProcessSnapshot all, BoostPass pass)
        {
            if (all == null || pass == null || pass.RendererPid <= 0)
            {
                irqProbe.InvalidateGameMask();
                return true;
            }
            ProcEntry renderer = all.Find(pass.RendererPid);
            if (renderer == null)
            {
                SealIrqObservation();
                return true;
            }

            IntPtr h = Native.OpenProcess(
                Native.PROCESS_SET_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION,
                false, pass.RendererPid);
            if (h == IntPtr.Zero)
            {
                if (Native.LastOpenProcessFailureWasNoSuchProcess())
                    SealIrqObservation();
                else
                    irqProbe.InvalidateGameMask();
                return true;
            }
            try
            {
                long creation;
                if (!VerifyRendererIdentity(
                        h, pass.RendererPid, pass, out creation))
                {
                    irqProbe.InvalidateGameMask();
                    return true;
                }
                if (!AttributionPlacementMatches(h, pass))
                {
                    irqProbe.RestartCurrentEpoch();
                    // 线程级归因 proof 比普通落核读回更严格 线程瞬时增删
                    // 查询被拒或显式 thread CPU Sets 都只应停掉 IRQ epoch
                    // 普通 placement 仍稳定时不能清缓存并每 500ms 重写设置
                    if (!PlacementMatches(h, pass))
                    {
                        lock (sync)
                        {
                            gamePlacement.Remove(pass.RendererPid);
                            gamePlacementStrict.Remove(pass.RendererPid);
                            gameBoostNextAudit.Remove(pass.RendererPid);
                        }
                    }
                    return false;
                }
                bool known, needTweak, needPlacement;
                ComputeAuditDue(pass.RendererPid, pass,
                    out known, out needTweak, out needPlacement);
                bool wouldMutate = AuditWouldMutateRenderer(
                        h, pass.RendererPid, creation,
                        pass, known, needTweak);
                if (wouldMutate)
                {
                    irqProbe.RestartCurrentEpoch();
                    lock (sync)
                        gameBoostNextAudit.Remove(pass.RendererPid);
                    return false;
                }
                ConfirmIrqCapture(
                    h, pass, pass.RendererPid, creation);
                return true;
            }
            catch { irqProbe.InvalidateGameMask(); return true; }
            finally { Native.CloseHandle(h); }
        }

        // 这个读回只验证 Pavise 的软/硬落核是否生效 不作为中断归因证据
        // 线程显式 CPU Sets 可以覆盖进程默认 CPU Sets 因此后者不能证明每个
        // 渲染线程都在 desiredMask 内
        private bool PlacementMatches(IntPtr h, BoostPass pass)
        {
            bool unreadable;
            return PlacementMatches(h, pass, out unreadable);
        }

        // unreadable 表示这一轮读不出落点 不是读到了不对的值
        //   反作弊在对局中回收或降权句柄很常见 一次读失败不能等同于落点没生效
        private bool PlacementMatches(IntPtr h, BoostPass pass, out bool unreadable)
        {
            unreadable = false;
            if (h == IntPtr.Zero || pass == null) return false;
            // 不限核时没有落点要确认 本来就无事可做 不能判成没生效
            //   判成没生效会让巡检每轮清掉落核缓存 重写一次 CPU Sets 并重复记一条日志
            //   CanConfirmMask 拒绝全核是对的 那是 IRQ 归因的证据门槛 与落核是否生效两回事
            if (pass.DesiredMask == allMask) return true;
            if (!IrqSessionProbe.CanConfirmMask(
                    pass.DesiredMask, allMask,
                    pass.RendererPid, pass.RendererCreation))
                return false;
            uint[] ids = CpuTopology.CustomCpuSetIds()
                ?? CpuTopology.AdaptiveGameCpuSetIds(pass.UseStrict);
            bool cpuSetsMatch = ids != null && ids.Length > 0
                && Native.CpuSetsMatch(h, ids);
            bool cpuSetsUnconstrained = Native.CpuSetsMatch(h, new uint[0]);
            ulong affinity = 0;
            if (!CpuTopology.MultiGroup && !Native.TryQueryAffinity(h, out affinity))
            {
                affinity = 0;
                unreadable = true;
            }
            if (pass.ManualPlacement && pass.DesiredMask != allMask)
                return !CpuTopology.MultiGroup && !unreadable && affinity == pass.DesiredMask;
            return IrqPlacementProof.PlacementProofMatches(
                pass.DesiredMask, affinity,
                CpuTopology.MultiGroup, cpuSetsMatch,
                cpuSetsUnconstrained);
        }

        // 中断归因必须证明 renderer 的每一个当前线程实际可运行集合
        // 进程默认 CPU Sets 会被线程显式 CPU Sets 覆盖 只有进程硬亲和性
        // 不能证明 desired 里的每一颗核确实仍属于 renderer 多组机器的 ulong
        // 无法完整表示全部组 所以宁可不采样 也不做不完整的证明
        private bool AttributionPlacementMatches(IntPtr h, BoostPass pass)
        {
            if (h == IntPtr.Zero || pass == null
                || !IrqSessionProbe.CanConfirmMask(
                    pass.DesiredMask, allMask,
                    pass.RendererPid, pass.RendererCreation))
                return false;
            return IrqPlacementProof.AttributionThreadPlacementMatches(
                h, pass.RendererPid, pass.RendererCreation,
                pass.DesiredMask, CpuTopology.MultiGroup);
        }

        private bool ConfirmIrqCapture(
            IntPtr h, BoostPass pass, int pid, long creation)
        {
            bool accepted = irqProbe.ConfirmGameMask(
                pass.DesiredMask, pid, creation);
            if (!accepted) RestoreIrqProofHardPin(h, pid);
            return accepted;
        }

        private enum IrqProofHandleState
        {
            Match = 0,
            Gone = 1,
            Mismatch = 2,
            Unknown = 3
        }

        private const uint DuplicateSameAccess = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            IntPtr sourceProcess, IntPtr sourceHandle,
            IntPtr targetProcess, out IntPtr targetHandle,
            uint desiredAccess, bool inheritHandle, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessId(IntPtr process);

        private static IntPtr DuplicateIrqProofRestoreHandle(IntPtr source)
        {
            if (source == IntPtr.Zero) return IntPtr.Zero;
            IntPtr duplicate;
            IntPtr self = Native.GetCurrentProcess();
            return DuplicateHandle(self, source, self, out duplicate,
                0, false, DuplicateSameAccess)
                ? duplicate : IntPtr.Zero;
        }

        private static IrqProofHandleState IrqProofHandleStateOf(
            IrqProofHardPin pin)
        {
            if (pin == null || pin.RestoreHandle == IntPtr.Zero)
                return IrqProofHandleState.Unknown;
            uint actualPid = GetProcessId(pin.RestoreHandle);
            if (actualPid != 0 && actualPid != (uint)pin.Pid)
                return IrqProofHandleState.Mismatch;
            if (!Native.StillActive(pin.RestoreHandle))
                return IrqProofHandleState.Gone;
            long creation, cpu;
            ulong disk;
            if (!Native.QueryProcessSample(
                    pin.RestoreHandle, out creation, out cpu, out disk))
                return IrqProofHandleState.Unknown;
            if (actualPid == 0 || creation != pin.Creation)
                return IrqProofHandleState.Mismatch;
            return IrqProofHandleState.Match;
        }

        private void RememberIrqProofHardPin(
            int pid, long creation, ulong originalAffinity,
            IntPtr restoreHandle, bool manual = false)
        {
            if (pid <= 0 || creation <= 0 || originalAffinity == 0
                || restoreHandle == IntPtr.Zero)
            {
                if (restoreHandle != IntPtr.Zero)
                    Native.CloseHandle(restoreHandle);
                return;
            }
            lock (sync)
            {
                IrqProofHardPin old;
                if (irqProofHardPins.TryGetValue(pid, out old))
                {
                    if (old.Creation == creation
                        && old.RestoreHandle != IntPtr.Zero)
                    {
                        Native.CloseHandle(restoreHandle);
                        return;
                    }
                    irqProofHardPins.Remove(pid);
                    if (old.RestoreHandle != IntPtr.Zero)
                        Native.CloseHandle(old.RestoreHandle);
                }
                irqProofHardPins[pid] = new IrqProofHardPin
                {
                    Pid = pid,
                    Manual = manual,
                    Creation = creation,
                    OriginalAffinity = originalAffinity,
                    RestoreHandle = restoreHandle
                };
            }
        }

        // 仅用于进程已确认退出/PID 换代 或其它恢复路径已经精确读回原值后
        // 活进程的普通失败路径必须保留 handle 继续重试 不能只删 marker
        private void ForgetIrqProofHardPin(int pid)
        {
            lock (sync)
            {
                IrqProofHardPin pin;
                if (!irqProofHardPins.TryGetValue(pid, out pin)) return;
                irqProofHardPins.Remove(pid);
                if (pin.RestoreHandle != IntPtr.Zero)
                    Native.CloseHandle(pin.RestoreHandle);
            }
        }

        private bool RestoreIrqProofHardPin(IntPtr ignored, int pid, bool includeManual = false)
        {
            lock (sync)
            {
                IrqProofHardPin pin;
                if (!irqProofHardPins.TryGetValue(pid, out pin)) return true;
                // 中断观测结束只释放观测自己的硬钉；手动方案持续到真实离场/恢复。
                if (pin.Manual && !includeManual) return true;
                IrqProofHandleState state = IrqProofHandleStateOf(pin);
                if (state == IrqProofHandleState.Gone
                    || state == IrqProofHandleState.Mismatch)
                {
                    irqProofHardPins.Remove(pid);
                    // 进程已退出或绑定身份不再一致时 同 PID 下的旧 proof
                    // 缓存也必须失效 否则 PID 复用后可能误认旧 desired 已生效
                    gamePlacement.Remove(pid);
                    gamePlacementStrict.Remove(pid);
                    if (pin.RestoreHandle != IntPtr.Zero)
                        Native.CloseHandle(pin.RestoreHandle);
                    return true;
                }
                if (state != IrqProofHandleState.Match) return false;
                if (!Native.SetProcessAffinityMask(
                        pin.RestoreHandle, (UIntPtr)pin.OriginalAffinity)
                    || Native.QueryAffinity(pin.RestoreHandle)
                        != pin.OriginalAffinity)
                    return false;
                irqProofHardPins.Remove(pid);
                // 与句柄移除保持在同一个锁域 Stop 超时后仍可能有 worker
                // 正在收尾 不能让它刚写入的新 placement 缓存被旧恢复动作误删
                gamePlacement.Remove(pid);
                gamePlacementStrict.Remove(pid);
                Native.CloseHandle(pin.RestoreHandle);
            }
            // hard affinity 已撤回原值后 普通 placement 缓存也已同步清除
            // 不能继续假称 desired 仍成立 proof-gap 会在下一轮重施加
            // 已 disarm 则只保留实际仍在进程上的软 CPU Sets
            return true;
        }

        private void RestoreOrphanedIrqProofHardPin(BoostPass pass)
        {
            RestoreAllIrqProofHardPins(false);
        }

        private bool RestoreAllIrqProofHardPins(bool includeManual = true)
        {
            List<int> pids;
            lock (sync)
                pids = new List<int>(irqProofHardPins.Keys);
            bool ok = true;
            foreach (int pid in pids)
                if (!RestoreIrqProofHardPin(IntPtr.Zero, pid, includeManual)) ok = false;
            return ok;
        }

#if PAVISE_SELFTEST
        internal static IntPtr DuplicateIrqProofRestoreHandleForTest(IntPtr source)
        {
            return DuplicateIrqProofRestoreHandle(source);
        }

        internal static bool IrqProofRestoreHandleForTest(
            IntPtr handle, int pid, long creation, ulong affinity)
        {
            var pin = new IrqProofHardPin
            {
                Pid = pid,
                Creation = creation,
                OriginalAffinity = affinity,
                RestoreHandle = handle
            };
            return IrqProofHandleStateOf(pin) == IrqProofHandleState.Match
                && Native.SetProcessAffinityMask(handle, (UIntPtr)affinity)
                && Native.QueryAffinity(handle) == affinity;
        }
#endif
    }
}
