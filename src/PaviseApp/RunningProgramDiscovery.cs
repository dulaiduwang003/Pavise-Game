using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
	internal static class RunningProgramDiscovery
	{
		internal static List<RunningProgram> Scan(Func<bool> canceled)
		{
			return Scan(canceled, false);
		}

		internal static List<RunningProgram> ScanForWhitelist(Func<bool> canceled)
		{
			return Scan(canceled, true);
		}

		private static List<RunningProgram> Scan(Func<bool> canceled, bool whitelist)
		{
			Process[] runningProcesses = null;
			try
			{
				if (IsCanceled(canceled))
				{
					return null;
				}
				int sessionId;
				int id;
				using (Process process = Process.GetCurrentProcess())
				{
					sessionId = process.SessionId;
					id = process.Id;
				}
				if (sessionId < 0)
				{
					return null;
				}
				string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
				string windowsRoot = (string.IsNullOrEmpty(folderPath) ? "C:\\Windows\\" : (folderPath.TrimEnd('\\') + "\\"));
				bool succeeded;
				HashSet<int> hashSet = RunningProgramWindows.Capture(out succeeded);
				if (!succeeded)
				{
					return null;
				}
				if (IsCanceled(canceled))
				{
					return null;
				}
				runningProcesses = Process.GetProcesses();
				Dictionary<string, RunningProgram> dictionary = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < runningProcesses.Length; i++)
				{
					if (IsCanceled(canceled))
					{
						return null;
					}
					Process process2 = runningProcesses[i];
					try
					{
						int id2 = process2.Id;
						if (id2 <= 4 || id2 == id || !hashSet.Contains(id2) || process2.SessionId != sessionId)
						{
							continue;
						}
						IntPtr intPtr = Native.OpenProcess(4096, false, id2);
						if (intPtr == IntPtr.Zero)
						{
							continue;
						}
						string pathValue;
						try
						{
							pathValue = Native.ImagePath(intPtr);
						}
						finally
						{
							Native.CloseHandle(intPtr);
						}
						string processNameValue = GameSessionDetector.ImageNameFromVerifiedPath(pathValue);
						if (!(whitelist ? IsWhitelistCandidate(processNameValue, pathValue, windowsRoot)
							: GameSessionDetector.IsLibraryCandidate(processNameValue, pathValue, windowsRoot)))
						{
							continue;
						}
						RunningProgram value;
						if (!dictionary.TryGetValue(pathValue, out value))
						{
							RunningProgram runningProgram = new RunningProgram();
							runningProgram.Path = pathValue;
							runningProgram.ProcessName = processNameValue;
							runningProgram.Title = processNameValue;
							value = runningProgram;
							dictionary.Add(pathValue, value);
						}
						if (!value.ProcessIds.Contains(id2))
						{
							value.ProcessIds.Add(id2);
							try
							{
								long workingSet64Limit = Math.Max(0L, process2.WorkingSet64);
								value.Memory = ((workingSet64Limit > long.MaxValue - value.Memory) ? long.MaxValue : (value.Memory + workingSet64Limit));
							}
							catch
							{
							}
						}
					}
					catch
					{
					}
					finally
					{
						runningProcesses[i] = null;
						if (process2 != null)
						{
							try
							{
								process2.Dispose();
							}
							catch
							{
							}
						}
					}
				}
				List<RunningProgram> list = new List<RunningProgram>(dictionary.Values);
				foreach (RunningProgram item in list)
				{
					if (IsCanceled(canceled))
					{
						return null;
					}
					item.Title = TitleOf(item.Path, item.ProcessName);
				}
				return IsCanceled(canceled) ? null : list;
			}
			catch
			{
				return null;
			}
			finally
			{
				if (runningProcesses != null)
				{
					Process[] processItems = runningProcesses;
					foreach (Process process3 in processItems)
					{
						if (process3 != null)
						{
							try
							{
								process3.Dispose();
							}
							catch
							{
							}
						}
					}
				}
			}
		}

		internal static bool IsWhitelistCandidate(string name, string path, string windowsRoot)
		{
			// Whitelisting protects ordinary apps too; game-only exclusions do not apply.
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)
				|| !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
				|| GameSessionDetector.IsNonGameRole(name, path)) return false;
			return string.IsNullOrEmpty(windowsRoot)
				|| !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsCanceled(Func<bool> canceled)
		{
			if (canceled != null)
			{
				return canceled();
			}
			return false;
		}

		private static string TitleOf(string path, string fallback)
		{
			try
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
				string fileDescription = ((!string.IsNullOrWhiteSpace(versionInfo.FileDescription)) ? versionInfo.FileDescription : versionInfo.ProductName);
				if (!string.IsNullOrWhiteSpace(fileDescription))
				{
					return fileDescription.Trim();
				}
			}
			catch
			{
			}
			return fallback;
		}
	}
}
