using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class GpuThrottleProbe
	{
		private static readonly object lk = new object();

		private static IntPtr[] gpus;

		private static bool gpusResolved;

		public static bool TryReadMask(out uint combined)
		{
			combined = 0u;
			if (!NvApi.Available)
			{
				return false;
			}
			IntPtr[] array;
			lock (lk)
			{
				if (!gpusResolved)
				{
					gpus = NvApi.EnumGpuHandles();
					gpusResolved = true;
				}
				array = gpus;
			}
			if (array == null)
			{
				lock (lk)
				{
					gpusResolved = false;
				}
				return false;
			}
			bool flag = false;
			IntPtr[] array2 = array;
			foreach (IntPtr gpu in array2)
			{
				uint mask;
				if (NvApi.TryGetPerfDecrease(gpu, out mask))
				{
					combined |= mask;
					flag = true;
				}
			}
			if (!flag)
			{
				lock (lk)
				{
					gpusResolved = false;
					gpus = null;
				}
			}
			return flag;
		}

		public static string InstantText()
		{
			uint combined;
			if (!TryReadMask(out combined))
			{
				return null;
			}
			if (combined == 0)
			{
				return Lang.T("t.gputhrottleprobe.6");
			}
			List<string> list = new List<string>();
			if ((combined & (true ? 1u : 0u)) != 0)
			{
				list.Add(Lang.T("t.gputhrottleprobe.7"));
			}
			if ((combined & 0x12u) != 0)
			{
				list.Add(Lang.T("t.gputhrottleprobe.8"));
			}
			if ((combined & 4u) != 0)
			{
				list.Add(Lang.T("t.gputhrottleprobe.9"));
			}
			if ((combined & 8u) != 0)
			{
				list.Add(Lang.T("t.gputhrottleprobe.10"));
			}
			if (list.Count == 0)
			{
				return Lang.T("t.gputhrottleprobe.11") + combined.ToString("X");
			}
			return string.Join(" ", list.ToArray());
		}
	}
}
