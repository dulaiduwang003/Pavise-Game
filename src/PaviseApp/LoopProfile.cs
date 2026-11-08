using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
	internal static class LoopProfile
	{
		private sealed class Phase
		{
			internal long Count;

			internal long WallTicks;

			internal long MaxWallTicks;

			internal ulong Cycles;

			internal ulong MaxCycles;
		}

		private static readonly Dictionary<string, Phase> phases = new Dictionary<string, Phase>(StringComparer.Ordinal);

		private static readonly List<string> order = new List<string>();

		private static long markTicks;

		private static long iterationStart;

		private static long windowStart;

		private static long iterations;

		private static long maxIterationTicks;

		private static ulong markCycles;

		private static ulong iterationCycles;

		private static ulong maxIterationCycles;

		private static double cyclesPerMs;

		private static readonly object countSync = new object();

		private static readonly Dictionary<string, long> counts = new Dictionary<string, long>(StringComparer.Ordinal);

		private static readonly Dictionary<string, double[]> spans = new Dictionary<string, double[]>(StringComparer.Ordinal);

		private static int gc0;

		private static int gc1;

		private static int gc2;

		[ThreadStatic]
		private static string spanName;

		[ThreadStatic]
		private static ulong spanCycles;

		[ThreadStatic]
		private static long spanTicks;

		[DllImport("kernel32.dll")]
		private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentThread();

		[DllImport("kernel32.dll")]
		private static extern int GetCurrentThreadId();

		[Conditional("PAVISE_LOOP_PROFILE")]
		internal static void Tag(string name)
		{
			Logger.Log("THREAD " + name + " tid=" + GetCurrentThreadId());
		}

		private static ulong Cycles()
		{
			ulong cycles;
			if (!QueryThreadCycleTime(GetCurrentThread(), out cycles))
			{
				return 0uL;
			}
			return cycles;
		}

		[Conditional("PAVISE_LOOP_PROFILE")]
		internal static void Enter(string name)
		{
			if (cyclesPerMs <= 0.0)
			{
				Calibrate();
			}
			spanName = name;
			spanTicks = Stopwatch.GetTimestamp();
			spanCycles = Cycles();
		}

		[Conditional("PAVISE_LOOP_PROFILE")]
		internal static void Leave()
		{
			string spanNameSnapshot = spanName;
			if (spanNameSnapshot == null)
			{
				return;
			}
			spanName = null;
			double cyclesPerMsRatio = (double)(Cycles() - spanCycles) / cyclesPerMs;
			double frequencyRatio = (double)(Stopwatch.GetTimestamp() - spanTicks) * 1000.0 / (double)Stopwatch.Frequency;
			lock (countSync)
			{
				double[] value;
				if (!spans.TryGetValue(spanNameSnapshot, out value))
				{
					value = new double[4];
					spans[spanNameSnapshot] = value;
				}
				value[0] += 1.0;
				value[1] += cyclesPerMsRatio;
				value[2] = Math.Max(value[2], cyclesPerMsRatio);
				value[3] += frequencyRatio;
			}
		}

		[Conditional("PAVISE_LOOP_PROFILE")]
		internal static void Count(string name)
		{
			lock (countSync)
			{
				long value;
				counts.TryGetValue(name, out value);
				counts[name] = value + 1;
			}
		}

		[Conditional("PAVISE_LOOP_PROFILE")]
		internal static void Begin()
		{
			if (cyclesPerMs <= 0.0)
			{
				Calibrate();
			}
			markTicks = (iterationStart = Stopwatch.GetTimestamp());
			markCycles = (iterationCycles = Cycles());
			if (windowStart == 0)
			{
				windowStart = iterationStart;
			}
		}

		[Conditional("PAVISE_LOOP_PROFILE")]
		internal static void Mark(string name)
		{
			if (markTicks != 0)
			{
				long timestamp = Stopwatch.GetTimestamp();
				ulong cyclesResult = Cycles();
				Phase value;
				if (!phases.TryGetValue(name, out value))
				{
					value = new Phase();
					phases[name] = value;
					order.Add(name);
				}
				long wallTicksValue = timestamp - markTicks;
				ulong cyclesValue = cyclesResult - markCycles;
				value.Count++;
				value.WallTicks += wallTicksValue;
				value.Cycles += cyclesValue;
				if (wallTicksValue > value.MaxWallTicks)
				{
					value.MaxWallTicks = wallTicksValue;
				}
				if (cyclesValue > value.MaxCycles)
				{
					value.MaxCycles = cyclesValue;
				}
				markTicks = timestamp;
				markCycles = cyclesResult;
			}
		}

		[Conditional("PAVISE_LOOP_PROFILE")]
		internal static void End()
		{
			if (iterationStart == 0)
			{
				return;
			}
			long timestamp = Stopwatch.GetTimestamp();
			ulong cyclesResult = Cycles();
			iterations++;
			long wallTicksValue = timestamp - iterationStart;
			ulong cyclesValue = cyclesResult - iterationCycles;
			if (wallTicksValue > maxIterationTicks)
			{
				maxIterationTicks = wallTicksValue;
			}
			if (cyclesValue > maxIterationCycles)
			{
				maxIterationCycles = cyclesValue;
			}
			Phase value;
			if (!phases.TryGetValue("(iteration)", out value))
			{
				value = new Phase();
				phases["(iteration)"] = value;
			}
			value.Count++;
			value.WallTicks += wallTicksValue;
			value.Cycles += cyclesValue;
			iterationStart = 0L;
			markTicks = 0L;
			if (timestamp - windowStart < 20 * Stopwatch.Frequency)
			{
				return;
			}
			StringBuilder stringBuilder = new StringBuilder("LOOP ");
			stringBuilder.Append(((double)(timestamp - windowStart) / (double)Stopwatch.Frequency).ToString("F1")).Append("s iterations=").Append(iterations)
				.Append(" cpu=")
				.Append(Ms(value.Cycles))
				.Append("ms maxIterCpu=")
				.Append(Ms(maxIterationCycles))
				.Append("ms maxIterWall=")
				.Append(WallMs(maxIterationTicks))
				.Append("ms");
			lock (countSync)
			{
				foreach (KeyValuePair<string, long> count in counts)
				{
					stringBuilder.Append(' ').Append(count.Key).Append('=')
						.Append(count.Value);
				}
				counts.Clear();
				foreach (KeyValuePair<string, double[]> span in spans)
				{
					stringBuilder.Append(" span:").Append(span.Key).Append(" n=")
						.Append(span.Value[0])
						.Append(" cpu=")
						.Append(span.Value[1].ToString("F1"))
						.Append("/max")
						.Append(span.Value[2].ToString("F1"))
						.Append(" wall=")
						.Append(span.Value[3].ToString("F1"));
				}
				spans.Clear();
			}
			int collectionCountResult = GC.CollectionCount(0);
			int collectionCountResult2 = GC.CollectionCount(1);
			int collectionCountResult3 = GC.CollectionCount(2);
			stringBuilder.Append(" gc0=").Append(collectionCountResult - gc0).Append(" gc1=")
				.Append(collectionCountResult2 - gc1)
				.Append(" gc2=")
				.Append(collectionCountResult3 - gc2)
				.Append(" heapMB=")
				.Append(((double)GC.GetTotalMemory(false) / 1048576.0).ToString("F1"));
			gc0 = collectionCountResult;
			gc1 = collectionCountResult2;
			gc2 = collectionCountResult3;
			stringBuilder.Append(" |");
			List<string> list = new List<string>(order);
			list.Sort((string a, string b) => phases[b].Cycles.CompareTo(phases[a].Cycles));
			foreach (string item in list)
			{
				Phase phase = phases[item];
				if (phase.Cycles != 0 || phase.WallTicks != 0)
				{
					stringBuilder.Append(' ').Append(item).Append(" cpu=")
						.Append(Ms(phase.Cycles))
						.Append("/max")
						.Append(Ms(phase.MaxCycles))
						.Append(" wall=")
						.Append(WallMs(phase.WallTicks))
						.Append("/max")
						.Append(WallMs(phase.MaxWallTicks))
						.Append(" n=")
						.Append(phase.Count)
						.Append(';');
				}
			}
			Logger.Log(stringBuilder.ToString());
			phases.Clear();
			order.Clear();
			iterations = 0L;
			maxIterationTicks = 0L;
			maxIterationCycles = 0uL;
			windowStart = timestamp;
		}

		private static void Calibrate()
		{
			ulong cyclesResult = Cycles();
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (stopwatch.ElapsedMilliseconds < 50)
			{
			}
			cyclesPerMs = (double)(Cycles() - cyclesResult) / Math.Max(1.0, stopwatch.Elapsed.TotalMilliseconds);
		}

		private static string Ms(ulong cycles)
		{
			if (!(cyclesPerMs > 0.0))
			{
				return "?";
			}
			return ((double)cycles / cyclesPerMs).ToString("F1");
		}

		private static string WallMs(long ticks)
		{
			return ((double)ticks * 1000.0 / (double)Stopwatch.Frequency).ToString("F1");
		}
	}
}
