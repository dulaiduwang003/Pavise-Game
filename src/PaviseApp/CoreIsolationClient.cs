using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class CoreIsolationClient
	{
		private readonly object gate = new object();

		private Process worker;
        private GameAffinityJobProtocol protocol;
        private bool stopSent, waitedForExit;
        private string applyScope;
        private static readonly object phaseGate = new object();
        private static readonly HashSet<string> failedScopes = new HashSet<string>(StringComparer.Ordinal);
        private static long automaticSession;
        internal static void SyncAutomaticSession(long session)
        {
            lock (phaseGate)
            {
                if (session <= automaticSession) return;
                automaticSession = session; failedScopes.Clear();
            }
            CoreIsolationWorker.SyncAutomaticSession(session);
        }

		private Timer heartbeat;

		private bool active;

		private bool failed;

		private string argument = "--core-isolation-worker";

		private ICoreIsolationStore journal = new CoreIsolationSettingsStore();

		internal static volatile string State = "schedule.isolation.idle";

		internal static ulong ActiveMask;

		internal string LastError { get; private set; }

		internal bool Ensure(ulong isolation, int pid, long creation, ulong game, bool manual = false)
		{
			lock (gate)
			{
                lock (phaseGate)
                {
                    applyScope = automaticSession + "|" + pid + "|" + creation + "|" + isolation + "|" + game + "|" + manual;
                    if (failed || failedScopes.Contains(applyScope)) return false;
                }
				if (worker == null)
				{
					try
					{
						long creation2;
						long cpu;
						ulong io;
						if (!Native.QueryProcessSample(new IntPtr(-1), out creation2, out cpu, out io))
						{
							return Break("identity");
						}
						worker = Process.Start(new ProcessStartInfo(typeof(CoreIsolationClient).Assembly.Location, argument + " " + Process.GetCurrentProcess().Id + " " + creation2)
						{
							UseShellExecute = false,
							CreateNoWindow = true,
							RedirectStandardInput = true,
							RedirectStandardOutput = true
						});
                        stopSent = waitedForExit = false;
                        protocol = new GameAffinityJobProtocol(worker);
						if (ReadReply() != "READY")
						{
							return Break("worker-start");
						}
						if (!Command((manual ? "MANUAL " : "BEGIN ") + isolation.ToString("X") + " " + pid + " " + creation + " " + game.ToString("X")))
						{
							return Break(LastError);
						}
						active = true;
						ActiveMask = isolation;
						State = "schedule.isolation.active";
						heartbeat = new Timer(delegate
						{
							Check();
						}, null, 2000, 2000);
						return true;
					}
					catch (Exception ex)
					{
						return Break(ex.GetType().Name);
					}
				}
				return Command("ALLOW " + pid + " " + creation + " " + game.ToString("X")) || Break(LastError);
			}
		}

        private string ReadReply() { return protocol.ReadReply(5000); }

		private bool Command(string command)
		{
			try
			{
				worker.StandardInput.WriteLine(command);
				worker.StandardInput.Flush();
				string reply = ReadReply();
				if (reply == "OK")
				{
					return true;
				}
				LastError = reply ?? "worker-exited";
			}
			catch (Exception ex)
			{
				LastError = ex.GetType().Name;
			}
			return false;
		}

		private void Check()
		{
			if (!Monitor.TryEnter(gate))
			{
				return;
			}
			try
			{
				if (active && !Command("PING"))
				{
					Break(LastError);
				}
			}
			finally
			{
				Monitor.Exit(gate);
			}
		}

		internal bool Drop(int pid, long creation)
		{
			lock (gate)
			{
				if (!active)
				{
					return !failed;
				}
				return Command("DROP " + pid + " " + creation) || Break(LastError);
			}
		}

		private bool Break(string reason)
		{
			LastError = reason;
			failed = true;
            lock (phaseGate) { if (applyScope != null) failedScopes.Add(applyScope); }
			active = false;
			bool closeWorkerSucceeded = CloseWorker(false);
            if (closeWorkerSucceeded) ActiveMask = 0uL;
			State = (closeWorkerSucceeded ? "schedule.isolation.failed" : "schedule.isolation.pending");
			Logger.Warn(Lang.T(State) + " (" + reason + ")");
			return false;
		}

        private bool CloseWorker(bool requestStop)
        {
            if (heartbeat != null) { heartbeat.Dispose(); heartbeat = null; }
            if (worker != null)
            {
                // Revoked output has one lifetime drain reader. STOP is a one-way
                // signal: no late READY/OK may be mistaken for a recovery reply.
                if (protocol != null) protocol.Revoke();
                if (!stopSent)
                {
                    stopSent = true;
                    try { worker.StandardInput.WriteLine("STOP"); worker.StandardInput.Flush(); } catch { }
                    try { worker.StandardInput.Close(); } catch { }
                }
                try
                {
                    if (!worker.HasExited)
                    {
                        if (waitedForExit) { ReportWorkerPending(); return false; }
                        waitedForExit = true;
                        if (!worker.WaitForExit(5000)) { ReportWorkerPending(); return false; }
                    }
                    if (protocol != null) { protocol.Dispose(); protocol = null; }
                    worker.Dispose(); worker = null;
                }
                catch { ReportWorkerPending(); return false; }
            }
            // The guardian must have exited before this process may take recovery.
            // Recover retains its engine and finite budget across every client caller.
            return CoreIsolationWorker.Recover(journal);
        }

        private void ReportWorkerPending()
        {
            // The guardian owns restoration until it exits. Observe that fact
            // without issuing another protocol command or parent restore.
            RecoveryAttemptTrace.Exhausted(delegate
            {
                lock (gate) { try { return worker == null || worker.HasExited; } catch { return false; } }
            });
        }

		private bool HasReceipt()
		{
			string value;
			if (journal.Read(out value))
			{
				return !string.IsNullOrEmpty(value);
			}
			return true;
		}

		internal bool Stop()
		{
			lock (gate)
			{
				active = false;
				bool closeWorkerSucceeded = CloseWorker(true);
				if (closeWorkerSucceeded)
				{
					failed = false;
					ActiveMask = 0uL;
					State = "schedule.isolation.idle";
				}
				else
				{
					failed = true;
					State = "schedule.isolation.pending";
				}
				return closeWorkerSucceeded;
			}
		}
	}
}
