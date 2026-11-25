using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal static class FrameCaptureReservation
    {
        internal const string ReceiptKey = "FrameCaptureReservationV1";
        internal const string SessionPrefix = "Pavise.FrameReserve.";
        internal const int NoSystemSlot = 1450;
        internal const int Canceled = 995;
        private const string LedgerMutex = @"Global\Pavise_FrameCaptureReservation";
        private const int LedgerError = 5, Busy = 170, InvalidReceipt = 13;
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);
        // GameMode sync only publishes requests
        // Capture gate precedes state  state can enter requests or the ledger mutex
        // Requests never enters state or runs ETW  capture admission is checked outside state
        private static readonly object requests = new object();
        private static readonly object state = new object();
        private static readonly SemaphoreSlim captureGate = new SemaphoreSlim(1, 1);
        private static int ownerPid;
        private static long ownerBirth;
        private static FrameCoordinationTrace.Reservation reservation;
        private static string receipt;
        private static Timer retry;
        private static bool running, requestWorkerQueued, requestPending, attemptPending, startupPending;
        private static volatile bool wanted;
        private static bool suspended, retiring, releaseRetryQueued;
        private static CaptureLease active;
        private static long nextFailureLog;

        private static bool ReadOwnerLocked()
        {
            if (ownerBirth > 0) return true;
            using (Process process = Process.GetCurrentProcess())
            {
                ownerPid = process.Id;
                try { ownerBirth = process.StartTime.ToFileTimeUtc(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return ownerBirth > 0;
        }

        private sealed class GateLease : IDisposable
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) captureGate.Release();
            }
        }

        internal sealed class CaptureLease : IDisposable
        {
            private readonly IDisposable gate;
            internal readonly FrameCoordinationTrace.Reservation Session;

            internal CaptureLease(IDisposable lease, FrameCoordinationTrace.Reservation session)
            { gate = lease; Session = session; }

            public void Dispose()
            {
                lock (state)
                {
                    if (active != this) return;
                    active = null;
                    bool rebuild = Session.NeedsRebuild;
                    try
                    {
                        if ((rebuild || !wanted) && !RetireLocked()) RetryReleaseLocked();
                        if (rebuild && wanted) AttemptLocked();
                        else SetRetryLocked();
                    }
                    finally { gate.Dispose(); }
                }
            }
        }

        internal static IDisposable Enter(Func<bool> canceled)
        {
            while (canceled == null || !canceled())
            {
                if (!captureGate.Wait(100)) continue;
                var lease = new GateLease();
                if (canceled == null || !canceled()) return lease;
                lease.Dispose();
                break;
            }
            return null;
        }

        internal static CaptureLease Acquire(Func<bool> alive, out int error)
        {
            IDisposable gate = Enter(() => !alive());
            error = gate == null ? Canceled : NoSystemSlot;
            if (gate == null) return null;
            try
            {
                lock (state)
                {
                    if (!wanted || suspended) return null;
                    if (ReservationMissing)
                    {
                        int attempt = AttemptLocked();
                        if (attempt != 0) { error = attempt; return null; }
                        if (!wanted || ReservationMissing) return null;
                    }
                    error = reservation.VerifyIdle();
                    if (error != 0)
                    {
                        NoteFailureLocked("verify", error);
                        if (RetireLocked()) AttemptLocked();
                        else SetRetryLocked();
                        return null;
                    }
                    if (!wanted) { error = NoSystemSlot; return null; }
                    active = new CaptureLease(gate, reservation);
                    gate = null;
                    return active;
                }
            }
            finally { if (gate != null) gate.Dispose(); }
        }

        internal static void StartRuntime(bool enabled)
        {
            lock (requests)
            {
                if (running) return;
                running = true;
                wanted = enabled;
                startupPending = true;
                QueueRequestLocked(enabled);
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            lock (requests)
            {
                if (!running || wanted == enabled) return;
                wanted = enabled;
                QueueRequestLocked(enabled);
            }
        }

        internal static void OnGameStarted()
        {
            lock (requests)
                if (running && wanted) QueueRequestLocked(true);
        }

        internal static void RequestStopRuntime()
        {
            lock (requests)
            {
                if (!running) return;
                running = wanted = false;
                QueueRequestLocked(false);
            }
        }

        internal static bool StopRuntime()
        {
            RequestStopRuntime();
            lock (state) return ReleaseLocked();
        }

        internal static void FinishAfterUiExit()
        {
            RequestStopRuntime();
            var cleanup = new Thread(delegate()
            {
                if (!captureGate.Wait(8000)) return;
                try { StopRuntime(); }
                finally { captureGate.Release(); }
            });
            cleanup.Start();
        }

        private static void QueueRequestLocked(bool attempt)
        {
            requestPending = true;
            attemptPending |= attempt;
            if (requestWorkerQueued) return;
            requestWorkerQueued = true;
            ThreadPool.QueueUserWorkItem(ProcessRequests);
        }

        private static void ProcessRequests(object unused)
        {
            while (true)
            {
                lock (state)
                {
                    bool attempt, startup;
                    lock (requests)
                    {
                        if (!requestPending)
                        {
                            requestWorkerQueued = false;
                            return;
                        }
                        attempt = attemptPending;
                        startup = startupPending;
                        requestPending = attemptPending = startupPending = false;
                    }
                    if (!wanted)
                    {
                        if (ReleaseLocked() && startup) RecoverRecordedLocked();
                    }
                    else if (attempt) AttemptLocked();
                }
            }
        }

        private static bool ReleaseLocked()
        {
            bool settled = active == null && !suspended && RetireLocked();
            if (!settled && retiring && !suspended && active == null) RetryReleaseLocked();
            SetRetryLocked();
            return settled;
        }

        private static void RetryReleaseLocked()
        {
            if (releaseRetryQueued) return;
            releaseRetryQueued = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(250);
                lock (state)
                {
                    releaseRetryQueued = false;
                    if (!retiring || suspended || active != null) return;
                    RetireLocked();
                    SetRetryLocked();
                }
            });
        }

        internal static bool SuspendIdleForInterrupt()
        {
            lock (state)
            {
                if (active != null || reservation == null || suspended || !reservation.OwnsSession)
                    return false;
                int code = WithLedger(StopSessionLocked);
                if (code != 0)
                {
                    NoteFailureLocked("lend", code);
                    return false;
                }
                suspended = true;
                SetRetryLocked();
                return true;
            }
        }

        internal static void ResumeAfterInterrupt()
        {
            lock (state)
            {
                if (!suspended) return;
                suspended = false;
                if (wanted) AttemptLocked();
                else if (!RetireLocked()) RetryReleaseLocked();
            }
        }

        internal static bool Recover()
        {
            lock (state)
            {
                if (active != null || reservation != null || suspended) return false;
                return RecoverRecordedLocked();
            }
        }

        private static bool RecoverRecordedLocked()
        {
            int code = WithLedger(RecoverLocked);
            if (code != 0) NoteFailureLocked("recover", code);
            return code == 0;
        }

        private static int AttemptLocked()
        {
            int code = 0;
            if (wanted && !suspended && active == null && ReservationMissing)
            {
                code = WithLedger(ReserveLocked);
                if (code != 0) NoteFailureLocked("reserve", code);
            }
            SetRetryLocked();
            return code;
        }

        private static int ReserveLocked()
        {
            if (!ReadOwnerLocked()) return LedgerError;
            if (!FrameCoordinationTrace.HealLegacyFrameForReservation()) return Busy;
            int status = reservation == null ? RecoverLocked() : StopOwnedLocked();
            if (status != 0) return status;
            retiring = false;
            Guid id = Guid.NewGuid();
            string owner = ownerPid.ToString(CultureInfo.InvariantCulture);
            receipt = owner + "|" + ownerBirth.ToString(CultureInfo.InvariantCulture) + "|" + id.ToString("N");
            if (!WriteReceipt(receipt)) return LedgerError;
            reservation = new FrameCoordinationTrace.Reservation(SessionPrefix + owner + "." + id.ToString("N"), id);
            status = reservation.Start();
            if (status != 0) retiring = StopOwnedLocked() != 0;
            return status;
        }

        private static bool RetireLocked()
        {
            if (reservation == null && receipt == null) return true;
            int code = WithLedger(StopOwnedLocked);
            retiring = code != 0;
            if (retiring) NoteFailureLocked("release", code);
            return !retiring;
        }

        private static int StopOwnedLocked()
        {
            int status = StopSessionLocked();
            if (status != 0) return status;
            if (receipt != null && !ClearReceipt(receipt)) return LedgerError;
            receipt = null;
            retiring = false;
            return 0;
        }

        private static int StopSessionLocked()
        {
            if (reservation == null) return 0;
            int status = reservation.Stop();
            if (status == 0) reservation = null;
            return status;
        }

        private static int RecoverLocked()
        {
            string saved;
            if (!Settings.TryLoadStr(ReceiptKey, out saved)) return LedgerError;
            if (saved.Length == 0) return 0;
            if (!ReadOwnerLocked()) return LedgerError;
            string[] parts = saved.Split('|');
            int pid;
            long birth;
            Guid id;
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out birth) || birth <= 0
                || !Guid.TryParseExact(parts[2], "N", out id) || id == Guid.Empty)
                return InvalidReceipt;
            if ((pid != ownerPid || birth != ownerBirth) && NativeFrameThreads.ProcessState(pid, birth) != 1) return Busy;
            string name = SessionPrefix + pid.ToString(CultureInfo.InvariantCulture) + "." + id.ToString("N");
            int status = FrameCoordinationTrace.Reservation.StopRecorded(name, id);
            return status != 0 ? status : ClearReceipt(saved) ? 0 : LedgerError;
        }

        private static bool WriteReceipt(string value)
        {
            string read;
            return Settings.SaveStrDurable(ReceiptKey, value) && Settings.TryLoadStr(ReceiptKey, out read) && read == value;
        }

        private static bool ClearReceipt(string expected)
        {
            string read;
            return Settings.TryLoadStr(ReceiptKey, out read) && (read.Length == 0 || read == expected && WriteReceipt(""));
        }

        private static int WithLedger(Func<int> action)
        {
            try
            {
                using (var mutex = new Mutex(false, LedgerMutex))
                {
                    try { if (!mutex.WaitOne(0)) return Busy; }
                    catch (AbandonedMutexException) { }
                    try { return action(); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (UnauthorizedAccessException) { return LedgerError; }
            catch (WaitHandleCannotBeOpenedException) { return LedgerError; }
        }

        private static bool ReservationMissing
        { get { return retiring || reservation == null || reservation.NeedsRebuild; } }

        private static void SetRetryLocked()
        {
            bool missing = wanted && !suspended && active == null && ReservationMissing;
            if (!missing)
            {
                if (retry != null) retry.Dispose();
                retry = null;
            }
            else if (retry == null)
            {
                Timer timer = null;
                timer = new Timer(delegate { Retry(timer); }, null, RetryInterval, Timeout.InfiniteTimeSpan);
                retry = timer;
            }
        }

        private static void Retry(Timer owner)
        {
            lock (state)
            {
                if (owner == null || retry != owner) return;
                retry.Dispose();
                retry = null;
                AttemptLocked();
            }
        }

        private static void NoteFailureLocked(string operation, int code)
        {
            long now = Stopwatch.GetTimestamp();
            if (now < nextFailureLog) return;
            nextFailureLog = now + Stopwatch.Frequency * 60;
            Logger.Warn(Logger.Format("游戏线程提优系统采样会话失败 操作={0} 错误码={1}",
                "Game thread boost system trace session failed operation={0} code={1}", operation, code));
        }

#if PAVISE_SELFTEST
        internal static bool WaitForRequestsForTest(int timeout)
        {
            return SpinWait.SpinUntil(delegate { lock (requests) return !requestWorkerQueued; }, timeout);
        }
        internal static bool RetryPendingForTest { get { lock (state) return retry != null; } }
        internal static void RetryForTest() { lock (state) Retry(retry); }
#endif
    }
}
