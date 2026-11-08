using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class LegacyPurge
	{
		private const string RegKey = "Software\\PaviseAuto";

		[ThreadStatic]
		private static Action<string> restoreProgress;

		private static readonly string[] DataFiles = new string[15]
		{
			"Pavise.irq-sessions.dat", "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt", "Pavise.autoignore.txt", "Pavise.profiles.dat", "Pavise.renderer-observations.dat", "Pavise.library-ignore.txn", "Pavise.log", "Pavise.log.old",
			"crash.log", "Pavise.preview.log", "Pavise.freeze.state", "Pavise.suppression.state", "backdrop.img"
		};

		private static readonly string[] AtomicDataFiles = new string[3] { "Pavise.whitelist.txt", "Pavise.autoignore.txt", "Pavise.suppression.state" };

		private static readonly string[] UniqueTempDataFiles = new string[5] { "Pavise.profiles.dat", "Pavise.irq-sessions.dat", "Pavise.renderer-observations.dat", "Pavise.library-ignore.txn", "Pavise.autoignore.txt" };

		private static void ReportRestoreProgress(string item)
		{
			try
			{
				if (restoreProgress != null)
				{
					restoreProgress(item);
				}
			}
			catch
			{
			}
		}

		private static List<string> RestoreOrHook(string dataDir, Action<string> progress = null)
		{
			Action<string> action = restoreProgress;
			restoreProgress = progress;
			try
			{
				List<string> list = RestoreEverything(dataDir);
				List<string> list2 = list;
				if (list2 == null)
				{
					List<string> list3 = new List<string>();
					list3.Add("系统还原未返回确认结果");
					list2 = list3;
				}
				return list2;
			}
			catch (Exception ex)
			{
				List<string> list4 = new List<string>();
				list4.Add("系统还原异常: " + ex.GetType().Name);
				return list4;
			}
			finally
			{
				restoreProgress = action;
			}
		}

		public static List<string> RestorePersistent(string dataDir)
		{
			return RestoreOrHook(dataDir);
		}

		internal static List<string> RestorePersistent(string dataDir, Action<string> progress)
		{
			return RestoreOrHook(dataDir, progress);
		}

		internal static bool HasHardThreadRecoveryDebt(string dataDir)
		{
			try
			{
				return HardThreadFileStore.CanonicalRootHasRecoveryDebt(Path.Combine(Path.GetFullPath(dataDir), "CriticalThreadHardAffinityV1"));
			}
			catch
			{
				return true;
			}
		}

		internal static bool ClearRestoredData(string dataDir, out int files, out int warnings)
		{
			files = 0;
			warnings = 0;
			string root;
			bool exists;
			string error;
			if (!TryDataRoot(dataDir, out root, out exists, out error))
			{
				warnings = 1;
				return false;
			}
			if (!IsSafeInteractiveDataRoot(root))
			{
				warnings = 1;
				return false;
			}
			if (HasHardThreadRecoveryDebt(root) || !GameNvCanonicalStore.SafeToClear(root) || LaboratoryManualActions.HasRecoveryRecords || LaboratoryManualActions.Busy)
			{
				warnings = 1;
				return false;
			}
			Settings.SuspendWritesForReset();
			Logger.SuspendWritesForReset();
			bool tryDeleteDataTree = (IsRoamingDataDir(root) ? TryDeleteDataTree(root, out files, out error) : DeleteDataFiles(root, out files, out error));
			if (!tryDeleteDataTree)
			{
				warnings++;
			}
			bool deleteRegistryTreeSucceeded = DeleteRegistryTree();
			if (!deleteRegistryTreeSucceeded)
			{
				warnings++;
			}
			if (tryDeleteDataTree)
			{
				return deleteRegistryTreeSucceeded;
			}
			return false;
		}

		internal static bool IsSafeInteractiveDataRoot(string dataDir)
		{
			string root;
			bool exists;
			string error;
			if (!TryDataRoot(dataDir, out root, out exists, out error))
			{
				return false;
			}
			try
			{
				string rootSnapshot = root;
				while (!string.IsNullOrEmpty(rootSnapshot))
				{
					if ((File.Exists(rootSnapshot) || Directory.Exists(rootSnapshot)) && (File.GetAttributes(rootSnapshot) & FileAttributes.ReparsePoint) != 0)
					{
						return false;
					}
					rootSnapshot = Path.GetDirectoryName(rootSnapshot);
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool DeleteRegistryTree()
		{
			try
			{
				using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software", true))
				{
					if (registryKey != null)
					{
						bool matchesRegistryKey2;
						using (RegistryKey registryKey2 = registryKey.OpenSubKey("PaviseAuto"))
						{
							matchesRegistryKey2 = registryKey2 != null;
						}
						if (matchesRegistryKey2)
						{
							registryKey.DeleteSubKeyTree("PaviseAuto", false);
							Settings.NoteExternalMutation();
						}
					}
				}
				using (RegistryKey registryKey3 = Registry.CurrentUser.OpenSubKey("Software\\PaviseAuto"))
				{
					return registryKey3 == null;
				}
			}
			catch
			{
				return false;
			}
		}

		private static void Step(string name, Func<bool> restore, List<string> failed)
		{
			ReportRestoreProgress(name);
			try
			{
				if (!restore())
				{
					failed.Add(name);
				}
			}
			catch
			{
				failed.Add(name);
			}
		}

		private static void StepIf(string name, Func<bool> enabled, Func<bool> disable, List<string> failed)
		{
			ReportRestoreProgress(name);
			try
			{
				if (enabled() && !disable())
				{
					failed.Add(name);
				}
			}
			catch
			{
				failed.Add(name);
			}
		}

		private static List<string> RestoreEverything(string dataDir)
		{
			List<string> list = new List<string>();
			ReportRestoreProgress(PerformancePolicy.Text("等待环境配置停止", "Waiting for environment configuration to stop"));
			if (!PrivacyControl.CloseWork(8000) || !PerformanceTuning.CloseEnvironmentWork(8000))
			{
				list.Add(PerformancePolicy.Text("环境配置尚未停止，恢复记录已保留，请稍后重试", "Environment configuration has not stopped; recovery records retained. Retry shortly."));
				return list;
			}
			Step("NVIDIA per-game profile recovery", () => GameNvPersistentRecovery.Restore(dataDir), list);
			StepIf(Lang.T("cpucap.title"), RetiredBackgroundCpuCap.HasReceipts, RetiredBackgroundCpuCap.Retire, list);
			Step(Lang.T("t.gamemodeenv.35"), PowerPlan.Restore, list);
			Step(Lang.T("t.legacypurge.1"), PowerPlan.RemoveCreatedPlan, list);
			Step(Lang.T("t.legacypurge.2"), PowerPlan.RemoveManagedPlan, list);
			StepIf(Lang.T("t.legacypurge.3"), PowerPlan.HasParkResidue, PowerPlan.RestoreParkState, list);
			Step(Lang.T("t.gamemodeenv.3"), UpdatePause.Restore, list);
			Step("Game DVR", GameDvr.Restore, list);
			Step("MMCSS", Mmcss.Restore, list);
			Step(Lang.T("t.gamemodeenv.5"), DisplayAwake.Restore, list);
			Step(Lang.T("gm.dwmboost"), DwmBoost.Restore, list);
			StepIf(Lang.T("gm.rsssteer"), () => RssSteer.HasResidue, RssSteer.Restore, list);
			Step("Laboratory system experiments", LaboratoryManualActions.RestoreAllForReset, list);
			Step(Lang.T("t.legacypurge.6"), PresenceQos.Restore, list);
			Step(Lang.T("t.legacypurge.7"), PowerOverlay.Restore, list);
			Step(Lang.T("t.gamemodeenv.6"), GpuPowerMax.Restore, list);
			Step(Lang.T("t.gamemodeenv.1"), DoTweak.Restore, list);
			Step(Lang.T("t.legacypurge.8"), SvcPause.Restore, list);
			StepIf(Lang.T("gm.pausesvc"), () => OptionalServicePause.HasResidue, OptionalServicePause.Restore, list);
			StepIf(Lang.T("set.timertick"), () => TimerTickTweak.OwnsState, TimerTickTweak.Restore, list);
			StepIf(Lang.T("set.gtimer"), () => GlobalTimerResTweak.OwnsState, GlobalTimerResTweak.Restore, list);
			StepIf(Lang.T("set.memcompress"), () => MemCompressTweak.OwnsState, MemCompressTweak.Restore, list);
			Step("Extreme", delegate
			{
				bool extremeUnlockedEnabled = Settings.Save("ExtremeUnlocked", false);
				extremeUnlockedEnabled &= Settings.SaveStr("ExtremeUnlockTicks", "");
				extremeUnlockedEnabled &= Settings.SaveStr("ExtremeOptOut", "");
				return extremeUnlockedEnabled & Settings.SaveStr("ExtremeEnvApplied", "");
			}, list);
			StepIf(Lang.T("set.gpupref"), () => GpuPrefStage.HasResidue, () => GpuPrefStage.Restore() || GpuPrefStage.AbandonUnprovableForReset(), list);
			StepIf(Lang.T("set.apppref"), () => AppGpuPreferences.HasResidue, () => AppGpuPreferences.RestoreAll() || AppGpuPreferences.AbandonUnprovableForReset(), list);
			Step(Lang.T("t.legacypurge.10"), NetTweak.Restore, list);
			StepIf("QoS", NetworkAffinityTweak.HasResidue, NetworkAffinityTweak.Disable, list);
			Step(Lang.T("t.legacypurge.11"), QuantumTweak.Restore, list);
			Step("VBS", VbsTweak.Restore, list);
			StepIf(Lang.T("set.gpuclock"), () => GpuClockLock.HasResidue, GpuClockLock.Restore, list);
			StepIf(Lang.T("set.nvvrr"), () => NvVrrWindowed.HasResidue, NvVrrWindowed.Restore, list);
			StepIf(Lang.T("set.intel.endurance"), () => IntelEndurance.HasResidue, IntelEndurance.Restore, list);
			StepIf(Lang.T("gm.laptopperf"), () => LaptopPerfMode.HasResidue, LaptopPerfMode.Restore, list);
			StepIf(Lang.T("t.linkmetric.name"), () => LinkMetricTweak.RepairedByPavise, LinkMetricTweak.Restore, list);
			Step(Lang.T("t.legacypurge.16"), InputMythTweak.Restore, list);
			Step(Lang.T("t.legacypurge.19"), AdlxTweaks.PurgeResidue, list);
			Step(Lang.T("t.legacypurge.21"), MsiModeTweak.Restore, list);
			Step(Lang.T("t.legacypurge.22"), IfeoBoost.RestoreAll, list);
			Step("FrameCoordination", FrameCoordination.HealFromCrash, list);
			Step("RenderLane", delegate
			{
				RenderLane.HealFromCrash();
				return !RenderLane.HasResidue();
			}, list);
			Step("MemShield 内存驻留未确认还原：请先退出仍在运行的游戏，再重启重试", () => MemShield.HealFromCrash() && !MemShield.HasResidue(), list);
			Step("AC hard affinity", () => new AntiCheatAffinityLease().Recover(), list);
			Step("DisplaySolo 显示拓扑未还原", delegate
			{
				if (DisplaySolo.HealFromCrash())
				{
					return !DisplaySolo.HasResidue();
				}
				string displaySoloSnapValue = Settings.LoadStr("DisplaySoloSnap", "");
				uint result;
				return displaySoloSnapValue.Length != 0 && (!uint.TryParse(displaySoloSnapValue, out result) || (result != 2 && result != 4 && result != 8)) && Settings.SaveStr("DisplaySoloSnap", "");
			}, list);
			StepIf("HAGS", HagsTweak.HasResidue, HagsTweak.Restore, list);
			StepIf("Windowed optimizations", () => DirectXGlobalPreference.Windowed.HasResidue, DirectXGlobalPreference.Windowed.Restore, list);
			StepIf("Windows VRR", () => DirectXGlobalPreference.Vrr.HasResidue, DirectXGlobalPreference.Vrr.Restore, list);
			StepIf("FSO", FsoTweak.HasResidue, FsoTweak.RestoreAll, list);
			StepIf("DPI", DpiTweak.HasResidue, DpiTweak.RestoreAll, list);
			StepIf(Lang.T("gm.pausemaint"), () => MaintenancePause.HasResidue, MaintenancePause.Restore, list);
			StepIf("AMD SAM", AmdSamTweak.HasResidue, AmdSamTweak.Restore, list);
			StepIf("FTH", () => FthTweak.RepairedByPavise, FthTweak.Restore, list);
			StepIf("CFG", CfgOffTweak.HasResidue, CfgOffTweak.RestoreAll, list);
			list.AddRange(PerformanceTuning.RestoreAllFailures(ReportRestoreProgress));
			StepIf(Lang.T("t.legacypurge.29"), () => IrqRelocate.HasResidue, IrqRelocate.Revert, list);
			StepIf("自动中断编排 已下架的注册表钉核清退", () => IrqAutoPilot.HasResidue, delegate
			{
				if (!IrqAutoPilot.RevertAll())
				{
					return false;
				}
				IrqAutoPilot.ClearForReset();
				return !IrqAutoPilot.HasResidue;
			}, list);
			StepIf(Lang.T("irqpin.prio.name"), () => IrqPriorityTweak.HasResidue, IrqPriorityTweak.RestoreAll, list);
			StepIf(Lang.T("irqcheck.purge"), () => GpuIrqCheck.HasResidue, GpuIrqCheck.RestoreAll, list);
			string[] textEntries = new string[12]
			{
				"pstate", "prerender", "lowlatcpl", "ullenable", "smooth", "shadercache", "ansel", "rebarfeat", "rebaropt", "rebarsize",
				"dlssovr", "dlsspreset"
			};
			foreach (string entryText in textEntries)
			{
				string i = entryText;
				Step("NVIDIA Profile:" + i, delegate
				{
					NvDrsTweaks.RestoreKind(i);
					return !NvDrsTweaks.HasSnapshotFor(i);
				}, list);
			}
			Step(Lang.T("schedule.isolation"), () => CoreIsolationWorker.Recover(new CoreIsolationSettingsStore()), list);
			Step(Lang.T("t.legacypurge.28"), delegate
			{
				string journal = Path.Combine(dataDir, "Pavise.suppression.state");
				string jobReason;
				return !HasHardThreadRecoveryDebt(dataDir) && GameAffinityJobLease.Recover(out jobReason) && RestoreProcessResidue(CrashGuard.HealFromCrash, delegate
				{
					SuppressionCore.HealFromCrash(journal);
				}, CrashGuard.HasPending, () => SuppressionCore.HasPendingJournalFile(journal));
			}, list);
			return list;
		}

		private static bool RestoreProcessResidue(Action healBoost, Action healSuppression, Func<bool> boostPending, Func<bool> suppressionPending)
		{
			healBoost();
			healSuppression();
			bool boostPendingSucceeded = boostPending();
			bool suppressionPendingSucceeded = suppressionPending();
			if (!boostPendingSucceeded && !suppressionPendingSucceeded)
			{
				return true;
			}
			Logger.Warn(Lang.T("log.legacypurge.45"));
			return false;
		}

		private static bool IsOwnedDataName(string name)
		{
			string[] dataFiles = DataFiles;
			foreach (string b in dataFiles)
			{
				if (string.Equals(name, b, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			string[] atomicDataFiles = AtomicDataFiles;
			foreach (string filePath in atomicDataFiles)
			{
				if (string.Equals(name, filePath + ".tmp", StringComparison.OrdinalIgnoreCase) || string.Equals(name, filePath + ".stale.bak", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			string[] uniqueTempDataFiles = UniqueTempDataFiles;
			foreach (string filePath2 in uniqueTempDataFiles)
			{
				string filePath2Offset = filePath2 + ".";
				Guid result;
				if (name.Length == filePath2Offset.Length + 32 + 4 && name.StartsWith(filePath2Offset, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name.Substring(filePath2Offset.Length, 32), "N", out result))
				{
					return true;
				}
			}
			Guid result2;
			if (name.Length == 43 && name.StartsWith("Pavise_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name.Substring(7, 32), "N", out result2))
			{
				return true;
			}
			if (name.StartsWith("Pavise.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("crash.", StringComparison.OrdinalIgnoreCase))
			{
				return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		internal static bool IsRoamingDataDir(string dir)
		{
			if (string.IsNullOrEmpty(dir))
			{
				return false;
			}
			try
			{
				string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				if (string.IsNullOrEmpty(folderPath))
				{
					return false;
				}
				if (!Same(Path.Combine(folderPath, "PaviseAuto"), dir))
				{
					return false;
				}
				if (!Directory.Exists(dir))
				{
					return false;
				}
				return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0;
			}
			catch
			{
				return false;
			}
		}

		private static bool Same(string a, string b)
		{
			string a2 = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string b2 = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return string.Equals(a2, b2, StringComparison.OrdinalIgnoreCase);
		}

		private static bool TryAttributes(string path, out FileAttributes attributes, out bool exists, out string error)
		{
			attributes = (FileAttributes)0;
			exists = false;
			error = null;
			try
			{
				attributes = File.GetAttributes(path);
				exists = true;
				return true;
			}
			catch (FileNotFoundException)
			{
				return true;
			}
			catch (DirectoryNotFoundException)
			{
				return true;
			}
			catch (Exception ex3)
			{
				error = path + " (" + ex3.GetType().Name + ")";
				return false;
			}
		}

		private static bool TryDataRoot(string dir, out string root, out bool exists, out string error)
		{
			root = null;
			exists = false;
			error = null;
			try
			{
				bool allConditionsMet = !string.IsNullOrEmpty(dir) && dir.Length >= 3 && dir[1] == ':' && (dir[2] == Path.DirectorySeparatorChar || dir[2] == Path.AltDirectorySeparatorChar);
				bool matchesStartsWith = !string.IsNullOrEmpty(dir) && dir.StartsWith("\\\\", StringComparison.Ordinal);
				if (string.IsNullOrWhiteSpace(dir) || (!allConditionsMet && !matchesStartsWith))
				{
					error = "数据目录不是明确的绝对路径";
					return false;
				}
				root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (Same(root, Path.GetPathRoot(root)))
				{
					error = "拒绝清理磁盘根目录";
					return false;
				}
				FileAttributes attributes;
				if (!TryAttributes(root, out attributes, out exists, out error))
				{
					return false;
				}
				if (!exists)
				{
					return true;
				}
				if ((attributes & FileAttributes.Directory) == 0)
				{
					error = "数据路径不是目录: " + root;
					return false;
				}
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					error = "拒绝清理重解析点数据目录: " + root;
					return false;
				}
				return true;
			}
			catch (Exception ex)
			{
				error = "数据路径校验失败: " + ex.GetType().Name;
				return false;
			}
		}

		private static void AddFailure(List<string> failures, string detail)
		{
			if (failures.Count < 8)
			{
				failures.Add(detail);
			}
			else if (failures.Count == 8)
			{
				failures.Add("另有未能清理的项目");
			}
		}

		private static bool CheckChildParents(string root, string path, out string error)
		{
			error = null;
			try
			{
				string fullPath = Path.GetFullPath(path);
				if (!Same(fullPath, root) && !fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					error = "拒绝越界路径: " + path;
					return false;
				}
				string rootSnapshot = (Same(fullPath, root) ? root : Path.GetDirectoryName(fullPath));
				while (!string.IsNullOrEmpty(rootSnapshot))
				{
					FileAttributes attributes;
					bool exists;
					if (!TryAttributes(rootSnapshot, out attributes, out exists, out error))
					{
						return false;
					}
					if (exists && ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0))
					{
						error = "数据目录结构已改变: " + rootSnapshot;
						return false;
					}
					if (Same(rootSnapshot, root))
					{
						return true;
					}
					rootSnapshot = Path.GetDirectoryName(rootSnapshot);
				}
				error = "无法核对数据目录边界: " + path;
				return false;
			}
			catch (Exception ex)
			{
				error = "数据目录边界校验失败: " + ex.GetType().Name;
				return false;
			}
		}

		private static void DeleteDataFile(string root, string path, ref int files, List<string> failures)
		{
			string error;
			FileAttributes attributes;
			bool exists;
			if (!CheckChildParents(root, path, out error))
			{
				AddFailure(failures, error);
			}
			else if (!TryAttributes(path, out attributes, out exists, out error))
			{
				AddFailure(failures, error);
			}
			else
			{
				if (!exists)
				{
					return;
				}
				if ((attributes & FileAttributes.Directory) != 0)
				{
					AddFailure(failures, "预期文件却发现目录，已保留: " + path);
					return;
				}
				try
				{
					if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
					{
						File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
					}
					File.Delete(path);
				}
				catch (Exception ex)
				{
					AddFailure(failures, path + " (" + ex.GetType().Name + ")");
					return;
				}
				if (!TryAttributes(path, out attributes, out exists, out error))
				{
					AddFailure(failures, error);
				}
				else if (exists)
				{
					AddFailure(failures, "文件仍存在: " + path);
				}
				else
				{
					files++;
				}
			}
		}

		private static bool DeleteDataFiles(string root, out int files, out string error)
		{
			files = 0;
			error = null;
			List<string> list = new List<string>();
			string root2;
			bool exists;
			if (!TryDataRoot(root, out root2, out exists, out error))
			{
				return false;
			}
			if (HasHardThreadRecoveryDebt(root2) || !GameNvCanonicalStore.SafeToClear(root2) || LaboratoryManualActions.HasRecoveryRecords || LaboratoryManualActions.Busy)
			{
				error = "Original runtime recovery is pending; data retained";
				return false;
			}
			if (!exists)
			{
				return true;
			}
			try
			{
				string[] fileSystemEntries = Directory.GetFileSystemEntries(root2);
				foreach (string path in fileSystemEntries)
				{
					if (IsOwnedDataName(Path.GetFileName(path)))
					{
						DeleteDataFile(root2, path, ref files, list);
					}
				}
			}
			catch (Exception ex)
			{
				AddFailure(list, root2 + " (" + ex.GetType().Name + ")");
			}
			string root3;
			string error2;
			if (!TryDataRoot(root2, out root3, out exists, out error2))
			{
				AddFailure(list, error2);
			}
			else if (exists)
			{
				try
				{
					string[] fileSystemEntries2 = Directory.GetFileSystemEntries(root3);
					foreach (string filePath in fileSystemEntries2)
					{
						if (IsOwnedDataName(Path.GetFileName(filePath)))
						{
							AddFailure(list, "残留: " + filePath);
						}
					}
				}
				catch (Exception ex2)
				{
					AddFailure(list, root3 + " (" + ex2.GetType().Name + ")");
				}
			}
			error = ((list.Count == 0) ? null : string.Join("; ", list.ToArray()));
			return list.Count == 0;
		}

		internal static bool TryDeleteDataTree(string dir, out int files, out string error)
		{
			files = 0;
			string root;
			bool exists;
			if (!TryDataRoot(dir, out root, out exists, out error))
			{
				return false;
			}
			if (HasHardThreadRecoveryDebt(root) || !GameNvCanonicalStore.SafeToClear(root) || LaboratoryManualActions.HasRecoveryRecords || LaboratoryManualActions.Busy)
			{
				error = "Original runtime recovery is pending; data retained";
				return false;
			}
			string[] textEntries = new string[8]
			{
				Path.GetTempPath(),
				AppDomain.CurrentDomain.BaseDirectory,
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
				Environment.GetFolderPath(Environment.SpecialFolder.Personal),
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				Environment.GetFolderPath(Environment.SpecialFolder.Windows)
			};
			foreach (string entryText in textEntries)
			{
				if (!string.IsNullOrEmpty(entryText) && Same(root, entryText))
				{
					error = "拒绝整树删除公共目录: " + root;
					return false;
				}
			}
			if (!exists)
			{
				return true;
			}
			List<string> list = new List<string>();
			DeleteDataTreeLevel(root, root, ref files, list);
			FileAttributes attributes;
			string error2;
			if (!TryAttributes(root, out attributes, out exists, out error2))
			{
				AddFailure(list, error2);
			}
			else if (exists)
			{
				AddFailure(list, "数据目录仍存在: " + root);
			}
			error = ((list.Count == 0) ? null : string.Join("; ", list.ToArray()));
			return list.Count == 0;
		}

		internal static int DeleteDataTree(string dir)
		{
			int files;
			string error;
			TryDeleteDataTree(dir, out files, out error);
			return files;
		}

		private static void DeleteDataTreeLevel(string root, string dir, ref int files, List<string> failures)
		{
			string error;
			FileAttributes attributes;
			bool exists;
			if (!CheckChildParents(root, dir, out error))
			{
				AddFailure(failures, error);
			}
			else if (!TryAttributes(dir, out attributes, out exists, out error))
			{
				AddFailure(failures, error);
			}
			else
			{
				if (!exists)
				{
					return;
				}
				if ((attributes & FileAttributes.Directory) == 0)
				{
					AddFailure(failures, "数据目录结构已改变: " + dir);
					return;
				}
				if ((attributes & FileAttributes.ReparsePoint) == 0)
				{
					try
					{
						string[] fileSystemEntries = Directory.GetFileSystemEntries(dir);
						foreach (string filePath in fileSystemEntries)
						{
							FileAttributes attributes2;
							if (!TryAttributes(filePath, out attributes2, out exists, out error))
							{
								AddFailure(failures, error);
							}
							else if (exists)
							{
								if ((attributes2 & FileAttributes.Directory) != 0)
								{
									DeleteDataTreeLevel(root, filePath, ref files, failures);
								}
								else
								{
									DeleteDataFile(root, filePath, ref files, failures);
								}
							}
						}
					}
					catch (Exception ex)
					{
						AddFailure(failures, dir + " (" + ex.GetType().Name + ")");
					}
				}
				if (!CheckChildParents(root, dir, out error))
				{
					AddFailure(failures, error);
				}
				else if (!TryAttributes(dir, out attributes, out exists, out error))
				{
					AddFailure(failures, error);
				}
				else
				{
					if (!exists)
					{
						return;
					}
					if ((attributes & FileAttributes.Directory) != 0)
					{
						try
						{
							if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
							{
								File.SetAttributes(dir, attributes & ~FileAttributes.ReadOnly);
							}
							Directory.Delete(dir, false);
							return;
						}
						catch (Exception ex2)
						{
							AddFailure(failures, dir + " (" + ex2.GetType().Name + ")");
							return;
						}
					}
					AddFailure(failures, "数据目录结构已改变: " + dir);
				}
			}
		}

		internal static bool DeleteOwnedFiles(string dir, out int files, out string error)
		{
			return DeleteDataFiles(dir, out files, out error);
		}

		public static bool WipeAll(string dataDir, bool includeSettings, string why, out int files, out string unrestored, Action<string> progress = null)
		{
			files = 0;
			unrestored = null;
			string root;
			bool exists;
			string error;
			if (!TryDataRoot(dataDir, out root, out exists, out error))
			{
				unrestored = "数据目录校验失败: " + error;
				return false;
			}
			Logger.Log(why + Lang.T("log.legacypurge.37"));
			List<string> list = RestoreOrHook(root, progress);
			if (list.Count > 0)
			{
				unrestored = PerformancePolicy.Text("系统还原未完成：", "System restoration incomplete:") + "\n" + string.Join("\n", list.ToArray());
				Logger.Log(why + Lang.T("log.legacypurge.38") + list.Count + Lang.T("log.legacypurge.31") + unrestored + Lang.T("log.legacypurge.39"));
				return false;
			}
			bool matchesIncludeSettings = includeSettings && IsRoamingDataDir(root);
			Logger.Log(why + " 系统还原已确认，开始清理本机数据");
			try
			{
				if (progress != null)
				{
					progress(PerformancePolicy.Text("清理本机配置与记录", "Clearing local settings and records"));
				}
			}
			catch
			{
			}
			if (includeSettings)
			{
				Settings.SuspendWritesForReset();
				Logger.SuspendWritesForReset();
			}
			if (!(matchesIncludeSettings ? TryDeleteDataTree(root, out files, out error) : DeleteDataFiles(root, out files, out error)))
			{
				unrestored = "数据清理未完成，注册表设置已保留: " + error;
				return false;
			}
			if (includeSettings && !DeleteRegistryTree())
			{
				unrestored = "注册表清理未完成: HKCU\\Software\\PaviseAuto";
				return false;
			}
			return true;
		}
	}
}
