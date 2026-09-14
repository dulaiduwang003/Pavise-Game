// @author bdth 2074055628@qq.com
// File purpose VRAM residency, declares a minimum VRAM reservation for the game when VRAM is tight, revoked at match end
using System;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal enum ShieldStage { Idle = 0, Observing = 1, Engaged = 2, Skipped = 3, Fused = 4 }

    // VRAM residency, targets long frames when the VRAM budget is tight
    //   read D3DKMTQueryVideoMemoryInfo to confirm the game keeps running right at the budget, then use
    //   D3DKMTChangeVideoMemoryReservation to declare a conservative minimum physical VRAM requirement
    //   this neither adds VRAM nor locks VRAM to the game, Reservation is only a hint the VRAM manager uses
    //   to judge the process's minimum working set, goal is fewer long frames from textures being evicted and paged back
    //
    // Why the read-back verification is mandatory
    //   the normal use of ChangeVideoMemoryReservation is a process declaring for itself
    //   declaring on behalf of another process is supported by the API shape, but whether the VRAM manager honors it is undocumented
    //   so always read back after writing, only a non-zero CurrentReservation counts, a success return code does not
    //   if the read-back shows nothing this path is dead on this machine, trip the circuit breaker and stop wasting matches
    //
    // Edge cases all skip outright, no guessing
    //   handle refused by anti-cheat, rendering GPU cannot be uniquely identified, game already has a non-zero reservation of its own
    //   iGPU unified memory, budget unreadable, reservable quota is zero
    internal static class VramShield
    {
        internal const string EnabledKey = "GmVramShield";
        private const string SnapKey = "VramShieldSnap";
        private const string FuseKey = "VramShieldFuse";

        // Criteria all take conservative values, better to do nothing
        internal const double EngageUsageShare = 0.90;   // usage must reach 90% of budget to count as tight
        internal const int EngageSamples = 3;            // three consecutive tight samples before acting
        internal const double ReserveFactor = 0.80;      // declare only 80% of current usage, not all of it
        internal const int SampleIntervalSeconds = 10;
        internal const int AdapterResolveMs = 400;

        // query needs QUERY_INFORMATION, write needs SET_INFORMATION, QUERY_LIMITED is refused for both on this machine
        internal const int ShieldAccess =
            Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SET_INFORMATION;

        // Two locks with a clear division of labor, never invert them
        //   opLk serializes the actions: sampling, write and revoke are mutually exclusive because they share the adapter handle
        //     without it the revoke from the UI thread toggling the switch would race the sampling running on the worker thread
        //     revoke closes the handle while sampling still holds a copy of the same handle, that is use-after-close
        //   lk only guards field reads/writes, always a short critical section, no file or registry IO inside
        //     Stage and Summarize are read by the UI thread, doing IO under the lock would stall the UI
        private static readonly object opLk = new object();
        private static readonly object lk = new object();
        private static Action mutationBegin;
        private static Action mutationEnd;
        private static ShieldStage stage;
        private static long nextSampleTicks;
        private static int pressureRun;
        private static int shieldPid;
        private static long shieldCreation;
        private static uint shieldAdapter;
        private static uint shieldPhys;
        private static ulong shieldBytes;
        private static ulong lastBudget;
        private static bool recoveryBlocked;

        public static bool Fused { get { return Settings.Load(FuseKey, false); } }

        public static void ConfigureMutationBoundary(Action begin, Action end)
        {
            lock (lk)
            {
                mutationBegin = begin;
                mutationEnd = end;
            }
        }

        private static void BeginMutation()
        {
            Action callback;
            lock (lk) callback = mutationBegin;
            if (callback != null) try { callback(); } catch { }
        }

        private static void EndMutation()
        {
            Action callback;
            lock (lk) callback = mutationEnd;
            if (callback != null) try { callback(); } catch { }
        }

        // The trip is recorded both in the registry and in stage, clearing only the registry leaves stage stuck at Fused
        //   DoRelease deliberately leaves Fused alone so a match change does not wipe the machine-level verdict, so this is the only place to clear it
        //   without clearing, the log line saying toggle the switch off and on to retry is a lie, it would take a process restart to revive
        public static void ClearFuse()
        {
            if (Fused) Settings.Save(FuseKey, false);
            lock (lk) if (stage == ShieldStage.Fused) stage = ShieldStage.Idle;
        }

        public static ShieldStage Stage { get { lock (lk) return stage; } }

        public static string Summarize()
        {
            lock (lk)
            {
                if (stage != ShieldStage.Engaged || lastBudget == 0) return null;
                return VidMmProbe.Gb(shieldBytes) + " GB / "
                    + VidMmProbe.Gb(lastBudget) + " GB";
            }
        }

        // A match change goes through ReportFinish + ReportBegin without Deactivate, so this must clean up fully on its own
        //   resetting stage alone is not enough, the previous match's shieldPid and adapter handle would linger
        //   two consequences of lingering: the old reservation stays attached to the previous game process and cannot be revoked
        //   and the next match sees a non-zero shieldAdapter and reuses it, querying the wrong card on dual-GPU machines
        public static bool Begin()
        {
            lock (opLk)
            {
                if (!DoRelease(Lang.T("t.vramshield.4"))) return false;
                lock (lk) nextSampleTicks = 0;
                return true;
            }
        }

        public static void SampleIfDue(bool want, int rendererPid, long rendererCreation)
        {
            // When the last restore was refused or could not be verified, do not overwrite that single original value
            // an explicit release or a fresh begin may retry
            lock (lk) if (recoveryBlocked) return;
            if (!want || rendererPid <= 0) { ReleaseIfAny(Lang.T("t.vramshield.3")); return; }
            bool mismatch;
            lock (lk)
            {
                // shieldPid records which process the current adapter handle and observation count belong to, set as soon as the handle is resolved
                //   cannot wait until engage succeeds, a renderer process switch does not necessarily happen while engaged
                //   a renderer process switch inside the same game does not go through Begin, if missed here
                //   the old handle gets reused by the new process, on dual-GPU machines that means querying the wrong card
                mismatch = shieldPid != 0
                    && (rendererPid != shieldPid || rendererCreation != shieldCreation);
                if (stage == ShieldStage.Fused) return;
                // Skipped means this process is skipped for this match, a process change warrants re-evaluation
                if (stage == ShieldStage.Skipped && !mismatch) return;
                long now = DateTime.UtcNow.Ticks;
                if (now < nextSampleTicks) return;
                nextSampleTicks = now + SampleIntervalSeconds * TimeSpan.TicksPerSecond;
            }
            lock (opLk)
            {
                try
                {
                    lock (lk) if (recoveryBlocked) return;
                    // Renderer process changed, revoke the old one first then observe again
                    if (mismatch && !DoRelease(Lang.T("t.vramshield.1"))) return;
                    Step(rendererPid, rendererCreation);
                }
                catch { }
            }
        }

        private static int adapterResolveBusy;
        private static RenderAdapter resolvedAdapter;
        private static int resolvedForPid;
        private static long resolvedForCreation;

        // Take the ready result if identity matches, otherwise queue one background resolve and return null this round
        private static RenderAdapter TakeResolvedAdapter(int pid, long creation)
        {
            lock (lk)
            {
                RenderAdapter ready = resolvedAdapter;
                resolvedAdapter = null;
                if (ready != null && resolvedForPid == pid && resolvedForCreation == creation) return ready;
            }
            if (Interlocked.CompareExchange(ref adapterResolveBusy, 1, 0) != 0) return null;
            bool queued = false;
            try
            {
                queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        RenderAdapter ra = GpuEvidence.ResolveRenderAdapter(pid, AdapterResolveMs);
                        if (ra != null)
                            lock (lk)
                            {
                                resolvedAdapter = ra;
                                resolvedForPid = pid;
                                resolvedForCreation = creation;
                            }
                    }
                    catch { }
                    finally { Interlocked.Exchange(ref adapterResolveBusy, 0); }
                });
            }
            catch { }
            finally { if (!queued) Interlocked.Exchange(ref adapterResolveBusy, 0); }
            return null;
        }

        private static void Step(int pid, long creation)
        {
            lock (lk) if (recoveryBlocked) return;
#if PAVISE_SELFTEST
            if (SampleStepForTest != null) { SampleStepForTest(pid, creation); return; }
#endif
            if (Fused) { lock (lk) stage = ShieldStage.Fused; return; }
            if (GpuInventory.IntegratedOnly)
            {
                SkipOnce(Lang.T("log.vramshield.1"));
                return;
            }

            IntPtr h = Native.OpenProcess(ShieldAccess, false, pid);
            if (h == IntPtr.Zero)
            {
                SkipOnce(Lang.T("log.vramshield.2"));
                return;
            }
            try
            {
                uint adapter;
                uint phys;
                bool engaged;
                lock (lk) { adapter = shieldAdapter; phys = shieldPhys; engaged = stage == ShieldStage.Engaged; }

                if (adapter == 0)
                {
                    // Resolving sleeps 400ms inside PDH, the sweep main loop thread must not wait along, hand it to the thread pool and pick up the result next round
                    RenderAdapter ra = TakeResolvedAdapter(pid, creation);
                    if (ra == null)
                    {
                        // No 3D usage sampled this round or result not back yet, not an error, check again next round
                        return;
                    }
                    if (ra.Ambiguous)
                    {
                        SkipOnce(Lang.T("log.vramshield.3"));
                        return;
                    }
                    if (!VidMmProbe.TryOpenAdapter(ra.LuidHigh, ra.LuidLow, out adapter))
                    {
                        SkipOnce(Lang.T("log.vramshield.4"));
                        return;
                    }
                    phys = ra.PhysIndex;
                    lock (lk)
                    {
                        shieldAdapter = adapter; shieldPhys = phys;
                        shieldPid = pid; shieldCreation = creation;
                    }
                }

                VramStatus st = VidMmProbe.Query(h, adapter, phys);
                if (!st.Ok || st.Budget == 0)
                {
                    // After a driver reset (TDR) the adapter handle is invalid, this is not the machine refusing to honor it, never trip the breaker
                    //   when already engaged the state must be zeroed along with it, dropping the handle alone is not enough
                    //   otherwise after reopening the handle next round it is still Engaged while TDR has already cleared the reservation
                    //   and a momentary driver reset would be taken as the reservation being cleared externally and trip the breaker
                    if (engaged) DoRelease(Lang.T("t.vramshield.5"));
                    else
                    {
                        lock (lk) { shieldAdapter = 0; shieldPhys = 0; }
                        VidMmProbe.CloseAdapter(adapter);
                    }
                    return;
                }
                lock (lk) lastBudget = st.Budget;

                if (engaged)
                {
                    // Already engaged, only verify liveness, trip the breaker if the reservation was cleared externally, never rewrite repeatedly
                    if (st.CurrentReservation == 0)
                    {
                        Settings.Save(FuseKey, true);
                        DoRelease(Lang.T("t.vramshield.2"));
                        lock (lk) stage = ShieldStage.Fused;
                        Logger.Log(Lang.T("log.vramshield.5"));
                    }
                    return;
                }

                if (st.CurrentReservation > 0)
                {
                    SkipOnce(Lang.F("log.vramshield.6", VidMmProbe.Gb(st.CurrentReservation)));
                    return;
                }
                if (st.AvailableForReservation == 0)
                {
                    SkipOnce(Lang.T("log.vramshield.7"));
                    return;
                }

                if (st.UsageShare < EngageUsageShare)
                {
                    lock (lk) pressureRun = 0;
                    return;
                }
                int run;
                lock (lk) { pressureRun++; run = pressureRun; stage = ShieldStage.Observing; }
                if (run < EngageSamples) return;

                ulong target = (ulong)(st.CurrentUsage * ReserveFactor);
                if (target > st.AvailableForReservation) target = st.AvailableForReservation;
                if (target == 0)
                {
                    SkipOnce(Lang.T("log.vramshield.7"));
                    return;
                }

                VramStatus after;
                BeginMutation();
                try
                {
                    // Writing the reservation, like the process suppression ledger, needs one confirmed
                    // Restore record
                    string snapshot = pid.ToString(CultureInfo.InvariantCulture)
                        + ":" + creation.ToString(CultureInfo.InvariantCulture);
                    if (Settings.LoadStr(SnapKey, "").Length != 0)
                    {
                        lock (lk) recoveryBlocked = true;
                        return;
                    }
                    if (!Settings.SaveStr(SnapKey, snapshot) || Settings.LoadStr(SnapKey, "") != snapshot) return;
                    VidMmProbe.SetReservation(h, adapter, phys, target);

                    // return code does not count, only the read-back CurrentReservation counts
                    after = VidMmProbe.Query(h, adapter, phys);
                    if (!after.Ok || after.CurrentReservation == 0)
                    {
                        Settings.Save(FuseKey, true);
                        // still Observing at this point, but the write may already have taken effect
                        // DoRelease revokes based on the snapshot, keeps the record when the restore is unconfirmed
                        DoRelease(Lang.T("t.vramshield.2"));
                        lock (lk) stage = ShieldStage.Fused;
                        Logger.Log(Lang.T("log.vramshield.8"));
                        return;
                    }
                }
                finally { EndMutation(); }

                lock (lk)
                {
                    stage = ShieldStage.Engaged;
                    shieldPid = pid;
                    shieldCreation = creation;
                    shieldBytes = after.CurrentReservation;
                }
                Logger.Log(Lang.F("log.vramshield.9",
                    VidMmProbe.Gb(after.CurrentReservation),
                    VidMmProbe.Gb(st.CurrentUsage),
                    VidMmProbe.Gb(st.Budget),
                    ((int)(st.UsageShare * 100)).ToString(CultureInfo.InvariantCulture)));
            }
            finally { Native.CloseHandle(h); }
        }

        // After a skip Step is not entered again this match, keeping the adapter handle is pointless, close it while here
        private static void SkipOnce(string why)
        {
            bool first;
            uint adapter;
            lock (lk)
            {
                first = stage != ShieldStage.Skipped;
                stage = ShieldStage.Skipped;
                adapter = shieldAdapter;
                shieldAdapter = 0; shieldPhys = 0;
            }
            if (adapter != 0) VidMmProbe.CloseAdapter(adapter);
            if (first && !string.IsNullOrEmpty(why)) Logger.Warn(why);
        }

        public static bool Release()
        {
            lock (opLk) return DoRelease(Lang.T("t.vramshield.3"));
        }

        // Touch nothing when there is nothing to do, this function is called once per second during a match
        //   the feature is off by default, without a fast empty check it is a wasted registry read every second
        private static bool ReleaseIfAny(string reason)
        {
            lock (lk)
                if (shieldAdapter == 0 && shieldPid == 0
                    && !recoveryBlocked && (stage == ShieldStage.Idle || stage == ShieldStage.Fused))
                    return true;
            lock (opLk) return DoRelease(reason);
        }

        // Must be called under opLk, IO always stays outside lk
        private static bool DoRelease(string reason)
        {
            int pid;
            long creation;
            uint adapter, phys;
            bool had;
            lock (lk)
            {
                pid = shieldPid; creation = shieldCreation;
                adapter = shieldAdapter; phys = shieldPhys;
                had = stage == ShieldStage.Engaged;
            }
            string snapshot = Settings.LoadStr(SnapKey, "");
            if (snapshot.Length != 0)
            {
                int recordedPid;
                long recordedCreation;
                if (!ParseSnapshot(snapshot, out recordedPid, out recordedCreation))
                {
                    lock (lk) recoveryBlocked = true;
                    return false;
                }
                if (had && (pid != recordedPid || creation != recordedCreation))
                {
                    lock (lk) recoveryBlocked = true;
                    return false;
                }
                if (pid != recordedPid || creation != recordedCreation) { adapter = 0; phys = 0; }
                pid = recordedPid; creation = recordedCreation;
            }
            if (snapshot.Length != 0 || had)
            {
                bool restored;
                BeginMutation();
                try { restored = RestoreReservation(pid, creation, adapter, phys); }
                catch { restored = false; }
                finally { EndMutation(); }
                if (!restored)
                {
                    lock (lk) recoveryBlocked = true;
                    // Normally the record already exists, in case an external writer deleted it by accident
                    // keep a copy of the original value in memory too
                    if (snapshot.Length == 0 && pid > 0 && creation > 0)
                        Settings.SaveStr(SnapKey, pid.ToString(CultureInfo.InvariantCulture)
                            + ":" + creation.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
            }
            // A modified or unwritable snapshot does not mean a new restore target may be cleared
            // leave it in plain sight for the final reset verification
            if (Settings.LoadStr(SnapKey, "") != snapshot
                || snapshot.Length != 0 && (!Settings.SaveStr(SnapKey, "") || Settings.LoadStr(SnapKey, "").Length != 0))
            {
                lock (lk) recoveryBlocked = true;
                return false;
            }
            uint closeAdapter;
            lock (lk)
            {
                closeAdapter = shieldAdapter;
                shieldPid = 0; shieldCreation = 0; shieldBytes = 0;
                shieldAdapter = 0; shieldPhys = 0;
                pressureRun = 0; lastBudget = 0;
                recoveryBlocked = false;
                if (stage != ShieldStage.Fused) stage = ShieldStage.Idle;
            }
            if (closeAdapter != 0) VidMmProbe.CloseAdapter(closeAdapter);
            if (had) Logger.Log(Lang.T("log.vramshield.10") + reason);
            return true;
        }

        // If Pavise exits abnormally the reservation stays attached to the game process, revoke it on next launch
        //   the reservation vanishes with process exit, so action is only needed if the game is still running and identity matches
        public static bool HealFromCrash()
        {
            lock (opLk) return DoRelease(Lang.T("t.vramshield.3"));
        }

        public static bool HasResidue()
        {
            lock (lk) if (recoveryBlocked || stage == ShieldStage.Engaged) return true;
            return Settings.LoadStr(SnapKey, "").Length != 0;
        }

        private static bool ParseSnapshot(string snap, out int pid, out long creation)
        {
            pid = 0;
            creation = 0;
            string[] parts = snap.Split(':');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out creation)
                && pid > 0 && creation > 0;
        }

        private static bool RestoreReservation(int pid, long creation, uint adapter, uint phys)
        {
#if PAVISE_SELFTEST
            if (RestoreReservationForTest != null) return RestoreReservationForTest(pid, creation, adapter, phys);
#endif
            if (pid <= 0 || creation <= 0) return false;
            IntPtr h = Native.OpenProcess(ShieldAccess, false, pid);
            if (h == IntPtr.Zero)
            {
                if (Native.LastOpenProcessFailureWasNoSuchProcess()) return true;
                IntPtr query = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (query == IntPtr.Zero) return Native.LastOpenProcessFailureWasNoSuchProcess();
                try
                {
                    long currentCreation, cpu; ulong io;
                    return Native.QueryProcessSample(query, out currentCreation, out cpu, out io)
                        && currentCreation != creation;
                }
                finally { Native.CloseHandle(query); }
            }
            try
            {
                long cr, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out cr, out cpu, out io)) return false;
                if (cr != creation) return true;
                // Old-format snapshots carry no adapter identity, after a crash
                // today's busiest GPU proves nothing about which card the reservation was placed on
                // keep the record until that process exits, do not guess
                if (adapter == 0) return false;
                VramStatus before = VidMmProbe.Query(h, adapter, phys);
                if (before == null || !before.Ok) return false;
                if (before.CurrentReservation == 0) return true;
                if (!VidMmProbe.SetReservation(h, adapter, phys, 0)) return false;
                VramStatus after = VidMmProbe.Query(h, adapter, phys);
                return after != null && after.Ok && after.CurrentReservation == 0;
            }
            catch { return false; }
            finally { Native.CloseHandle(h); }
        }

#if PAVISE_SELFTEST
        internal static Func<int, long, uint, uint, bool> RestoreReservationForTest;
        internal static Action<int, long> SampleStepForTest;
        internal static bool RecoveryBlockedForTest { get { lock (lk) return recoveryBlocked; } }

        internal static void ResetRecoveryForTest()
        {
            lock (lk) { resolvedAdapter = null; resolvedForPid = 0; resolvedForCreation = 0; }
            Interlocked.Exchange(ref adapterResolveBusy, 0);
            lock (opLk)
            lock (lk)
            {
                if (shieldAdapter != 0) throw new InvalidOperationException("Cannot discard a live native VRAM adapter in an isolated test");
                stage = ShieldStage.Idle;
                nextSampleTicks = 0;
                pressureRun = shieldPid = 0;
                shieldCreation = 0;
                shieldPhys = 0;
                shieldBytes = lastBudget = 0;
                recoveryBlocked = false;
                RestoreReservationForTest = null;
                SampleStepForTest = null;
            }
        }
#endif
    }
}
