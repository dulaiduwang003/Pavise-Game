using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class DisplaySolo
	{
		internal const string SnapKey = "DisplaySoloSnap";

		internal const uint TopologyInternal = 1u;

		internal const uint TopologyClone = 2u;

		internal const uint TopologyExtend = 4u;

		internal const uint TopologyExternal = 8u;

		private const uint QdcOnlyActivePaths = 2u;

		private const uint QdcDatabaseCurrent = 4u;

		private const uint SdcApply = 128u;

		private const int SmRemoteSession = 4096;

		private const uint PathInfoSize = 72u;

		private const uint ModeInfoSize = 64u;

		private static readonly object lk = new object();

		private static bool SettledAsSingleDisplay()
		{
			if (RemoteSession())
			{
				return false;
			}
			return ActivePathCount() == 1;
		}

		public static bool Restore()
		{
			lock (lk)
			{
				string displaySoloSnapValue = Settings.LoadStr("DisplaySoloSnap", "");
				if (displaySoloSnapValue.Length == 0)
				{
					return true;
				}
				uint result;
				if (!uint.TryParse(displaySoloSnapValue, out result) || (result != 2 && result != 4 && result != 8))
				{
					return false;
				}
				uint topology;
				if (!TrySetTopology(result))
				{
					if (!SettledAsSingleDisplay())
					{
						return false;
					}
				}
				else if ((!TryCurrentTopology(out topology) || topology != result) && !SettledAsSingleDisplay())
				{
					return false;
				}
				if (!Settings.SaveStr("DisplaySoloSnap", "") || Settings.LoadStr("DisplaySoloSnap", "").Length != 0)
				{
					return false;
				}
				Logger.Log(Lang.T("log.solo.2"));
				return true;
			}
		}

		public static bool HealFromCrash()
		{
			return Restore();
		}

		public static bool HasResidue()
		{
			return Settings.LoadStr("DisplaySoloSnap", "").Length != 0;
		}

		private static int ActivePathCount()
		{
			try
			{
				uint numPaths;
				uint numModes;
				if (GetDisplayConfigBufferSizes(2u, out numPaths, out numModes) != 0)
				{
					return -1;
				}
				return (int)numPaths;
			}
			catch
			{
				return -1;
			}
		}

		private static bool TryCurrentTopology(out uint topology)
		{
			topology = 0u;
			IntPtr intPtr = IntPtr.Zero;
			IntPtr intPtr2 = IntPtr.Zero;
			try
			{
				uint numPaths;
				uint numModes;
				if (GetDisplayConfigBufferSizes(4u, out numPaths, out numModes) != 0 || numPaths == 0 || numPaths > 64 || numModes > 128)
				{
					return false;
				}
				intPtr = Marshal.AllocHGlobal((int)(numPaths * 72));
				intPtr2 = Marshal.AllocHGlobal((int)(Math.Max(numModes, 1u) * 64));
				if (QueryDisplayConfig(4u, ref numPaths, intPtr, ref numModes, intPtr2, out topology) != 0)
				{
					return false;
				}
				return topology != 0;
			}
			catch
			{
				return false;
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(intPtr);
				}
				if (intPtr2 != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(intPtr2);
				}
			}
		}

		private static bool TrySetTopology(uint topology)
		{
			try
			{
				return SetDisplayConfig(0u, IntPtr.Zero, 0u, IntPtr.Zero, topology | 0x80u) == 0;
			}
			catch
			{
				return false;
			}
		}

		private static bool RemoteSession()
		{
			try
			{
				return GetSystemMetrics(4096) != 0;
			}
			catch
			{
				return true;
			}
		}

		[DllImport("user32.dll")]
		private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

		[DllImport("user32.dll")]
		private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, out uint topologyId);

		[DllImport("user32.dll")]
		private static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

		[DllImport("user32.dll")]
		private static extern int GetSystemMetrics(int index);
	}
}
