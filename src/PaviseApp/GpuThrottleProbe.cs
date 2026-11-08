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
			IntPtr[] gpusSnapshot;
			lock (lk)
			{
				if (!gpusResolved)
				{
					gpus = NvApi.EnumGpuHandles();
					gpusResolved = true;
				}
				gpusSnapshot = gpus;
			}
			if (gpusSnapshot == null)
			{
				lock (lk)
				{
					gpusResolved = false;
				}
				return false;
			}
			bool readMaskSucceeded = false;
			IntPtr[] nativeHandleItems = gpusSnapshot;
			foreach (IntPtr gpu in nativeHandleItems)
			{
				uint mask;
				if (NvApi.TryGetPerfDecrease(gpu, out mask))
				{
					combined |= mask;
					readMaskSucceeded = true;
				}
			}
			if (!readMaskSucceeded)
			{
				lock (lk)
				{
					gpusResolved = false;
					gpus = null;
				}
			}
			return readMaskSucceeded;
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
