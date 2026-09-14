// @author bdth 2074055628@qq.com
// File purpose Boost target probing, evicting vanished processes and boost state read-back verification
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private enum BoostTarget { Alive = 0, Gone = 1, Unopenable = 2 }

        private static BoostTarget ProbeBoostTarget(int pid, long creation)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 87
                    ? BoostTarget.Gone : BoostTarget.Unopenable;
            try
            {
                long cur, cpu;
                ulong io;
                if (!Native.QueryProcessSample(h, out cur, out cpu, out io)) return BoostTarget.Unopenable;
                if (creation > 0 && cur != creation) return BoostTarget.Gone;
                return Native.StillActive(h) ? BoostTarget.Alive : BoostTarget.Gone;
            }
            finally { Native.CloseHandle(h); }
        }

        private const int VanishGiveUpTries = 20;
        private readonly Dictionary<int, int> boostVanishTries = new Dictionary<int, int>();

        internal int DropVanishedBoosts()
        {
            var pending = new List<KeyValuePair<int, Snap>>();
            lock (sync)
                foreach (KeyValuePair<int, Snap> kv in gameBoost) pending.Add(kv);
            if (pending.Count == 0) return 0;

            int dropped = 0, abandoned = 0;
            foreach (KeyValuePair<int, Snap> kv in pending)
            {
                BoostTarget state = ProbeBoostTarget(kv.Key, kv.Value.Creation);
                if (state == BoostTarget.Alive)
                {
                    lock (sync) boostVanishTries.Remove(kv.Key);
                    continue;
                }
                bool unopenable = state == BoostTarget.Unopenable;
                if (unopenable)
                {
                    int tries;
                    lock (sync)
                    {
                        boostVanishTries.TryGetValue(kv.Key, out tries);
                        tries++;
                        boostVanishTries[kv.Key] = tries;
                    }
                    if (tries < VanishGiveUpTries) continue;
                    abandoned++;
                }
                // Even if a new OpenProcess is already denied by anti-cheat, the handle retained before the first
                // hard pin must still restore and read back first, a live process whose restore failed must not lose its only restore handle
                if (!RestoreIrqProofHardPin(IntPtr.Zero, kv.Key, true)) continue;
                if (!unopenable) CrashGuard.ReleaseBoostProcess(kv.Key, kv.Value.Creation);
                lock (sync)
                {
                    boostVanishTries.Remove(kv.Key);
                    gameBoost.Remove(kv.Key); gameGpu.Remove(kv.Key);
                    gamePlacement.Remove(kv.Key); gamePlacementStrict.Remove(kv.Key);
                    boostFail.Remove(kv.Key); boostStateWarned.Remove(kv.Key);
                    boostStateVerified.Remove(kv.Key); boostHandleStripped.Remove(kv.Key);
                    boostEcoGaveUp.Remove(kv.Key); placementGaveUp.Remove(kv.Key);
                    placementFail.Remove(kv.Key); boostStateFail.Remove(kv.Key);
                    gameBoostNextAudit.Remove(kv.Key); boostDenied.Remove(kv.Key);
                    tweakApplied.Remove(kv.Key);
                }
                dropped++;
            }
            if (abandoned > 0) Logger.Warn(Lang.T("log.gamemodeboost.58") + dropped);
            else if (dropped > 0) Logger.Log(Lang.T("log.gamemodeboost.57") + dropped);
            return dropped;
        }

        private void PruneDeadBoosts(HashSet<int> live)
        {
            lock (sync)
            {
                boostDenied.RemoveWhere(x => !live.Contains(x));
                boostStateWarned.RemoveWhere(x => !live.Contains(x));
                boostStateVerified.RemoveWhere(x => !live.Contains(x));
                boostHandleStripped.RemoveWhere(x => !live.Contains(x));
                boostEcoGaveUp.RemoveWhere(x => !live.Contains(x));
                placementGaveUp.RemoveWhere(x => !live.Contains(x));
                tweakApplied.RemoveWhere(x => !live.Contains(x));
                List<int> dead = null;
                foreach (int k in gameBoost.Keys)
                    if (!live.Contains(k)) { if (dead == null) dead = new List<int>(); dead.Add(k); }
                if (dead != null)
                    foreach (int k in dead)
                    {
                        if (!RestoreIrqProofHardPin(IntPtr.Zero, k, true)) continue;
                        Snap old = gameBoost[k];
                        CrashGuard.ReleaseBoostProcess(k, old.Creation);
                        gameBoost.Remove(k); gameGpu.Remove(k); gamePlacement.Remove(k); gamePlacementStrict.Remove(k);
                        boostFail.Remove(k); boostStateWarned.Remove(k); boostStateVerified.Remove(k);
                        gameBoostNextAudit.Remove(k); placementFail.Remove(k); boostStateFail.Remove(k);
                    }
            }
        }

        internal static bool RendererIdentityMatches(
            int expectedPid, long expectedCreation,
            string expectedName, int actualPid,
            long actualCreation, string actualName)
        {
            return expectedPid > 0
                && expectedPid == actualPid
                && expectedCreation > 0
                && expectedCreation == actualCreation
                && !string.IsNullOrEmpty(expectedName)
                && !string.IsNullOrEmpty(actualName)
                && string.Equals(
                    expectedName, actualName,
                    StringComparison.OrdinalIgnoreCase);
        }

        private void OnGameHandleStripped(int pid, string rendererName, uint granted)
        {
            string ac = KernelAntiCheat.DescribeForLog(rendererName);
            Logger.Log(Lang.T("log.gamemodeboost.3") + rendererName + " pid " + pid + Lang.T("log.gamemodeboost.34")
                + (ac == null ? Lang.T("nav.tame") : ac) + Lang.T("log.gamemodeboost.35") + granted.ToString("X")
                + Lang.T("log.gamemodeboost.36"));

            ProtectedGameRoster.Remember(rendererName);
        }

        internal static bool ApplyAndVerifyBoostState(IntPtr process, out uint actualPriority, out int actualIo, out int error)
        {
            return ApplyAndVerifyBoostState(process, Native.HIGH_PRIORITY_CLASS, out actualPriority, out actualIo, out error);
        }

        internal static bool ApplyAndVerifyBoostState(IntPtr process, uint priorityTarget, out uint actualPriority, out int actualIo, out int error)
        {
            error = 0;
            actualIo = Native.QueryIoPriority(process);
            if (actualIo != 3)
            {
            if (!Native.EnsureBoostPrivilege()) error = 1314;
                else
                {
                    int status;
                    if (!Native.TrySetIoPriority(process, 3, out status)) error = status;
                }
            }

            actualPriority = Native.GetPriorityClass(process);
            if (actualPriority != priorityTarget && !Native.SetPriorityClass(process, priorityTarget))
                error = Marshal.GetLastWin32Error();

            actualPriority = Native.GetPriorityClass(process);
            actualIo = Native.QueryIoPriority(process);
            return actualPriority == priorityTarget && actualIo == 3;
        }

        private static string EcoStateText(IntPtr process)
        {
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state))
                return Lang.T("t.gamemodeboost.33") + QoSDump(process);
            return (Native.EcoClearedMasksOk(control, state)
                ? Lang.T("t.gamemodeboost.38") : Lang.T("t.gamemodeboost.33"))
                + QoSDump(process);
        }

        internal static string QoSDump(IntPtr process)
        {
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return Lang.T("t.gamemodeboost.37");
            return "(control=0x" + control.ToString("X") + " state=0x" + state.ToString("X") + ")";
        }

        internal static bool HighQoSVerified(IntPtr process)
        {
            if (!Native.PowerThrottlingSupported) return true;
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return false;
            return Native.HighQoSMasksOk(control, state);
        }

        private static bool ApplyAndVerifyGpuBoost(IntPtr process)
        {
            int current;
            if (Native.D3DKMTGetProcessSchedulingPriorityClass(process, out current) != 0) return false;
            if (current != Native.GpuPriorityHigh
                && Native.D3DKMTSetProcessSchedulingPriorityClass(process, Native.GpuPriorityHigh) != 0) return false;
            return Native.D3DKMTGetProcessSchedulingPriorityClass(process, out current) == 0
                && current == Native.GpuPriorityHigh;
        }
    }
}
