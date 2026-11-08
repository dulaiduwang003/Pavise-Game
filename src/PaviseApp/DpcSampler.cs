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
			int num = Math.Min(64, Environment.ProcessorCount);
			int num2 = Marshal.SizeOf(typeof(ProcessorPerf));
			IntPtr intPtr = Marshal.AllocHGlobal(num2 * num);
			try
			{
				int returned;
				if (NtQuerySystemInformation(8, intPtr, num2 * num, out returned) != 0)
				{
					return false;
				}
				int num3 = Math.Min(num, returned / num2);
				if (num3 <= 0)
				{
					return false;
				}
				irq = new long[num3];
				idle = new long[num3];
				for (int i = 0; i < num3; i++)
				{
					ProcessorPerf processorPerf = (ProcessorPerf)Marshal.PtrToStructure((IntPtr)((long)intPtr + i * num2), typeof(ProcessorPerf));
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
				int num = windowMs - (int)stopwatch.ElapsedMilliseconds;
				if (num > 0)
				{
					Thread.Sleep(Math.Min(200, num));
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
			double num2 = (double)stopwatch.ElapsedTicks * (10000000.0 / (double)Stopwatch.Frequency);
			if (irq2.Length != irq.Length || num2 <= 0.0)
			{
				return null;
			}
			if (progress != null)
			{
				progress(1f);
			}
			double[] array = new double[irq.Length];
			double num3 = 0.0;
			for (int i = 0; i < irq.Length; i++)
			{
				array[i] = (double)Math.Max(0L, irq2[i] - irq[i]) / num2;
				num3 += (double)Math.Max(0L, idle2[i] - idle[i]) / num2;
			}
			cpuBusy = Math.Max(0.0, Math.Min(1.0, 1.0 - num3 / (double)irq.Length));
			return array;
		}
	}
}
