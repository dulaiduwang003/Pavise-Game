using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
	internal static class DpcSampler
	{
#pragma warning disable 0649
		private struct ProcessorPerf
		{
			public long IdleTime;

			public long KernelTime;

			public long UserTime;

			public long DpcTime;

			public long InterruptTime;

			public uint InterruptCount;
		}
#pragma warning restore 0649

		[DllImport("ntdll.dll")]
		private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

		public static double[] MeasureInterruptRates(int windowMs)
		{
			double cpuBusy;
			return MeasureLoad(windowMs, out cpuBusy);
		}

		internal static bool ReadPerfRows(out long[] irq, out long[] idle)
		{
			irq = null;
			idle = null;
			int processorCountLimit = Math.Min(64, Environment.ProcessorCount);
			int sizeOfResult = Marshal.SizeOf(typeof(ProcessorPerf));
			IntPtr intPtr = Marshal.AllocHGlobal(sizeOfResult * processorCountLimit);
			try
			{
				int returned;
				if (NtQuerySystemInformation(8, intPtr, sizeOfResult * processorCountLimit, out returned) != 0)
				{
					return false;
				}
				int processorCountLimitLimit = Math.Min(processorCountLimit, returned / sizeOfResult);
				if (processorCountLimitLimit <= 0)
				{
					return false;
				}
				irq = new long[processorCountLimitLimit];
				idle = new long[processorCountLimitLimit];
				for (int i = 0; i < processorCountLimitLimit; i++)
				{
					ProcessorPerf processorPerf = (ProcessorPerf)Marshal.PtrToStructure((IntPtr)((long)intPtr + i * sizeOfResult), typeof(ProcessorPerf));
					irq[i] = processorPerf.DpcTime + processorPerf.InterruptTime;
					idle[i] = processorPerf.IdleTime;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		public static double[] MeasureLoad(int windowMs, out double cpuBusy, Func<bool> cancelled = null, Action<float> progress = null)
		{
			cpuBusy = 0.0;
			if (cancelled != null && cancelled())
			{
				return null;
			}
			if (windowMs < 200)
			{
				windowMs = 200;
			}
			long[] irq;
			long[] idle;
			if (!ReadPerfRows(out irq, out idle))
			{
				return null;
			}
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (stopwatch.ElapsedMilliseconds < windowMs)
			{
				if (cancelled != null && cancelled())
				{
					return null;
				}
				if (progress != null)
				{
					progress(Math.Min(1f, (float)stopwatch.ElapsedMilliseconds / (float)windowMs));
				}
				int remainingWindowMs = windowMs - (int)stopwatch.ElapsedMilliseconds;
				if (remainingWindowMs > 0)
				{
					Thread.Sleep(Math.Min(200, remainingWindowMs));
				}
			}
			if (cancelled != null && cancelled())
			{
				return null;
			}
			long[] irq2;
			long[] idle2;
			if (!ReadPerfRows(out irq2, out idle2))
			{
				return null;
			}
			double scaledElapsedTicks = (double)stopwatch.ElapsedTicks * (10000000.0 / (double)Stopwatch.Frequency);
			if (irq2.Length != irq.Length || scaledElapsedTicks <= 0.0)
			{
				return null;
			}
			if (progress != null)
			{
				progress(1f);
			}
			double[] measuredValueItems = new double[irq.Length];
			double measuredValue = 0.0;
			for (int i = 0; i < irq.Length; i++)
			{
				measuredValueItems[i] = (double)Math.Max(0L, irq2[i] - irq[i]) / scaledElapsedTicks;
				measuredValue += (double)Math.Max(0L, idle2[i] - idle[i]) / scaledElapsedTicks;
			}
			cpuBusy = Math.Max(0.0, Math.Min(1.0, 1.0 - measuredValue / (double)irq.Length));
			return measuredValueItems;
		}
	}
}
