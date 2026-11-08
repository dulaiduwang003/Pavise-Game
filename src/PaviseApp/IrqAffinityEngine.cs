using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
	internal sealed class IrqAffinityEngine
	{
		private sealed class Target
		{
			public string DeviceId;

			public ReversibleReg Policy;

			public ReversibleReg Mask;
		}

		private const int PolicySpecifiedProcessors = 4;

		private readonly string settingsKey;

		private readonly string slotPrefix;

		private readonly string logPrefix;

		internal IrqWriteResult LastResult = new IrqWriteResult();

		public bool EnabledByPavise
		{
			get
			{
				return Settings.Load(settingsKey, false);
			}
		}

		public bool HasResidue
		{
			get
			{
				if (!EnabledByPavise)
				{
					return LoadTouched().Count > 0;
				}
				return true;
			}
		}

		private string TouchedKey
		{
			get
			{
				return slotPrefix + "Touched";
			}
		}

		public IrqAffinityEngine(string settingsKey, string slotPrefix, string logPrefix)
		{
			this.settingsKey = settingsKey;
			this.slotPrefix = slotPrefix;
			this.logPrefix = logPrefix;
		}

		public List<string> TouchedDevices()
		{
			return LoadTouched();
		}

		public bool TryTouchedDevices(out List<string> ids)
		{
			ids = new List<string>();
			string value;
			if (!Settings.TryLoadStr(TouchedKey, out value))
			{
				return false;
			}
			string[] parts = (value ?? "").Split('\n');
			foreach (string entryText in parts)
			{
				string entryTextTrimmed = entryText.Trim();
				if (entryTextTrimmed.Length > 0 && !ids.Contains(entryTextTrimmed))
				{
					ids.Add(entryTextTrimmed);
				}
			}
			return true;
		}

		private static string StableSlot(string deviceId)
		{
			int stableSlotValue = 17;
			foreach (char c in deviceId)
			{
				stableSlotValue = stableSlotValue * 31 + c;
			}
			return stableSlotValue.ToString("X8");
		}

		internal static byte[] MaskToBytes(ulong mask)
		{
			byte[] rawBytes = new byte[8];
			for (int i = 0; i < 8; i++)
			{
				rawBytes[i] = (byte)((mask >> i * 8) & 0xFF);
			}
			return rawBytes;
		}

		internal static ulong BytesToMask(byte[] b)
		{
			if (b == null || b.Length < 8)
			{
				return 0uL;
			}
			ulong bytesToMaskResult = 0uL;
			for (int i = 0; i < 8; i++)
			{
				bytesToMaskResult |= (ulong)b[i] << i * 8;
			}
			return bytesToMaskResult;
		}

		private List<Target> BuildTargets(List<string> deviceIds)
		{
			List<Target> list = new List<Target>();
			foreach (string deviceId in deviceIds)
			{
				string subKey = "SYSTEM\\CurrentControlSet\\Enum\\" + deviceId + "\\Device Parameters\\Interrupt Management\\Affinity Policy";
				string slotPrefixOffset = slotPrefix + StableSlot(deviceId);
				list.Add(new Target
				{
					DeviceId = deviceId,
					Policy = new ReversibleReg(Registry.LocalMachine, subKey, "DevicePolicy", RegistryValueKind.DWord, slotPrefixOffset + "_Policy"),
					Mask = new ReversibleReg(Registry.LocalMachine, subKey, "AssignmentSetOverride", RegistryValueKind.Binary, slotPrefixOffset + "_Mask")
				});
			}
			return list;
		}

		internal static void ReportMsiState(List<string> deviceIds)
		{
			if (deviceIds == null)
			{
				return;
			}
			foreach (string deviceId in deviceIds)
			{
				try
				{
					string name = "SYSTEM\\CurrentControlSet\\Enum\\" + deviceId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties";
					using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(name))
					{
						object value = ((registryKey == null) ? null : registryKey.GetValue("MSISupported"));
						if (value == null)
						{
							Logger.Log(Lang.T("log.irqaffinityengine.1") + deviceId + Lang.T("log.irqaffinityengine.2"));
						}
						else if (Convert.ToInt64(value) == 0)
						{
							Logger.Log(Lang.T("log.irqaffinityengine.1") + deviceId + Lang.T("log.irqaffinityengine.3"));
						}
					}
				}
				catch
				{
				}
			}
		}

		public bool Enable(List<string> deviceIds)
		{
			return Enable(deviceIds, CpuTopology.BoostMask);
		}

		internal static bool SupportsExactMask(bool multiGroup, ulong mask, ulong all)
		{
			if (!multiGroup && mask != 0 && all != 0)
			{
				return (mask & ~all) == 0;
			}
			return false;
		}

		public bool Enable(List<string> deviceIds, ulong preferredMask)
		{
			LastResult = new IrqWriteResult();
			if (!SupportsExactMask(CpuTopology.MultiGroup, preferredMask, CpuTopology.AllMask))
			{
				LastResult.Failed = ((deviceIds == null) ? 1 : deviceIds.Count);
				LastResult.Failure = Lang.T("irq.exact.unsupported");
				return false;
			}
			ReportMsiState(deviceIds);
			if (deviceIds == null || deviceIds.Count == 0)
			{
				Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.4"));
				return false;
			}
			List<Target> list = BuildTargets(deviceIds);
			List<string> ids;
			if (!TryTouchedDevices(out ids))
			{
				LastResult.Failed = deviceIds.Count;
				LastResult.Failure = Lang.T("irq.write.journalfailed");
				return false;
			}
			List<string> list2 = new List<string>(ids);
			foreach (Target item in list)
			{
				if (!list2.Contains(item.DeviceId))
				{
					list2.Add(item.DeviceId);
				}
			}
			if (!SaveTouched(list2))
			{
				LastResult.Failed = deviceIds.Count;
				LastResult.Failure = Lang.T("irq.write.journalfailed");
				Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.8"));
				return false;
			}
			object policyValue = 4;
			byte[] maskBytes = MaskToBytes(preferredMask);
			bool enableReady = false;
			string stamp = BootStamp();
			List<Target> list3 = new List<Target>();
			List<string> list4 = new List<string>();
			foreach (Target t in list)
			{
				bool configured = t.Policy.Matches(policyValue) && t.Mask.Matches(maskBytes) && (ids.Contains(t.DeviceId) || (!t.Policy.HasBackup && !t.Mask.HasBackup));
				string bootKey = BootKeyFor(t.DeviceId);
				int rollbackFailed = LastResult.RollbackFailed;
				bool attempted;
				if (ApplyTarget(LastResult, configured, stamp, (string value) => Settings.SaveStr(bootKey, value), () => Settings.LoadStr(bootKey, ""), () => t.Policy.Apply(policyValue) & t.Mask.Apply(maskBytes), () => t.Policy.Restore() & t.Mask.Restore(), out attempted))
				{
					enableReady = true;
					if (attempted)
					{
						list3.Add(t);
					}
				}
				else
				{
					if (LastResult.RollbackFailed > rollbackFailed)
					{
						list4.Add(t.DeviceId);
					}
					Logger.Log(logPrefix + t.DeviceId + " " + LastResult.Describe());
				}
			}
			if (!enableReady)
			{
				foreach (string item2 in list4)
				{
					if (!ids.Contains(item2))
					{
						ids.Add(item2);
					}
				}
				SaveTouched(ids);
				return false;
			}
			foreach (Target item3 in list3)
			{
				if (!ids.Contains(item3.DeviceId))
				{
					ids.Add(item3.DeviceId);
				}
			}
			foreach (string item4 in list4)
			{
				if (!ids.Contains(item4))
				{
					ids.Add(item4);
				}
			}
			bool saveTouchedSucceeded = SaveTouched(ids);
			Settings.Save(settingsKey, true);
			if (!saveTouchedSucceeded || !Settings.Load(settingsKey, false))
			{
				List<string> list5 = new List<string>();
				foreach (Target item5 in list3)
				{
					if (!(item5.Policy.Restore() & item5.Mask.Restore()))
					{
						list5.Add(item5.DeviceId);
					}
				}
				List<string> list6 = new List<string>();
				foreach (string item6 in ids)
				{
					bool enableReady2 = false;
					foreach (Target item7 in list3)
					{
						if (string.Equals(item7.DeviceId, item6, StringComparison.OrdinalIgnoreCase))
						{
							enableReady2 = true;
							break;
						}
					}
					if (!enableReady2 || list5.Contains(item6))
					{
						list6.Add(item6);
					}
				}
				SaveTouched(list6);
				LastResult.Failed += list3.Count;
				LastResult.Succeeded -= list3.Count;
				LastResult.RollbackFailed += list5.Count;
				LastResult.RolledBack += list3.Count - list5.Count;
				LastResult.Failure = Lang.T("irq.write.journalfailed");
				Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.9"));
				return false;
			}
			if (list3.Count > 0)
			{
				Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.10") + list3.Count + Lang.T("log.irqaffinityengine.11") + Lang.T("log.irqaffinityengine.12") + preferredMask.ToString("X") + " " + Lang.T("log.irqaffinityengine.14"));
			}
			if (LastResult.Failed == 0)
			{
				return LastResult.RollbackFailed == 0;
			}
			return false;
		}

		private string BootKeyFor(string deviceId)
		{
			return slotPrefix + StableSlot(deviceId ?? "") + "_BootStamp";
		}

		public IrqRebootState GetRebootState(string deviceId)
		{
			return RebootStateFromStamps(Settings.LoadStr(BootKeyFor(deviceId), ""), BootStamp());
		}

		internal static bool RebootedSinceStamp(string saved, string now)
		{
			return RebootStateFromStamps(saved, now) == IrqRebootState.Rebooted;
		}

		internal static IrqRebootState RebootStateFromStamps(string saved, string now)
		{
			return BootSessionIdentity.Compare(saved, now);
		}

		internal static bool ApplyTarget(IrqWriteResult result, bool configured, string stamp, Func<string, bool> save, Func<string> load, Func<bool> apply, Func<bool> restore, out bool attempted)
		{
			attempted = false;
			bool applyTargetReady = false;
			try
			{
				applyTargetReady = ApplyWithBootStamp(configured, stamp, save, load, apply, out attempted);
			}
			catch
			{
			}
			if (attempted)
			{
				result.Attempted++;
			}
			if (applyTargetReady)
			{
				result.Succeeded++;
				if (!attempted)
				{
					result.Unchanged++;
				}
				return true;
			}
			result.Failed++;
			if (attempted)
			{
				bool applyTargetReady2 = false;
				try
				{
					applyTargetReady2 = restore();
				}
				catch
				{
				}
				if (applyTargetReady2)
				{
					result.RolledBack++;
				}
				else
				{
					result.RollbackFailed++;
				}
			}
			return false;
		}

		internal static bool ApplyWithBootStamp(bool alreadyConfigured, string stamp, Func<string, bool> saveStamp, Func<string> loadStamp, Func<bool> apply, out bool attempted)
		{
			attempted = false;
			if (alreadyConfigured)
			{
				return true;
			}
			if (!BootSessionIdentity.Valid(stamp) || saveStamp == null || loadStamp == null || apply == null)
			{
				return false;
			}
			try
			{
				if (!saveStamp(stamp) || !string.Equals(loadStamp(), stamp, StringComparison.Ordinal))
				{
					return false;
				}
			}
			catch
			{
				return false;
			}
			attempted = true;
			return apply();
		}

		internal void RememberBootStampForTest(string deviceId)
		{
			Settings.SaveStr(BootKeyFor(deviceId), BootStamp());
		}

		public void ForgetBootStamp(string deviceId)
		{
			Settings.Remove(BootKeyFor(deviceId));
		}

		internal static string BootStamp()
		{
			return BootSessionIdentity.Read() ?? "";
		}

		private List<string> LoadTouched()
		{
			List<string> list = new List<string>();
			string[] loadStrParts = Settings.LoadStr(TouchedKey, "").Split('\n');
			foreach (string entryText in loadStrParts)
			{
				string entryTextTrimmed = entryText.Trim();
				if (entryTextTrimmed.Length > 0 && !list.Contains(entryTextTrimmed))
				{
					list.Add(entryTextTrimmed);
				}
			}
			return list;
		}

		private bool SaveTouched(List<string> ids)
		{
			string joinResult = string.Join("\n", ids.ToArray());
			string value;
			if (Settings.SaveStr(TouchedKey, joinResult) && Settings.TryLoadStr(TouchedKey, out value))
			{
				return value == joinResult;
			}
			return false;
		}

		public bool RestoreOnly(List<string> deviceIds)
		{
			if (deviceIds == null || deviceIds.Count == 0)
			{
				return true;
			}
			List<string> ids;
			if (!TryTouchedDevices(out ids))
			{
				return false;
			}
			return RestoreTouchedScope(ids, deviceIds, delegate(string id)
			{
				Target target = BuildTargets(new List<string> { id })[0];
				return target.Policy.Restore() & target.Mask.Restore();
			}, SaveTouched, ForgetBootStamp, () => Settings.Save(settingsKey, false) && !Settings.Load(settingsKey, true));
		}

		internal static bool RestoreTouchedScope(List<string> touched, List<string> selected, Func<string, bool> restore, Func<List<string>, bool> save, Action<string> forgetBoot, Func<bool> disable)
		{
			if (touched == null || selected == null)
			{
				return false;
			}
			List<string> list = new List<string>(touched);
			bool restoreTouchedScopeSucceeded = true;
			foreach (string item in selected)
			{
				bool restoreTouchedScopeReady = false;
				try
				{
					restoreTouchedScopeReady = restore(item);
				}
				catch
				{
				}
				if (!restoreTouchedScopeReady)
				{
					restoreTouchedScopeSucceeded = false;
					continue;
				}
				try
				{
					if (forgetBoot != null)
					{
						forgetBoot(item);
					}
				}
				catch
				{
					restoreTouchedScopeSucceeded = false;
					continue;
				}
				for (int listIndex = list.Count - 1; listIndex >= 0; listIndex--)
				{
					if (string.Equals(list[listIndex], item, StringComparison.OrdinalIgnoreCase))
					{
						list.RemoveAt(listIndex);
					}
				}
			}
			if (restoreTouchedScopeSucceeded && list.Count == 0)
			{
				try
				{
					if (disable == null || !disable())
					{
						return false;
					}
				}
				catch
				{
					return false;
				}
			}
			try
			{
				if (save == null || !save(list))
				{
					restoreTouchedScopeSucceeded = false;
				}
			}
			catch
			{
				restoreTouchedScopeSucceeded = false;
			}
			return restoreTouchedScopeSucceeded;
		}

		public bool Disable(List<string> deviceIds)
		{
			List<string> ids;
			if (!TryTouchedDevices(out ids))
			{
				return false;
			}
			if (deviceIds != null)
			{
				foreach (string deviceId in deviceIds)
				{
					if (!ids.Contains(deviceId))
					{
						ids.Add(deviceId);
					}
				}
			}
			List<Target> list = BuildTargets(ids);
			bool disableSucceeded = true;
			int processedCount = 0;
			List<string> list2 = new List<string>();
			foreach (Target item in list)
			{
				bool matchesHasBackup = item.Policy.HasBackup || item.Mask.HasBackup;
				bool matchesRestore = item.Policy.Restore() & item.Mask.Restore();
				if (matchesHasBackup && matchesRestore)
				{
					processedCount++;
				}
				if (matchesRestore)
				{
					ForgetBootStamp(item.DeviceId);
				}
				if (!matchesRestore)
				{
					disableSucceeded = false;
					list2.Add(item.DeviceId);
				}
			}
			if (!SaveTouched(list2))
			{
				disableSucceeded = false;
			}
			if (disableSucceeded)
			{
				Settings.Save(settingsKey, false);
				if (Settings.Load(settingsKey, true))
				{
					return false;
				}
				Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.22") + processedCount + Lang.T("log.irqaffinityengine.23"));
			}
			else
			{
				Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.24") + list2.Count + Lang.T("log.irqaffinityengine.25"));
			}
			return disableSucceeded;
		}
	}
}
