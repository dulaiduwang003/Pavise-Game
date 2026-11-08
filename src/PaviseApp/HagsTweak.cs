using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class HagsTweak
	{
		private struct AdapterInfo
		{
			public uint HAdapter;

			public uint LuidLow;

			public int LuidHigh;

			public uint NumOfSources;

			public int PresentMoveRegionsPreferred;
		}

		private struct EnumAdapters
		{
			public uint NumAdapters;

			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
			public AdapterInfo[] Adapters;
		}

		private struct CloseAdapter
		{
			public uint HAdapter;
		}

		private const string GfxKey = "SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";

		private const string Val = "HwSchMode";

		private static readonly ReversibleReg Sch = new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers", "HwSchMode", RegistryValueKind.DWord, "PrevHwSch");

		private static readonly object schedLk = new object();

		private static long schedCheckedTicks;

		private static bool schedInitialized;

		private static bool schedKnown;

		private static bool schedSupported;

		private static bool schedEnabled;

		public static bool EnabledByPavise
		{
			get
			{
				return Settings.Load("HagsOnByPavise", false);
			}
		}

		[DllImport("gdi32.dll")]
		private static extern int D3DKMTEnumAdapters(ref EnumAdapters args);

		[DllImport("gdi32.dll")]
		private static extern int D3DKMTCloseAdapter(ref CloseAdapter args);

		public static bool SchedulingAllowsGpuDemotionCached()
		{
			long timestamp = Stopwatch.GetTimestamp();
			lock (schedLk)
			{
				if (!schedInitialized || timestamp - schedCheckedTicks >= Stopwatch.Frequency * 60)
				{
					schedCheckedTicks = timestamp;
					schedInitialized = true;
					try
					{
						schedKnown = TryQueryState(out schedSupported, out schedEnabled);
					}
					catch
					{
						schedKnown = false;
						schedSupported = (schedEnabled = false);
					}
				}
				return schedKnown && !schedEnabled;
			}
		}

		public static bool TryQueryState(out bool supported, out bool enabled)
		{
			supported = false;
			enabled = false;
			EnumAdapters enumAdapters = default(EnumAdapters);
			enumAdapters.Adapters = new AdapterInfo[16];
			EnumAdapters args = enumAdapters;
			List<HardwareSchedulingObservation> list = new List<HardwareSchedulingObservation>();
			HashSet<uint> hashSet = new HashSet<uint>();
			bool inventoryComplete = false;
			try
			{
				if (D3DKMTEnumAdapters(ref args) != 0 || args.NumAdapters == 0 || args.NumAdapters > 16)
				{
					return false;
				}
				inventoryComplete = true;
				for (int i = 0; i < args.NumAdapters && i < 16; i++)
				{
					AdapterInfo adapterInfo = args.Adapters[i];
					if (adapterInfo.HAdapter == 0 || !hashSet.Add(adapterInfo.HAdapter))
					{
						inventoryComplete = false;
						continue;
					}
					uint luidHigh = (uint)adapterInfo.LuidHigh;
					string verifiedRendererLuid = luidHigh.ToString("x8", CultureInfo.InvariantCulture) + adapterInfo.LuidLow.ToString("x8", CultureInfo.InvariantCulture);
					list.Add(HardwareSchedulingProbe.Query(verifiedRendererLuid));
				}
			}
			catch
			{
				inventoryComplete = false;
			}
			finally
			{
				HashSet<uint> hashSet2 = new HashSet<uint>();
				for (int j = 0; j < args.NumAdapters && j < 16; j++)
				{
					uint hAdapter = args.Adapters[j].HAdapter;
					if (hAdapter == 0)
					{
						inventoryComplete = false;
						continue;
					}
					if (!hashSet2.Add(hAdapter))
					{
						inventoryComplete = false;
						continue;
					}
					try
					{
						CloseAdapter closeAdapter = default(CloseAdapter);
						closeAdapter.HAdapter = hAdapter;
						CloseAdapter args2 = closeAdapter;
						if (D3DKMTCloseAdapter(ref args2) != 0)
						{
							inventoryComplete = false;
						}
					}
					catch
					{
						inventoryComplete = false;
					}
				}
			}
			return AggregateCurrentState(list, inventoryComplete, out supported, out enabled);
		}

		internal static bool AggregateCurrentState(IEnumerable<HardwareSchedulingObservation> observations, bool inventoryComplete, out bool supported, out bool enabled)
		{
			supported = false;
			enabled = false;
			bool inventoryCompleteSnapshot = inventoryComplete;
			int processedCount = 0;
			if (observations == null)
			{
				return false;
			}
			foreach (HardwareSchedulingObservation observation in observations)
			{
				processedCount++;
				if (observation == null || !observation.IsKnown)
				{
					inventoryCompleteSnapshot = false;
					continue;
				}
				supported |= observation.SupportedByDriver == true;
				enabled |= observation.State == HardwareSchedulingState.Enabled;
			}
			if (inventoryCompleteSnapshot)
			{
				return processedCount > 0;
			}
			return false;
		}

		internal static int? ConfiguredMode()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers"))
				{
					if (registryKey == null)
					{
						return null;
					}
					object value = registryKey.GetValue("HwSchMode");
					return (value is int) ? new int?((int)value) : null;
				}
			}
			catch
			{
				return null;
			}
		}

		public static bool CurrentlyOn()
		{
			bool supported;
			bool enabled;
			if (TryQueryState(out supported, out enabled))
			{
				return enabled;
			}
			return false;
		}

		public static bool Enable()
		{
			return HagsPreference.Set(true);
		}

		public static bool HasResidue()
		{
			if (!EnabledByPavise)
			{
				return Sch.HasBackup;
			}
			return true;
		}

		public static bool Restore()
		{
			if (!HagsPreference.UnverifiedLegacyReceipt) return HagsPreference.Restore();
			try
			{
				if (!Sch.HasBackup)
				{
					Settings.Save("HagsOnByPavise", false);
					return !Settings.Load("HagsOnByPavise", true);
				}
				if (!Sch.Restore())
				{
					return false;
				}
				Settings.Save("HagsOnByPavise", false);
				Logger.Log(Lang.T("log.hagstweak.6"));
				return !Settings.Load("HagsOnByPavise", true);
			}
			catch
			{
				return false;
			}
		}

		public static bool Disable()
		{
			return HagsPreference.Set(false);
		}
	}
}
