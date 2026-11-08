using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class ThreadLaneProbe
	{
		internal struct Lane
		{
			public int Tid;

			public long Cpu100Ns;

			public double SharePercent;

			public bool CanQuery;

			public bool CanSet;

			public int QueryError;

			public int SetError;
		}

		internal struct Report
		{
			public int Pid;

			public string Name;

			public int ThreadCount;

			public int SampledThreads;

			public double WallSeconds;

			public double TotalCores;

			public List<Lane> Top;

			public int LeaderStableRounds;

			public int Rounds;

			public string Error;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetThreadTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

		private static long CpuOf(int tid, out bool canQuery, out int queryError)
		{
			canQuery = false;
			queryError = 0;
			IntPtr intPtr = Native.OpenThread(2048, false, tid);
			if (intPtr == IntPtr.Zero)
			{
				queryError = Marshal.GetLastWin32Error();
				return -1L;
			}
			try
			{
				long creation;
				long exit;
				long kernel;
				long user;
				if (!GetThreadTimes(intPtr, out creation, out exit, out kernel, out user))
				{
					queryError = Marshal.GetLastWin32Error();
					return -1L;
				}
				canQuery = true;
				return kernel + user;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private static bool CanOpenForSet(int tid, out int error)
		{
			error = 0;
			IntPtr intPtr = Native.OpenThread(1024, false, tid);
			if (intPtr == IntPtr.Zero)
			{
				error = Marshal.GetLastWin32Error();
				return false;
			}
			Native.CloseHandle(intPtr);
			return true;
		}

		public static Report Run(int pid, int rounds, int intervalMs)
		{
			Report report = default(Report);
			report.Pid = pid;
			report.Top = new List<Lane>();
			report.Rounds = rounds;
			Report result = report;
			try
			{
				using (Process process = Process.GetProcessById(pid))
				{
					result.Name = process.ProcessName;
				}
			}
			catch (Exception ex)
			{
				result.Error = ex.Message;
				return result;
			}
			Dictionary<int, long> dictionary = new Dictionary<int, long>();
			Dictionary<int, long> dictionary2 = new Dictionary<int, long>();
			Dictionary<int, int> dictionary3 = new Dictionary<int, int>();
			int leaderStableRoundsValue = 0;
			int runValue = 0;
			long ticks = DateTime.UtcNow.Ticks;
			for (int i = 0; i < rounds; i++)
			{
				Dictionary<int, long> dictionary4 = new Dictionary<int, long>();
				try
				{
					using (Process process2 = Process.GetProcessById(pid))
					{
						ProcessThreadCollection threads = process2.Threads;
						result.ThreadCount = threads.Count;
						foreach (ProcessThread item2 in threads)
						{
							bool canQuery;
							int queryError;
							long value = CpuOf(item2.Id, out canQuery, out queryError);
							if (!canQuery)
							{
								dictionary3[item2.Id] = queryError;
								continue;
							}
							dictionary4[item2.Id] = value;
							if (!dictionary.ContainsKey(item2.Id))
							{
								dictionary[item2.Id] = value;
							}
						}
					}
				}
				catch (Exception ex2)
				{
					result.Error = ex2.Message;
					break;
				}
				if (dictionary2.Count > 0)
				{
					int runValue2 = 0;
					long numericValue = -1L;
					foreach (KeyValuePair<int, long> item3 in dictionary4)
					{
						long value2;
						if (dictionary2.TryGetValue(item3.Key, out value2))
						{
							long remainingValue = item3.Value - value2;
							if (remainingValue > numericValue)
							{
								numericValue = remainingValue;
								runValue2 = item3.Key;
							}
						}
					}
					if (runValue2 != 0 && runValue2 == runValue)
					{
						leaderStableRoundsValue++;
					}
					runValue = runValue2;
				}
				dictionary2 = dictionary4;
				if (i < rounds - 1)
				{
					Thread.Sleep(intervalMs);
				}
			}
			result.WallSeconds = (double)(DateTime.UtcNow.Ticks - ticks) / 10000000.0;
			result.LeaderStableRounds = leaderStableRoundsValue;
			result.SampledThreads = dictionary2.Count;
			List<Lane> list = new List<Lane>();
			long numericValue2 = 0L;
			foreach (KeyValuePair<int, long> item4 in dictionary2)
			{
				long value3;
				if (dictionary.TryGetValue(item4.Key, out value3))
				{
					long remainingValue2 = item4.Value - value3;
					if (remainingValue2 < 0)
					{
						remainingValue2 = 0L;
					}
					numericValue2 += remainingValue2;
					list.Add(new Lane
					{
						Tid = item4.Key,
						Cpu100Ns = remainingValue2
					});
				}
			}
			list.Sort((Lane a, Lane b) => b.Cpu100Ns.CompareTo(a.Cpu100Ns));
			double scaledWallSeconds = result.WallSeconds * 10000000.0;
			result.TotalCores = ((scaledWallSeconds > 0.0) ? ((double)numericValue2 / scaledWallSeconds) : 0.0);
			for (int j = 0; j < list.Count && j < 8; j++)
			{
				Lane item = list[j];
				item.SharePercent = ((numericValue2 > 0) ? ((double)item.Cpu100Ns * 100.0 / (double)numericValue2) : 0.0);
				item.CanQuery = true;
				item.CanSet = CanOpenForSet(item.Tid, out item.SetError);
				result.Top.Add(item);
			}
			foreach (KeyValuePair<int, int> item5 in dictionary3)
			{
				if (result.Top.Count < 12)
				{
					result.Top.Add(new Lane
					{
						Tid = item5.Key,
						QueryError = item5.Value
					});
					continue;
				}
				break;
			}
			return result;
		}

		public static string Format(Report r)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.1"));
			stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.2") + (r.Name ?? "?") + " pid " + r.Pid);
			if (!string.IsNullOrEmpty(r.Error))
			{
				stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.3") + r.Error);
			}
			stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.4") + r.ThreadCount + Lang.T("t.threadlaneprobe.5") + r.SampledThreads + Lang.T("t.threadlaneprobe.6") + r.Rounds + Lang.T("t.threadlaneprobe.7") + r.WallSeconds.ToString("F1") + "s");
			stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.8") + r.TotalCores.ToString("F2") + Lang.T("t.threadlaneprobe.9"));
			stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.10") + r.LeaderStableRounds + Lang.T("t.threadlaneprobe.11") + Math.Max(0, r.Rounds - 2));
			stringBuilder.AppendLine();
			stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.12"));
			stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.13"));
			foreach (Lane item in r.Top)
			{
				if (!item.CanQuery)
				{
					object[] instanceItems = new object[5] { "  ", null, null, null, null };
					int tid = item.Tid;
					instanceItems[1] = tid.ToString().PadRight(9);
					instanceItems[2] = Lang.T("t.threadlaneprobe.14");
					instanceItems[3] = item.QueryError;
					instanceItems[4] = " ";
					stringBuilder.AppendLine(string.Concat(instanceItems));
				}
				else
				{
					string[] textEntries = new string[5] { "  ", null, null, null, null };
					int tid2 = item.Tid;
					textEntries[1] = tid2.ToString().PadRight(9);
					double sharePercent = item.SharePercent;
					textEntries[2] = sharePercent.ToString("F1").PadLeft(8);
					textEntries[3] = Lang.T("t.threadlaneprobe.15").PadRight(9);
					textEntries[4] = (item.CanSet ? Lang.T("t.threadlaneprobe.16") : (Lang.T("t.threadlaneprobe.17") + item.SetError + " "));
					stringBuilder.AppendLine(string.Concat(textEntries));
				}
			}
			stringBuilder.AppendLine();
			bool formatReady = false;
			bool formatReady2 = false;
			foreach (Lane item2 in r.Top)
			{
				if (item2.CanSet)
				{
					formatReady = true;
				}
				if (item2.CanQuery && !item2.CanSet)
				{
					formatReady2 = true;
				}
			}
			stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.18") + ((formatReady && !formatReady2) ? Lang.T("t.threadlaneprobe.19") : (formatReady ? Lang.T("t.threadlaneprobe.20") : Lang.T("t.threadlaneprobe.21"))));
			return stringBuilder.ToString();
		}
	}
}
