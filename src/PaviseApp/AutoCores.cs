using System;
using System.Diagnostics;

namespace PaviseApp
{
	internal static class AutoCores
	{
		internal const int MinSpareCores = 2;

		internal const int MinGameLogical = 2;

		internal const int WindowMs = 5000;

		internal const int MaxWindowMs = 60000;

		internal static readonly string[] RetiredSettings = new string[8] { "GmManualCorePlanV1", "GmCoreMask", "GmStrictCores", "GmCoreDomainAlt", "GmHeavySqueezeV2", "GmHeavyCoreMaskV1", "GmHardAffinityV1", "GmAffinityGuardV1" };

		private static volatile AutoCoreStatus status = new AutoCoreStatus();

		internal static long NowTicks
		{
			get
			{
				long timestamp = Stopwatch.GetTimestamp();
				long frequency = Stopwatch.Frequency;
				return timestamp / frequency * 10000000 + timestamp % frequency * 10000000 / frequency;
			}
		}

		internal static AutoCoreStatus Status
		{
			get
			{
				return status;
			}
		}

		internal static bool Supported
		{
			get
			{
				if (IntPtr.Size == 8 && Native.OsBuild() >= 19041)
				{
					return !CpuTopology.MultiGroup;
				}
				return false;
			}
		}

		internal static void Publish(AutoCoreStatus next)
		{
			status = next ?? new AutoCoreStatus();
		}

		internal static ulong WholeCores(ulong mask, ulong[] cores)
		{
			ulong num = 0uL;
			if (cores == null)
			{
				return 0uL;
			}
			foreach (ulong num2 in cores)
			{
				if ((num2 & mask) != 0)
				{
					num |= num2;
				}
			}
			return num;
		}

		internal static int CountBits(ulong mask)
		{
			int num = 0;
			while (mask != 0)
			{
				mask &= mask - 1;
				num++;
			}
			return num;
		}

		internal static int PhysicalCount(ulong mask, ulong[] cores)
		{
			int num = 0;
			if (cores != null)
			{
				foreach (ulong num2 in cores)
				{
					if (num2 != 0 && (mask & num2) == num2)
					{
						num++;
					}
				}
			}
			return num;
		}

		internal static ulong LiveAllocated()
		{
			try
			{
				IsolationCpuState isolationCpuState = new CoreIsolationNative().ReadSystem(IntPtr.Zero);
				return (isolationCpuState == null) ? 0 : isolationCpuState.Allocated;
			}
			catch
			{
				return 0uL;
			}
		}

		internal static string DiagnosticLine()
		{
			AutoCoreStatus autoCoreStatus = Status;
			AutoCorePlan plan = autoCoreStatus.Plan;
			string text = ((plan == null) ? "no manual plan" : (plan.Available ? (plan.Kind + " 0x" + plan.Exclusive.ToString("X") + " (" + plan.Cores + " cores)") : ("unavailable " + plan.Blocked)));
			if (plan != null && plan.Allocated != 0)
			{
				text = text + " · allocated 0x" + plan.Allocated.ToString("X");
			}
			string text2 = (autoCoreStatus.Pending ? "recovery pending" : (autoCoreStatus.ObservationOnly ? "disabled" : ((!autoCoreStatus.Watching) ? "no match" : (autoCoreStatus.Engaged ? "exclusive" : (autoCoreStatus.Exclusive ? "exclusive requested" : ((autoCoreStatus.Blocked != null) ? ("shared (" + autoCoreStatus.Blocked + ")") : "shared"))))));
			return text + " · " + text2 + " · game 0x" + autoCoreStatus.GameMask.ToString("X") + " · hard target 0x" + autoCoreStatus.ExpectedMask.ToString("X") + " · observed " + (autoCoreStatus.AffinityRead ? ("0x" + autoCoreStatus.ObservedMask.ToString("X")) : "unreadable") + " · verified " + autoCoreStatus.AffinityVerified + " · background " + autoCoreStatus.BackgroundMoved + " · work wall " + autoCoreStatus.WorkMs.ToString("F1") + "ms" + (string.IsNullOrEmpty(autoCoreStatus.PlacementEvidence) ? "" : (" · placement {" + autoCoreStatus.PlacementEvidence + "}"));
		}

		internal static string ValidateRange(ulong isolation, ulong game, ulong all, ulong[] cores, ulong allocated, bool manual = false)
		{
			if (all == 0 || cores == null || cores.Length == 0)
			{
				return "topology";
			}
			ulong num = 0uL;
			foreach (ulong num2 in cores)
			{
				if (num2 == 0 || (num2 & ~all) != 0 || (num & num2) != 0)
				{
					return "topology";
				}
				num |= num2;
			}
			if (num != all)
			{
				return "topology";
			}
			if (game == 0 || (game & ~all) != 0 || CountBits(game) < (manual ? 1 : 2))
			{
				return "game";
			}
			if (isolation == 0 || (isolation & ~all) != 0 || (!manual && (isolation & 1) != 0) || WholeCores(isolation, cores) != isolation)
			{
				return "whole";
			}
			int num3 = 0;
			foreach (ulong num4 in cores)
			{
				if ((num4 & isolation) == 0 && (num4 & ~allocated) != 0)
				{
					num3++;
				}
			}
			if (num3 >= 2)
			{
				return null;
			}
			return "spare";
		}

		internal static bool WindowUsable(ThreadTimes before, ThreadTimes after)
		{
			if (before == null || after == null || before.Creation != after.Creation)
			{
				return false;
			}
			long num = after.Ticks - before.Ticks;
			if (num >= 40000000)
			{
				return num <= 600000000;
			}
			return false;
		}
	}
}
