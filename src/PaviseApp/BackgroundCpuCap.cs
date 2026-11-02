using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PaviseApp
{
	internal sealed class BackgroundCpuCap
	{
		private sealed class Lease
		{
			internal int Pid;

			internal decimal Percent;

            internal bool RateConfirmed;

			internal long Creation;

			internal IntPtr Process;

			internal IntPtr Job;

			internal string Name;
		}

		internal const decimal AutomaticPercent = 0.01m;

		internal const decimal ServicePercent = 5m;

		internal const string ReceiptKey = "BackgroundCpuCapJobsV1";

		private readonly object gate = new object();

		private readonly Dictionary<int, Lease> leases = new Dictionary<int, Lease>();

		private readonly Dictionary<int, long> refused = new Dictionary<int, long>();

		private Process guard;

		private Timer heartbeat;

		private bool failed;

		private string prefix;

		private long lastSync;

		internal string Error { get; private set; }

		internal int ActiveCount
		{
			get
			{
				lock (gate)
				{
					int num = 0;
					foreach (Lease value in leases.Values)
					{
						if (value.Percent > 0m)
						{
							num++;
						}
					}
					return num;
				}
			}
		}

        internal KeyValuePair<int, long>[] CaptureAppliedActivity()
        {
            lock (gate)
            {
                var result = new List<KeyValuePair<int, long>>();
                // A stopped guardian releases its jobs; its old receipts are not active caps.
                if (guard == null || guard.HasExited) return result.ToArray();
                foreach (Lease lease in leases.Values)
                    if (lease.RateConfirmed && lease.Percent > 0m && CpuCapNative.IsAlive(lease.Process))
                        result.Add(new KeyValuePair<int, long>(lease.Pid, lease.Creation));
                return result.ToArray();
            }
        }

		internal static bool Recover()
		{
			string value;
			if (!Settings.TryLoadStr("BackgroundCpuCapJobsV1", out value))
			{
				return false;
			}
			if (string.IsNullOrEmpty(value))
			{
				return true;
			}
			List<string> list = new List<string>();
			string[] array = value.Split(new char[1] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string text in array)
			{
				if (!text.StartsWith("Local\\Pavise.CpuCap.V1.", StringComparison.Ordinal) || text.Length > 200)
				{
					list.Add(text);
					continue;
				}
				IntPtr intPtr = CpuCapNative.OpenJobObject(6u, false, text);
				if (intPtr == IntPtr.Zero)
				{
					if (Marshal.GetLastWin32Error() != 2)
					{
						list.Add(text);
					}
					continue;
				}
				try
				{
					if (!CpuCapNative.SetRate(intPtr, 0m))
					{
						list.Add(text);
					}
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
			}
			if (Settings.SaveStr("BackgroundCpuCapJobsV1", string.Join("\n", list.ToArray())))
			{
				return list.Count == 0;
			}
			return false;
		}

		private bool SaveReceipt()
		{
			List<string> list = new List<string>();
			foreach (Lease value in leases.Values)
			{
				list.Add(value.Name);
			}
			return Settings.SaveStr("BackgroundCpuCapJobsV1", string.Join("\n", list.ToArray()));
		}

		private string ReadReply()
		{
			StreamReader reader = guard.StandardOutput;
			Task<string> task = Task.Factory.StartNew(() => reader.ReadLine());
			if (!task.Wait(3000))
			{
				throw new TimeoutException("cpu-cap-guardian");
			}
			return task.Result;
		}

		private bool Command(string command)
		{
			try
			{
				guard.StandardInput.WriteLine(command);
				guard.StandardInput.Flush();
				return ReadReply() == "OK";
			}
			catch
			{
				return false;
			}
		}

		private bool EnsureGuard()
		{
			if (failed)
			{
				return false;
			}
			if (guard != null)
			{
				if (!Command("PING"))
				{
					return Fail("guardian-lost");
				}
				return true;
			}
			if (!Recover())
			{
				return Fail("recovery-pending");
			}
			try
			{
				long creation;
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(new IntPtr(-1), out creation, out cpu, out io))
				{
					return Fail("identity");
				}
				int id;
				using (Process process = Process.GetCurrentProcess())
				{
					id = process.Id;
				}
				prefix = "Local\\Pavise.CpuCap.V1." + Guid.NewGuid().ToString("N") + ".";
				guard = Process.Start(new ProcessStartInfo(typeof(BackgroundCpuCap).Assembly.Location, "--background-cpu-cap-guard " + id + " " + creation + " " + prefix)
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardInput = true,
					RedirectStandardOutput = true
				});
				if (ReadReply() != "READY")
				{
					return Fail("guardian-start");
				}
				heartbeat = new Timer(delegate
				{
					Patrol();
				}, null, 1000, 1000);
				return true;
			}
			catch (Exception ex)
			{
				return Fail(ex.GetType().Name);
			}
		}

		private void Patrol()
		{
			if (!Monitor.TryEnter(gate))
			{
				return;
			}
			try
			{
				if ((ulong)((long)Native.GetTickCount64() - lastSync) > 12000uL && !ReleaseAllLocked())
				{
					Fail("stale-release-pending");
				}
				else if (!ReleaseLocked(GameSessionDetector.ForegroundPid()))
				{
					Fail("foreground-release-pending");
				}
				else if (!failed && guard != null && !Command("PING"))
				{
					Fail("guardian-lost");
				}
			}
			catch
			{
				Fail("patrol");
			}
			finally
			{
				Monitor.Exit(gate);
			}
		}

		private bool Fail(string reason)
		{
			bool flag = !failed || Error != reason;
			Error = reason;
			failed = true;
			ReleaseAllLocked();
			if (guard != null)
			{
				try
				{
					guard.StandardInput.Close();
				}
				catch
				{
				}
			}
			if (flag)
			{
				Logger.Warn(Lang.F("cpucap.failed", reason));
			}
			return false;
		}

		internal bool Sync(Dictionary<int, long> wanted, decimal percent, HashSet<int> servicePids = null)
		{
			lock (gate)
			{
				lastSync = (long)Native.GetTickCount64();
				if (!CpuCapNative.ValidActivePercent(percent))
				{
					return Fail("invalid-rate");
				}
				List<int> list = new List<int>();
				bool flag = true;
				foreach (Lease value4 in leases.Values)
				{
					long value;
					if (!CpuCapNative.IsAlive(value4.Process))
					{
						Native.CloseHandle(value4.Process);
						Native.CloseHandle(value4.Job);
						list.Add(value4.Pid);
					}
					else if (!wanted.TryGetValue(value4.Pid, out value) || value != value4.Creation)
					{
						flag &= ReleaseLocked(value4.Pid);
					}
				}
				foreach (int item in list)
				{
					leases.Remove(item);
				}
				if (!flag)
				{
					return Fail("release-pending");
				}
				if (wanted.Count == 0)
				{
					return true;
				}
				if (!EnsureGuard())
				{
					return false;
				}
				foreach (KeyValuePair<int, long> item2 in wanted)
				{
					decimal num = ((servicePids != null && servicePids.Contains(item2.Key)) ? 5m : percent);
					if (item2.Key == GameSessionDetector.ForegroundPid())
					{
						if (!ReleaseLocked(item2.Key))
						{
							return Fail("foreground-release");
						}
					}
					else
					{
						long value2;
						if (refused.TryGetValue(item2.Key, out value2) && value2 == item2.Value)
						{
							continue;
						}
						Lease value3;
						if (!leases.TryGetValue(item2.Key, out value3))
						{
							if (leases.Count >= 256)
							{
								continue;
							}
							IntPtr intPtr = CpuCapNative.OpenTarget(item2.Key, item2.Value);
							if (intPtr == IntPtr.Zero)
							{
								Refuse(item2, "process-access");
								continue;
							}
							bool inside;
							if (!CpuCapNative.IsProcessInJob(intPtr, IntPtr.Zero, out inside) || inside)
							{
								Native.CloseHandle(intPtr);
								Refuse(item2, "existing-job");
								continue;
							}
							string text = prefix + item2.Key + "." + item2.Value;
							IntPtr intPtr2 = CpuCapNative.Create(text);
							if (intPtr2 == IntPtr.Zero)
							{
								Native.CloseHandle(intPtr);
								Refuse(item2, "create-job");
								continue;
							}
							Lease lease = new Lease();
							lease.Pid = item2.Key;
							lease.Creation = item2.Value;
							lease.Process = intPtr;
							lease.Job = intPtr2;
							lease.Name = text;
							value3 = lease;
							leases.Add(item2.Key, value3);
							if (!SaveReceipt())
							{
								return Fail("receipt-write");
							}
							if (!Command("WATCH " + text + " " + item2.Key + " " + item2.Value))
							{
								return Fail("guardian-watch");
							}
							if (!CpuCapNative.AssignProcessToJobObject(intPtr2, intPtr))
							{
								Refuse(item2, "assign-job");
								continue;
							}
						}
						else if (value3.Creation != item2.Value)
						{
							continue;
						}
						if (value3.Percent == num)
						{
							continue;
						}
						value3.Percent = num;
                        value3.RateConfirmed = false;
						if (!CpuCapNative.SetRate(value3.Job, num))
						{
							Refuse(item2, "rate-write");
							if (!ReleaseLocked(item2.Key))
							{
								return Fail("rollback-pending");
							}
						}
						else
						{
							Logger.Info(Lang.F("cpucap.applied", item2.Key, num));
                            value3.RateConfirmed = true;
						}
					}
				}
				return true;
			}
		}

		private void Refuse(KeyValuePair<int, long> target, string reason)
		{
			refused[target.Key] = target.Value;
			Logger.Info(Lang.F("cpucap.skipped", target.Key, reason));
		}

		private bool ReleaseLocked(int pid)
		{
			Lease value;
			if (!leases.TryGetValue(pid, out value) || value.Percent == 0m)
			{
				return true;
			}
			if (CpuCapNative.IsAlive(value.Process) && !CpuCapNative.SetRate(value.Job, 0m))
			{
				return false;
			}
			value.Percent = 0m;
			return true;
		}

		internal bool Release(int pid)
		{
			lock (gate)
			{
				return ReleaseLocked(pid);
			}
		}

		private bool ReleaseAllLocked()
		{
			bool flag = true;
			foreach (int key in leases.Keys)
			{
				flag &= ReleaseLocked(key);
			}
			return flag;
		}

		internal bool Clear()
		{
			lock (gate)
			{
				return ReleaseAllLocked();
			}
		}

		internal bool Stop()
		{
			lock (gate)
			{
				if (!ReleaseAllLocked())
				{
					return false;
				}
				if (heartbeat != null)
				{
					heartbeat.Dispose();
					heartbeat = null;
				}
				if (guard != null)
				{
					try
					{
						guard.StandardInput.Close();
					}
					catch
					{
					}
					try
					{
						if (!guard.WaitForExit(3000))
						{
							return false;
						}
					}
					catch
					{
						return false;
					}
					guard.Dispose();
					guard = null;
				}
				foreach (Lease value in leases.Values)
				{
					Native.CloseHandle(value.Job);
					Native.CloseHandle(value.Process);
				}
				leases.Clear();
				refused.Clear();
				bool flag = prefix == null || SaveReceipt();
				failed = !flag;
				if (flag)
				{
					Error = null;
				}
				return flag;
			}
		}
	}
}
