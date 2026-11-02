using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PaviseApp
{
	internal sealed class BackgroundFreeze
	{
		private readonly object gate = new object();

		private Process guard;

		private int activeCount;

		private readonly Dictionary<int, long> activeTargets = new Dictionary<int, long>();

		private bool failed;

		private int currentGame;

		private long currentCreation;

		private string automationToken = "";

		internal string Error { get; private set; }

		internal string DiagnosticLine
		{
			get
			{
				lock (gate)
				{
					return "backend=process-state-object supported=" + FreezeNative.Supported + " game=" + currentGame + "/" + currentCreation + " active=" + ActiveCount + " helper=" + GuardState() + " error=" + (Error ?? "none") + " ordinaryJobPercent=0.01 serviceJobPercent=5 altTabThaw=false";
				}
			}
		}

		internal int ActiveCount
		{
			get
			{
				lock (gate)
				{
					return (guard != null && !guard.HasExited) ? activeCount : 0;
				}
			}
		}

		internal bool HasPending
		{
			get
			{
				lock (gate)
				{
					return guard != null && !guard.HasExited;
				}
			}
		}

        internal KeyValuePair<int, long>[] CaptureAppliedActivity()
        {
            lock (gate)
            {
                var result = new List<KeyValuePair<int, long>>();
                if (guard == null || guard.HasExited) return result.ToArray();
                foreach (KeyValuePair<int, long> target in activeTargets)
                    result.Add(target);
                return result.ToArray();
            }
        }

		private string GuardState()
		{
			try
			{
				return (guard == null) ? "none" : (guard.Id + (guard.HasExited ? (":exit=" + guard.ExitCode + ":" + ExitReason(guard.ExitCode)) : ":alive"));
			}
			catch
			{
				return "unavailable";
			}
		}

		private static string ExitReason(int code)
		{
			switch (code)
			{
			case 0:
				return "completed";
			case 1:
				return "startup-validation";
			case 2:
				return "watchdog";
			case 3:
				return "parent-exited";
			case 4:
				return "input-closed";
			case 5:
				return "stale-scan";
			case 6:
				return "invalid-protocol";
			case 7:
				return "guardian-exception";
			default:
				return "terminated-or-os-error";
			}
		}

		private void Note(string message, bool warning = false)
		{
			string msg = (string.IsNullOrEmpty(automationToken) ? "" : ("AUTO [" + automationToken + "] ")) + "[FREEZE] game=" + currentGame + "/" + currentCreation + " helper=" + GuardState() + " " + message;
			if (warning)
			{
				Logger.Warn(msg);
			}
			else
			{
				Logger.Info(msg);
			}
		}

		private void ReadTrace()
		{
			string text = Exchange("REPORT");
			if (text == null || !text.StartsWith("TRACE ", StringComparison.Ordinal))
			{
				throw new IOException("guardian-trace-reply");
			}
			string @string = Encoding.UTF8.GetString(Convert.FromBase64String(text.Substring(6)));
			string[] array = @string.Split('\n');
			foreach (string text2 in array)
			{
				if (text2.StartsWith("refused ", StringComparison.Ordinal) || text2.StartsWith("anchor-rejected ", StringComparison.Ordinal) || text2.StartsWith("trace-truncated ", StringComparison.Ordinal))
				{
					Note(text2, true);
				}
			}
		}

		internal bool Owns(int pid, long creation)
		{
			lock (gate)
			{
				long value;
				return activeTargets.TryGetValue(pid, out value) && value == creation && guard != null && !guard.HasExited;
			}
		}

		private string ReadReply()
		{
			StreamReader reader = guard.StandardOutput;
			Task<string> task = Task.Factory.StartNew(() => reader.ReadLine());
			if (!task.Wait(3000))
			{
				throw new TimeoutException("freeze-guardian");
			}
			return task.Result;
		}

		private bool Start()
		{
			if (failed || !FreezeNative.Supported)
			{
				return false;
			}
			if (guard != null)
			{
				return !guard.HasExited;
			}
			long creation;
			long cpu;
			ulong io;
			if (!Native.QueryProcessSample(new IntPtr(-1), out creation, out cpu, out io))
			{
				return false;
			}
			int id;
			using (Process process = Process.GetCurrentProcess())
			{
				id = process.Id;
			}
			guard = Process.Start(new ProcessStartInfo(typeof(BackgroundFreeze).Assembly.Location, "--background-freeze-guard " + id + " " + creation)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true
			});
			return ReadReply() == "READY";
		}

		private string Exchange(string message)
		{
			Process helper = guard;
			Task<string> task = Task.Factory.StartNew(delegate
			{
				helper.StandardInput.WriteLine(message);
				helper.StandardInput.Flush();
				return helper.StandardOutput.ReadLine();
			});
			if (!task.Wait(3000))
			{
				throw new TimeoutException("freeze-guardian");
			}
			return task.Result;
		}

		internal bool Release(int pid, long creation, string reason = "protection-added")
		{
			lock (gate)
			{
				long value;
				if (!activeTargets.TryGetValue(pid, out value) || value != creation)
				{
					return true;
				}
				try
				{
					if (guard == null || guard.HasExited || Exchange("RELEASE " + pid + " " + creation) == "OK")
					{
						activeTargets.Remove(pid);
						activeCount = activeTargets.Count;
						Logger.Info(Lang.F("freeze.released", 1));
						return true;
					}
				}
				catch (Exception ex)
				{
					Note("release-failed reason=" + reason + " error=" + ex.GetType().Name, true);
				}
				return Clear("release-failed");
			}
		}

		internal HashSet<int> Sync(Dictionary<int, FreezeTarget> wanted, int gamePid, long gameCreation, string sessionToken = "")
		{
			lock (gate)
			{
				HashSet<int> hashSet = new HashSet<int>();
				automationToken = sessionToken;
				currentGame = gamePid;
				currentCreation = gameCreation;
				if (wanted.Count == 0 || gamePid <= 4 || gameCreation <= 0)
				{
					Clear((gamePid <= 4 || gameCreation <= 0) ? "invalid-game-anchor" : "no-candidates");
					return hashSet;
				}
				if (!FreezeNative.Supported || failed)
				{
					return hashSet;
				}
				try
				{
					if (!Start())
					{
						throw new InvalidOperationException("guardian-start");
					}
					if (wanted.Count > 4096)
					{
						throw new InvalidOperationException("target-capacity");
					}
					StringBuilder stringBuilder = new StringBuilder("SYNC " + gamePid + " " + gameCreation);
					foreach (FreezeTarget value in wanted.Values)
					{
						stringBuilder.Append(' ').Append(value.Pid).Append(':')
							.Append(value.Creation)
							.Append(':')
							.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Path)));
					}
					if (stringBuilder.Length > 1048576)
					{
						throw new InvalidOperationException("target-capacity");
					}
					string text = Exchange(stringBuilder.ToString());
					if (text == null || !text.StartsWith("OK ", StringComparison.Ordinal))
					{
						throw new IOException("guardian-reply");
					}
					string[] array = text.Substring(3).Split(',');
					foreach (string text2 in array)
					{
						if (text2.Length != 0)
						{
							int result;
							if (!int.TryParse(text2, out result) || !wanted.ContainsKey(result))
							{
								throw new IOException("guardian-identity");
							}
							hashSet.Add(result);
						}
					}
					if (activeCount != hashSet.Count)
					{
						Logger.Info(Lang.F("freeze.status", hashSet.Count));
					}
					activeTargets.Clear();
					foreach (int item in hashSet)
					{
						activeTargets[item] = wanted[item].Creation;
					}
					activeCount = hashSet.Count;
					Error = null;
					ReadTrace();
				}
				catch (Exception ex)
				{
					failed = true;
					Error = ex.GetType().Name + ":" + ex.Message;
					Clear("backend-failed");
					hashSet.Clear();
					Logger.Warn(Lang.F("freeze.failed", Error));
				}
				return hashSet;
			}
		}

		internal bool Clear(string reason = "policy-release")
		{
			lock (gate)
			{
				if (guard == null)
				{
					activeCount = 0;
					activeTargets.Clear();
					return true;
				}
				try
				{
					if (!guard.HasExited)
					{
						guard.Kill();
						if (!guard.WaitForExit(1500))
						{
							Note("cleanup-pending reason=" + reason, true);
							return false;
						}
					}
					if (activeTargets.Count > 0)
					{
						Logger.Info(Lang.F("freeze.released", activeTargets.Count));
					}
					guard.Dispose();
					guard = null;
					activeCount = 0;
					activeTargets.Clear();
					return true;
				}
				catch (Exception ex)
				{
					Note("cleanup-failed reason=" + reason + " error=" + ex.GetType().Name, true);
					return false;
				}
			}
		}
	}
}
