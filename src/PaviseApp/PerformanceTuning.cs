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
				string text = PerformancePolicy.Text("对已登记 StorPort 的设备持久关闭三项电源管理：磁盘空闲电源管理、存储控制器运行时电源管理和 D3Cold。默认关闭；开启或恢复后需重启电脑，关闭时恢复原配置，不适用项自动跳过。这些设备将不再进入对应的省电状态，空闲与睡眠功耗可能上升。与电源计划在对局期间调整的存储设置相互独立。", "Persistently disables three power-management settings on devices with registered StorPort configuration: disk idle power management, storage-controller runtime power management and D3Cold. Off by default. Restart after enabling or restoring; switching off restores original settings and unsupported targets are skipped. These devices stop entering the matching power-saving states, so idle and sleep power may rise. Independent of the storage settings the power plan adjusts during matches.");
				HashSet<string> owned = StorageOwned();
				foreach (string item in StorageMembers())
				{
					string text2 = text;
					text = text2 + "\n" + EnvironmentMemberName(item) + PerformancePolicy.Text("：", ": ") + StorageMemberLabel(item, owned);
				}
				return text;
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
			bool flag = false;
			try
			{
				flag = QueueEnvironmentWork(delegate
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
			if (!flag)
			{
				lock (gate)
				{
					busy.Remove(id);
					Monitor.PulseAll(gate);
				}
				SetState(id, on ? "failed" : "restore-failed");
			}
			return flag;
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
			bool flag = true;
			bool flag2 = false;
			bool flag3 = false;
			bool flag4 = false;
			if (on)
			{
				string[] array = null;
				if (!RestoreWithBudget(new PerformanceActionStore.Receipt { Id = "environment-discovery:" + id, Target = "system" }, 3,
					delegate { array = PerformanceActionBackend.EnvironmentTargets(id); return true; })) { SetState(id, "stopped"); return false; }
				if (array.Length == 0)
				{
					SetState(id, "unsupported");
					return false;
				}
				if (id == "EnvCpuMitigationsV1")
				{
					return ApplyCpuMitigationsGroup(array);
				}
				string[] array2 = array;
				foreach (string target in array2)
				{
					ActionFailureStore.BeginAutomaticCycle(id, target, EnvironmentAutomaticCycle(id));
					if (EnvironmentWorkClosing)
					{
						return false;
					}
					if (ActionFailureStore.IsIsolated(id, target) && !RestoreWhere((PerformanceActionStore.Receipt receipt) => receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase)))
					{
						ActionFailureStore.Trip(id, target, "Recovery incomplete before retry");
						flag = false;
						flag3 = true;
						flag4 = true;
						continue;
					}
					string reason;
					ActionFailureStore.BeginAutomaticCycle(id, target, EnvironmentAutomaticCycle(id));
					if (!ActionFailureStore.TryBeginApply(id, target, out reason))
					{
						flag = false;
						flag3 = true;
						continue;
					}
					try
					{
						if (!(id == "EnvQuantumProfileV1") || !QuantumRecovery.HasLegacyRecords)
						{
							goto IL_01e0;
						}
						string text = Boot();
						if (!BootSessionIdentity.Valid(text))
						{
							ActionFailureStore.CancelApply(id, target);
							flag = false;
							continue;
						}
						if (!Settings.SaveStrDurable(id + "Boot", text))
						{
							throw new InvalidOperationException("Boot journal persistence failed");
						}
						bool flag5 = RestoreWithBudget(new PerformanceActionStore.Receipt { Id = id, Target = "legacy-quantum" }, 3, QuantumRecovery.RestoreAll);
						flag2 |= QuantumRecovery.LastWrote;
						if (flag5)
						{
							goto IL_01e0;
						}
						ActionFailureStore.CancelApply(id, target);
						Logger.Warn(id + " | " + QuantumRecovery.LastFailure);
						flag = false;
						flag4 = true;
						goto end_IL_0127;
						IL_0261:
						string text2;
						if (!PerformanceActionBackend.Apply(id, target, text2))
						{
							throw new InvalidOperationException(PerformanceActionBackend.ApplyFailureHint(id, target));
						}
						if (!ActionFailureStore.CompleteApply(id, target))
						{
							flag4 |= !RestoreWhere((PerformanceActionStore.Receipt receipt) => receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase));
							flag = false;
							flag3 = true;
						}
						goto end_IL_0127;
						IL_01e0:
						string text3 = PerformanceActionBackend.Read(id, target);
						text2 = PerformanceActionBackend.WantedRegistry(id, text3);
						if (!(text2 != text3))
						{
							goto IL_0261;
						}
						string text4 = Boot();
						if (!BootSessionIdentity.Valid(text4))
						{
							ActionFailureStore.CancelApply(id, target);
							flag = false;
							continue;
						}
						if (!Settings.SaveStrDurable(id + "Boot", text4))
						{
							throw new InvalidOperationException("Boot journal persistence failed");
						}
						flag2 = true;
						goto IL_0261;
						end_IL_0127:;
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
							flag4 = true;
						}
						flag = false;
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
							flag4 = true;
						}
						flag = false;
					}
					catch (Exception ex3)
					{
						ActionFailureStore.Trip(id, target, ex3.Message);
						flag4 |= !RestoreWhere((PerformanceActionStore.Receipt receipt) => receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase));
						flag = false;
						flag3 = true;
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
						string text5 = Boot();
						if (!BootSessionIdentity.Valid(text5) || !Settings.SaveStrDurable(id + "Boot", text5))
						{
							flag = false;
						}
						else
						{
							flag2 = true;
							if (!RestoreWithBudget(new PerformanceActionStore.Receipt { Id = id, Target = "legacy-quantum" }, 3, QuantumRecovery.RestoreAll))
							{
								flag = false;
							}
						}
					}
				}
				catch (Exception ex4)
				{
					flag = false;
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
						flag2 = true;
						if (!RestorePerformanceReceipt(item, delegate { string boot = Boot(); return BootSessionIdentity.Valid(boot) && Settings.SaveStrDurable(id + "Boot", boot); }))
						{
							flag = false;
						}
					}
					catch (Exception ex5)
					{
						flag = false;
						Logger.Warn(id + " | " + ex5.Message);
					}
				}
			}
			string state = (flag ? ((!flag2) ? VerifiedEnvironmentState(id, on) : (on ? "configured" : "restored")) : ((!on) ? "restore-failed" : (flag4 ? "restore-failed" : (flag3 ? "stopped" : "failed"))));
			if (flag && id == PerformanceActionCatalog.Quantum) state = QuantumLiveUpdate.Complete(on, state);
			SetState(id, state);
			PerformanceAction performanceAction = PerformanceActionCatalog.Find(id);
			if (performanceAction != null)
			{
				string msg = performanceAction.Title + " | " + Label(State(id));
				if (flag)
				{
					Logger.Info(msg);
				}
				else
				{
					Logger.Warn(msg);
				}
			}
			return flag;
		}

		private static string VerifiedEnvironmentState(string id, bool on)
		{
			string text = Settings.LoadStr(id + "State", "");
			if (id == PerformanceActionCatalog.Quantum && (on ? text == "live-applied" : text == "live-restored"))
				return text;
			if (!on)
			{
				if (!(text == "restored"))
				{
					return "off";
				}
				return text;
			}
			if (text == "configured")
			{
				return text;
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
			bool flag = false;
			try
			{
				foreach (string text in targets)
				{
					if (EnvironmentWorkClosing)
					{
						throw new OperationCanceledException();
					}
					string text2 = PerformanceActionBackend.Read("EnvCpuMitigationsV1", text);
					string text3 = PerformanceActionBackend.WantedRegistry("EnvCpuMitigationsV1", text, text2);
					if (text2 != text3)
					{
						string text4 = Boot();
						if (!BootSessionIdentity.Valid(text4))
						{
							throw new OperationCanceledException();
						}
						if (!Settings.SaveStrDurable("EnvCpuMitigationsV1Boot", text4))
						{
							throw new InvalidOperationException("CPU mitigation boot journal persistence failed");
						}
						flag = true;
					}
					if (!PerformanceActionBackend.Apply("EnvCpuMitigationsV1", text, text3))
					{
						throw new InvalidOperationException(text + " | " + PerformanceActionBackend.ApplyFailureHint("EnvCpuMitigationsV1", text));
					}
				}
				if (!ActionFailureStore.CompleteApply("EnvCpuMitigationsV1", "system"))
				{
					bool flag2 = RestoreWhere(selected);
					SetState("EnvCpuMitigationsV1", flag2 ? "stopped" : "restore-failed");
					return false;
				}
				SetState("EnvCpuMitigationsV1", flag ? "configured" : VerifiedEnvironmentState("EnvCpuMitigationsV1", true));
				return true;
			}
			catch (NotSupportedException)
			{
				bool flag3 = RestoreWhere(selected);
				if (flag3)
				{
					ActionFailureStore.CancelApply("EnvCpuMitigationsV1", "system");
				}
				else
				{
					ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", "CPU mitigation recovery incomplete after unsupported apply");
				}
				SetState("EnvCpuMitigationsV1", flag3 ? "unsupported" : "restore-failed");
				return false;
			}
			catch (OperationCanceledException)
			{
				bool flag4 = RestoreWhere(selected);
				if (flag4)
				{
					ActionFailureStore.CancelApply("EnvCpuMitigationsV1", "system");
				}
				else
				{
					ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", "CPU mitigation recovery incomplete after cancelled apply");
				}
				SetState("EnvCpuMitigationsV1", flag4 ? "unverified" : "restore-failed");
				return false;
			}
			catch (Exception ex3)
			{
				ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", ex3.Message);
				bool flag5 = RestoreWhere(selected);
				SetState("EnvCpuMitigationsV1", flag5 ? "stopped" : "restore-failed");
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
				string text = PerformancePolicy.Text("性能配置恢复记录读取失败", "Performance recovery journal could not be read") + " | " + PsRunner.NormalizeError(ex.Message);
				list.Add(text);
				Logger.Warn(text);
				return list;
			}
			foreach (PerformanceActionStore.Receipt item in list2)
			{
				if (!selected(item))
				{
					continue;
				}
				PerformanceAction performanceAction = PerformanceActionCatalog.Find(item.Id);
				string text2 = ((performanceAction == null) ? item.Id : performanceAction.Title);
				try
				{
					if (progress != null)
					{
						progress(text2);
					}
				}
				catch
				{
				}
				string text3 = null;
				try
				{
					if (PrivacyControl.IsAction(item.Id))
					{
						if (!PrivacyControl.RestoreReceipt(item))
						{
							text3 = PerformancePolicy.Text("隐私配置恢复未完成，记录已保留", "Privacy restoration is incomplete; records are retained");
						}
					}
					else if (performanceAction == null)
					{
						text3 = PerformancePolicy.Text("无法识别恢复项目，已保留恢复记录", "Unknown recovery action; recovery record retained");
					}
					else if (!RestorePerformanceReceipt(item))
					{
						text3 = PerformanceActionBackend.RestoreFailureHint(item);
					}
				}
				catch (Exception ex2)
				{
					text3 = PsRunner.NormalizeError(ex2.Message);
				}
				if (text3 != null)
				{
					string text4 = text2 + " | " + item.Target + " | " + text3;
					list.Add(text4);
					Logger.Warn("Performance restore | " + text4);
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
				string text = State(item);
				if (text == "failed" || text == "stopped")
				{
					list.Add(EnvironmentMemberName(item));
				}
				if (text == "restore-failed")
				{
					list2.Add(EnvironmentMemberName(item));
				}
			}
			string text2 = "";
			if (list.Count > 0)
			{
				text2 = text2 + "\n" + PerformancePolicy.Text("执行受限项：", "Blocked settings: ") + string.Join(", ", list.ToArray());
			}
			if (list2.Count > 0)
			{
				text2 = text2 + "\n" + PerformancePolicy.Text("恢复未完成项：", "Restoration incomplete: ") + string.Join(", ", list2.ToArray());
			}
			return text2;
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
			int num = 0;
			foreach (string member in members)
			{
				if (selected(member))
				{
					num++;
				}
			}
			return num;
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
			int num = 0;
			int num2 = 0;
			bool flag = false;
			bool flag2 = false;
			bool flag3 = false;
			bool flag4 = false;
			bool flag5 = false;
			bool flag6 = false;
			bool flag7 = false;
			foreach (string member in members)
			{
				bool flag8 = selected(member);
				string text = State(member);
				if (flag8)
				{
					num++;
					if (text != "unsupported")
					{
						num2++;
					}
				}
				flag |= text == "failed";
				flag2 |= text == "stopped";
				flag3 |= text == "restore-failed";
				flag4 |= text == "pending-reboot";
				flag5 |= text == "restore-pending-reboot";
				flag6 |= text == "unverified" || (flag8 && text.Length == 0);
				flag7 |= text == "restored";
			}
			if (flag3)
			{
				return "restore-failed";
			}
			if (flag2)
			{
				return "stopped";
			}
			if (flag)
			{
				return "failed";
			}
			if (num > 0 && num < members.Count)
			{
				return "partial";
			}
			if (flag4)
			{
				return "pending-reboot";
			}
			if (flag5)
			{
				return "restore-pending-reboot";
			}
			if (flag6)
			{
				return "unverified";
			}
			if (num > 0)
			{
				if (num2 != 0)
				{
					return "ready";
				}
				return "unsupported";
			}
			if (!flag7)
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
			string text = GroupState(members, selected);
			if (text == "partial")
			{
				return PerformancePolicy.Text("部分启用 · 保留旧版选择 · ", "Partly enabled; existing choices retained · ") + GroupEnabledCount(members, selected) + "/" + members.Count;
			}
			string text2 = ((text == "ready") ? Lang.T("env.performance.ready") : Label(text));
			if (text == "working")
			{
				return text2;
			}
			int num = 0;
			int num2 = 0;
			bool flag = false;
			foreach (string member in members)
			{
				string text3 = State(member);
				if (text3 == "unsupported")
				{
					num++;
				}
				switch (text3)
				{
				case "failed":
				case "stopped":
				case "restore-failed":
					num2++;
					break;
				}
				flag |= text3 == "pending-reboot" || text3 == "restore-pending-reboot";
			}
			if (num2 > 0)
			{
                text2 = num2 + PerformancePolicy.Text(" 项受限", " blocked settings") + (flag ? PerformancePolicy.Text(" · 已写入项需重启", " · Written settings require restart") : "");
			}
			if (num != 0)
			{
				return text2 + PerformancePolicy.Text(" · 跳过不适用项：", " · Unsupported settings skipped: ") + num;
			}
			return text2;
		}

		private static void RestoreBundlePreferences(List<string> members, bool[] previous)
		{
			for (int i = 0; i < members.Count; i++)
			{
				bool flag;
				try
				{
					flag = EnvironmentPreference(members[i]) == previous[i] || Settings.SaveDurable(members[i], previous[i]);
				}
				catch
				{
					flag = false;
				}
				if (!flag)
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
			bool[] array = new bool[members.Count];
			bool flag = false;
			lock (gate)
			{
				if (EnvironmentWorkClosing || busy.Count > 0)
				{
					return false;
				}
				for (int i = 0; i < members.Count; i++)
				{
					array[i] = EnvironmentPreference(members[i]);
				}
				for (int j = 0; j < members.Count; j++)
				{
					bool flag2;
					try
					{
						flag2 = Settings.SaveDurable(members[j], on);
					}
					catch (Exception ex)
					{
						flag2 = false;
						Logger.Warn("Environment preference save | " + ex.Message);
					}
					if (!flag2)
					{
						RestoreBundlePreferences(members, array);
						return false;
					}
				}
				busy.Add(busyKey);
				try
				{
					flag = QueueEnvironmentWork(delegate
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
				if (!flag)
				{
					lock (gate)
					{
						busy.Remove(busyKey);
						Monitor.PulseAll(gate);
						RestoreBundlePreferences(members, array);
					}
				}
			}
			return flag;
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
			string text = State(key);
			if (StorageRecoveryPending(key, owned) && text != "restore-failed" && text != "working")
			{
				return StorageRecoveryPendingText;
			}
			if (text.Length == 0)
			{
				if (!EnvironmentPreference(key))
				{
					return Label("off");
				}
				return Lang.T("env.performance.pending");
			}
			if (!(text == "ready"))
			{
				return Label(text);
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
			string text = GroupState(list, EnvironmentPreference);
			switch (text)
			{
			case "working":
			case "restore-failed":
			case "stopped":
			case "failed":
				return text;
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
					return text;
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
			bool flag;
			try
			{
				flag = RestoreOwnedVbs();
			}
			catch
			{
				flag = false;
			}
			if (flag)
			{
				ActionFailureStore.CancelApply("EnvVbsDisabledV1", "system");
			}
			else
			{
				ActionFailureStore.Trip("EnvVbsDisabledV1", "system", "Recovery incomplete after skipped VBS apply");
			}
			SetState("EnvVbsDisabledV1", flag ? state : "restore-failed");
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
				bool flag;
				object state;
				if (!(on ? (!VbsConfigured()) : VbsTweak.OwnsState))
				{
					string text = Settings.LoadStr("EnvVbsDisabledV1State", "");
					flag = !on || ActionFailureStore.CompleteApply("EnvVbsDisabledV1", "system");
					if (flag)
					{
						bool num;
						if (!on)
						{
							num = text == "restored";
						}
						else
						{
							if (text == "configured")
							{
								goto IL_0117;
							}
							num = VbsTweak.OwnsState;
						}
						if (num)
						{
							goto IL_0117;
						}
						state = (on ? "external" : "off");
					}
					else
					{
						state = "stopped";
					}
					goto IL_012d;
				}
				string text2 = Boot();
				if (!BootSessionIdentity.Valid(text2) || EnvironmentWorkClosing)
				{
					if (on)
					{
						ActionFailureStore.CancelApply("EnvVbsDisabledV1", "system");
					}
					SetState("EnvVbsDisabledV1", on ? "unverified" : "restore-failed");
					return false;
				}
				bool flag2 = on ? Settings.SaveStrDurable("EnvVbsDisabledV1Boot", text2) && ChangeVbs(true)
					: RestoreOwnedVbs(() => Settings.SaveStrDurable("EnvVbsDisabledV1Boot", text2));
				if (flag2)
				{
					flag2 = (on ? VbsConfigured() : (!VbsTweak.OwnsState));
				}
				if (!flag2)
				{
					throw new InvalidOperationException("VBS apply or readback failed");
				}
				if (on)
				{
					flag2 = ActionFailureStore.CompleteApply("EnvVbsDisabledV1", "system");
					if (!flag2 && !RestoreOwnedVbs())
					{
						SetState("EnvVbsDisabledV1", "restore-failed");
						return false;
					}
				}
				SetState("EnvVbsDisabledV1", (!flag2) ? "stopped" : (on ? "configured" : "restored"));
				return flag2;
				IL_0117:
				state = (on ? "configured" : "restored");
				goto IL_012d;
				IL_012d:
				SetState("EnvVbsDisabledV1", (string)state);
				return flag;
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
			bool flag = false;
			try
			{
				flag = Monitor.TryEnter(gate, timeoutMs);
				if (!flag)
				{
					return false;
				}
				while (busy.Count > 0)
				{
					int num = (int)Math.Max(0L, timeoutMs - stopwatch.ElapsedMilliseconds);
					if (num <= 0)
					{
						return false;
					}
					Monitor.Wait(gate, num);
				}
				return true;
			}
			finally
			{
				if (flag)
				{
					Monitor.Exit(gate);
				}
			}
		}
	}
}
