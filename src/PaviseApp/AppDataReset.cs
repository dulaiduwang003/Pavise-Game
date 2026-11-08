using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
	internal static class AppDataReset
	{
		private static readonly HashSet<string> Preferences = BuildPreferences();

		private static readonly HashSet<string> UserFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt", "Pavise.autoignore.txt", "Pavise.profiles.dat", "Pavise.renderer-observations.dat", "Pavise.library-ignore.txn", "Pavise.log", "Pavise.log.old", "Pavise.preview.log",
			"crash.log", "backdrop.img", "donate.png"
		};

		private static readonly string[] AtomicFiles = new string[7] { "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt", "Pavise.autoignore.txt", "Pavise.profiles.dat", "Pavise.renderer-observations.dat", "Pavise.library-ignore.txn" };

		private static HashSet<string> BuildPreferences()
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			hashSet.Add("Lang");
			hashSet.Add("UiLight");
			hashSet.Add("SecurityCompatibilityWarningHidden");
			hashSet.Add("AutoHideOnGame");
			hashSet.Add("DeepTuningLastPage");
			hashSet.Add("BackdropDim");
			hashSet.Add("LogWritesEnabled");
			hashSet.Add("GameModeOn");
			hashSet.Add("GameExitGraceSeconds");
			hashSet.Add("TameOn");
			hashSet.Add("AcModeV1");
			hashSet.Add("GameDvrOff");
			hashSet.Add("GmMmcss");
			hashSet.Add("GmAutoAdd");
			hashSet.Add("GmAudioLowLatV1");
			hashSet.Add("GmRsr");
			hashSet.Add("GmGpuPowerMax");
			hashSet.Add("GpuPrefStageOn");
			hashSet.Add("AppGpuAutoV1");
			hashSet.Add("AppGpuAutoHandledV1");
			hashSet.Add("GmEnglishInputV1");
			hashSet.Add("GmLaptopPerfV1");
			hashSet.Add("AutoLaptopPerformanceV1");
			hashSet.Add(LaptopPerfMode.PreferenceKey);
			hashSet.Add("AutoBackgroundCoreLayoutV1");
			hashSet.Add("AutoDisableFullscreenOptimizations");
			hashSet.Add("GmAggressive");
			hashSet.Add("GmAdaptiveEscalateV1");
			hashSet.Add("GmRenderLane");
			hashSet.Add("FrameCoordination");
			hashSet.Add("GmStandbyCleanerOptionsV1");
			hashSet.Add("GmCacheWarm");
			hashSet.Add("GameLowLatencyOn");
			hashSet.Add("GmWsTrimV1");
			hashSet.Add("GmIdlePolicyV1");
			hashSet.Add("GmPauseServices");
			hashSet.Add("NvVrrWindowed");
			hashSet.Add("NvSmoothMotion");
			hashSet.Add("NvRebar");
			hashSet.Add("NvDlss");
			hashSet.Add("AmdAfmf");
			hashSet.Add("AmdAntiLag");
			hashSet.Add("PowerPlanChoice");
            hashSet.Add("PowerPlanSourceV1");
			hashSet.Add("PowerPlanFailStreak");
			hashSet.Add("CoreLearnIndex");
			hashSet.Add("LastSessionBrief");
			hashSet.Add("EvidenceMode");
			hashSet.Add("IrqSessionProbe");
			hashSet.Add("IrqObserveSkipsV1");
			hashSet.Add("IrqLastCaptureResultV1");
			hashSet.Add("AcProtectedGames");
			hashSet.Add("SelfProtectedProcs");
			hashSet.Add("LolColumnEnabled");
			hashSet.Add("LolColumnCleanupEnabled");
			hashSet.Add("LolColumnHeadlessEnabled");
			hashSet.Add("LolInstallPath");
			hashSet.Add("LolWeGamePath");
			hashSet.Add("WgWeGamePath");
			hashSet.Add("DonateImageId");
			hashSet.Add("AutoFrameCoordinationV1");
			hashSet.Add("FrameCoordinationCaptureSessionsV1");
			hashSet.Add("PerfMonitorEnabledV1");
			hashSet.Add("PerfMonitorEnabledV2");
			hashSet.Add("GameRunReportsEnabledV1");
			hashSet.Add("PerfMonitorDisplayIntervalMsV2");
			hashSet.Add("PerfMonitorTextStyleV2");
			hashSet.Add("PerfMonitorColorsV1");
			hashSet.Add("PerfMonitorLayoutV1");
			hashSet.Add("PerfMonitorMetricsV1");
			hashSet.Add("PerfMonitorIntervalMsV1");
			hashSet.Add("PerfMonitorAppearanceV1");
			hashSet.Add("PerfMonitorHotkeyV1");
			hashSet.Add("PlatformRuntimeRemovedV1");
			hashSet.Add("AutomaticPolicyV1");
			hashSet.Add(CpuMitigationPolicy.ExpandedPreferenceKey);
			HashSet<string> hashSet2 = hashSet;
			foreach (PolicyItem item2 in PolicyCatalog.All)
			{
				if (item2.Key != "AutoTimerTick" && item2.Key != "AutoGlobalTimer")
				{
					hashSet2.Add(item2.Key);
				}
			}
			PerformanceAction[] all = PerformanceActionCatalog.All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (!performanceAction.Environment)
				{
					hashSet2.Add(performanceAction.Key);
				}
			}
			PerformanceAction[] retired = PerformanceActionCatalog.Retired;
			foreach (PerformanceAction performanceAction2 in retired)
			{
				hashSet2.Add(performanceAction2.Key);
			}
			string[] retiredSettings = AutoCores.RetiredSettings;
			foreach (string item in retiredSettings)
			{
				hashSet2.Add(item);
			}
			for (int l = 0; l < 7; l++)
			{
				hashSet2.Add("ModeAccent" + l);
				hashSet2.Add(CoreMotionPreferences.KeyPrefix + l);
			}
			string[] envKeys = GameMode.EnvKeys;
			foreach (string text in envKeys)
			{
				hashSet2.Add("EnvFuse_" + text);
			}
			return hashSet2;
		}

		internal static bool IsUserPreference(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			if (RecoveryRecordKeys.Contains(name) || RecoveryRecordKeys.IsFailureRecord(name))
			{
				return false;
			}
			if (name.Equals("PrivacySetting_EnvPrivacyNtfsMemoryV1Boot", StringComparison.OrdinalIgnoreCase) || name.Equals("PrivacySetting_EnvPrivacyNtfsMemoryV1Phase", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (Preferences.Contains(name) || LaboratorySelection.IsPreference(name))
			{
				return true;
			}
			if (name == "MatchPausePcaV1")
			{
				return true;
			}
			string[] array = new string[6] { "Tame_", "CoreLearn_", "WgShellAuto_", "WgShellFuse_", "PrivacySetting_", "WgShellProfileV2_" };
			foreach (string text in array)
			{
				if (name.Length > text.Length && name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		internal static bool IsUserDataFile(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			if (UserFiles.Contains(name))
			{
				return true;
			}
			string[] atomicFiles = AtomicFiles;
			foreach (string text in atomicFiles)
			{
				if (string.Equals(name, text + ".tmp", StringComparison.OrdinalIgnoreCase) || string.Equals(name, text + ".stale.bak", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				string text2 = text + ".";
				Guid result;
				if (name.Length == text2.Length + 32 + 4 && name.StartsWith(text2, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name.Substring(text2.Length, 32), "N", out result))
				{
					return true;
				}
			}
			if (name.StartsWith("Pavise.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("crash.", StringComparison.OrdinalIgnoreCase))
			{
				return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static string ValidateDataDirectory(string directory)
		{
			if (string.IsNullOrWhiteSpace(directory) || !Paths.IsAbsoluteStartupDataPath(directory))
			{
				throw new IOException("Invalid app data directory");
			}
			string text = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.Equals(text, Path.GetPathRoot(text).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("A drive root is not an app data directory");
			}
			for (string text2 = text; !string.IsNullOrEmpty(text2); text2 = Path.GetDirectoryName(text2))
			{
				FileAttributes attributes;
				try
				{
					attributes = File.GetAttributes(text2);
				}
				catch (FileNotFoundException)
				{
					continue;
				}
				catch (DirectoryNotFoundException)
				{
					continue;
				}
				if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
				{
					throw new IOException("App data directory is redirected or is not a directory");
				}
			}
			return text;
		}

		private static void Progress(Action<string> progress, string message)
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

		private static void Warning(ResetDataReport result, List<string> failures, string message)
		{
			result.Warnings++;
			if (failures.Count < 3)
			{
				failures.Add(message);
			}
		}

		internal static ResetDataReport Execute(string dataDir, Func<bool> stopRuntime, Action<string> progress = null)
		{
			ResetDataReport resetDataReport = new ResetDataReport();
			try
			{
				ValidateDataDirectory(dataDir);
			}
			catch (Exception ex)
			{
				resetDataReport.Warnings = 1;
				resetDataReport.Failure = Lang.T("wipe.pathfail") + " (" + ex.GetType().Name + ")";
				return resetDataReport;
			}
			Progress(progress, PerformancePolicy.Text("正在停止后台任务", "Stopping background tasks"));
			try
			{
				if (stopRuntime == null || !stopRuntime())
				{
					resetDataReport.Warnings = 1;
					resetDataReport.Failure = Lang.T("wipe.stopfail");
					return resetDataReport;
				}
			}
			catch (Exception ex2)
			{
				resetDataReport.Warnings = 1;
				resetDataReport.Failure = Lang.T("wipe.stopfail") + " (" + ex2.GetType().Name + ")";
				return resetDataReport;
			}
			return ClearStoppedData(dataDir, progress);
		}

		internal static ResetDataReport ClearStoppedData(string dataDir, Action<string> progress = null)
		{
			ResetDataReport resetDataReport = new ResetDataReport();
			List<string> list = new List<string>();
			string text;
			try
			{
				text = ValidateDataDirectory(dataDir);
			}
			catch (Exception ex)
			{
				resetDataReport.Warnings = 1;
				resetDataReport.Failure = Lang.T("wipe.pathfail") + " (" + ex.GetType().Name + ")";
				return resetDataReport;
			}
			if (LegacyPurge.HasHardThreadRecoveryDebt(text) || !GameNvCanonicalStore.SafeToClear(text)
                || LaboratoryFileFilter.HasReceipt || LaboratoryAudioEffects.HasReceipt || LaboratoryClockRepair.HasUnfinishedRecovery || LaboratoryManualActions.Busy)
			{
				resetDataReport.Warnings = 1;
				resetDataReport.Failure = "Original runtime recovery is pending; original records and preferences retained";
				return resetDataReport;
			}
			Settings.SuspendWritesForReset();
			Logger.SuspendWritesForReset();
			Progress(progress, PerformancePolicy.Text("正在重置软件设置", "Resetting application preferences"));
			try
			{
				int removed;
				string failure;
				if (!Settings.ClearUserPreferencesForReset(IsUserPreference, out removed, out failure))
				{
					Warning(resetDataReport, list, PerformancePolicy.Text("部分软件设置未能删除", "Some preferences could not be removed") + " | " + failure);
				}
			}
			catch (Exception ex2)
			{
				Warning(resetDataReport, list, "Preference cleanup | " + ex2.GetType().Name);
			}
			Progress(progress, PerformancePolicy.Text("正在清理游戏库和记录", "Clearing game library and records"));
			try
			{
				ValidateDataDirectory(text);
				if (Directory.Exists(text))
				{
					string[] fileSystemEntries = Directory.GetFileSystemEntries(text, "*", SearchOption.TopDirectoryOnly);
					foreach (string path in fileSystemEntries)
					{
						if (IsUserDataFile(Path.GetFileName(path)))
						{
							try
							{
								ValidateDataDirectory(text);
								DeleteFile(path);
								resetDataReport.Files++;
							}
							catch (Exception ex3)
							{
								Warning(resetDataReport, list, Path.GetFileName(path) + " (" + ex3.GetType().Name + ")");
							}
						}
					}
				}
			}
			catch (Exception ex4)
			{
				Warning(resetDataReport, list, "User data cleanup | " + ex4.GetType().Name);
			}
			resetDataReport.Completed = resetDataReport.Warnings == 0;
			resetDataReport.Failure = ((list.Count == 0) ? null : string.Join("; ", list.ToArray()));
			Progress(progress, PerformancePolicy.Text("软件数据清理完成", "Application data cleanup finished"));
			return resetDataReport;
		}

		private static void DeleteFile(string path)
		{
			int num = 0;
			while (true)
			{
				try
				{
					FileAttributes attributes;
					try
					{
						attributes = File.GetAttributes(path);
					}
					catch (FileNotFoundException)
					{
						break;
					}
					catch (DirectoryNotFoundException)
					{
						break;
					}
					if ((attributes & FileAttributes.Directory) != 0)
					{
						throw new IOException("Expected an owned file, found a directory");
					}
					if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
					{
						File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
					}
					File.Delete(path);
					try
					{
						File.GetAttributes(path);
					}
					catch (FileNotFoundException)
					{
						break;
					}
					catch (DirectoryNotFoundException)
					{
						break;
					}
					throw new IOException("File is still present");
				}
				catch (IOException)
				{
					if (num >= 2)
					{
						throw;
					}
					Thread.Sleep(100);
				}
				num++;
			}
		}
	}
}
