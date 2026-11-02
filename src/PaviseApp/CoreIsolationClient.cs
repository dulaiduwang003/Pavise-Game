using System;
using System.Diagnostics;
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
				if (failed)
				{
					return false;
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

		private string ReadReply()
		{
			StreamReader output = worker.StandardOutput;
			Task<string> task = Task.Factory.StartNew(() => output.ReadLine());
			if (!task.Wait(5000))
			{
				throw new TimeoutException("isolation-worker");
			}
			return task.Result;
		}

		private bool Command(string command)
		{
			try
			{
				worker.StandardInput.WriteLine(command);
				worker.StandardInput.Flush();
				string text = ReadReply();
				if (text == "OK")
				{
					return true;
				}
				LastError = text ?? "worker-exited";
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
			active = false;
			bool flag = CloseWorker(false);
			ActiveMask = 0uL;
			State = (flag ? "schedule.isolation.failed" : "schedule.isolation.pending");
			Logger.Warn(Lang.T(State) + " (" + reason + ")");
			return false;
		}

		private bool CloseWorker(bool requestStop)
		{
			if (heartbeat != null)
			{
				heartbeat.Dispose();
				heartbeat = null;
			}
			bool flag = true;
			if (worker != null)
			{
				if (requestStop)
				{
					flag = Command("STOP");
				}
				try
				{
					worker.StandardInput.Close();
				}
				catch
				{
				}
				try
				{
					if (!worker.WaitForExit(5000))
					{
						return false;
					}
				}
				catch
				{
					flag = false;
				}
				try
				{
					flag &= worker.ExitCode == 0;
				}
				catch
				{
					flag = false;
				}
				worker.Dispose();
				worker = null;
			}
			if (CoreIsolationWorker.Recover(journal))
			{
				if (!flag)
				{
					return !HasReceipt();
				}
				return true;
			}
			return false;
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
				bool flag = CloseWorker(true);
				if (flag)
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
				return flag;
			}
		}
	}
}
