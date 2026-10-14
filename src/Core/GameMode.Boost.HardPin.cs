// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool PlacementMatches(IntPtr h, BoostPass pass, out bool unreadable, out ulong observed)
        {
            unreadable = false;
            observed = 0;
            if (h == IntPtr.Zero || pass == null) return false;
            if (!pass.Exclusive && pass.DesiredMask == allMask) return true;
            if (pass.DesiredMask == 0 || (pass.DesiredMask & ~allMask) != 0 || CpuTopology.MultiGroup
                || pass.RendererPid <= 0 || pass.RendererCreation <= 0)
                return false;
            if (!Native.TryQueryAffinity(h, out observed))
            {
                observed = 0;
                unreadable = true;
                return false;
            }
            return observed == pass.DesiredMask;
        }

        private enum HardPinHandleState
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

        private static IntPtr DuplicateRestoreHandle(IntPtr source)
        {
            if (source == IntPtr.Zero) return IntPtr.Zero;
            IntPtr duplicate;
            IntPtr self = Native.GetCurrentProcess();
            return DuplicateHandle(self, source, self, out duplicate,
                0, false, DuplicateSameAccess)
                ? duplicate : IntPtr.Zero;
        }

        private static HardPinHandleState HardPinHandleStateOf(HardPin pin)
        {
            if (pin == null || pin.RestoreHandle == IntPtr.Zero)
                return HardPinHandleState.Unknown;
            uint actualPid = GetProcessId(pin.RestoreHandle);
            if (actualPid != 0 && actualPid != (uint)pin.Pid)
                return HardPinHandleState.Mismatch;
            if (!Native.StillActive(pin.RestoreHandle))
                return HardPinHandleState.Gone;
            long creation, cpu;
            ulong disk;
            if (!Native.QueryProcessSample(
                    pin.RestoreHandle, out creation, out cpu, out disk))
                return HardPinHandleState.Unknown;
            if (actualPid == 0 || creation != pin.Creation)
                return HardPinHandleState.Mismatch;
            return HardPinHandleState.Match;
        }

        private void RememberHardPin(
            int pid, long creation, ulong originalAffinity, ulong appliedAffinity, IntPtr restoreHandle)
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
                HardPin old;
                if (hardPins.TryGetValue(pid, out old))
                {
                    if (old.Creation == creation
                        && old.RestoreHandle != IntPtr.Zero)
                    {
                        Native.CloseHandle(restoreHandle);
                        return;
                    }
                    hardPins.Remove(pid);
                    if (old.RestoreHandle != IntPtr.Zero)
                        Native.CloseHandle(old.RestoreHandle);
                }
                hardPins[pid] = new HardPin
                {
                    Pid = pid,
                    Creation = creation,
                    OriginalAffinity = originalAffinity,
                    AppliedAffinity = appliedAffinity,
                    RestoreHandle = restoreHandle
                };
            }
        }

        private void ForgetHardPin(int pid)
        {
            lock (sync)
            {
                HardPin pin;
                if (!hardPins.TryGetValue(pid, out pin)) return;
                hardPins.Remove(pid);
                if (pin.RestoreHandle != IntPtr.Zero)
                    Native.CloseHandle(pin.RestoreHandle);
            }
        }

        private bool RestoreHardPin(int pid)
        {
            lock (sync)
            {
                HardPin pin;
                if (!hardPins.TryGetValue(pid, out pin)) return true;
                HardPinHandleState state = HardPinHandleStateOf(pin);
                if (state == HardPinHandleState.Gone
                    || state == HardPinHandleState.Mismatch)
                {
                    hardPins.Remove(pid);
                    gamePlacement.Remove(pid);
                    if (pin.RestoreHandle != IntPtr.Zero)
                        Native.CloseHandle(pin.RestoreHandle);
                    return true;
                }
                if (state != HardPinHandleState.Match) return false;
                if (!CrashGuard.RestoreOwnedAffinity(pin.RestoreHandle, pin.OriginalAffinity, pin.AppliedAffinity)) return false;
                // 恢复系统成功后仍需清除持久收据，才释放所有权。
                if (!CrashGuard.TrackBoostAffinity(pid, pin.Creation, 0)) return false;
                hardPins.Remove(pid);
                gamePlacement.Remove(pid);
                Native.CloseHandle(pin.RestoreHandle);
            }
            return true;
        }

        private bool RestoreAllHardPins()
        { return RestoreHardPinsExcept(0, 0); }

        private bool RestoreHardPinsExcept(int keepPid, long keepCreation)
        {
            var pids = new List<int>();
            lock (sync)
                foreach (var entry in hardPins)
                    if (keepPid <= 0 || entry.Key != keepPid || entry.Value.Creation != keepCreation)
                        pids.Add(entry.Key);
            bool ok = true;
            foreach (int pid in pids)
                if (!RestoreHardPin(pid)) ok = false;
            return ok;
        }

#if PAVISE_SELFTEST
        internal static IntPtr DuplicateRestoreHandleForTest(IntPtr source)
        {
            return DuplicateRestoreHandle(source);
        }

        internal static bool HardPinRestoreHandleForTest(
            IntPtr handle, int pid, long creation, ulong affinity)
        {
            var pin = new HardPin
            {
                Pid = pid,
                Creation = creation,
                OriginalAffinity = affinity,
                RestoreHandle = handle
            };
            return HardPinHandleStateOf(pin) == HardPinHandleState.Match
                && Native.SetProcessAffinityMask(handle, (UIntPtr)affinity)
                && Native.QueryAffinity(handle) == affinity;
        }
#endif
    }
}
