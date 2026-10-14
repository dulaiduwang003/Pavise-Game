// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal enum LaneState
    {
        Idle = 0,
        Trying = 1,
        Engaged = 2,
        Unavailable = 3
    }

    internal enum LaneDecision
    {
        None = 0,
        Pin = 1,
        Unpin = 2
    }

    internal sealed class LaneJudge
    {
        public const int ConfirmWins = 3;
        public const int UnpinStrikes = 2;
        public const int MaxUnpins = 2;
        public const double ReverifyHealthyShare = 0.15;

        private int candidateTid;
        private int streak;
        private int pinnedTid;
        private int strikes;
        private int unpins;

        public int PinnedTid { get { return pinnedTid; } }
        public bool GaveUp { get { return unpins >= MaxUnpins; } }

        public LaneDecision Observe(int winnerTid, double winnerShare, double pinnedShare)
        {
            if (pinnedTid == 0)
            {
                if (winnerTid <= 0 || winnerShare < RenderLane.MinDominantShare)
                { candidateTid = 0; streak = 0; return LaneDecision.None; }
                if (winnerTid == candidateTid) streak++;
                else { candidateTid = winnerTid; streak = 1; }
                if (streak < ConfirmWins) return LaneDecision.None;
                pinnedTid = winnerTid; candidateTid = 0; streak = 0; strikes = 0;
                return LaneDecision.Pin;
            }
            if (pinnedShare >= ReverifyHealthyShare) { strikes = 0; return LaneDecision.None; }
            if (winnerTid == pinnedTid || winnerShare < RenderLane.MinDominantShare)
                return LaneDecision.None;
            if (++strikes < UnpinStrikes) return LaneDecision.None;
            pinnedTid = 0; strikes = 0; unpins++;
            return LaneDecision.Unpin;
        }

        public void ResetCandidate() { candidateTid = 0; streak = 0; }
        public void PinFailed() { pinnedTid = 0; ResetCandidate(); }
    }

    internal static partial class RenderLane
    {
        internal const double MinDominantShare = 0.35;
        private const int SampleGapMs = 800;
        private const int MaxThreads = 512;
        private const int BatchWindows = 6;
        internal const int MaxIdentifyTries = 5;
        private const int RetryGapSeconds = 60;
        private const int ReverifyGapSeconds = 30;

        private static readonly object sync = new object();
        private static readonly object operationGate = new object();
        private static bool shutdownClosed;
        private static int lanePid;
        private static long laneCreation;
        private static int laneTid;
        private static int laneOriginalPriority;
        private static bool laneApplied;
        private static int laneGen;
        private static int trackPid;
        private static long trackCreation;
        private static int trackGen;
        private static int trackBatch;
        private static int gaveUpPid;
        private static long gaveUpCreation;

        internal struct Candidate
        {
            public int Tid;
            public long ThreadCreation;
            public double Share;
            public int ThreadCount;
            public SampleFailure Failure;
            public int NativeError;
        }

        public static bool IsActiveFor(int pid, long creation)
        {
            lock (sync) return laneApplied && lanePid == pid && laneCreation == creation;
        }

        public static LaneState StateFor(int pid, long creation)
        {
            lock (sync)
            {
                if (laneApplied && lanePid == pid && laneCreation == creation) return LaneState.Engaged;
                if (gaveUpPid == pid && gaveUpCreation == creation) return LaneState.Unavailable;
                if (trackPid == pid && trackCreation == creation) return LaneState.Trying;
                return LaneState.Idle;
            }
        }

        public static void EnsureForGame(int pid, long creation, string gameName)
        {
            Thread worker;
            lock (sync)
            {
                if (shutdownClosed) return;
                if (pid <= 0) return;
                if (laneApplied && lanePid == pid && laneCreation == creation) return;
                if (gaveUpPid == pid && gaveUpCreation == creation) return;
                if (trackPid == pid && trackCreation == creation) return;
                laneGen++;
                int gen = laneGen;
                trackPid = pid; trackCreation = creation; trackGen = gen; trackBatch = 0;
                worker = new Thread(delegate () { Work(pid, creation, gameName, gen); });
                worker.IsBackground = true;
                worker.Name = "Pavise.RenderLane";
                worker.Priority = ThreadPriority.BelowNormal;
            }
            worker.Start();
        }

        private static bool GenAlive(int gen) { lock (sync) return !shutdownClosed && gen == laneGen; }

        private static bool WaitAlive(int ms, int gen)
        {
#if PAVISE_SELFTEST
            if (WaitForTest != null) return GenAlive(gen) && WaitForTest(ms, gen) && GenAlive(gen);
#endif
            int slept = 0;
            while (slept < ms)
            {
                int step = Math.Min(2000, ms - slept);
                Thread.Sleep(step);
                slept += step;
                if (!GenAlive(gen)) return false;
            }
            return true;
        }

        private static void Work(int pid, long creation, string gameName, int gen)
        {
            try { WorkLoop(pid, creation, gameName, gen); }
            catch { }
            finally
            {
                lock (sync)
                    if (trackGen == gen) { trackPid = 0; trackCreation = 0; trackGen = 0; }
            }
        }

        private static void WorkLoop(int pid, long creation, string gameName, int gen)
        {
            var judge = new LaneJudge();
            int batch = 0;
            while (GenAlive(gen))
            {
                bool pinned;
                lock (sync) pinned = laneApplied && lanePid == pid && laneCreation == creation;
                if (!pinned)
                {
                    batch++;
                    lock (sync) if (trackGen == gen) trackBatch = batch;
                    bool logThis = batch == 1 || batch >= MaxIdentifyTries;
                    if (!IdentifyBatch(pid, creation, gameName, gen, judge, logThis, out pinned))
                        return;
                    if (pinned) continue;
                    if (judge.GaveUp) { GiveUp(pid, creation); return; }
                    if (batch >= MaxIdentifyTries) { GiveUp(pid, creation); return; }
                    if (!WaitAlive(RetryGapSeconds * 1000, gen)) return;
                }
                else
                {
                    if (!WaitAlive(ReverifyGapSeconds * 1000, gen)) return;
                    if (!VerifyOnce(pid, creation, gameName, gen, judge)) return;
                }
            }
        }

        private static bool IdentifyBatch(int pid, long creation, string gameName, int gen,
            LaneJudge judge, bool logThis, out bool pinnedNow)
        {
            pinnedNow = false;
            int sampleFails = 0;
            Candidate last = new Candidate();
            Candidate lastFailure = new Candidate();
            bool sawSpread = false;
            int previousTid = 0;
            long previousThreadCreation = 0;
            // 重试间隔或样本缺失后不合并旧投票。
            judge.ResetCandidate();
            for (int w = 0; w < BatchWindows; w++)
            {
                if (!GenAlive(gen)) return false;
                Candidate best;
                double ignore;
                if (!TryIdentifyProcess(pid, creation, 0, out best, out ignore))
                {
                    sampleFails++; lastFailure = best; judge.ResetCandidate();
                    if (best.Failure == SampleFailure.ProcessGone || best.Failure == SampleFailure.IdentityChanged)
                    {
                        if (GenAlive(gen)) Logger.Info(SampleFailureText(pid, best));
                        return false;
                    }
                    if (best.Failure != SampleFailure.NoCpuProgress) { sampleFails = BatchWindows; break; }
                    continue;
                }
                if (best.Tid != previousTid || best.ThreadCreation != previousThreadCreation)
                    judge.ResetCandidate();
                previousTid = best.Tid; previousThreadCreation = best.ThreadCreation;
                last = best;
                if (best.Share < MinDominantShare) sawSpread = true;
                if (judge.Observe(best.Tid, best.Share, 0) != LaneDecision.Pin) continue;
                PinOutcome outcome = TryPin(pid, creation, best, gameName, gen, logThis);
                if (outcome == PinOutcome.Pinned) { pinnedNow = true; return true; }
                if (outcome == PinOutcome.Fatal) { GiveUp(pid, creation); return false; }
                if (outcome == PinOutcome.Canceled) return false;
                judge.PinFailed();
            }
            if (!GenAlive(gen)) return false;
            if (logThis)
            {
                if (sampleFails >= BatchWindows)
                {
                    if (lastFailure.Failure == SampleFailure.NoCpuProgress)
                        Logger.Info(SampleFailureText(pid, lastFailure));
                    else Logger.Warn(SampleFailureText(pid, lastFailure));
                }
                else if (sawSpread || last.Tid != 0)
                    Logger.Info(Lang.T("log.renderlane.3") + (gameName ?? "?") + Lang.T("log.renderlane.4")
                        + (last.Share * 100).ToString("F0") + Lang.T("log.renderlane.5") + last.ThreadCount
                        + Lang.T("log.renderlane.6"));
            }
            return true;
        }

        internal enum PinOutcome { Pinned = 0, Retryable = 1, Fatal = 2, Canceled = 3 }

        private static PinOutcome TryPin(int pid, long creation, Candidate best, string gameName,
            int gen, bool logThis)
        {
            return RunPinMutation(gen, delegate
            {
#if PAVISE_SELFTEST
                if (PinCandidateForTest != null) return PinCandidateForTest(pid, creation, best, gen);
#endif
                return TryPinLocked(pid, creation, best, gameName, gen, logThis);
            });
        }

        private static PinOutcome RunGenerationMutation(int gen, Func<PinOutcome> action)
        {
            lock (operationGate)
            {
                // 优先级写入前检查退出状态，避免恢复后再写回。
                if (!GenAlive(gen)) return PinOutcome.Canceled;
                return action();
            }
        }

        private static PinOutcome RunPinMutation(int gen, Func<PinOutcome> pin)
        {
            return RunGenerationMutation(gen, delegate
            {
                // 旧线程分配恢复完成后才允许新分配。
                if (!RestoreLaneLocked()) return PinOutcome.Retryable;
                if (!GenAlive(gen)) return PinOutcome.Canceled;
                return pin();
            });
        }

        private static PinOutcome TryPinLocked(int pid, long creation, Candidate best, string gameName,
            int gen, bool logThis)
        {
            IntPtr h = Native.OpenThread(
                Native.THREAD_SET_LIMITED_INFORMATION | Native.THREAD_QUERY_LIMITED_INFORMATION,
                false, best.Tid);
            if (h == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 87)
                {
                    if (logThis) Logger.Log(Lang.T("log.renderlane.changed"));
                    return PinOutcome.Retryable;
                }
                Logger.Log(Lang.T("log.renderlane.7") + " Win32=" + error);
                return PinOutcome.Fatal;
            }
            try
            {
                long born, exited, kernel, user;
                if (Native.QueryThreadOwnerPid(h) != pid || !ProcessAlive(pid, creation)
                    || !Native.GetThreadTimes(h, out born, out exited, out kernel, out user)
                    || exited != 0 || born != best.ThreadCreation)
                {
                    if (logThis) Logger.Log(Lang.T("log.renderlane.changed"));
                    return PinOutcome.Retryable;
                }
                return TryPinPriorityLocked(pid, creation, best, gameName, gen, logThis,
                    delegate { return Native.GetThreadPriority(h); },
                    delegate(int priority) { return Native.SetThreadPriority(h, priority); });
            }
            finally { Native.CloseHandle(h); }
        }

        private static PinOutcome TryPinPriorityLocked(int pid, long creation, Candidate best, string gameName,
            int gen, bool logThis, Func<int> readPriority, Func<int, bool> setPriority)
        {
            int original = readPriority();
            if (original == Native.THREAD_PRIORITY_ERROR_RETURN)
            {
                if (logThis) Logger.Log(Lang.T("log.renderlane.8"));
                return PinOutcome.Retryable;
            }
            if (original >= Native.THREAD_PRIORITY_HIGHEST)
            {
                if (logThis) Logger.Log(Lang.T("log.renderlane.3") + (gameName ?? "?") + Lang.T("log.renderlane.9"));
                return PinOutcome.Fatal;
            }
            string journal = JournalLine(pid, creation, best.Tid, original);
            if (!SaveJournal(pid, creation, best.Tid, original))
            {
                if (logThis) Logger.Log(Lang.T("log.renderlane.10"));
                return PinOutcome.Retryable;
            }
            if (!setPriority(Native.THREAD_PRIORITY_HIGHEST))
            {
                if (RestorePriorityVerified(readPriority, setPriority, original)) ClearJournal(journal);
                if (logThis) Logger.Warn(Lang.T("log.renderlane.11"));
                return PinOutcome.Retryable;
            }
            int actual = readPriority();
            if (actual != Native.THREAD_PRIORITY_HIGHEST)
            {
                if (RestorePriorityVerified(readPriority, setPriority, original)) ClearJournal(journal);
                if (logThis) Logger.Log(Lang.T("log.renderlane.12") + actual + Lang.T("log.renderlane.13"));
                return PinOutcome.Retryable;
            }
            bool canceled = false;
            lock (sync)
            {
                if (gen != laneGen) canceled = true;
                else
                {
                    lanePid = pid; laneCreation = creation;
                    laneTid = best.Tid; laneOriginalPriority = original; laneApplied = true;
                }
            }
            if (canceled)
            {
                if (RestorePriorityVerified(readPriority, setPriority, original)) ClearJournal(journal);
                if (logThis) Logger.Log(Lang.T("log.renderlane.14"));
                return PinOutcome.Canceled;
            }
            Logger.Log(Lang.T("log.renderlane.15") + (gameName ?? "?") + Lang.T("log.renderlane.16") + best.Tid
                + Lang.T("log.renderlane.17") + (best.Share * 100).ToString("F0") + Lang.T("log.renderlane.5") + best.ThreadCount
                + Lang.T("log.renderlane.18") + original + " " + Native.THREAD_PRIORITY_HIGHEST);
            return PinOutcome.Pinned;
        }

        private static bool VerifyOnce(int pid, long creation, string gameName, int gen, LaneJudge judge)
        {
            int tid;
            int original;
            lock (sync)
            {
                if (!laneApplied || lanePid != pid || laneCreation != creation) return true;
                tid = laneTid; original = laneOriginalPriority;
            }
            Candidate best;
            double pinnedShare;
            if (!TryIdentifyProcess(pid, creation, tid, out best, out pinnedShare))
                return ProcessAlive(pid, creation);
            if (judge.Observe(best.Tid, best.Share, pinnedShare) != LaneDecision.Unpin) return true;

            lock (operationGate)
            {
            if (!GenAlive(gen)) return false;
            if (!RestoreThread(pid, creation, tid, original))
            {
                Logger.Log(Lang.T("log.renderlane.21") + tid + Lang.T("log.renderlane.22"));
                GiveUp(pid, creation);
                return false;
            }
            lock (sync)
            {
                if (gen == laneGen && laneApplied && lanePid == pid && laneCreation == creation)
                { laneApplied = false; lanePid = 0; laneCreation = 0; laneTid = 0; }
            }
            if (!ClearJournal(JournalLine(pid, creation, tid, original))) return false;
            Logger.Log(Lang.T("log.renderlane.25") + tid + Lang.T("log.renderlane.26") + best.Tid
                + Lang.T("log.renderlane.17") + (best.Share * 100).ToString("F0") + "%");
            if (judge.GaveUp)
            {
                Logger.Log(Lang.T("log.renderlane.27") + (gameName ?? "?") + Lang.T("log.renderlane.28"));
                GiveUp(pid, creation);
                return false;
            }
            return true;
            }
        }

        private static bool ProcessAlive(int pid, long creation)
        {
            try
            {
                using (Process target = Process.GetProcessById(pid))
                    return creation <= 0 || target.StartTime.ToFileTimeUtc() == creation;
            }
            catch { return false; }
        }

        private static void GiveUp(int pid, long creation)
        {
            lock (sync) { gaveUpPid = pid; gaveUpCreation = creation; }
        }

        internal static bool ReleaseExcept(int keepPid, long keepCreation)
        {
            lock (operationGate)
            {
                lock (sync)
                    if (keepPid > 0 && (trackPid == keepPid && trackCreation == keepCreation || laneApplied && lanePid == keepPid && laneCreation == keepCreation)
                        && (!laneApplied || lanePid == keepPid && laneCreation == keepCreation)) return true;
                return ReleaseLocked();
            }
        }

        public static bool Release()
        {
            lock (operationGate) return ReleaseLocked();
        }

        // 关闭准入后限时排空，超时不等于写入已停止。
        internal static bool CloseForShutdown(int timeoutMs)
        {
            if (timeoutMs < 0) return false;
            lock (sync) { shutdownClosed = true; laneGen++; }
            if (Monitor.IsEntered(operationGate) || !Monitor.TryEnter(operationGate, timeoutMs)) return false;
            try { return ReleaseLocked(); }
            catch { return false; }
            finally { Monitor.Exit(operationGate); }
        }

        private static bool ReleaseLocked()
        {
            lock (sync)
            {
                laneGen++;
                trackPid = trackGen = trackBatch = 0; trackCreation = 0;
                gaveUpPid = 0; gaveUpCreation = 0;
            }
            return RestoreLaneLocked();
        }

        private static bool RestoreLaneLocked()
        {
            int pid, tid, original;
            long creation;
            bool applied;
            lock (sync)
            {
                applied = laneApplied;
                pid = lanePid; creation = laneCreation; tid = laneTid; original = laneOriginalPriority;
            }
            // 即使未标记分配生效，已有收据也必须恢复。
            if (!applied) return HealFromCrashLocked();
            bool ok = RestoreThread(pid, creation, tid, original);
            if (ok)
            {
                lock (sync)
                {
                    laneApplied = false; lanePid = 0; laneCreation = 0; laneTid = 0;
                }
                ok = ClearJournal(JournalLine(pid, creation, tid, original));
                Logger.Log(Lang.T("log.renderlane.19") + tid + Lang.T("log.renderlane.20") + original);
            }
            else Logger.Log(Lang.T("log.renderlane.21") + tid + Lang.T("log.renderlane.22"));
            return ok;
        }

        public static bool HasResidue()
        {
            string raw;
            return !Settings.TryLoadStr("RenderLane", out raw) || raw.Length > 0;
        }

        public static void HealFromCrash()
        {
            lock (operationGate) HealFromCrashLocked();
        }

        private static bool HealFromCrashLocked()
        {
            string raw;
            if (!Settings.TryLoadStr("RenderLane", out raw)) return false;
            if (raw.Length == 0) return true;
            int pid, tid, original;
            long creation;
            if (!ParseJournal(raw, out pid, out creation, out tid, out original)) return false;
            if (RestoreThread(pid, creation, tid, original))
            {
                bool cleared = ClearJournal(raw);
                Logger.Log(Lang.T("log.renderlane.23") + tid + Lang.T("log.renderlane.24") + original);
                return cleared;
            }
            return false;
        }

        private static bool RestoreThread(int pid, long creation, int tid, int original)
        {
            string stored;
            if (!Settings.TryLoadStr("RenderLane", out stored)) return false;
#if PAVISE_SELFTEST
            if (RestoreThreadForTest != null) return RestoreThreadForTest(pid, creation, tid, original);
#endif
            if (pid <= 0 || creation <= 0 || tid <= 0) return false;
            try
            {
                using (Process target = Process.GetProcessById(pid))
                {
                    if (creation > 0 && target.StartTime.ToFileTimeUtc() != creation) return true;
                }
            }
            catch (ArgumentException) { return true; }
            catch { return false; } // 身份不可读不等于恢复完成。
            IntPtr h = Native.OpenThread(
                Native.THREAD_SET_LIMITED_INFORMATION | Native.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 87;
            try
            {
                int owner = Native.QueryThreadOwnerPid(h);
                if (owner < 0) return false;
                if (owner != pid) return true;
                return RestorePriorityVerified(h, original);
            }
            finally { Native.CloseHandle(h); }
        }

        private static string JournalLine(int pid, long creation, int tid, int original)
        {
            return pid + "|" + creation + "|" + tid + "|" + original;
        }

        private static bool SaveJournal(int pid, long creation, int tid, int original)
        {
            string stored;
            if (!Settings.TryLoadStr("RenderLane", out stored) || stored.Length != 0) return false;
            string line = JournalLine(pid, creation, tid, original);
            return Settings.SaveStr("RenderLane", line)
                && Settings.TryLoadStr("RenderLane", out stored) && stored == line;
        }

        private static bool RestorePriorityVerified(IntPtr thread, int original)
        {
            return RestorePriorityVerified(
                delegate { return Native.GetThreadPriority(thread); },
                delegate(int priority) { return Native.SetThreadPriority(thread, priority); }, original);
        }

        private static bool RestorePriorityVerified(Func<int> readPriority, Func<int, bool> setPriority, int original)
        {
            return readPriority() == original || setPriority(original) && readPriority() == original;
        }

        private static bool ClearJournal(string expected)
        {
            string current;
            if (!Settings.TryLoadStr("RenderLane", out current)) return false;
            if (current.Length == 0) return true;
            if (current != expected) return false;
            return Settings.SaveStr("RenderLane", "")
                && Settings.TryLoadStr("RenderLane", out current) && current.Length == 0;
        }

#if PAVISE_SELFTEST
        internal static Func<int, long, int, int, bool> RestoreThreadForTest;
        internal static Func<int, int, bool> WaitForTest;
        internal static Func<int, long, Candidate, int, PinOutcome> PinCandidateForTest;
        internal static Func<int, long, int, SampleResult> SampleForTest;

        internal static PinOutcome TryPinNativeForTest(int pid, long creation, Candidate candidate, int gen)
        {
            return TryPin(pid, creation, candidate, "owned-thread-test", gen, false);
        }

        internal static void RunWorkerForTest(int pid, long creation)
        {
            int gen;
            lock (sync) { gen = ++laneGen; trackPid = pid; trackCreation = creation; trackGen = gen; }
            Work(pid, creation, "retry-test", gen);
        }

        internal static PinOutcome TryPinForTest(int pid, long creation, int tid, int gen,
            Func<int> readPriority, Func<int, bool> setPriority)
        {
            if (readPriority == null || setPriority == null)
                throw new ArgumentException("In-memory thread priority access is required");
            return RunPinMutation(gen, delegate
            {
                return TryPinPriorityLocked(pid, creation,
                    new Candidate { Tid = tid, Share = 0.8, ThreadCount = 2 },
                    "in-memory", gen, false, readPriority, setPriority);
            });
        }

        internal static int ShutdownGenerationForTest { get { lock (sync) return laneGen; } }

        internal static bool RunShutdownMutationForTest(int gen, Func<bool> action)
        {
            return RunGenerationMutation(gen, delegate
            {
                return action != null && action() ? PinOutcome.Pinned : PinOutcome.Canceled;
            }) == PinOutcome.Pinned;
        }

        internal static void ResetShutdownForTest()
        {
            lock (operationGate)
            lock (sync)
            {
                shutdownClosed = false;
                laneGen++;
                laneApplied = false;
                lanePid = laneTid = laneOriginalPriority = 0;
                laneCreation = 0;
                trackPid = trackGen = trackBatch = gaveUpPid = 0;
                trackCreation = gaveUpCreation = 0;
                RestoreThreadForTest = null;
                WaitForTest = null; PinCandidateForTest = null; SampleForTest = null;
            }
        }
#endif

        internal static bool ParseJournal(string raw, out int pid, out long creation, out int tid, out int original)
        {
            pid = 0; creation = 0; tid = 0; original = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            string[] parts = raw.Split('|');
            return parts.Length == 4
                && int.TryParse(parts[0], out pid) && pid > 0
                && long.TryParse(parts[1], out creation)
                && int.TryParse(parts[2], out tid) && tid > 0
                && int.TryParse(parts[3], out original);
        }
    }
}
