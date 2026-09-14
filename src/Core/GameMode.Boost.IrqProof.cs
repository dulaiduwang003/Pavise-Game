// @author bdth 2074055628@qq.com
// File purpose Interrupt attribution proof for game processes, thread placement verification and hard-pin restore
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
                if (!AttributionPlacementHolds(h, pass, renderer))
                {
                    irqProbe.RestartCurrentEpoch();
                    // Thread-level attribution proof is stricter than the ordinary placement read-back, threads come and go instantly
                    // A denied query or explicit thread CPU Sets should only stop the IRQ epoch
                    // While ordinary placement is still stable we must not clear the cache and rewrite settings every 500ms
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

        // This read-back only verifies whether Pavise's soft/hard placement took effect, not interrupt attribution evidence
        // Explicit thread CPU Sets can override process default CPU Sets, so the latter can't prove every
        // render thread sits within desiredMask
        private bool PlacementMatches(IntPtr h, BoostPass pass)
        {
            bool unreadable;
            return PlacementMatches(h, pass, out unreadable);
        }

        // unreadable means placement couldn't be read this pass, not that a wrong value was read
        //   Anti-cheat commonly revokes or downgrades handles mid-match, one failed read isn't the same as placement not taking effect
        private bool PlacementMatches(IntPtr h, BoostPass pass, out bool unreadable)
        {
            ulong observed;
            return PlacementMatches(h, pass, out unreadable, out observed);
        }

        // observed is the hard affinity read back from the process handle this pass, 0 when unreadable or on multi-group machines
        //   When manual placement is changed back externally it decides whether the game can still reach the exclusive cores
        private bool PlacementMatches(IntPtr h, BoostPass pass, out bool unreadable, out ulong observed)
        {
            unreadable = false;
            observed = 0;
            if (h == IntPtr.Zero || pass == null) return false;
            // With no core restriction there's no placement to confirm, nothing to do anyway, must not be judged as not applied
            //   Judging it not applied would make every audit pass clear the placement cache, rewrite CPU Sets once and log a duplicate line
            //   CanConfirmMask rejecting all cores is correct, that's the evidence threshold for IRQ attribution, separate from whether placement took effect
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
            observed = affinity;
            if (pass.ManualPlacement && pass.DesiredMask != allMask)
                return !CpuTopology.MultiGroup && !unreadable && affinity == pass.DesiredMask;
            return IrqPlacementProof.PlacementProofMatches(
                pass.DesiredMask, affinity,
                CpuTopology.MultiGroup, cpuSetsMatch,
                cpuSetsUnconstrained);
        }

        // Interrupt attribution must prove the actual runnable set of every current renderer thread
        // Process default CPU Sets get overridden by explicit thread CPU Sets, and process hard affinity alone
        // can't prove every core in desired still belongs to the renderer, a ulong on multi-group machines
        // can't represent all groups completely, so rather skip sampling than produce an incomplete proof
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

        // A full thread proof every pass during capture is too expensive, a game with 300+ threads would open thousands of handles every 500ms
        //   Changed to only checking process-level placement per pass (three syscalls), the full proof reruns only when the thread count changed or 3s passed since the last one
        //   Thread count changes are also spaced at least 1s apart, thread pool jitter must not push the full proof back to every pass
        private const long IrqFullProofIntervalTicks = 3 * TimeSpan.TicksPerSecond;
        private const long IrqFullProofFloorTicks = TimeSpan.TicksPerSecond;
        private long irqFullProofTicks, irqFullProofCreation;
        private int irqFullProofPid, irqFullProofThreads;

        private bool AttributionPlacementHolds(IntPtr h, BoostPass pass, ProcEntry renderer)
        {
            long now = DateTime.UtcNow.Ticks;
            long age = now - irqFullProofTicks;
            bool sameTarget = irqFullProofTicks > 0
                && irqFullProofPid == pass.RendererPid && irqFullProofCreation == pass.RendererCreation;
            int threads = renderer != null ? renderer.Threads : 0;
            bool full = !sameTarget || age >= IrqFullProofIntervalTicks
                || (threads != irqFullProofThreads && age >= IrqFullProofFloorTicks);
            if (!full)
                return IrqPlacementProof.AttributionProcessPlacementMatches(
                    h, pass.RendererPid, pass.RendererCreation, pass.DesiredMask, CpuTopology.MultiGroup);
            if (!AttributionPlacementMatches(h, pass))
            {
                irqFullProofTicks = 0;
                return false;
            }
            irqFullProofTicks = now;
            irqFullProofPid = pass.RendererPid;
            irqFullProofCreation = pass.RendererCreation;
            irqFullProofThreads = threads;
            return true;
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

        // Only for a process confirmed exited/PID generation change, or after another restore path has already read back the original value exactly
        // The ordinary failure path of a live process must keep the handle and retry, never just delete the marker
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
                // Ending interrupt observation only releases the observation's own hard pin, the manual plan lasts until real exit or restore
                if (pin.Manual && !includeManual) return true;
                IrqProofHandleState state = IrqProofHandleStateOf(pin);
                if (state == IrqProofHandleState.Gone
                    || state == IrqProofHandleState.Mismatch)
                {
                    irqProofHardPins.Remove(pid);
                    // When the process has exited or the bound identity no longer matches, the old proof under the same PID
                    // cache must be invalidated too, or after PID reuse the old desired could be mistaken as applied
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
                // Keep this in the same lock scope as the handle removal, after Stop times out a worker may still
                // be winding down, its freshly written placement cache must not be deleted by an old restore action
                gamePlacement.Remove(pid);
                gamePlacementStrict.Remove(pid);
                Native.CloseHandle(pin.RestoreHandle);
            }
            // Once hard affinity has been reverted to the original, the ordinary placement cache is also cleared in sync
            // Can't keep pretending desired still holds, the proof gap gets reapplied next pass
            // Once disarmed only the soft CPU Sets actually still on the process are kept
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
