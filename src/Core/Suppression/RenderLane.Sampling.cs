using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class RenderLane
    {
        internal enum SampleFailure { None, NoCpuProgress, NoReadableThreads, ProcessUnavailable, ProcessGone, IdentityChanged }
        internal struct ThreadSample
        {
            public long Creation;
            public long CpuTicks;
        }

        internal static bool TryIdentify(int pid, out Candidate best)
        {
            double ignored;
            return TryIdentifyProcess(pid, 0, 0, out best, out ignored);
        }

        internal static bool TryIdentify(int pid, int watchTid, out Candidate best, out double watchShare)
        {
            return TryIdentifyProcess(pid, 0, watchTid, out best, out watchShare);
        }

        internal static bool TryIdentifyProcess(int pid, long creation, int watchTid, out Candidate best, out double watchShare)
        {
#if PAVISE_SELFTEST
            if (SampleForTest != null)
            {
                SampleResult result = SampleForTest(pid, creation, watchTid);
                best = result.Best; watchShare = result.WatchShare; return result.Success;
            }
#endif
            best = new Candidate(); watchShare = 0;
            var first = new Dictionary<int, ThreadSample>();
            if (!SampleThreads(pid, creation, first, out best)) return false;
            Thread.Sleep(SampleGapMs);
            var second = new Dictionary<int, ThreadSample>();
            if (!SampleThreads(pid, creation, second, out best)) return false;
            return SelectCandidate(first, second, watchTid, out best, out watchShare);
        }

        internal static bool SelectCandidate(Dictionary<int, ThreadSample> first, Dictionary<int, ThreadSample> second,
            int watchTid, out Candidate best, out double watchShare)
        {
            best = new Candidate { ThreadCount = second.Count, Failure = SampleFailure.NoCpuProgress };
            watchShare = 0;
            long total = 0, bestDelta = -1, watchDelta = 0;
            foreach (KeyValuePair<int, ThreadSample> item in second)
            {
                ThreadSample before;
                if (!first.TryGetValue(item.Key, out before) || before.Creation != item.Value.Creation) continue;
                long delta = item.Value.CpuTicks - before.CpuTicks;
                if (delta <= 0) continue;
                total += delta;
                if (delta > bestDelta) { bestDelta = delta; best.Tid = item.Key; best.ThreadCreation = item.Value.Creation; }
                if (watchTid == item.Key) watchDelta = delta;
            }
            if (best.Tid == 0 || total <= 0) return false;
            best.Failure = SampleFailure.None;
            best.Share = bestDelta / (double)total;
            if (watchTid != 0) watchShare = watchDelta / (double)total;
            return true;
        }

        private static bool SampleThreads(int pid, long creation, Dictionary<int, ThreadSample> into, out Candidate status)
        {
            status = new Candidate { Failure = SampleFailure.NoReadableThreads };
            try
            {
                // 两次线程采样间隔为 800 ms，不能复用 1.5 秒缓存。
                ThreadTimes native = ProcessSnapshotSource.SampleThreads(pid, creation, 0);
                if (native != null)
                {
                    for (int i = 0; i < native.Ids.Length && i < MaxThreads; i++)
                    {
                        if (native.Ids[i] <= 0 || native.Ids[i] > int.MaxValue) continue;
                        ReadAccessibleThread(pid, (int)native.Ids[i], native.Created[i], into, ref status);
                    }
                    return into.Count > 0;
                }
                using (Process target = Process.GetProcessById(pid))
                {
                    if (creation > 0 && target.StartTime.ToFileTimeUtc() != creation)
                    { status.Failure = SampleFailure.IdentityChanged; return false; }
                    int seen = 0;
                    foreach (ProcessThread thread in target.Threads)
                    {
                        using (thread)
                        {
                            if (++seen > MaxThreads) break;
                            ReadAccessibleThread(pid, thread.Id, 0, into, ref status);
                        }
                    }
                    return into.Count > 0;
                }
            }
            catch (ArgumentException) { status.Failure = SampleFailure.ProcessGone; }
            catch (InvalidOperationException) { status.Failure = SampleFailure.ProcessGone; }
            catch (Win32Exception error) { status.Failure = SampleFailure.ProcessUnavailable; status.NativeError = error.NativeErrorCode; }
            catch { status.Failure = SampleFailure.ProcessUnavailable; }
            return false;
        }

        private static void ReadAccessibleThread(int pid, int tid, long expectedCreation,
            Dictionary<int, ThreadSample> into, ref Candidate status)
        {
            IntPtr handle = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
            if (handle == IntPtr.Zero) { status.NativeError = Marshal.GetLastWin32Error(); return; }
            try
            {
                // 采样后重新校验线程及进程创建时间。
                if (Native.QueryThreadOwnerPid(handle) != pid) return;
                long born, exited, kernel, user;
                if (!Native.GetThreadTimes(handle, out born, out exited, out kernel, out user))
                { status.NativeError = Marshal.GetLastWin32Error(); return; }
                if (expectedCreation > 0 && born != expectedCreation) return;
                into[tid] = new ThreadSample { Creation = born, CpuTicks = kernel + user };
            }
            finally { Native.CloseHandle(handle); }
        }

        private static string SampleFailureText(int pid, Candidate status)
        {
            string key = status.Failure == SampleFailure.NoCpuProgress ? "idle"
                : status.Failure == SampleFailure.ProcessGone ? "exited"
                : status.Failure == SampleFailure.IdentityChanged ? "identity"
                : status.Failure == SampleFailure.ProcessUnavailable ? "process" : "threads";
            return Lang.T("log.renderlane.sample." + key) + " PID " + pid
                + (status.NativeError == 0 ? "" : " Win32=" + status.NativeError);
        }

#if PAVISE_SELFTEST
        internal struct SampleResult
        {
            public bool Success;
            public Candidate Best;
            public double WatchShare;
        }
#endif
    }
}
