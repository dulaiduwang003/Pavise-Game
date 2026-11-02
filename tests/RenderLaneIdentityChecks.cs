#if PAVISE_RENDERLANE_IDENTITY_CHECKS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // Isolated doubles: no real thread handle, priority write or preference store is touched.
    internal static class Native
    {
        public const int THREAD_SET_LIMITED_INFORMATION = 0x0400;
        public const int THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        public const int THREAD_PRIORITY_HIGHEST = 2;
        public const int THREAD_PRIORITY_ERROR_RETURN = 0x7FFFFFFF;
        internal sealed class FakeThread
        {
            internal int Owner, Priority, Writes;
            internal long Born, Exited, Cpu, CpuStep;
            internal bool TimesFail, PriorityFail;
        }
        internal static readonly Dictionary<int, FakeThread> Threads = new Dictionary<int, FakeThread>();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern void SetLastError(uint code);
        internal static int Opens;
        public static IntPtr OpenThread(int access, bool inherit, int tid)
        {
            Opens++;
            if (Threads.ContainsKey(tid)) return new IntPtr(tid);
            SetLastError(87); return IntPtr.Zero;
        }
        public static bool CloseHandle(IntPtr handle) { return true; }
        public static int QueryThreadOwnerPid(IntPtr handle) { return Threads[(int)handle].Owner; }
        public static bool GetThreadTimes(IntPtr handle, out long born, out long exited, out long kernel, out long user)
        {
            FakeThread thread = Threads[(int)handle];
            thread.Cpu += thread.CpuStep;
            born = thread.Born; exited = thread.Exited; kernel = 0; user = thread.Cpu;
            return !thread.TimesFail;
        }
        public static int GetThreadPriority(IntPtr handle)
        { FakeThread thread = Threads[(int)handle]; return thread.PriorityFail ? THREAD_PRIORITY_ERROR_RETURN : thread.Priority; }
        public static bool SetThreadPriority(IntPtr handle, int priority)
        { FakeThread thread = Threads[(int)handle]; thread.Writes++; thread.Priority = priority; return true; }
    }
    internal static class Logger
    {
        internal static readonly List<string> Lines = new List<string>();
        public static void Log(string line) { Lines.Add(line); }
        public static void Info(string line) { Lines.Add(line); }
        public static void Warn(string line) { Lines.Add(line); }
    }
    internal static class Lang { public static string T(string key) { return key; } }
    internal static class PerformancePolicy { internal static string Text(string zh, string en) { return zh; } }
    internal static class Settings
    {
        internal static readonly Dictionary<string, string> Store = new Dictionary<string, string>();
        internal static string DenyKey, FailSaveKey;
        internal static bool TryLoadStr(string name, out string value)
        {
            value = "";
            if (name == DenyKey) return false;
            string stored;
            if (Store.TryGetValue(name, out stored)) value = stored;
            return true;
        }
        public static bool SaveStr(string name, string value)
        {
            if (name == FailSaveKey) return false;
            Store[name] = value; return true;
        }
    }
    internal sealed class ThreadTimes { public long[] Ids, Created, Cpu; }
    internal static class ProcessSnapshotSource
    {
        internal static ThreadTimes Next;
        // Like the kernel snapshot, each sample carries the accumulated CPU time of every listed thread.
        internal static ThreadTimes SampleThreads(int pid, long creation, int maxAgeMs)
        {
            if (Next == null) return null;
            var sample = new ThreadTimes { Ids = (long[])Next.Ids.Clone(), Created = (long[])Next.Created.Clone(), Cpu = new long[Next.Ids.Length] };
            for (int i = 0; i < sample.Ids.Length; i++)
            {
                Native.FakeThread thread;
                if (Native.Threads.TryGetValue((int)sample.Ids[i], out thread)) { thread.Cpu += thread.CpuStep; sample.Cpu[i] = thread.Cpu; }
            }
            return sample;
        }
    }

    internal static class RenderLaneIdentityChecks
    {
        private const long FirstBirth = 133000000000000001, ReusedBirth = 133000000000000777, OtherBirth = 133000000000000555;
        private const int Tid = 111, OtherTid = 222, GameManaged = 15;
        private const string MainKey = "RenderLane", BirthKey = "RenderLaneThreadBirthV1";
        private const string RestoredMark = "log.renderlane.20", HealedMark = "log.renderlane.24", ReleasedMark = "未写回原值";
        private static int assertions, pid;
        private static long processBirth;
        private static readonly Type Lane = typeof(RenderLane);
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;

        private static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException("FAIL " + message); }
        private static object Get(string name) { return Lane.GetField(name, Hidden).GetValue(null); }
        private static void Set(string name, object value) { Lane.GetField(name, Hidden).SetValue(null, value); }
        private static object Call(string name, params object[] args)
        {
            try { return Lane.GetMethod(name, Hidden).Invoke(null, args); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private static string Value(string key)
        { string value; return Settings.Store.TryGetValue(key, out value) ? value : ""; }
        private static string Line(long creation) { return pid + "|" + creation + "|" + Tid + "|0"; }
        private static string Line() { return Line(processBirth); }
        private static Native.FakeThread Seed(int tid, long born, int priority)
        {
            var thread = new Native.FakeThread { Owner = pid, Born = born, Priority = priority, CpuStep = 1 };
            Native.Threads[tid] = thread; return thread;
        }
        private static bool LastLogHas(string mark)
        { return Logger.Lines.Count > 0 && Logger.Lines[Logger.Lines.Count - 1].Contains(mark); }

        private static void Reset()
        {
            Settings.Store.Clear(); Settings.DenyKey = null; Settings.FailSaveKey = null;
            Native.Threads.Clear(); ProcessSnapshotSource.Next = null; Logger.Lines.Clear();
            Set("shutdownClosed", false); Set("laneApplied", false); Set("lanePid", 0); Set("laneCreation", 0L);
            Set("laneTid", 0); Set("laneThreadCreation", 0L); Set("laneOriginalPriority", 0);
            Set("laneGen", (int)Get("laneGen") + 1);
        }
        private static RenderLane.PinOutcome TryPin(Native.FakeThread thread, long birth)
        {
            var best = new RenderLane.Candidate { Tid = Tid, ThreadCreation = birth, Share = .8, ThreadCount = 2 };
            Func<int> read = delegate { return thread.Priority; };
            Func<int, bool> write = delegate(int priority) { thread.Writes++; thread.Priority = priority; return true; };
            return (RenderLane.PinOutcome)Call("TryPinPriorityLocked", pid, processBirth, best, "identity-test", (int)Get("laneGen"), false, read, write);
        }
        private static Native.FakeThread PinFirst()
        {
            Native.FakeThread thread = Seed(Tid, FirstBirth, 0);
            Check(TryPin(thread, FirstBirth) == RenderLane.PinOutcome.Pinned && thread.Priority == Native.THREAD_PRIORITY_HIGHEST,
                "the isolated pin did not reach its applied state");
            thread.Writes = 0; Logger.Lines.Clear();
            return thread;
        }
        private static bool ParsesAsLegacy(string raw)
        {
            var args = new object[] { raw, 0, 0L, 0, 0 };
            return (bool)Lane.GetMethod("ParseJournal", Hidden).Invoke(null, args)
                && (int)args[1] == pid && (long)args[2] == processBirth && (int)args[3] == Tid && (int)args[4] == 0;
        }
        private static void Crash(long creation, string birthRecord)
        { Settings.SaveStr(MainKey, Line(creation)); Settings.SaveStr(BirthKey, birthRecord); }
        private static object PinnedJudge()
        {
            var judge = new LaneJudge();
            for (int i = 0; i < LaneJudge.ConfirmWins; i++) judge.Observe(Tid, .9, 0);
            Check(judge.PinnedTid == Tid, "the isolated judge did not reach its pinned state");
            return judge;
        }
        private static void DominantOther(long tidBirth)
        {
            Native.Threads[Tid].CpuStep = 1;
            Seed(OtherTid, OtherBirth, 0).CpuStep = 100;
            ProcessSnapshotSource.Next = new ThreadTimes { Ids = new long[] { Tid, OtherTid }, Created = new long[] { tidBirth, OtherBirth } };
        }
        private static bool Verify(object judge)
        { return (bool)Call("VerifyOnce", pid, processBirth, "identity-test", (int)Get("laneGen"), judge); }

        private static void Run()
        {
            using (Process self = Process.GetCurrentProcess()) { pid = self.Id; processBirth = self.StartTime.ToFileTimeUtc(); }
            Native.FakeThread thread;

            // Receipt shape: the primary journal keeps the four-field layout an older build can read.
            Reset(); PinFirst();
            Check(Value(MainKey) == Line() && ParsesAsLegacy(Value(MainKey)), "the primary journal is no longer the legacy four-field receipt");
            Check(Value(BirthKey) == Line() + "|" + FirstBirth, "a new pin did not record the original thread creation time");
            Check(RenderLane.Release() && Native.Threads[Tid].Priority == 0 && Native.Threads[Tid].Writes == 1, "the original thread was not restored on release");
            Check(Value(MainKey) == "" && !RenderLane.HasResidue() && !(bool)Get("laneApplied"), "a restored lane kept its journal");
            Check(Value(BirthKey) == "", "a settled receipt kept its thread creation record");
            Check(LastLogHas(RestoredMark), "a real restoration was not logged as restored");

            // Thread identity: a reused TID or an exited original is never written.
            Reset(); PinFirst(); Seed(Tid, ReusedBirth, Native.THREAD_PRIORITY_HIGHEST);
            Check(RenderLane.Release() && Native.Threads[Tid].Writes == 0 && Native.Threads[Tid].Priority == Native.THREAD_PRIORITY_HIGHEST,
                "release wrote the old priority onto a new thread that reused the TID");
            Check(Value(MainKey) == "" && !(bool)Get("laneApplied"), "a gone original thread kept its debt after release");
            Check(LastLogHas(ReleasedMark) && !LastLogHas(RestoredMark), "an unwritten release was logged as a restoration");

            Reset(); thread = PinFirst(); thread.Exited = FirstBirth + 1;
            Check(RenderLane.Release() && thread.Writes == 0 && Value(MainKey) == "", "release wrote to an exited original thread or kept its debt");

            Reset(); thread = PinFirst(); thread.TimesFail = true;
            Check(!RenderLane.Release() && thread.Writes == 0 && Value(MainKey) == Line() && RenderLane.HasResidue(),
                "an unreadable thread identity was acknowledged as restored");
            thread.TimesFail = false;
            Check(RenderLane.Release() && thread.Priority == 0 && Value(MainKey) == "", "readable identity did not finish the retained restore");

            // Expected value: only the HIGHEST written by this feature is taken back.
            foreach (int changed in new[] { GameManaged, -1 })
            {
                Reset(); thread = PinFirst(); thread.Priority = changed;
                Check(RenderLane.Release() && thread.Writes == 0 && thread.Priority == changed,
                    "release overwrote a priority the game changed after the boost");
                Check(Value(MainKey) == "" && LastLogHas(ReleasedMark), "an externally changed priority kept its debt or was logged as restored");
            }
            Reset(); thread = PinFirst(); thread.Priority = 0;
            Check(RenderLane.Release() && thread.Writes == 0 && Value(MainKey) == "", "an already original priority was rewritten or kept its debt");
            Reset(); thread = PinFirst(); thread.PriorityFail = true;
            Check(!RenderLane.Release() && thread.Writes == 0 && Value(MainKey) == Line(), "an unreadable current priority was acknowledged");
            thread.PriorityFail = false;
            Check(RenderLane.Release() && thread.Priority == 0, "a readable priority did not finish the retained restore");

            // Crash recovery reads the bound creation record.
            Reset(); Crash(processBirth, Line() + "|" + FirstBirth); Seed(Tid, ReusedBirth, Native.THREAD_PRIORITY_HIGHEST);
            RenderLane.HealFromCrash();
            Check(Native.Threads[Tid].Writes == 0 && Native.Threads[Tid].Priority == Native.THREAD_PRIORITY_HIGHEST,
                "crash recovery wrote the old priority onto a reused TID");
            Check(Value(MainKey) == "" && LastLogHas(ReleasedMark), "crash recovery kept a gone thread's debt or logged it as restored");
            Reset(); Crash(processBirth, Line() + "|" + FirstBirth); thread = Seed(Tid, FirstBirth, Native.THREAD_PRIORITY_HIGHEST);
            RenderLane.HealFromCrash();
            Check(thread.Priority == 0 && Value(MainKey) == "" && LastLogHas(HealedMark), "crash recovery did not restore the same original thread");

            // Legacy or unprovable receipts: settle without writing, otherwise keep the debt.
            Reset(); Crash(processBirth, ""); thread = Seed(Tid, FirstBirth, Native.THREAD_PRIORITY_HIGHEST);
            RenderLane.HealFromCrash();
            Check(thread.Writes == 0 && Value(MainKey) == Line() && RenderLane.HasResidue(),
                "a legacy receipt without thread identity was written while its owner was unprovable");
            thread.Priority = 0; RenderLane.HealFromCrash();
            Check(thread.Writes == 0 && Value(MainKey) == "", "a legacy receipt already at its original value was not settled");
            Reset(); Crash(processBirth, ""); thread = Seed(Tid, FirstBirth, GameManaged);
            RenderLane.HealFromCrash();
            Check(thread.Writes == 0 && Value(MainKey) == "", "a legacy receipt whose value is no longer ours was rewritten or kept");
            Reset(); Crash(processBirth + 1, ""); thread = Seed(Tid, FirstBirth, Native.THREAD_PRIORITY_HIGHEST);
            RenderLane.HealFromCrash();
            Check(thread.Writes == 0 && Value(MainKey) == "" && LastLogHas(ReleasedMark), "a gone process was written or logged as restored");
            Reset(); Crash(processBirth, pid + "|" + processBirth + "|" + Tid + "|-1|" + FirstBirth);
            thread = Seed(Tid, FirstBirth, Native.THREAD_PRIORITY_HIGHEST);
            RenderLane.HealFromCrash();
            Check(thread.Writes == 0 && Value(MainKey) == Line(), "a creation record bound to another receipt was trusted");
            Reset(); Crash(processBirth, Line() + "|" + FirstBirth); thread = Seed(Tid, FirstBirth, 0);
            Settings.DenyKey = BirthKey; RenderLane.HealFromCrash();
            Check(thread.Writes == 0 && Value(MainKey) == Line(), "an unreadable creation record was acknowledged");
            Settings.DenyKey = null; RenderLane.HealFromCrash();
            Check(Value(MainKey) == "", "a readable creation record did not settle the retained receipt");

            // Pin ordering: an existing debt keeps its creation record; a failed record aborts before any write.
            Reset(); Crash(processBirth, Line() + "|" + FirstBirth); thread = Seed(Tid, FirstBirth, 0);
            Check(TryPin(thread, ReusedBirth) == RenderLane.PinOutcome.Retryable && thread.Writes == 0,
                "a pin over an unresolved journal reached the thread");
            Check(Value(BirthKey) == Line() + "|" + FirstBirth && Value(MainKey) == Line(), "a refused pin replaced the creation record of an existing debt");
            Reset(); thread = Seed(Tid, FirstBirth, 0); Settings.FailSaveKey = BirthKey;
            Check(TryPin(thread, FirstBirth) == RenderLane.PinOutcome.Retryable && thread.Writes == 0,
                "a pin continued after its creation record could not be saved");
            Check(Value(MainKey) == "", "a failed creation record left a primary journal behind");
            Reset(); thread = Seed(Tid, FirstBirth, 0); Settings.FailSaveKey = MainKey;
            Check(TryPin(thread, FirstBirth) == RenderLane.PinOutcome.Retryable && thread.Writes == 0,
                "a pin continued after its primary journal could not be saved");

            // Re-verification uses the in-memory creation time of the pinned thread.
            Reset(); PinFirst(); object judge = PinnedJudge(); Seed(Tid, ReusedBirth, Native.THREAD_PRIORITY_HIGHEST); DominantOther(ReusedBirth);
            Native.Opens = 0;
            Check(Verify(judge) && (bool)Get("laneApplied"), "the first dominance strike unpinned early");
            // Sampling reads thread times from the process snapshot; it opens no game thread.
            Check(Native.Opens == 0, "thread sampling opened game thread handles");
            bool unpinned = Verify(judge);
            Check(Native.Threads[Tid].Writes == 0 && Native.Threads[Tid].Priority == Native.THREAD_PRIORITY_HIGHEST,
                "re-verification wrote the old priority onto a reused TID");
            Check(unpinned && Value(MainKey) == "" && !(bool)Get("laneApplied"), "re-verification kept a gone thread's debt");
            Reset(); PinFirst(); judge = PinnedJudge(); DominantOther(FirstBirth);
            Check(Verify(judge) && Verify(judge) && Native.Threads[Tid].Priority == 0 && Value(MainKey) == "" && !(bool)Get("laneApplied"),
                "re-verification did not restore the same original thread");
        }

        private static int Main()
        {
            try { Run(); Console.WriteLine("PASS render lane identity assertions=" + assertions); return 0; }
            catch (Exception error) { Console.WriteLine(error.Message.StartsWith("FAIL ") ? error.Message : "FAIL unexpected " + error); return 1; }
        }
    }
}
#endif
