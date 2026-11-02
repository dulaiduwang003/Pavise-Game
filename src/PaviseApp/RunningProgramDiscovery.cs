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
			Process[] array = null;
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
				array = Process.GetProcesses();
				Dictionary<string, RunningProgram> dictionary = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < array.Length; i++)
				{
					if (IsCanceled(canceled))
					{
						return null;
					}
					Process process2 = array[i];
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
						string text;
						try
						{
							text = Native.ImagePath(intPtr);
						}
						finally
						{
							Native.CloseHandle(intPtr);
						}
						string text2 = GameSessionDetector.ImageNameFromVerifiedPath(text);
						if (!(whitelist ? IsWhitelistCandidate(text2, text, windowsRoot)
							: GameSessionDetector.IsLibraryCandidate(text2, text, windowsRoot)))
						{
							continue;
						}
						RunningProgram value;
						if (!dictionary.TryGetValue(text, out value))
						{
							RunningProgram runningProgram = new RunningProgram();
							runningProgram.Path = text;
							runningProgram.ProcessName = text2;
							runningProgram.Title = text2;
							value = runningProgram;
							dictionary.Add(text, value);
						}
						if (!value.ProcessIds.Contains(id2))
						{
							value.ProcessIds.Add(id2);
							try
							{
								long num = Math.Max(0L, process2.WorkingSet64);
								value.Memory = ((num > long.MaxValue - value.Memory) ? long.MaxValue : (value.Memory + num));
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
						array[i] = null;
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
				if (array != null)
				{
					Process[] array2 = array;
					foreach (Process process3 in array2)
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
				string text = ((!string.IsNullOrWhiteSpace(versionInfo.FileDescription)) ? versionInfo.FileDescription : versionInfo.ProductName);
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text.Trim();
				}
			}
			catch
			{
			}
			return fallback;
		}
	}
}
