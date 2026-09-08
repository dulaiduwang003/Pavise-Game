using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PaviseApp
{
    internal sealed class CoreIsolationClient
    {
        private readonly object gate = new object();
        private Process worker;
        private Timer heartbeat;
        private bool active, failed;
        private string argument = CoreIsolationWorker.Argument;
        private ICoreIsolationStore journal = new CoreIsolationSettingsStore();
        internal static volatile string State = "schedule.isolation.idle";
        internal static ulong ActiveMask;
        internal string LastError { get; private set; }
#if PAVISE_SELFTEST
        internal void UseTestWorker() { argument = "--core-isolation-test-worker"; journal = new CoreIsolationTestStore(); }
        internal int TestWorkerPid { get { lock (gate) return worker == null ? 0 : worker.Id; } }
        internal void KillWorkerForTest() { lock (gate) { if (worker != null) { worker.Kill(); worker.WaitForExit(3000); } } }
#endif
        internal bool Ensure(ulong isolation, int pid, long creation, ulong game)
        {
            lock (gate)
            {
                if (failed) return false;
                if (worker == null)
                {
                    try
                    {
                        long selfCreation, cpu; ulong io;
                        if (!Native.QueryProcessSample(new IntPtr(-1), out selfCreation, out cpu, out io)) return Break("identity");
                        worker = Process.Start(new ProcessStartInfo(typeof(CoreIsolationClient).Assembly.Location,
                            argument + " " + Process.GetCurrentProcess().Id + " " + selfCreation)
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
                        if (ReadReply() != "READY") return Break("worker-start");
                        if (!Command("BEGIN " + isolation.ToString("X") + " " + pid + " " + creation + " " + game.ToString("X")))
                            return Break(LastError);
                        active = true; ActiveMask = isolation; State = "schedule.isolation.active";
                        heartbeat = new Timer(delegate { Check(); }, null, 2000, 2000);
                        return true;
                    }
                    catch (Exception e) { return Break(e.GetType().Name); }
                }
                return Command("ALLOW " + pid + " " + creation + " " + game.ToString("X")) || Break(LastError);
            }
        }

        private string ReadReply()
        {
            StreamReader output = worker.StandardOutput;
            var read = Task.Factory.StartNew(() => output.ReadLine());
            if (!read.Wait(5000)) throw new TimeoutException("isolation-worker");
            return read.Result;
        }
        private bool Command(string command)
        {
            try
            {
                worker.StandardInput.WriteLine(command); worker.StandardInput.Flush();
                string reply = ReadReply();
                if (reply == "OK") return true;
                LastError = reply ?? "worker-exited";
            }
            catch (Exception e) { LastError = e.GetType().Name; }
            return false;
        }
        private void Check()
        {
            // Avoid piling up timer callbacks while another command is pending.
            if (!Monitor.TryEnter(gate)) return;
            try { if (active && !Command("PING")) Break(LastError); }
            finally { Monitor.Exit(gate); }
        }
        internal bool Drop(int pid, long creation)
        {
            lock (gate)
            {
                if (!active) return !failed;
                return Command("DROP " + pid + " " + creation) || Break(LastError);
            }
        }
        private bool Break(string reason)
        {
            LastError = reason; failed = true; active = false;
            bool clean = CloseWorker(false);
            ActiveMask = 0;
            State = clean ? "schedule.isolation.failed" : "schedule.isolation.pending";
            Logger.Warn(Lang.T(State) + " (" + reason + ")");
            return false;
        }
        private bool CloseWorker(bool requestStop)
        {
            if (heartbeat != null) { heartbeat.Dispose(); heartbeat = null; }
            bool clean = true;
            if (worker != null)
            {
                if (requestStop) clean = Command("STOP");
                // EOF tells the worker to restore. Never kill the recovery worker.
                try { worker.StandardInput.Close(); } catch { }
                try { if (!worker.WaitForExit(5000)) return false; }
                catch { clean = false; }
                try { clean &= worker.ExitCode == 0; } catch { clean = false; }
                worker.Dispose(); worker = null;
            }
            // A crashed helper leaves a durable receipt. Once its mutex is free,
            // recover it here, including failures after native writes succeeded.
            return CoreIsolationWorker.Recover(journal) && (clean || !HasReceipt());
        }
        private bool HasReceipt()
        {
            string value; return !journal.Read(out value) || !string.IsNullOrEmpty(value);
        }
        internal bool Stop()
        {
            lock (gate)
            {
                active = false;
                bool clean = CloseWorker(true);
                if (clean) { failed = false; ActiveMask = 0; State = "schedule.isolation.idle"; }
                else { failed = true; State = "schedule.isolation.pending"; }
                return clean;
            }
        }
    }
}
