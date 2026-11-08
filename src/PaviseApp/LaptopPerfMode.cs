using System;
using System.Globalization;
using System.Management;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
	internal static class LaptopPerfMode
	{
		private enum Vendor
		{
			None,
			Lenovo,
			Asus,
			AsusGHelper
		}

		internal interface IBackend
		{
			bool Read(int vendor, out int mode);

			bool Write(int vendor, int mode);
		}

		internal interface IPerformanceBackend
		{
			bool IsPerformance(int vendor, int mode);
		}

		private sealed class FirmwareBackend : IBackend, IPerformanceBackend
		{
			public bool IsPerformance(int vendor, int mode)
			{
				return vendor == (int)Vendor.AsusGHelper ? AsusPerformanceController.IsPerformance(mode)
					: mode == PerformanceModeOf((Vendor)vendor);
			}

			public bool Read(int vendor, out int mode)
			{
				if (vendor == (int)Vendor.AsusGHelper) return AsusPerformanceController.Read(out mode);
				return ReadMode((Vendor)vendor, out mode);
			}

			public bool Write(int vendor, int mode)
			{
				if (vendor == (int)Vendor.AsusGHelper) return AsusPerformanceController.Write(mode);
				return WriteMode((Vendor)vendor, mode);
			}
		}

		internal const string SnapKey = "LaptopPerfSnap";

		internal const string PreferenceKey = "AutoLaptopPerformanceV2";

		private const string Scope = "root\\WMI";

		private const int LenovoPerformance = 3;

		private static readonly object lk = new object();

#if PAVISE_SELFTEST
		internal static IBackend BackendForTest;
		internal static Func<string, bool> SaveSnapshotForTest;
		internal static Func<string> ReadSnapshotForTest;
		internal static Func<bool> OnAcPowerForTest;
		private sealed class UnavailableBackend : IBackend
		{
			public bool Read(int vendor, out int mode) { mode = -1; return false; }
			public bool Write(int vendor, int mode) { return false; }
		}
		private static readonly IBackend firmware = new UnavailableBackend();
#else
		private static readonly IBackend firmware = new FirmwareBackend();
#endif
		private static DateTime supportExpires;
		private static int supportedVendor;
		private static bool sessionObserved, sessionYielded, applyConfirmed;
		private static int sessionVendor;
		private static int sessionExpectedMode;
		private static string sessionReceipt;
		private static volatile string lastStatus = "off", lastDetail = "";
		private static volatile string supportDetail = "";

		public static string LastStatus { get { return lastStatus; } }
		public static string LastDetail { get { return lastDetail; } }
		public static string SupportDetail { get { return supportDetail; } }

		public static bool HasResidue
		{
			get
			{
				string value;
				if (ReadSnapshot(out value))
				{
					return !string.IsNullOrEmpty(value);
				}
				return true;
			}
		}

		private static IBackend Backend
		{
			get
			{
#if PAVISE_SELFTEST
				if (BackendForTest != null) return BackendForTest;
#endif
				return firmware;
			}
		}

		private static bool SaveSnapshot(string value)
		{
#if PAVISE_SELFTEST
			if (SaveSnapshotForTest != null) return SaveSnapshotForTest(value);
#endif
			return Settings.SaveStrDurable("LaptopPerfSnap", value);
		}

		private static bool ReadSnapshot(out string value)
		{
			value = null;
			try
			{
#if PAVISE_SELFTEST
				if (ReadSnapshotForTest != null) { value = ReadSnapshotForTest(); return value != null; }
#endif
				return Settings.TryLoadStr(SnapKey, out value) && value != null;
			}
			catch { return false; }
		}

		private static bool PersistSnapshot(string value)
		{
			try
			{
				string check;
				return SaveSnapshot(value) && ReadSnapshot(out check) && string.Equals(check, value, StringComparison.Ordinal);
			}
			catch { return false; }
		}

		private static string Text(string chinese, string english) { return Lang.Cur == 1 ? english : chinese; }
		private static bool Result(string status, string chinese, string english, bool ok)
		{ lastDetail = Text(chinese, english); lastStatus = status; return ok; }

		private static bool OnAcPower()
		{
#if PAVISE_SELFTEST
			if (BackendForTest != null) return OnAcPowerForTest == null || OnAcPowerForTest();
#endif
			try { return Native.ConfirmedAcPower(); } catch { return false; }
		}

		// Only call this bounded WMI probe from a background worker, not a UI paint/poll.
		public static bool SupportedCached()
		{
			lock (lk)
			{
				if (DateTime.UtcNow < supportExpires) return supportedVendor != 0;
				supportedVendor = 0;
				supportDetail = "";
				supportExpires = DateTime.UtcNow.AddSeconds(30);
#if PAVISE_SELFTEST
				bool laptop = BackendForTest != null;
#else
				bool laptop;
				try { laptop = Native.HasSystemBattery(); } catch { laptop = false; }
#endif
				if (!laptop) return false;
				IBackend device = Backend;
				foreach (int vendor in new[] { (int)Vendor.Lenovo, (int)Vendor.AsusGHelper, (int)Vendor.Asus })
				{
					int mode;
					try { if (device.Read(vendor, out mode) && KnownMode(vendor, mode)) { supportedVendor = vendor; break; } }
					catch { }
				}
#if !PAVISE_SELFTEST
				if (supportedVendor == (int)Vendor.AsusGHelper) supportDetail = AsusPerformanceController.Detail;
				else if (supportedVendor == (int)Vendor.Lenovo) supportDetail = Text("联想 SmartFan：原档读取与切档核验可用", "Lenovo SmartFan: original-mode reads and switch verification available");
				else supportDetail = AsusPerformanceController.UnavailableDetail();
#endif
				return supportedVendor != 0;
			}
		}

		public static bool Activate() { return Activate(delegate { return true; }); }

		public static bool Activate(Func<bool> admission)
		{
			lock (lk)
			{
				if (sessionYielded) return Result("external", "用户或厂商已更改性能档，本次对局不再接管", "The user or vendor changed the mode; this session remains yielded", true);
				if (!Admitted(admission)) return Result("waiting-power", "等待插电且有效的游戏会话", "Waiting for AC power and a valid game session", false);
				string receipt;
				if (!ReadSnapshot(out receipt)) return Result("recovery-pending", "无法核验原档恢复记录，未切换厂商档位", "The recovery record could not be verified; no vendor mode was changed", false);
				if (sessionReceipt != null && !string.Equals(receipt, sessionReceipt, StringComparison.Ordinal))
					return Result("recovery-pending", "原档恢复记录已变更，未继续接管", "The original-mode record changed; control is stopped", false);
				if (receipt.Length > 0 && (sessionReceipt == null || !applyConfirmed))
					return Result("recovery-pending", "原档恢复尚未结清，未新增厂商切档", "Original-mode recovery is unresolved; no new mode is applied", false);
				IBackend device = Backend;
				try
				{
					int vendor = sessionObserved ? sessionVendor : (SupportedCached() ? supportedVendor : 0);
					int original;
					if (vendor == 0 || !device.Read(vendor, out original) || original < 0)
						return Result("unsupported", supportDetail.Length > 0 ? supportDetail : "未核验到受支持的联想/华硕标准性能档", supportDetail.Length > 0 ? supportDetail : "No supported Lenovo/ASUS standard performance mode was verified", false);
					int target = PerformanceModeOf((Vendor)vendor);
					IPerformanceBackend performance = device as IPerformanceBackend;
					if (sessionObserved)
					{
						if (original == sessionExpectedMode && (original == target || performance == null || performance.IsPerformance(vendor, original)))
							return Result(sessionReceipt == null ? "already" : "applied", "厂商性能档已就绪", "The vendor performance mode is ready", true);
						return YieldExternal(receipt);
					}
					if (!KnownMode(vendor, original)) return Result("unsupported", "当前为未知或自定义厂商档位，未接管", "The current vendor mode is unknown or custom; it is not controlled", false);
					sessionVendor = vendor;
					if (original == target || (performance != null && performance.IsPerformance(vendor, original)))
					{
						sessionObserved = true;
						sessionExpectedMode = original;
						return Result("already", "原本已处于厂商性能档，未认领或写入", "The performance mode was already selected; nothing was owned or written", true);
					}
					string pending = vendor.ToString(CultureInfo.InvariantCulture) + "|" + original.ToString(CultureInfo.InvariantCulture) + "|" + target.ToString(CultureInfo.InvariantCulture);
					if (!PersistSnapshot(pending)) return Result("recovery-pending", "原档记录未能持久保存并读回，未切档", "The original mode could not be durably saved and read back; no mode was changed", false);
					sessionReceipt = pending;
					sessionObserved = true;
					sessionExpectedMode = target;
					int before;
					if (!Admitted(admission))
					{
						if (!PersistSnapshot("")) return Result("recovery-pending", "已取消切档，但恢复记录尚待结清", "The mode switch was cancelled, but the recovery record still needs settlement", false);
						sessionReceipt = null; sessionObserved = false;
						return Result("waiting-power", "供电或游戏状态已变更，未切换厂商档位", "Power or game state changed; no mode was switched", false);
					}
					if (!device.Read(vendor, out before)) return Result("recovery-pending", "写入前无法再次读取原档，保留恢复记录", "The original mode could not be re-read before writing; its recovery record is retained", false);
					if (before != original) return YieldExternal(pending);
					if (!Admitted(admission)) return Result("recovery-pending", "写入前供电或游戏状态已变更，保留原档记录", "Power or game state changed before writing; the original-mode record is retained", false);
					int check;
					if (!device.Write(vendor, target) || !device.Read(vendor, out check) || check != target)
						return Result("failed", "厂商切档未通过读回，保留原档恢复记录", "The vendor switch did not pass readback; its original-mode record is retained", false);
					applyConfirmed = true;
					Logger.Log(Text("笔记本厂商性能档已切换；退局恢复原档", "Laptop vendor performance mode applied; the original is restored on session exit"));
					return Result("applied", "厂商性能档已应用，退局恢复原档", "Vendor performance mode applied; the original will be restored on session exit", true);
				}
				catch (Exception error) { return Result("failed", "厂商性能档操作失败，保留恢复记录：" + error.GetType().Name, "Vendor mode operation failed; recovery records retained: " + error.GetType().Name, false); }
			}
		}

		private static bool Admitted(Func<bool> admission)
		{ try { return admission != null && admission() && OnAcPower(); } catch { return false; } }

		private static bool YieldExternal(string receipt)
		{
			sessionYielded = true;
			if (receipt.Length > 0 && !PersistSnapshot("")) return Result("recovery-pending", "已让出外部档位，原档记录尚待结清", "The external mode was preserved; the recovery record still needs settlement", false);
			sessionReceipt = null; applyConfirmed = false;
			return Result("external", "用户或厂商已改档，本次对局停止接管", "The user or vendor changed the mode; control is yielded for this session", true);
		}

		public static void EndSession()
		{
			lock (lk) { sessionObserved = sessionYielded = applyConfirmed = false; sessionVendor = sessionExpectedMode = 0; sessionReceipt = null; }
		}

#if PAVISE_SELFTEST
		internal static void ResetForTest()
		{
			lock (lk)
			{
				EndSession(); supportExpires = DateTime.MinValue; supportedVendor = 0;
				BackendForTest = null; SaveSnapshotForTest = null; ReadSnapshotForTest = null; OnAcPowerForTest = null;
				lastStatus = "off"; lastDetail = supportDetail = "";
			}
		}
#endif

		private static bool KnownMode(int vendor, int mode)
		{
			if (vendor == (int)Vendor.AsusGHelper) return mode >= 0 && mode <= 4;
			if (vendor != 1)
			{
				if (vendor == 2 && mode >= 0)
				{
					return mode <= 2;
				}
				return false;
			}
			if (mode >= 1)
			{
				return mode <= 3;
			}
			return false;
		}

		private static ManagementObject First(string className)
		{
			using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM " + className))
			{
				managementObjectSearcher.Options.Timeout = TimeSpan.FromSeconds(3.0);
				using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
				{
					using (ManagementObjectCollection.ManagementObjectEnumerator managementObjectEnumerator = managementObjectCollection.GetEnumerator())
					{
						if (managementObjectEnumerator.MoveNext())
						{
							return (ManagementObject)managementObjectEnumerator.Current;
						}
					}
				}
			}
			return null;
		}

		private static bool ReadMode(Vendor vendor, out int mode)
		{
			mode = -1;
			// ASUS DSTS reports capabilities, not the current profile or legacy restore state.
			if (vendor == Vendor.Asus) return false;
			try
			{
				switch (vendor)
				{
				case Vendor.Lenovo:
				{
					using (ManagementObject managementObject2 = First("LENOVO_GAMEZONE_DATA"))
					{
						if (managementObject2 == null)
						{
							return false;
						}
						using (ManagementBaseObject managementBaseObject3 = managementObject2.InvokeMethod("GetSmartFanMode", null, InvocationOptions()))
						{
							if (managementBaseObject3 == null)
							{
								return false;
							}
							mode = Convert.ToInt32(managementBaseObject3["Data"]);
							return mode >= 0;
						}
					}
				}
				}
			}
			catch
			{
			}
			return false;
		}

		private static bool WriteMode(Vendor vendor, int mode)
		{
			try
			{
				switch (vendor)
				{
				case Vendor.Lenovo:
				{
					using (ManagementObject managementObject2 = First("LENOVO_GAMEZONE_DATA"))
					{
						if (managementObject2 == null)
						{
							return false;
						}
						using (ManagementBaseObject managementBaseObject3 = managementObject2.GetMethodParameters("SetSmartFanMode"))
						{
							managementBaseObject3["Data"] = (uint)mode;
							using (managementObject2.InvokeMethod("SetSmartFanMode", managementBaseObject3, InvocationOptions()))
							{
							}
						}
					}
					int mode3;
					return VerifyReadback(delegate { return ReadMode(vendor, out mode3) ? mode3 : -1; }, mode, delegate { Thread.Sleep(100); });
				}
				}
			}
			catch
			{
			}
			return false;
		}

		private static int PerformanceModeOf(Vendor vendor)
		{
			if (vendor != Vendor.Lenovo)
			{
				return 1;
			}
			return 3;
		}

		internal static bool VerifyReadback(Func<int> read, int expected, Action pause)
		{
			// Lenovo firmware can acknowledge before SmartFan updates; poll without a second firmware write.
			Stopwatch watch = Stopwatch.StartNew();
			for (int attempt = 0; attempt < 8; attempt++)
			{
				int current = read();
				if (current == expected) return true;
				if (current < 0 || watch.ElapsedMilliseconds >= 1500) return false;
				pause();
			}
			return false;
		}

		private static InvokeMethodOptions InvocationOptions()
		{
			InvokeMethodOptions invokeMethodOptions = new InvokeMethodOptions();
			invokeMethodOptions.Timeout = TimeSpan.FromSeconds(3.0);
			return invokeMethodOptions;
		}

		public static bool Restore()
		{
			lock (lk)
			{
				try
				{
					string value;
					if (!ReadSnapshot(out value)) return RecoveryFailed();
					if (sessionReceipt != null && !string.Equals(value, sessionReceipt, StringComparison.Ordinal)) return RecoveryFailed();
					if (value.Length == 0) return true;
					string[] array = value.Split('|');
					int vendorRaw, original;
					if ((array.Length != 2 && array.Length != 3) || !int.TryParse(array[0], out vendorRaw) || !int.TryParse(array[1], out original) || !KnownMode(vendorRaw, original)) return RecoveryFailed();
					int target = PerformanceModeOf((Vendor)vendorRaw);
					if (array.Length == 3 && (!int.TryParse(array[2], out target) || target != PerformanceModeOf((Vendor)vendorRaw))) return RecoveryFailed();
					IBackend device = Backend;
					int current;
					if (!device.Read(vendorRaw, out current) || current < 0) return RecoveryFailed();
					if (current == original)
					{
						if (current != target) sessionYielded = true;
						if (!PersistSnapshot("")) return RecoveryFailed();
						sessionReceipt = null; applyConfirmed = false;
						return Result("restored", "当前已是原厂商档位，未再次写入", "The original vendor mode is already selected; nothing was written", true);
					}
					if (current != target)
					{
						// Any externally selected mode wins, including the original standard mode.
						sessionYielded = true;
						if (!PersistSnapshot("")) return RecoveryFailed();
						sessionReceipt = null; applyConfirmed = false;
						Logger.Log(Lang.T(current == original ? "log.laptopperf.6" : "log.laptopperf.5"));
						return Result("external", "已保留当前厂商档位并停止本对局接管", "The current vendor mode was preserved and this session is yielded", true);
					}
					int check;
					if (!device.Write(vendorRaw, original) || !device.Read(vendorRaw, out check) || check != original) return RecoveryFailed();
					if (!PersistSnapshot("")) return RecoveryFailed();
					sessionReceipt = null; applyConfirmed = false; sessionObserved = false;
					Logger.Log(Lang.T("log.laptopperf.6"));
					return Result("restored", "厂商原性能档已恢复", "The original vendor performance mode was restored", true);
				}
				catch { return RecoveryFailed(); }
			}
		}

		private static bool RecoveryFailed()
		{ return Result("restore-failed", "厂商原档恢复未完成，保留恢复记录", "Original vendor mode recovery is incomplete; its record is retained", false); }

		public static void HealFromCrash()
		{
			if (HasResidue && Restore())
			{
				Logger.Log(Lang.T("log.laptopperf.7"));
			}
		}
	}
}
