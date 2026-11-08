using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class UninstallMode
	{
		public static UninstallReport ExecuteInteractive(string dataDir, string exePath, Func<bool> stopRuntime, Action<string> progress)
		{
			return ExecuteInteractive(dataDir, exePath, stopRuntime, progress, TaskHelper.RemoveStartupTaskForUninstall);
		}

		internal static UninstallReport ExecuteInteractive(string dataDir, string exePath, Func<bool> stopRuntime, Action<string> progress, Func<bool> removeStartupTask)
		{
			UninstallReport uninstallReport = new UninstallReport();
			if (!LegacyPurge.IsSafeInteractiveDataRoot(dataDir) || string.IsNullOrWhiteSpace(exePath))
			{
				uninstallReport.Failure = Lang.T("wipe.pathfail");
				return uninstallReport;
			}
			Report(progress, PerformancePolicy.Text("停止对局守护", "Stopping game protection"));
			try
			{
				uninstallReport.RuntimeStopped = stopRuntime != null && stopRuntime();
			}
			catch
			{
				uninstallReport.RuntimeStopped = false;
			}
			if (!uninstallReport.RuntimeStopped)
			{
				uninstallReport.Failure = Lang.T("wipe.stopfail");
				return uninstallReport;
			}
			Report(progress, PerformancePolicy.Text("还原 Pavise 的系统改动", "Restoring changes made by Pavise"));
			List<string> list;
			try
			{
				list = LegacyPurge.RestorePersistent(dataDir, progress);
			}
			catch
			{
				List<string> list2 = new List<string>();
				list2.Add("restore");
				list = list2;
			}
			if (list == null)
			{
				List<string> list3 = new List<string>();
				list3.Add("restore");
				list = list3;
			}
			uninstallReport.Warnings += list.Count;
			if (LegacyPurge.HasHardThreadRecoveryDebt(dataDir) || !GameNvCanonicalStore.SafeToClear(dataDir))
			{
				uninstallReport.Failure = "Original runtime recovery is pending; application and records retained";
				uninstallReport.Warnings++;
				return uninstallReport;
			}
			Report(progress, PerformancePolicy.Text("清除开机启动", "Removing startup registration"));
			try
			{
				uninstallReport.StartupRemoved = removeStartupTask != null && removeStartupTask();
			}
			catch
			{
				uninstallReport.StartupRemoved = false;
			}
			if (!uninstallReport.StartupRemoved)
			{
				uninstallReport.Warnings++;
			}
			Report(progress, PerformancePolicy.Text("清理软件配置与记录", "Clearing app settings and records"));
			if (list.Count == 0)
			{
				try
				{
					int files;
					int warnings;
					uninstallReport.Cleared = LegacyPurge.ClearRestoredData(dataDir, out files, out warnings);
					uninstallReport.Files = files;
					uninstallReport.Warnings += warnings;
				}
				catch
				{
					uninstallReport.Warnings++;
				}
			}
			else
			{
				try
				{
					ResetDataReport resetDataReport = AppDataReset.ClearStoppedData(dataDir, progress);
					uninstallReport.Cleared = resetDataReport.Completed;
					uninstallReport.Files = resetDataReport.Files;
					uninstallReport.Warnings += resetDataReport.Warnings;
				}
				catch
				{
					uninstallReport.Warnings++;
				}
			}
			Report(progress, PerformancePolicy.Text("准备退出并删除程序", "Preparing to exit and remove the executable"));
			return uninstallReport;
		}

		private static void Report(Action<string> progress, string message)
		{
			try
			{
				if (progress != null)
				{
					progress(message);
				}
			}
			catch
			{
			}
		}

		public static UninstallReport Execute(string dataDir, string exeDir, Func<bool> stopRuntime)
		{
			return Execute(dataDir, exeDir, stopRuntime, TaskHelper.RemoveStartupTaskForUninstall);
		}

		internal static UninstallReport Execute(string dataDir, string exeDir, Func<bool> stopRuntime, Func<bool> removeStartupTask)
		{
			UninstallReport uninstallReport = new UninstallReport();
			if (string.IsNullOrWhiteSpace(dataDir))
			{
				uninstallReport.Failure = Lang.T("wipe.pathfail");
				return uninstallReport;
			}
			try
			{
				if (stopRuntime == null || !stopRuntime())
				{
					uninstallReport.Failure = Lang.T("wipe.stopfail");
					return uninstallReport;
				}
			}
			catch (Exception ex)
			{
				uninstallReport.Failure = Lang.T("wipe.stopfail") + " (" + ex.GetType().Name + ")";
				return uninstallReport;
			}
			if (LegacyPurge.HasHardThreadRecoveryDebt(dataDir) || !GameNvCanonicalStore.SafeToClear(dataDir))
			{
				uninstallReport.Failure = "Original runtime recovery is pending; application and records retained";
				uninstallReport.Warnings++;
				return uninstallReport;
			}
			try
			{
				if (removeStartupTask == null || !removeStartupTask())
				{
					uninstallReport.Failure = Lang.F("uninstall.taskfail", TaskHelper.LastSchtasksError ?? "PaviseAuto");
					return uninstallReport;
				}
			}
			catch (Exception ex2)
			{
				uninstallReport.Failure = Lang.F("uninstall.taskfail", "PaviseAuto (" + ex2.GetType().Name + ")");
				return uninstallReport;
			}
			int files;
			try
			{
				string unrestored;
				if (!LegacyPurge.WipeAll(dataDir, true, Lang.T("btn.uninstall"), out files, out unrestored))
				{
					uninstallReport.Failure = unrestored;
					return uninstallReport;
				}
			}
			catch (Exception ex3)
			{
				uninstallReport.Failure = Lang.T("wipe.cleanupfail") + " (" + ex3.GetType().Name + ")";
				return uninstallReport;
			}
			uninstallReport.Files = files;
			uninstallReport.Cleared = true;
			uninstallReport.Leftovers = 0;
			return uninstallReport;
		}

		internal static List<string> LegacyDataDirs(string roaming, string local, string common, string exeDir)
		{
			List<string> list = new List<string>();
			string[] textEntries = new string[3]
			{
				Join(roaming, "Aegis"),
				Join(local, "Pavise"),
				Join(common, "Pavise")
			};
			foreach (string entryText in textEntries)
			{
				if (entryText != null && (string.IsNullOrEmpty(exeDir) || !IsSameOrParent(entryText, exeDir)))
				{
					list.Add(entryText);
				}
			}
			return list;
		}

		private static string Join(string root, string name)
		{
			if (string.IsNullOrEmpty(root))
			{
				return null;
			}
			try
			{
				return Path.GetFullPath(Path.Combine(root, name));
			}
			catch
			{
				return null;
			}
		}

		private static bool IsSameOrParent(string parent, string child)
		{
			try
			{
				string getFullPathTrimmed = Path.GetFullPath(parent).TrimEnd('\\', '/');
				string getFullPathTrimmed2 = Path.GetFullPath(child).TrimEnd('\\', '/');
				if (string.Equals(getFullPathTrimmed, getFullPathTrimmed2, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				return getFullPathTrimmed2.StartsWith(getFullPathTrimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || getFullPathTrimmed2.StartsWith(getFullPathTrimmed + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return true;
			}
		}

		public static bool Run(string resultPath)
		{
			StringBuilder stringBuilder = new StringBuilder();
			bool runSucceeded = false;
			Mutex shutdownGate = null;
			try
			{
				Paths.Init();
				Lang.Init();
				string directoryName = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
				UninstallReport uninstallReport = Execute(Paths.Data, directoryName, delegate
				{
					shutdownGate = StopOtherInstances();
					return shutdownGate != null;
				});
				runSucceeded = uninstallReport.Cleared;
				stringBuilder.Append("cleared=").Append(runSucceeded ? "1" : "0").Append("\r\n");
				stringBuilder.Append("files=").Append(uninstallReport.Files).Append("\r\n");
				stringBuilder.Append("leftovers=").Append(uninstallReport.Leftovers).Append("\r\n");
				if (!string.IsNullOrEmpty(uninstallReport.Failure))
				{
					stringBuilder.Append("unrestored=").Append(uninstallReport.Failure).Append("\r\n");
				}
			}
			catch (Exception ex)
			{
				stringBuilder.Append("cleared=0\r\nerror=").Append(ex.GetType().Name).Append(' ')
					.Append(ex.Message)
					.Append("\r\n");
				runSucceeded = false;
			}
			finally
			{
				if (shutdownGate != null)
				{
					try
					{
						shutdownGate.ReleaseMutex();
					}
					catch
					{
					}
					shutdownGate.Dispose();
				}
			}
			try
			{
				if (!string.IsNullOrEmpty(resultPath))
				{
					File.WriteAllText(resultPath, stringBuilder.ToString(), new UTF8Encoding(false));
				}
			}
			catch
			{
			}
			return runSucceeded;
		}

		private static Mutex StopOtherInstances()
		{
			try
			{
				using (EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting("Global\\Pavise_Exit"))
				{
					eventWaitHandle.Set();
				}
			}
			catch
			{
			}
			return AcquireShutdownGate("Global\\Pavise_SingleInstance", 10000);
		}

		internal static Mutex AcquireShutdownGate(string mutexName, int timeoutMilliseconds)
		{
			Mutex mutex = null;
			try
			{
				mutex = new Mutex(false, mutexName);
				try
				{
					if (mutex.WaitOne(timeoutMilliseconds))
					{
						return mutex;
					}
				}
				catch (AbandonedMutexException)
				{
					return mutex;
				}
			}
			catch
			{
			}
			if (mutex != null)
			{
				mutex.Dispose();
			}
			return null;
		}
	}
}
