using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
	internal static class PerformanceTuning
	{
		internal const string EnvironmentBundleKey = "EnvPerformanceBundleV1";

		internal const string StorageGroupKey = "EnvStoragePowerGroupV1";

		internal const string EnvironmentVbsKey = "EnvVbsDisabledV1";

		private static readonly object gate = new object();
		private static readonly object selectionSignatureGate = new object();
		private static int selectionSignatureGeneration = -1;
		private static bool selectionSignatureVisuals;
		private static string selectionSignature;

		private static readonly Dictionary<string, int> recoveryFailures = new Dictionary<string, int>(StringComparer.Ordinal);
		private static long recoverySessionSerial;
		internal static long RecoveryCycleSerial { get { return Interlocked.Read(ref recoverySessionSerial); } }

		internal static void BeginRecoveryCycle(long gameSessionSerial)
		{
			lock (SystemConfigurationGate.Sync)
			{
				// Late work from an old session must not refill a newer session's budget.
				if (gameSessionSerial > recoverySessionSerial)
				{
					Interlocked.Exchange(ref recoverySessionSerial, gameSessionSerial);
					PrivacyControl.BeginRecoveryCycle(gameSessionSerial);
				}
			}
		}

		internal static bool RestoreWithBudget(PerformanceActionStore.Receipt receipt, int maximumFailures, Func<bool> restore)
		{
			if (receipt == null || restore == null) return false;
			lock (SystemConfigurationGate.Sync)
			{
				// Mutable receipt diagnostics/commit flags are intentionally excluded.
				string key = recoverySessionSerial + "|" + RecoveryPart(receipt.Id) + RecoveryPart(receipt.Target)
					+ RecoveryPart(receipt.Original) + RecoveryPart(receipt.Expected) + RecoveryPart(receipt.VisualOwner);
				int failures;
				recoveryFailures.TryGetValue(key, out failures);
				Func<bool> phaseChanged = delegate
				{
					lock (SystemConfigurationGate.Sync)
					{
						int remaining;
						return !key.StartsWith(recoverySessionSerial + "|", StringComparison.Ordinal)
							|| !recoveryFailures.TryGetValue(key, out remaining) || remaining < maximumFailures;
					}
				};
				if (failures >= maximumFailures) { RecoveryAttemptTrace.Exhausted(phaseChanged); return false; }
				bool restored = false;
				try { restored = restore(); return restored; }
				finally
				{
					if (restored) recoveryFailures.Remove(key);
					else
					{
						recoveryFailures[key] = ++failures;
						if (failures >= maximumFailures) RecoveryAttemptTrace.Exhausted(phaseChanged);
						else RecoveryAttemptTrace.DeferredUntil(DateTime.UtcNow.Ticks);
						Logger.Warn("Performance recovery: action=" + receipt.Id + " target=" + receipt.Target
							+ " phase=" + recoverySessionSerial + " attempt=" + failures + "/" + maximumFailures
							+ (failures >= maximumFailures ? "; active retries stopped; receipt retained" : "; receipt retained"));
					}
				}
			}
		}

		private static string RecoveryPart(string value)
		{
			return (value == null ? -1 : value.Length) + ":" + value;
		}

		internal static bool RestorePerformanceReceipt(PerformanceActionStore.Receipt receipt, Func<bool> prepare = null)
		{
			bool restored = RestoreWithBudget(receipt, 5, () => (prepare == null || prepare()) && PerformanceActionBackend.Restore(receipt));
			if (restored) ActionFailureStore.CancelApply(receipt.Id, receipt.Target);
			return restored;
		}

		internal static bool ReadRecoveryReceipts(out List<PerformanceActionStore.Receipt> receipts)
		{
			List<PerformanceActionStore.Receipt> read = null;
			bool ok = RestoreWithBudget(new PerformanceActionStore.Receipt { Id = "recovery-journal", Target = "performance" }, 5,
				delegate { read = PerformanceActionStore.All(); return true; });
			receipts = read;
			return ok;
		}

		private static readonly HashSet<string> busy = new HashSet<string>();

		private static readonly Dictionary<string, string> states = new Dictionary<string, string>();

		private static volatile HashSet<string> storageOwned = new HashSet<string>(StringComparer.Ordinal);

		internal static volatile bool EnvironmentWorkClosing;

#if PAVISE_SELFTEST
		internal static Func<bool> CpuMitigationsSupportedForTest;
		internal static Func<WaitCallback, bool> EnvironmentQueueForTest;
		internal static Func<string> BootForTest;
#endif

		internal static string EnvironmentBundleTitle
		{
			get
			{
				return PerformancePolicy.Text("系统性能增强 · 需重启", "System performance boost · restart required");
			}
		}

		internal static string EnvironmentBundleDetail
		{
			get
			{
				return PerformancePolicy.Text("统一控制关闭 VBS（含内存完整性和 hypervisor）、CPU 软件缓解、NTFS 最后访问时间和音频电源管理，共四项。开启或恢复后需重启电脑；关闭时恢复原配置。不适用项自动跳过。关闭 hypervisor 会影响 WSL2、Hyper-V 等虚拟化功能。存储电源管理已移到下方单独的高级选项。", "Combines four settings: disabling VBS (including Memory Integrity and the hypervisor), CPU software mitigations, NTFS last-access updates and audio power management. Restart after enabling or restoring. Switching off restores original settings; unsupported targets are skipped. Disabling the hypervisor affects WSL2, Hyper-V and other virtualization features. Storage power settings are a separate advanced option below.") + EnvironmentBundleIssues();
			}
		}

		internal static bool EnvironmentBundleEnabled
		{
			get
			{
				return EnvironmentEnabledCount() > 0;
			}
		}

		internal static string StorageGroupTitle
		{
			get
			{
				return PerformancePolicy.Text("存储设备电源管理 · 高级 · 需重启", "Storage device power management · Advanced · restart required");
			}
		}

		internal static string StorageGroupDetail
		{
			get
			{
				string textResult = PerformancePolicy.Text("对已登记 StorPort 的设备持久关闭三项电源管理：磁盘空闲电源管理、存储控制器运行时电源管理和 D3Cold。默认关闭；开启或恢复后需重启电脑，关闭时恢复原配置，不适用项自动跳过。这些设备将不再进入对应的省电状态，空闲与睡眠功耗可能上升。与电源计划在对局期间调整的存储设置相互独立。", "Persistently disables three power-management settings on devices with registered StorPort configuration: disk idle power management, storage-controller runtime power management and D3Cold. Off by default. Restart after enabling or restoring; switching off restores original settings and unsupported targets are skipped. These devices stop entering the matching power-saving states, so idle and sleep power may rise. Independent of the storage settings the power plan adjusts during matches.");
				HashSet<string> owned = StorageOwned();
				foreach (string item in StorageMembers())
				{
					string message = textResult;
					textResult = message + "\n" + EnvironmentMemberName(item) + PerformancePolicy.Text("：", ": ") + StorageMemberLabel(item, owned);
				}
				return textResult;
			}
		}

		private static string StorageRecoveryPendingText
		{
			get
			{
				return PerformancePolicy.Text("原配置待恢复 · 关闭此项即可恢复", "Original configuration not yet restored · switch off to restore");
			}
		}

		internal static bool StorageGroupEnabled
		{
			get
			{
				HashSet<string> hashSet = StorageOwned();
				foreach (string item in StorageMembers())
				{
					if (EnvironmentPreference(item) || hashSet.Contains(item) || State(item) == "restore-failed")
					{
						return true;
					}
				}
				return false;
			}
		}

		internal static string Boot()
		{
#if PAVISE_SELFTEST
			if (BootForTest != null) return BootForTest();
#endif
			return BootSessionIdentity.Read();
		}

		internal static bool Busy(string id)
		{
			lock (gate)
			{
				return busy.Contains(id);
			}
		}

		internal static bool ActionSelected(string id)
		{
			PerformanceAction performanceAction = PerformanceActionCatalog.Find(id);
			if (performanceAction == null || performanceAction.Environment)
			{
				return false;
			}
			if (id == "MatchVisualEffectsV1")
			{
				return MatchVisualEffects.Enabled;
			}
			return Settings.LoadCached(id, false);
		}

		internal static bool LegacyActionSelected(string id)
		{
			return ActionSelected(id);
		}

		internal static string ActionSelectionSignature()
		{
			lock (selectionSignatureGate)
			{
				int generation = Settings.MutationGeneration;
				bool visuals = MatchVisualEffects.Enabled;
				if (selectionSignature != null && selectionSignatureGeneration == generation
					&& selectionSignatureVisuals == visuals)
					return selectionSignature;
				string signature = BuildActionSelectionSignature();
				if (generation == Settings.MutationGeneration && visuals == MatchVisualEffects.Enabled)
				{
					selectionSignature = signature;
					selectionSignatureGeneration = generation;
					selectionSignatureVisuals = visuals;
				}
				return signature;
			}
		}

		private static string BuildActionSelectionSignature()
		{
			List<string> list = new List<string>();
			PerformanceAction[] all = PerformanceActionCatalog.All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (!performanceAction.Environment && ActionSelected(performanceAction.Key))
				{
					list.Add(performanceAction.Key);
				}
			}
			return string.Join(",", list.ToArray());
		}

		internal static string State(string id)
		{
			lock (gate)
			{
				if (busy.Contains(id))
				{
					return "working";
				}
				string value;
				if (!states.TryGetValue(id, out value))
				{
					value = Settings.LoadStr(id + "State", "");
				}
				bool live = value == "live-applied" || value == "live-restored";
				if (value != "configured" && value != "restored" && !live)
				{
					return value;
				}
				object result;
				switch (BootSessionIdentity.Compare(Settings.LoadStr(id + "Boot", ""), Boot()))
				{
				default:
					result = live ? value : ((value == "configured") ? "pending-reboot" : "restore-pending-reboot");
					break;
				case IrqRebootState.Rebooted:
					result = ((value == "configured" || value == "live-applied") ? "ready" : "restored");
					break;
				case IrqRebootState.Unknown:
					result = "unverified";
					break;
				}
				return (string)result;
			}
		}

		internal static bool SetQuantumProfile(int index, Action completed)
		{
			if (index < 0 || index >= QuantumProfiles.Values.Length)
			{
				return false;
			}
			lock (gate)
			{
				if (EnvironmentWorkClosing || busy.Contains("EnvQuantumProfileV1") || busy.Contains("EnvPerformanceBundleV1") || busy.Contains("EnvStoragePowerGroupV1"))
				{
					return false;
				}
				string val = Settings.LoadStr("EnvQuantumSelectedProfileV1", "38");
				bool previousOn = Settings.LoadCached(PerformanceActionCatalog.Quantum, false);
				if (index > 0 && !Settings.SaveStr("EnvQuantumSelectedProfileV1", QuantumProfiles.Values[index].ToString(CultureInfo.InvariantCulture)))
				{
					return false;
				}
				if (SetEnvironment("EnvQuantumProfileV1", index > 0, completed))
				{
					return true;
				}
				Settings.SaveStr("EnvQuantumSelectedProfileV1", val);
				Settings.SaveDurable(PerformanceActionCatalog.Quantum, previousOn);
				return false;
			}
		}

		internal static bool SetEnvironment(string id, bool on, Action completed)
		{
			PerformanceAction action = PerformanceActionCatalog.Find(id);
			if (action == null || !action.Environment)
			{
				return false;
			}
			lock (gate)
			{
				if (EnvironmentWorkClosing || busy.Contains(id) || busy.Contains("EnvPerformanceBundleV1") || busy.Contains("EnvStoragePowerGroupV1") || !Settings.Save(id, on))
				{
					return false;
				}
				busy.Add(id);
			}
			bool setEnvironmentSucceeded = false;
			try
			{
				setEnvironmentSucceeded = QueueEnvironmentWork(delegate
				{
					try
					{
						ApplyEnvironment(id, on);
					}
					catch (Exception ex2)
					{
						SetState(id, on ? "failed" : "restore-failed");
						Logger.Warn(action.Title + " | " + ex2.Message);
					}
					finally
					{
						lock (gate)
						{
							busy.Remove(id);
							Monitor.PulseAll(gate);
						}
						if (completed != null)
						{
							try
							{
								completed();
							}
							catch (Exception ex3)
							{
								Logger.Warn("Environment completion | " + ex3.Message);
							}
						}
					}
				});
			}
			catch (Exception ex)
			{
				Logger.Warn(action.Title + " | " + ex.Message);
			}
			if (!setEnvironmentSucceeded)
			{
				lock (gate)
				{
					busy.Remove(id);
					Monitor.PulseAll(gate);
				}
				SetState(id, on ? "failed" : "restore-failed");
			}
			return setEnvironmentSucceeded;
		}

		internal static bool CpuMitigationsExpanded
		{
			get { return Settings.LoadCached(CpuMitigationPolicy.ExpandedPreferenceKey, false); }
		}

		internal static bool CpuMitigationsExpansionSupported()
		{
#if PAVISE_SELFTEST
			if (CpuMitigationsSupportedForTest != null) return CpuMitigationsSupportedForTest();
#endif
			try
			{
				using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
					"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
					return key != null && CpuMitigationPolicy.Supports(Native.OsBuild(), key.GetValue("VendorIdentifier") as string);
			}
			catch { return false; }
		}

		internal static bool SetCpuMitigations(bool on, Action completed, bool expanded = true)
		{
			lock (gate)
			{
				if (EnvironmentWorkClosing || busy.Contains(PerformanceActionCatalog.CpuMitigations)
					|| busy.Contains(EnvironmentBundleKey) || busy.Contains(StorageGroupKey)) return false;
				if (on && expanded && !CpuMitigationsExpansionSupported())
				{
					SetState(PerformanceActionCatalog.CpuMitigations, "unsupported");
					return false;
				}
				bool previous = CpuMitigationsExpanded;
				bool previousOn = Settings.LoadCached(PerformanceActionCatalog.CpuMitigations, false);
				// Retain expanded-scope intent until its owned values restore, even after an OFF request.
				if (on && !Settings.SaveDurable(CpuMitigationPolicy.ExpandedPreferenceKey, expanded)) return false;
				if (SetEnvironment(PerformanceActionCatalog.CpuMitigations, on, completed)) return true;
				if (on) Settings.SaveDurable(CpuMitigationPolicy.ExpandedPreferenceKey, previous);
				Settings.SaveDurable(PerformanceActionCatalog.CpuMitigations, previousOn);
				return false;
			}
		}

		internal static void SetState(string id, string state)
		{
			lock (gate)
			{
				states[id] = state;
			}
			Settings.SaveStr(id + "State", state);
		}

		internal static string EnvironmentAutomaticCycle(string id)
		{
			return RecoveryCycleSerial + "\n" + id
				+ (id == PerformanceActionCatalog.Quantum ? "\nquantum:" + QuantumProfiles.Target : "");
		}

		internal static bool ApplyEnvironment(string id, bool on)
		{
			if (id == PerformanceActionCatalog.Quantum) QuantumLiveUpdate.Begin();
			if (EnvironmentWorkClosing)
			{
				return false;
			}
			bool applyEnvironmentSucceeded = true;
			bool applyEnvironmentReady = false;
			bool applyEnvironmentReady2 = false;
			bool applyEnvironmentReady3 = false;
			if (on)
			{
				string[] environmentTargetsResult = null;
				if (!RestoreWithBudget(new PerformanceActionStore.Receipt { Id = "environment-discovery:" + id, Target = "system" }, 3,
					delegate { environmentTargetsResult = PerformanceActionBackend.EnvironmentTargets(id); return true; })) { SetState(id, "stopped"); return false; }
				if (environmentTargetsResult.Length == 0)
				{
					SetState(id, "unsupported");
					return false;
				}
				if (id == "EnvCpuMitigationsV1")
				{
					return ApplyCpuMitigationsGroup(environmentTargetsResult);
				}
				string[] textEntries = environmentTargetsResult;
				foreach (string target in textEntries)
				{
					ActionFailureStore.BeginAutomaticCycle(id, target, EnvironmentAutomaticCycle(id));
					if (EnvironmentWorkClosing)
					{
						return false;
					}
					if (ActionFailureStore.IsIsolated(id, target) && !RestoreWhere((PerformanceActionStore.Receipt receipt) => receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase)))
					{
						ActionFailureStore.Trip(id, target, "Recovery incomplete before retry");
						applyEnvironmentSucceeded = false;
						applyEnvironmentReady2 = true;
						applyEnvironmentReady3 = true;
						continue;
					}
					string reason;
					ActionFailureStore.BeginAutomaticCycle(id, target, EnvironmentAutomaticCycle(id));
					if (!ActionFailureStore.TryBeginApply(id, target, out reason))
					{
						applyEnvironmentSucceeded = false;
						applyEnvironmentReady2 = true;
						continue;
					}
					try
					{
						if (!(id == "EnvQuantumProfileV1") || !QuantumRecovery.HasLegacyRecords)
						{
							goto ReadRegistryTarget;
						}
						string bootResult = Boot();
						if (!BootSessionIdentity.Valid(bootResult))
						{
							ActionFailureStore.CancelApply(id, target);
							applyEnvironmentSucceeded = false;
							continue;
						}
						if (!Settings.SaveStrDurable(id + "Boot", bootResult))
						{
							throw new InvalidOperationException("Boot journal persistence failed");
						}
						bool restoreWithBudgetSucceeded = RestoreWithBudget(new PerformanceActionStore.Receipt { Id = id, Target = "legacy-quantum" }, 3, QuantumRecovery.RestoreAll);
						applyEnvironmentReady |= QuantumRecovery.LastWrote;
						if (restoreWithBudgetSucceeded)
						{
							goto ReadRegistryTarget;
						}
						ActionFailureStore.CancelApply(id, target);
						Logger.Warn(id + " | " + QuantumRecovery.LastFailure);
						applyEnvironmentSucceeded = false;
						applyEnvironmentReady3 = true;
						goto FinishRegistryAction;
						ApplyRegistryAction:
						string wantedRegistryResult;
						if (!PerformanceActionBackend.Apply(id, target, wantedRegistryResult))
						{
							throw new InvalidOperationException(PerformanceActionBackend.ApplyFailureHint(id, target));
						}
						if (!ActionFailureStore.CompleteApply(id, target))
						{
							applyEnvironmentReady3 |= !RestoreWhere((PerformanceActionStore.Receipt receipt) => receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase));
							applyEnvironmentSucceeded = false;
							applyEnvironmentReady2 = true;
						}
						goto FinishRegistryAction;
						ReadRegistryTarget:
						string readResult = PerformanceActionBackend.Read(id, target);
						wantedRegistryResult = PerformanceActionBackend.WantedRegistry(id, readResult);
						if (!(wantedRegistryResult != readResult))
						{
							goto ApplyRegistryAction;
						}
						string bootResult2 = Boot();
						if (!BootSessionIdentity.Valid(bootResult2))
						{
							ActionFailureStore.CancelApply(id, target);
							applyEnvironmentSucceeded = false;
							continue;
						}
						if (!Settings.SaveStrDurable(id + "Boot", bootResult2))
						{
							throw new InvalidOperationException("Boot journal persistence failed");
						}
						applyEnvironmentReady = true;
						goto ApplyRegistryAction;
						FinishRegistryAction:;
					}
					catch (NotSupportedException)
					{
						if (RestoreWhere((PerformanceActionStore.Receipt receipt) => receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase)))
						{
							ActionFailureStore.CancelApply(id, target);
						}
						else
						{
							ActionFailureStore.Trip(id, target, "Recovery incomplete after unsupported apply");
							applyEnvironmentReady3 = true;
						}
						applyEnvironmentSucceeded = false;
					}
					catch (OperationCanceledException)
					{
						if (RestoreWhere((PerformanceActionStore.Receipt receipt) => receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase)))
						{
							ActionFailureStore.CancelApply(id, target);
						}
						else
						{
							ActionFailureStore.Trip(id, target, "Recovery incomplete after cancelled apply");
							applyEnvironmentReady3 = true;
						}
						applyEnvironmentSucceeded = false;
					}
					catch (Exception ex3)
					{
						ActionFailureStore.Trip(id, target, ex3.Message);
						applyEnvironmentReady3 |= !RestoreWhere((PerformanceActionStore.Receipt receipt) => receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase));
						applyEnvironmentSucceeded = false;
						applyEnvironmentReady2 = true;
						Logger.Warn(id + " | " + ex3.Message);
					}
				}
			}
			else if (id == "EnvQuantumProfileV1")
			{
				if (EnvironmentWorkClosing)
				{
					return false;
				}
				try
				{
					if (QuantumRecovery.HasRecords())
					{
						string bootResult3 = Boot();
						if (!BootSessionIdentity.Valid(bootResult3) || !Settings.SaveStrDurable(id + "Boot", bootResult3))
						{
							applyEnvironmentSucceeded = false;
						}
						else
						{
							applyEnvironmentReady = true;
							if (!RestoreWithBudget(new PerformanceActionStore.Receipt { Id = id, Target = "legacy-quantum" }, 3, QuantumRecovery.RestoreAll))
							{
								applyEnvironmentSucceeded = false;
							}
						}
					}
				}
				catch (Exception ex4)
				{
					applyEnvironmentSucceeded = false;
					Logger.Warn(id + " | " + ex4.Message);
				}
			}
			else
			{
				List<PerformanceActionStore.Receipt> receipts;
				if (!ReadRecoveryReceipts(out receipts)) { SetState(id, "restore-failed"); return false; }
				foreach (PerformanceActionStore.Receipt item in receipts)
				{
					if (!(item.Id == id))
					{
						continue;
					}
					if (EnvironmentWorkClosing)
					{
						return false;
					}
					try
					{
						applyEnvironmentReady = true;
						if (!RestorePerformanceReceipt(item, delegate { string boot = Boot(); return BootSessionIdentity.Valid(boot) && Settings.SaveStrDurable(id + "Boot", boot); }))
						{
							applyEnvironmentSucceeded = false;
						}
					}
					catch (Exception ex5)
					{
						applyEnvironmentSucceeded = false;
						Logger.Warn(id + " | " + ex5.Message);
					}
				}
			}
			string state = (applyEnvironmentSucceeded ? ((!applyEnvironmentReady) ? VerifiedEnvironmentState(id, on) : (on ? "configured" : "restored")) : ((!on) ? "restore-failed" : (applyEnvironmentReady3 ? "restore-failed" : (applyEnvironmentReady2 ? "stopped" : "failed"))));
			if (applyEnvironmentSucceeded && id == PerformanceActionCatalog.Quantum) state = QuantumLiveUpdate.Complete(on, state);
			SetState(id, state);
			PerformanceAction performanceAction = PerformanceActionCatalog.Find(id);
			if (performanceAction != null)
			{
				string msg = performanceAction.Title + " | " + Label(State(id));
				if (applyEnvironmentSucceeded)
				{
					Logger.Info(msg);
				}
				else
				{
					Logger.Warn(msg);
				}
			}
			return applyEnvironmentSucceeded;
		}

		private static string VerifiedEnvironmentState(string id, bool on)
		{
			string value = Settings.LoadStr(id + "State", "");
			if (id == PerformanceActionCatalog.Quantum && (on ? value == "live-applied" : value == "live-restored"))
				return value;
			if (!on)
			{
				if (!(value == "restored"))
				{
					return "off";
				}
				return value;
			}
			if (value == "configured")
			{
				return value;
			}
			foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
			{
				if (item.Id == id)
				{
					return "configured";
				}
			}
			return "external";
		}

		private static bool ApplyCpuMitigationsGroup(string[] targets)
		{
			if (CpuMitigationsExpanded && !CpuMitigationsExpansionSupported())
			{
				SetState(PerformanceActionCatalog.CpuMitigations, "unsupported");
				return false;
			}
			Predicate<PerformanceActionStore.Receipt> selected = (PerformanceActionStore.Receipt receipt) => receipt.Id == "EnvCpuMitigationsV1";
			ActionFailureStore.BeginAutomaticCycle("EnvCpuMitigationsV1", "system", RecoveryCycleSerial + "\nexpanded:" + CpuMitigationsExpanded);
			if (ActionFailureStore.IsIsolated("EnvCpuMitigationsV1", "system") && !RestoreWhere(selected))
			{
				ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", "CPU mitigation recovery incomplete before retry");
				SetState("EnvCpuMitigationsV1", "restore-failed");
				return false;
			}
			ActionFailureStore.CancelApply("EnvCpuMitigationsV1", "system");
			ActionFailureStore.BeginAutomaticCycle("EnvCpuMitigationsV1", "system", RecoveryCycleSerial + "\nexpanded:" + CpuMitigationsExpanded);
			string reason;
			if (!ActionFailureStore.TryBeginApply("EnvCpuMitigationsV1", "system", out reason))
			{
				SetState("EnvCpuMitigationsV1", "stopped");
				return false;
			}
			bool applyCpuMitigationsGroupReady = false;
			try
			{
				foreach (string entryText in targets)
				{
					if (EnvironmentWorkClosing)
					{
						throw new OperationCanceledException();
					}
					string readResult = PerformanceActionBackend.Read("EnvCpuMitigationsV1", entryText);
					string wantedRegistryResult = PerformanceActionBackend.WantedRegistry("EnvCpuMitigationsV1", entryText, readResult);
					if (readResult != wantedRegistryResult)
					{
						string bootResult = Boot();
						if (!BootSessionIdentity.Valid(bootResult))
						{
							throw new OperationCanceledException();
						}
						if (!Settings.SaveStrDurable("EnvCpuMitigationsV1Boot", bootResult))
						{
							throw new InvalidOperationException("CPU mitigation boot journal persistence failed");
						}
						applyCpuMitigationsGroupReady = true;
					}
					if (!PerformanceActionBackend.Apply("EnvCpuMitigationsV1", entryText, wantedRegistryResult))
					{
						throw new InvalidOperationException(entryText + " | " + PerformanceActionBackend.ApplyFailureHint("EnvCpuMitigationsV1", entryText));
					}
				}
				if (!ActionFailureStore.CompleteApply("EnvCpuMitigationsV1", "system"))
				{
					bool restoreWhereSucceeded = RestoreWhere(selected);
					SetState("EnvCpuMitigationsV1", restoreWhereSucceeded ? "stopped" : "restore-failed");
					return false;
				}
				SetState("EnvCpuMitigationsV1", applyCpuMitigationsGroupReady ? "configured" : VerifiedEnvironmentState("EnvCpuMitigationsV1", true));
				return true;
			}
			catch (NotSupportedException)
			{
				bool restoreWhereSucceeded2 = RestoreWhere(selected);
				if (restoreWhereSucceeded2)
				{
					ActionFailureStore.CancelApply("EnvCpuMitigationsV1", "system");
				}
				else
				{
					ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", "CPU mitigation recovery incomplete after unsupported apply");
				}
				SetState("EnvCpuMitigationsV1", restoreWhereSucceeded2 ? "unsupported" : "restore-failed");
				return false;
			}
			catch (OperationCanceledException)
			{
				bool restoreWhereSucceeded3 = RestoreWhere(selected);
				if (restoreWhereSucceeded3)
				{
					ActionFailureStore.CancelApply("EnvCpuMitigationsV1", "system");
				}
				else
				{
					ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", "CPU mitigation recovery incomplete after cancelled apply");
				}
				SetState("EnvCpuMitigationsV1", restoreWhereSucceeded3 ? "unverified" : "restore-failed");
				return false;
			}
			catch (Exception ex3)
			{
				ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", ex3.Message);
				bool restoreWhereSucceeded4 = RestoreWhere(selected);
				SetState("EnvCpuMitigationsV1", restoreWhereSucceeded4 ? "stopped" : "restore-failed");
				Logger.Warn("EnvCpuMitigationsV1 | " + ex3.Message);
				return false;
			}
		}

		internal static bool RestoreWhere(Predicate<PerformanceActionStore.Receipt> selected)
		{
			return RestoreFailures(selected).Count == 0;
		}

		private static List<string> RestoreFailures(Predicate<PerformanceActionStore.Receipt> selected, Action<string> progress = null)
		{
			List<string> list = new List<string>();
			List<PerformanceActionStore.Receipt> list2;
			try
			{
				if (!ReadRecoveryReceipts(out list2)) { list.Add("Recovery journal read budget exhausted; records retained"); return list; }
			}
			catch (Exception ex)
			{
				string normalizeErrorOffset = PerformancePolicy.Text("性能配置恢复记录读取失败", "Performance recovery journal could not be read") + " | " + PsRunner.NormalizeError(ex.Message);
				list.Add(normalizeErrorOffset);
				Logger.Warn(normalizeErrorOffset);
				return list;
			}
			foreach (PerformanceActionStore.Receipt item in list2)
			{
				if (!selected(item))
				{
					continue;
				}
				PerformanceAction performanceAction = PerformanceActionCatalog.Find(item.Id);
				string id = ((performanceAction == null) ? item.Id : performanceAction.Title);
				try
				{
					if (progress != null)
					{
						progress(id);
					}
				}
				catch
				{
				}
				string textResult = null;
				try
				{
					if (PrivacyControl.IsAction(item.Id))
					{
						if (!PrivacyControl.RestoreReceipt(item))
						{
							textResult = PerformancePolicy.Text("隐私配置恢复未完成，记录已保留", "Privacy restoration is incomplete; records are retained");
						}
					}
					else if (performanceAction == null)
					{
						textResult = PerformancePolicy.Text("无法识别恢复项目，已保留恢复记录", "Unknown recovery action; recovery record retained");
					}
					else if (!RestorePerformanceReceipt(item))
					{
						textResult = PerformanceActionBackend.RestoreFailureHint(item);
					}
				}
				catch (Exception ex2)
				{
					textResult = PsRunner.NormalizeError(ex2.Message);
				}
				if (textResult != null)
				{
					string textResultOffset = id + " | " + item.Target + " | " + textResult;
					list.Add(textResultOffset);
					Logger.Warn("Performance restore | " + textResultOffset);
				}
			}
			return list;
		}

		internal static List<string> RestoreAllFailures(Action<string> progress = null)
		{
			lock (gate)
			{
				if (busy.Count > 0)
				{
					List<string> list = new List<string>();
					list.Add(PerformancePolicy.Text("环境配置仍在运行，恢复记录已保留，请稍后重试", "Environment configuration is still running; recovery records retained. Retry shortly."));
					return list;
				}
			}
			return RestoreFailures((PerformanceActionStore.Receipt param0) => true, progress);
		}

		internal static bool RestoreAll()
		{
			return RestoreAllFailures().Count == 0;
		}

		internal static bool RestoreSession()
		{
			return RestoreWhere((PerformanceActionStore.Receipt r) => r.Id == "AutoDefenderSessionV1" || r.Id == "AutoBackgroundFpsV1" || r.Id == "MatchVisualEffectsV1");
		}

		internal static string Label(string state)
		{
			switch (state)
			{
			case "live-applied":
				return PerformancePolicy.Text("已在线应用 · 无需重启电脑", "Applied online; no PC restart");
			case "live-restored":
				return PerformancePolicy.Text("已在线恢复 · 无需重启电脑", "Restored online; no PC restart");
			case "working":
				return PerformancePolicy.Text("正在配置", "Configuring");
			case "configured":
				return PerformancePolicy.Text("已配置 · 下次启动生效", "Configured; next launch");
			case "applied":
				return PerformancePolicy.Text("已执行", "Active");
			case "pending-reboot":
				return PerformancePolicy.Text("已配置 · 需重启电脑", "Configured; PC restart required");
			case "restore-pending-reboot":
				return PerformancePolicy.Text("已恢复 · 需重启电脑", "Restored; PC restart required");
			case "ready":
				return PerformancePolicy.Text("已重启 · 配置已写入", "Restarted; configuration written");
			case "unverified":
				return PerformancePolicy.Text("重启状态待确认", "Restart state unverified");
			case "external":
				return PerformancePolicy.Text("系统已配置", "Already configured by system");
			case "off":
				return PerformancePolicy.Text("已关闭", "Off");
			case "unsupported":
				return PerformancePolicy.Text("无适用设备或接口", "No applicable target");
			case "inactive":
				return PerformancePolicy.Text("Defender 未运行 · 已跳过", "Defender off; skipped");
			case "failed":
				return PerformancePolicy.Text("配置未完成 · 查看记录", "Configuration incomplete; see records");
			case "stopped":
				return PerformancePolicy.Text("执行失败 · 已停止应用，保留恢复记录", "Apply failed; further application stopped and recovery records retained");
			case "restore-failed":
				return PerformancePolicy.Text("恢复未完成 · 查看记录", "Restore pending");
			case "restored":
				return PerformancePolicy.Text("已恢复", "Restored");
			default:
				return PerformancePolicy.Text("自动待命", "Automatic standby");
			}
		}

		private static string EnvironmentMemberName(string key)
		{
			switch (key)
			{
			case "EnvCpuMitigationsV1":
				return PerformancePolicy.Text("CPU 缓解", "CPU mitigations");
			case "EnvNtfsLastAccessV1":
				return PerformancePolicy.Text("NTFS 访问时间", "NTFS last access");
			case "EnvStorageIdleV1":
				return PerformancePolicy.Text("存储空闲电源", "Storage idle power");
			case "EnvStorageRuntimeV1":
				return PerformancePolicy.Text("存储运行时电源", "Storage runtime power");
			case "EnvStorageD3ColdV1":
				return PerformancePolicy.Text("存储 D3Cold", "Storage D3Cold");
			case "EnvAudioIdleV1":
				return PerformancePolicy.Text("音频空闲电源", "Audio idle power");
			default:
				return "VBS";
			}
		}

		private static string EnvironmentBundleIssues()
		{
			List<string> list = new List<string>();
			List<string> list2 = new List<string>();
			foreach (string item in EnvironmentMembers())
			{
				string stateResult = State(item);
				if (stateResult == "failed" || stateResult == "stopped")
				{
					list.Add(EnvironmentMemberName(item));
				}
				if (stateResult == "restore-failed")
				{
					list2.Add(EnvironmentMemberName(item));
				}
			}
			string environmentBundleIssuesResult = "";
			if (list.Count > 0)
			{
				environmentBundleIssuesResult = environmentBundleIssuesResult + "\n" + PerformancePolicy.Text("执行受限项：", "Blocked settings: ") + string.Join(", ", list.ToArray());
			}
			if (list2.Count > 0)
			{
				environmentBundleIssuesResult = environmentBundleIssuesResult + "\n" + PerformancePolicy.Text("恢复未完成项：", "Restoration incomplete: ") + string.Join(", ", list2.ToArray());
			}
			return environmentBundleIssuesResult;
		}

		private static List<string> EnvironmentMembers()
		{
			List<string> list = new List<string>();
			PerformanceAction[] all = PerformanceActionCatalog.All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (EnvironmentBundleMember(performanceAction))
				{
					list.Add(performanceAction.Key);
				}
			}
			list.Add("EnvVbsDisabledV1");
			return list;
		}

		private static bool EnvironmentBundleMember(PerformanceAction action)
		{
			if (action.Environment && action.Key != "EnvQuantumProfileV1")
			{
				return !StorageMember(action.Key);
			}
			return false;
		}

		private static bool EnvironmentPreference(string key)
		{
			if (key == "EnvVbsDisabledV1")
			{
				return Settings.LoadCached(key, VbsTweak.OwnsState);
			}
			return Settings.LoadCached(key, false);
		}

		private static int GroupEnabledCount(List<string> members, Predicate<string> selected)
		{
			int processedCount = 0;
			foreach (string member in members)
			{
				if (selected(member))
				{
					processedCount++;
				}
			}
			return processedCount;
		}

		internal static int EnvironmentEnabledCount()
		{
			return GroupEnabledCount(EnvironmentMembers(), EnvironmentPreference);
		}

		internal static bool EnvironmentBundleBusy()
		{
			lock (gate)
			{
				return busy.Count > 0;
			}
		}

		internal static string EnvironmentBundleState()
		{
			return GroupState(EnvironmentMembers(), EnvironmentPreference);
		}

		private static string GroupState(List<string> members, Predicate<string> selected)
		{
			if (EnvironmentBundleBusy())
			{
				return "working";
			}
			int processedCount = 0;
			int processedCount2 = 0;
			bool groupStateReady = false;
			bool groupStateReady2 = false;
			bool groupStateReady3 = false;
			bool groupStateReady4 = false;
			bool groupStateReady5 = false;
			bool groupStateReady6 = false;
			bool groupStateReady7 = false;
			foreach (string member in members)
			{
				bool selectedSucceeded = selected(member);
				string stateResult = State(member);
				if (selectedSucceeded)
				{
					processedCount++;
					if (stateResult != "unsupported")
					{
						processedCount2++;
					}
				}
				groupStateReady |= stateResult == "failed";
				groupStateReady2 |= stateResult == "stopped";
				groupStateReady3 |= stateResult == "restore-failed";
				groupStateReady4 |= stateResult == "pending-reboot";
				groupStateReady5 |= stateResult == "restore-pending-reboot";
				groupStateReady6 |= stateResult == "unverified" || (selectedSucceeded && stateResult.Length == 0);
				groupStateReady7 |= stateResult == "restored";
			}
			if (groupStateReady3)
			{
				return "restore-failed";
			}
			if (groupStateReady2)
			{
				return "stopped";
			}
			if (groupStateReady)
			{
				return "failed";
			}
			if (processedCount > 0 && processedCount < members.Count)
			{
				return "partial";
			}
			if (groupStateReady4)
			{
				return "pending-reboot";
			}
			if (groupStateReady5)
			{
				return "restore-pending-reboot";
			}
			if (groupStateReady6)
			{
				return "unverified";
			}
			if (processedCount > 0)
			{
				if (processedCount2 != 0)
				{
					return "ready";
				}
				return "unsupported";
			}
			if (!groupStateReady7)
			{
				return "off";
			}
			return "restored";
		}

		internal static string EnvironmentBundleLabel()
		{
			return GroupLabel(EnvironmentMembers(), EnvironmentPreference);
		}

		private static string GroupLabel(List<string> members, Predicate<string> selected)
		{
			string groupStateResult = GroupState(members, selected);
			if (groupStateResult == "partial")
			{
				return PerformancePolicy.Text("部分启用 · 保留旧版选择 · ", "Partly enabled; existing choices retained · ") + GroupEnabledCount(members, selected) + "/" + members.Count;
			}
			string envPerformanceReadyText = ((groupStateResult == "ready") ? Lang.T("env.performance.ready") : Label(groupStateResult));
			if (groupStateResult == "working")
			{
				return envPerformanceReadyText;
			}
			int processedCount = 0;
			int processedCount2 = 0;
			bool groupLabelReady = false;
			foreach (string member in members)
			{
				string stateResult = State(member);
				if (stateResult == "unsupported")
				{
					processedCount++;
				}
				switch (stateResult)
				{
				case "failed":
				case "stopped":
				case "restore-failed":
					processedCount2++;
					break;
				}
				groupLabelReady |= stateResult == "pending-reboot" || stateResult == "restore-pending-reboot";
			}
			if (processedCount2 > 0)
			{
                envPerformanceReadyText = processedCount2 + PerformancePolicy.Text(" 项受限", " blocked settings") + (groupLabelReady ? PerformancePolicy.Text(" · 已写入项需重启", " · Written settings require restart") : "");
			}
			if (processedCount != 0)
			{
				return envPerformanceReadyText + PerformancePolicy.Text(" · 跳过不适用项：", " · Unsupported settings skipped: ") + processedCount;
			}
			return envPerformanceReadyText;
		}

		private static void RestoreBundlePreferences(List<string> members, bool[] previous)
		{
			for (int i = 0; i < members.Count; i++)
			{
				bool matchesSaveDurable;
				try
				{
					matchesSaveDurable = EnvironmentPreference(members[i]) == previous[i] || Settings.SaveDurable(members[i], previous[i]);
				}
				catch
				{
					matchesSaveDurable = false;
				}
				if (!matchesSaveDurable)
				{
					Logger.Warn("Environment preference restore | " + members[i]);
				}
			}
		}

		internal static bool SetEnvironmentBundle(bool on, Action completed)
		{
			return SetEnvironmentGroup("EnvPerformanceBundleV1", EnvironmentMembers(), on, completed, ApplyEnvironmentBundle);
		}

		private static bool SetEnvironmentGroup(string busyKey, List<string> members, bool on, Action completed, Func<bool, bool> apply)
		{
			bool[] conditionMetItems = new bool[members.Count];
			bool setEnvironmentGroupSucceeded = false;
			lock (gate)
			{
				if (EnvironmentWorkClosing || busy.Count > 0)
				{
					return false;
				}
				for (int i = 0; i < members.Count; i++)
				{
					conditionMetItems[i] = EnvironmentPreference(members[i]);
				}
				for (int j = 0; j < members.Count; j++)
				{
					bool saveDurableSucceeded;
					try
					{
						saveDurableSucceeded = Settings.SaveDurable(members[j], on);
					}
					catch (Exception ex)
					{
						saveDurableSucceeded = false;
						Logger.Warn("Environment preference save | " + ex.Message);
					}
					if (!saveDurableSucceeded)
					{
						RestoreBundlePreferences(members, conditionMetItems);
						return false;
					}
				}
				busy.Add(busyKey);
				try
				{
					setEnvironmentGroupSucceeded = QueueEnvironmentWork(delegate
					{
						try
						{
							apply(on);
						}
						finally
						{
							lock (gate)
							{
								busy.Remove(busyKey);
								Monitor.PulseAll(gate);
							}
							if (completed != null)
							{
								try
								{
									completed();
								}
								catch
								{
								}
							}
						}
					});
				}
				catch (Exception ex2)
				{
					Logger.Warn("Environment configuration queue | " + ex2.Message);
				}
				if (!setEnvironmentGroupSucceeded)
				{
					lock (gate)
					{
						busy.Remove(busyKey);
						Monitor.PulseAll(gate);
						RestoreBundlePreferences(members, conditionMetItems);
					}
				}
			}
			return setEnvironmentGroupSucceeded;
		}

		internal static bool ApplyEnvironmentBundle(bool on)
		{
			bool result = true;
			PerformanceAction[] all = PerformanceActionCatalog.All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (!EnvironmentBundleMember(performanceAction))
				{
					continue;
				}
				if (EnvironmentWorkClosing)
				{
					return false;
				}
				try
				{
					if (!ApplyEnvironment(performanceAction.Key, on) && State(performanceAction.Key) != "unsupported")
					{
						result = false;
					}
				}
				catch (Exception ex)
				{
					result = false;
					SetState(performanceAction.Key, on ? "failed" : "restore-failed");
					Logger.Warn(performanceAction.Title + " | " + ex.Message);
				}
			}
			if (EnvironmentWorkClosing)
			{
				return false;
			}
			try
			{
				if (!ApplyEnvironmentVbs(on))
				{
					result = false;
				}
			}
			catch (Exception ex2)
			{
				result = false;
				SetState("EnvVbsDisabledV1", on ? "failed" : "restore-failed");
				Logger.Warn("VBS | " + ex2.Message);
			}
			return result;
		}

		internal static bool StorageMember(string key)
		{
			if (!(key == "EnvStorageIdleV1") && !(key == "EnvStorageRuntimeV1"))
			{
				return key == "EnvStorageD3ColdV1";
			}
			return true;
		}

		internal static List<string> StorageMembers()
		{
			List<string> list = new List<string>();
			PerformanceAction[] all = PerformanceActionCatalog.All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (performanceAction.Environment && StorageMember(performanceAction.Key))
				{
					list.Add(performanceAction.Key);
				}
			}
			return list;
		}

		private static string StorageMemberLabel(string key, HashSet<string> owned)
		{
			string stateResult = State(key);
			if (StorageRecoveryPending(key, owned) && stateResult != "restore-failed" && stateResult != "working")
			{
				return StorageRecoveryPendingText;
			}
			if (stateResult.Length == 0)
			{
				if (!EnvironmentPreference(key))
				{
					return Label("off");
				}
				return Lang.T("env.performance.pending");
			}
			if (!(stateResult == "ready"))
			{
				return Label(stateResult);
			}
			return Lang.T("env.performance.ready");
		}

		private static HashSet<string> StorageOwned()
		{
			HashSet<string> hashSet = storageOwned;
			try
			{
				List<PerformanceActionStore.Receipt> receipts;
				if (PerformanceActionStore.TryAll(out receipts))
				{
					hashSet = new HashSet<string>(StringComparer.Ordinal);
					foreach (PerformanceActionStore.Receipt item in receipts)
					{
						if (StorageMember(item.Id))
						{
							hashSet.Add(item.Id);
						}
					}
				}
			}
			catch
			{
				hashSet = new HashSet<string>(StorageMembers(), StringComparer.Ordinal);
			}
			storageOwned = hashSet;
			return hashSet;
		}

		private static bool StorageRecoveryPending(string key, HashSet<string> owned)
		{
			if (!EnvironmentPreference(key))
			{
				return owned.Contains(key);
			}
			return false;
		}

		internal static int StorageEnabledCount()
		{
			return GroupEnabledCount(StorageMembers(), EnvironmentPreference);
		}

		internal static string StorageGroupState()
		{
			List<string> list = StorageMembers();
			string groupStateResult = GroupState(list, EnvironmentPreference);
			switch (groupStateResult)
			{
			case "working":
			case "restore-failed":
			case "stopped":
			case "failed":
				return groupStateResult;
			default:
			{
				HashSet<string> owned = StorageOwned();
				{
					foreach (string item in list)
					{
						if (StorageRecoveryPending(item, owned))
						{
							return "recovery-pending";
						}
					}
					return groupStateResult;
				}
			}
			}
		}

		internal static string StorageGroupLabel()
		{
			if (StorageGroupState() == "recovery-pending")
			{
				return StorageRecoveryPendingText;
			}
			return GroupLabel(StorageMembers(), EnvironmentPreference);
		}

		internal static bool SetStorageGroup(bool on, Action completed)
		{
			return SetEnvironmentGroup("EnvStoragePowerGroupV1", StorageMembers(), on, completed, ApplyStorageGroup);
		}

		internal static bool ApplyStorageGroup(bool on)
		{
			bool result = true;
			foreach (string item in StorageMembers())
			{
				if (EnvironmentWorkClosing)
				{
					return false;
				}
				try
				{
					if (!ApplyEnvironment(item, on) && State(item) != "unsupported")
					{
						result = false;
					}
				}
				catch (Exception ex)
				{
					result = false;
					SetState(item, on ? "failed" : "restore-failed");
					Logger.Warn(PerformanceActionCatalog.Find(item).Title + " | " + ex.Message);
				}
			}
			return result;
		}

		private static bool VbsConfigured()
		{
			return VbsTweak.CurrentlyConfiguredOff();
		}

		private static bool ChangeVbs(bool on)
		{
			if (!on)
			{
				return VbsTweak.Restore();
			}
			return VbsTweak.Disable();
		}

		private static bool RestoreOwnedVbs(Func<bool> prepare = null)
		{
			if (VbsTweak.OwnsState)
			{
				bool restored = RestoreWithBudget(new PerformanceActionStore.Receipt { Id = EnvironmentVbsKey, Target = "system" }, 3,
					() => (prepare == null || prepare()) && ChangeVbs(false) && !VbsTweak.OwnsState);
				if (restored) ActionFailureStore.CancelApply(EnvironmentVbsKey, "system");
				return restored;
			}
			return true;
		}

		private static bool SkipVbsApply(bool on, string state)
		{
			if (!on)
			{
				SetState("EnvVbsDisabledV1", "restore-failed");
				return false;
			}
			bool restoreOwnedVbsSucceeded;
			try
			{
				restoreOwnedVbsSucceeded = RestoreOwnedVbs();
			}
			catch
			{
				restoreOwnedVbsSucceeded = false;
			}
			if (restoreOwnedVbsSucceeded)
			{
				ActionFailureStore.CancelApply("EnvVbsDisabledV1", "system");
			}
			else
			{
				ActionFailureStore.Trip("EnvVbsDisabledV1", "system", "Recovery incomplete after skipped VBS apply");
			}
			SetState("EnvVbsDisabledV1", restoreOwnedVbsSucceeded ? state : "restore-failed");
			return false;
		}

		internal static bool ApplyEnvironmentVbs(bool on)
		{
			if (EnvironmentWorkClosing)
			{
				return false;
			}
			if (on)
			{
				try
				{
					ActionFailureStore.BeginAutomaticCycle("EnvVbsDisabledV1", "system", RecoveryCycleSerial + "\ndisabled");
					if (ActionFailureStore.IsIsolated("EnvVbsDisabledV1", "system") && !RestoreOwnedVbs())
					{
						throw new InvalidOperationException("Recovery incomplete before retry");
					}
				}
				catch (Exception ex)
				{
					ActionFailureStore.Trip("EnvVbsDisabledV1", "system", ex.Message);
					SetState("EnvVbsDisabledV1", "restore-failed");
					return false;
				}
				string reason;
				ActionFailureStore.BeginAutomaticCycle("EnvVbsDisabledV1", "system", RecoveryCycleSerial + "\ndisabled");
				if (!ActionFailureStore.TryBeginApply("EnvVbsDisabledV1", "system", out reason))
				{
					SetState("EnvVbsDisabledV1", VbsTweak.OwnsState ? "restore-failed" : "stopped");
					return false;
				}
			}
			try
			{
				bool matchesCompleteApply;
				object state;
				if (!(on ? (!VbsConfigured()) : VbsTweak.OwnsState))
				{
					string envVbsDisabledV1StateValue = Settings.LoadStr("EnvVbsDisabledV1State", "");
					matchesCompleteApply = !on || ActionFailureStore.CompleteApply("EnvVbsDisabledV1", "system");
					if (matchesCompleteApply)
					{
						bool matchesEnvVbsDisabledV1StateValue;
						if (!on)
						{
							matchesEnvVbsDisabledV1StateValue = envVbsDisabledV1StateValue == "restored";
						}
						else
						{
							if (envVbsDisabledV1StateValue == "configured")
							{
								goto SelectCompletedVbsState;
							}
							matchesEnvVbsDisabledV1StateValue = VbsTweak.OwnsState;
						}
						if (matchesEnvVbsDisabledV1StateValue)
						{
							goto SelectCompletedVbsState;
						}
						state = (on ? "external" : "off");
					}
					else
					{
						state = "stopped";
					}
					goto PublishVbsState;
				}
				string bootResult = Boot();
				if (!BootSessionIdentity.Valid(bootResult) || EnvironmentWorkClosing)
				{
					if (on)
					{
						ActionFailureStore.CancelApply("EnvVbsDisabledV1", "system");
					}
					SetState("EnvVbsDisabledV1", on ? "unverified" : "restore-failed");
					return false;
				}
				bool matchesSaveStrDurable = on ? Settings.SaveStrDurable("EnvVbsDisabledV1Boot", bootResult) && ChangeVbs(true)
					: RestoreOwnedVbs(() => Settings.SaveStrDurable("EnvVbsDisabledV1Boot", bootResult));
				if (matchesSaveStrDurable)
				{
					matchesSaveStrDurable = (on ? VbsConfigured() : (!VbsTweak.OwnsState));
				}
				if (!matchesSaveStrDurable)
				{
					throw new InvalidOperationException("VBS apply or readback failed");
				}
				if (on)
				{
					matchesSaveStrDurable = ActionFailureStore.CompleteApply("EnvVbsDisabledV1", "system");
					if (!matchesSaveStrDurable && !RestoreOwnedVbs())
					{
						SetState("EnvVbsDisabledV1", "restore-failed");
						return false;
					}
				}
				SetState("EnvVbsDisabledV1", (!matchesSaveStrDurable) ? "stopped" : (on ? "configured" : "restored"));
				return matchesSaveStrDurable;
				SelectCompletedVbsState:
				state = (on ? "configured" : "restored");
				goto PublishVbsState;
				PublishVbsState:
				SetState("EnvVbsDisabledV1", (string)state);
				return matchesCompleteApply;
			}
			catch (NotSupportedException)
			{
				return SkipVbsApply(on, "unsupported");
			}
			catch (OperationCanceledException)
			{
				return SkipVbsApply(on, "");
			}
			catch (Exception ex4)
			{
				if (on)
				{
					ActionFailureStore.Trip("EnvVbsDisabledV1", "system", ex4.Message);
					try
					{
						if (!RestoreOwnedVbs())
						{
							SetState("EnvVbsDisabledV1", "restore-failed");
							return false;
						}
					}
					catch
					{
						SetState("EnvVbsDisabledV1", "restore-failed");
						return false;
					}
				}
				SetState("EnvVbsDisabledV1", on ? "stopped" : "restore-failed");
				return false;
			}
		}

		internal static bool QueueEnvironmentWork(WaitCallback work)
		{
#if PAVISE_SELFTEST
			if (EnvironmentQueueForTest != null) return EnvironmentQueueForTest(work);
#endif
			return ThreadPool.QueueUserWorkItem(work);
		}

		internal static bool CloseEnvironmentWork(int timeoutMs)
		{
			if (timeoutMs < 0)
			{
				return false;
			}
			EnvironmentWorkClosing = true;
			Stopwatch stopwatch = Stopwatch.StartNew();
			bool closeEnvironmentWorkReady = false;
			try
			{
				closeEnvironmentWorkReady = Monitor.TryEnter(gate, timeoutMs);
				if (!closeEnvironmentWorkReady)
				{
					return false;
				}
				while (busy.Count > 0)
				{
					int lowerBound = (int)Math.Max(0L, timeoutMs - stopwatch.ElapsedMilliseconds);
					if (lowerBound <= 0)
					{
						return false;
					}
					Monitor.Wait(gate, lowerBound);
				}
				return true;
			}
			finally
			{
				if (closeEnvironmentWorkReady)
				{
					Monitor.Exit(gate);
				}
			}
		}
	}
}
