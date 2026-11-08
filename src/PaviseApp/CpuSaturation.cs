using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal sealed class CpuSaturation
	{
		public const double EnterUtilization = 0.9;

		public const double ExitUtilization = 0.8;

		public const long EnterHoldTicks = 100000000L;

		public const long ExitHoldTicks = 50000000L;

		public const long StaleTicks = 100000000L;

		private const long NotTiming = -1L;

		private readonly double enterUtilization;

		private readonly double exitUtilization;

		internal CpuSaturation() : this(EnterUtilization, ExitUtilization) { }

		internal CpuSaturation(double enter, double exit)
		{
			if (!(enter > 0 && enter <= 1 && exit >= 0 && exit < enter))
				throw new ArgumentOutOfRangeException("enter", "CPU thresholds must satisfy 0 <= exit < enter <= 1.");
			enterUtilization = enter;
			exitUtilization = exit;
		}

		private bool saturated;

		private long enterSince = -1L;

		private long exitSince = -1L;

		private long staleSince = -1L;

		private long lastIdle = -1L;

		private long lastTotal = -1L;

		public bool Saturated
		{
			get
			{
				return saturated;
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

		public bool Update(double utilization, long now)
		{
			if (double.IsNaN(utilization))
			{
				enterSince = -1L;
				exitSince = -1L;
				if (staleSince == -1)
				{
					staleSince = now;
				}
				else if (saturated && now - staleSince >= 100000000)
				{
					saturated = false;
				}
				return saturated;
			}
			staleSince = -1L;
			if (!saturated)
			{
				if (utilization >= enterUtilization)
				{
					if (enterSince == -1)
					{
						enterSince = now;
					}
					else if (now - enterSince >= 100000000)
					{
						saturated = true;
						enterSince = -1L;
						exitSince = -1L;
					}
				}
				else
				{
					enterSince = -1L;
				}
			}
			else if (utilization < exitUtilization)
			{
				if (exitSince == -1)
				{
					exitSince = now;
				}
				else if (now - exitSince >= 50000000)
				{
					saturated = false;
					enterSince = -1L;
					exitSince = -1L;
				}
			}
			else
			{
				exitSince = -1L;
			}
			return saturated;
		}

		public void Reset()
		{
			saturated = false;
			enterSince = -1L;
			exitSince = -1L;
			staleSince = -1L;
			lastIdle = -1L;
			lastTotal = -1L;
		}

		public double Sample()
		{
			long idle;
			long kernel;
			long user;
			if (!GetSystemTimes(out idle, out kernel, out user))
			{
				return double.NaN;
			}
			long kernelOffset = kernel + user;
			double result = double.NaN;
			if (lastIdle >= 0)
			{
				result = Utilization(idle - lastIdle, kernelOffset - lastTotal);
			}
			lastIdle = idle;
			lastTotal = kernelOffset;
			return result;
		}

		internal static double Utilization(long idleDelta, long totalDelta)
		{
			if (totalDelta <= 0 || idleDelta < 0 || idleDelta > totalDelta)
			{
				return double.NaN;
			}
			return 1.0 - (double)idleDelta / (double)totalDelta;
		}
	}
}
