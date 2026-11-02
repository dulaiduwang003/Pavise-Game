using System.Globalization;

namespace PaviseApp
{
	internal static class GpuPowerMax
	{
		internal sealed class State
		{
			internal string Vendor;

			internal string Id;

			internal long Current;

			internal long Maximum;
		}

		internal const string SnapKey = "GpuPowerSnap";

		private static readonly object lk = new object();

		private static string VendorGpuId(GpuVendor vendor)
		{
			GpuAdapter gpuAdapter = null;
			GpuAdapter[] array = GpuInventory.Adapters();
			foreach (GpuAdapter gpuAdapter2 in array)
			{
				if (gpuAdapter2.Vendor == vendor)
				{
					if (gpuAdapter != null)
					{
						return "";
					}
					gpuAdapter = gpuAdapter2;
				}
			}
			object obj;
			if (gpuAdapter != null)
			{
				obj = GpuInventory.VenDevKey(gpuAdapter.HardwareId);
				if (obj == null)
				{
					return "";
				}
			}
			else
			{
				obj = "";
			}
			return (string)obj;
		}

		private static bool Read(string vendor, out State state)
		{
			state = null;
			GpuVendor vendor2 = ((vendor == "nv") ? GpuVendor.Nvidia : GpuVendor.Amd);
			string text = VendorGpuId(vendor2);
			if (text.Length == 0)
			{
				return false;
			}
			if (vendor == "nv")
			{
				uint current;
				uint def;
				uint max;
				if (!NvApi.Available || !NvApi.TryGetPowerLimit(out current, out def, out max) || current == 0 || max < current)
				{
					return false;
				}
				state = new State
				{
					Vendor = vendor,
					Id = text,
					Current = current,
					Maximum = max
				};
			}
			else
			{
				int current2;
				int max2;
				if (!AdlxTweaks.PowerLimitGetFirst(out current2, out max2) || max2 < current2)
				{
					return false;
				}
				state = new State
				{
					Vendor = vendor,
					Id = text,
					Current = current2,
					Maximum = max2
				};
			}
			return true;
		}

		private static bool Write(State state, long value)
		{
			State state2;
			if (!Read(state.Vendor, out state2) || state2.Id != state.Id || value > state2.Maximum)
			{
				return false;
			}
			if (state.Vendor == "nv")
			{
				if (value > 0 && value <= uint.MaxValue)
				{
					return NvApi.TrySetPowerLimit((uint)value);
				}
				return false;
			}
			if (value >= int.MinValue && value <= int.MaxValue)
			{
				return AdlxTweaks.PowerLimitSetFirst((int)value);
			}
			return false;
		}

		private static bool Save(string text)
		{
			if (Settings.SaveStrDurable("GpuPowerSnap", text))
			{
				return Settings.LoadStr("GpuPowerSnap", "!") == text;
			}
			return false;
		}

		public static bool Restore()
		{
			lock (lk)
			{
				string value;
				if (!Settings.TryLoadStr("GpuPowerSnap", out value))
				{
					return false;
				}
				if (value.Length == 0)
				{
					return true;
				}
				string[] array = value.Split(':');
				long result;
				long result2;
				if (array.Length != 4 || (array[0] != "nv" && array[0] != "amd") || array[1].Length == 0 || !long.TryParse(array[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || !long.TryParse(array[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out result2) || result > result2)
				{
					return false;
				}
				State state;
				if (!Read(array[0], out state) || state.Id != array[1])
				{
					return false;
				}
				if (state.Current != result2)
				{
					return Save("");
				}
				if (!Write(state, result))
				{
					return false;
				}
				State state2;
				if (!Read(array[0], out state2) || state2.Id != array[1] || state2.Current != result)
				{
					return false;
				}
				if (!Save(""))
				{
					return false;
				}
				Logger.Log("GPU power limit: original value restored and verified");
				return true;
			}
		}

		public static bool HasResidue()
		{
			string value;
			if (Settings.TryLoadStr("GpuPowerSnap", out value))
			{
				return !string.IsNullOrEmpty(value);
			}
			return true;
		}

		public static void HealFromCrash()
		{
			if (HasResidue())
			{
				Restore();
			}
		}
	}
}
