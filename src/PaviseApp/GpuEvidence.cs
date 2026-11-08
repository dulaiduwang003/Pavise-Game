using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
	internal static class GpuEvidence
	{
		internal sealed class GpuEngineSampler : IDisposable
		{
			private IntPtr query;

			private IntPtr counter;

			private bool primed;

			public bool IsOpen
			{
				get
				{
					return query != IntPtr.Zero;
				}
			}

			public bool Open()
			{
				return Open(0);
			}

			public bool Open(int targetPid)
			{
				Close();
				try
				{
					if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
					{
						query = IntPtr.Zero;
						return false;
					}
					string text = ((targetPid > 4) ? ("pid_" + targetPid.ToString(CultureInfo.InvariantCulture) + "_*engtype_3D") : "*engtype_3D");
					if (PdhAddEnglishCounterW(query, "\\GPU Engine(" + text + ")\\Utilization Percentage", IntPtr.Zero, out counter) != 0)
					{
						Close();
						return false;
					}
					primed = PdhCollectQueryData(query) == 0;
					return true;
				}
				catch
				{
					Close();
					return false;
				}
			}

			public RenderAdapter Resolve(int pid)
			{
				return Resolve(pid, false);
			}

			public RenderAdapter Resolve(int pid, bool strict)
			{
				if (query == IntPtr.Zero || pid <= 0)
				{
					return null;
				}
				try
				{
					if (PdhCollectQueryData(query) != 0)
					{
						return null;
					}
					if (!primed)
					{
						primed = true;
						return null;
					}
					return PickAdapter(counter, pid, strict);
				}
				catch
				{
					return null;
				}
			}

			public void Close()
			{
				if (!(query == IntPtr.Zero))
				{
					try
					{
						PdhCloseQuery(query);
					}
					catch
					{
					}
					query = IntPtr.Zero;
					counter = IntPtr.Zero;
					primed = false;
				}
			}

			public void Dispose()
			{
				Close();
			}
		}

#pragma warning disable 0649
		private struct PdhFmtCounterValue
		{
			public uint CStatus;

			public double DoubleValue;
		}

		private struct PdhFmtCounterValueItem
		{
			public IntPtr Name;

			public PdhFmtCounterValue Value;
		}
#pragma warning restore 0649

		internal const double MinElectUtilization = 10.0;

		internal const int BurstRounds = 2;

		internal const int BurstIntervalMs = 700;

		private const uint PDH_FMT_DOUBLE = 512u;

		private const uint PDH_MORE_DATA = 2147485650u;

		private static bool IsUnavailableObservation(uint status)
		{
			// PDH_NO_DATA also covers instances that have not appeared yet. Neither
			// it nor PDH_CSTATUS_NO_INSTANCE is positive GPU evidence or a fatal read.
			return status == 0x800007D5u || status == 0x800007D1u;
		}

		public static Dictionary<int, double> Sample3D(int rounds, int intervalMs, Func<bool> canceled)
		{
			return SampleCore(rounds, intervalMs, canceled, false, 0, 0u, false, null);
		}

		internal static Dictionary<int, double> Sample3D(int rounds, int intervalMs, Func<bool> canceled, int targetPid, out GpuSampleDiagnostics diagnostics)
		{
			diagnostics = new GpuSampleDiagnostics
			{
				TargetPid = targetPid
			};
			return SampleCore(rounds, intervalMs, canceled, false, 0, 0u, false, diagnostics);
		}

		public static Dictionary<int, double> SampleAdapter3D(int luidHigh, uint luidLow, int rounds, int intervalMs, Func<bool> canceled)
		{
			return SampleCore(rounds, intervalMs, canceled, true, luidHigh, luidLow, true, null);
		}

		private static Dictionary<int, double> SampleCore(int rounds, int intervalMs, Func<bool> canceled, bool filterAdapter, int luidHigh, uint luidLow, bool sustained, GpuSampleDiagnostics diagnostics)
		{
			IntPtr query = IntPtr.Zero;
			try
			{
				uint num = PdhOpenQueryW(null, IntPtr.Zero, out query);
				if (num != 0 || query == IntPtr.Zero)
				{
					if (diagnostics != null)
					{
						diagnostics.Failure("PdhOpenQuery", num);
					}
					return null;
				}
				IntPtr counter;
				num = PdhAddEnglishCounterW(query, "\\GPU Engine(*engtype_3D)\\Utilization Percentage", IntPtr.Zero, out counter);
				if (num != 0)
				{
					if (diagnostics != null)
					{
						diagnostics.Failure("PdhAddEnglishCounter", num);
					}
					return null;
				}
				num = PdhCollectQueryData(query);
				if (IsUnavailableObservation(num))
				{
					if (diagnostics != null) diagnostics.Unavailable("PdhCollectQueryData.prime", num);
				}
				else if (num != 0)
				{
					if (diagnostics != null)
					{
						diagnostics.Failure("PdhCollectQueryData.prime", num);
					}
					return null;
				}
				Dictionary<int, double> dictionary = null;
				int num2 = 0;
				bool failedRound = false;
				for (int i = 0; i < rounds; i++)
				{
					if (canceled != null && canceled())
					{
						break;
					}
					Thread.Sleep(intervalMs);
					num = PdhCollectQueryData(query);
					if (IsUnavailableObservation(num))
					{
						if (diagnostics != null) diagnostics.Unavailable("PdhCollectQueryData.sample", num);
						if (dictionary == null || sustained) dictionary = new Dictionary<int, double>();
						continue;
					}
					if (num != 0)
					{
						failedRound = true;
						if (diagnostics != null)
						{
							diagnostics.Failure("PdhCollectQueryData.sample", num);
						}
						continue;
					}
					if (diagnostics != null)
					{
						diagnostics.CollectedRounds++;
					}
					Dictionary<int, double> dictionary2 = ReadByPid(counter, filterAdapter, luidHigh, luidLow, diagnostics);
					if (dictionary2 == null)
					{
						failedRound = true;
						continue;
					}
					num2++;
					if (diagnostics != null)
					{
						diagnostics.ValidRounds++;
					}
					if (dictionary == null)
					{
						dictionary = dictionary2;
						continue;
					}
					if (sustained)
					{
						Dictionary<int, double> dictionary3 = new Dictionary<int, double>();
						foreach (KeyValuePair<int, double> item in dictionary)
						{
							double value;
							if (dictionary2.TryGetValue(item.Key, out value))
							{
								dictionary3[item.Key] = ((value < item.Value) ? value : item.Value);
							}
						}
						dictionary = dictionary3;
						continue;
					}
					foreach (KeyValuePair<int, double> item2 in dictionary2)
					{
						double value2;
						if (!dictionary.TryGetValue(item2.Key, out value2) || item2.Value > value2)
						{
							dictionary[item2.Key] = item2.Value;
						}
					}
				}
				if (sustained && num2 < 2)
				{
					return dictionary == null || failedRound ? null : new Dictionary<int, double>();
				}
				return dictionary;
			}
			catch (Exception ex)
			{
				if (diagnostics != null)
				{
					diagnostics.ExceptionType = ex.GetType().Name;
				}
				return null;
			}
			finally
			{
				if (query != IntPtr.Zero)
				{
					try
					{
						PdhCloseQuery(query);
					}
					catch
					{
					}
				}
			}
		}

		private static Dictionary<int, double> ReadByPid(IntPtr counter, bool filterAdapter, int luidHigh, uint luidLow, GpuSampleDiagnostics diagnostics)
		{
			uint bufferSize = 0u;
			uint itemCount = 0u;
			uint num = PdhGetFormattedCounterArrayW(counter, 512u, ref bufferSize, ref itemCount, IntPtr.Zero);
			if (num == 0 && itemCount == 0) return new Dictionary<int, double>();
			if (IsUnavailableObservation(num))
			{
				if (diagnostics != null) diagnostics.Unavailable("PdhGetFormattedCounterArray.size", num);
				return new Dictionary<int, double>();
			}
			if (num != 2147485650u || bufferSize == 0)
			{
				if (diagnostics != null)
				{
					diagnostics.Failure("PdhGetFormattedCounterArray.size", num);
				}
				return null;
			}
			IntPtr intPtr = Marshal.AllocHGlobal((int)bufferSize);
			try
			{
				num = PdhGetFormattedCounterArrayW(counter, 512u, ref bufferSize, ref itemCount, intPtr);
				if (IsUnavailableObservation(num))
				{
					if (diagnostics != null) diagnostics.Unavailable("PdhGetFormattedCounterArray.data", num);
					return new Dictionary<int, double>();
				}
				if (num != 0)
				{
					if (diagnostics != null)
					{
						diagnostics.Failure("PdhGetFormattedCounterArray.data", num);
					}
					return null;
				}
				Dictionary<int, double> dictionary = new Dictionary<int, double>();
				bool observed = itemCount == 0, failedInstance = false;
				int num2 = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
				for (uint num3 = 0u; num3 < itemCount; num3++)
				{
					PdhFmtCounterValueItem pdhFmtCounterValueItem = (PdhFmtCounterValueItem)Marshal.PtrToStructure(new IntPtr(intPtr.ToInt64() + num3 * num2), typeof(PdhFmtCounterValueItem));
					if (IsUnavailableObservation(pdhFmtCounterValueItem.Value.CStatus))
					{
						observed = true;
						if (diagnostics != null) diagnostics.Unavailable("counter.CStatus", pdhFmtCounterValueItem.Value.CStatus);
						continue;
					}
					if (pdhFmtCounterValueItem.Value.CStatus <= 1) observed = true;
					else failedInstance = true;
					string instanceName = Marshal.PtrToStringUni(pdhFmtCounterValueItem.Name);
					int num4 = ParsePid(instanceName);
					if (diagnostics != null)
					{
						diagnostics.Instances++;
					}
					if (diagnostics != null && num4 > 0 && num4 == diagnostics.TargetPid)
					{
						diagnostics.TargetInstances++;
						if (pdhFmtCounterValueItem.Value.CStatus > 1)
						{
							diagnostics.TargetRejectedInstances++;
							diagnostics.TargetStatus = pdhFmtCounterValueItem.Value.CStatus;
						}
					}
					int luidHigh2;
					uint luidLow2;
					uint phys;
					if (pdhFmtCounterValueItem.Value.CStatus > 1)
					{
						if (diagnostics != null)
						{
							diagnostics.RejectedInstances++;
							diagnostics.Failure("counter.CStatus", pdhFmtCounterValueItem.Value.CStatus);
						}
					}
					else if (num4 <= 0)
					{
						if (diagnostics != null)
						{
							diagnostics.RejectedInstances++;
						}
					}
					else if (!filterAdapter || (ParseAdapter(instanceName, out luidHigh2, out luidLow2, out phys) && luidHigh2 == luidHigh && luidLow2 == luidLow))
					{
						double doubleValue = pdhFmtCounterValueItem.Value.DoubleValue;
						if (!(doubleValue < 0.0))
						{
							double value;
							dictionary.TryGetValue(num4, out value);
							dictionary[num4] = value + doubleValue;
						}
					}
				}
				return failedInstance && !observed ? null : dictionary;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		internal static int ParsePid(string instanceName)
		{
			if (string.IsNullOrEmpty(instanceName))
			{
				return 0;
			}
			if (!instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
			{
				return 0;
			}
			int num = instanceName.IndexOf('_', 4);
			if (num <= 4)
			{
				return 0;
			}
			int result;
			if (!int.TryParse(instanceName.Substring(4, num - 4), out result) || result <= 0)
			{
				return 0;
			}
			return result;
		}

		internal static bool ParseAdapter(string instanceName, out int luidHigh, out uint luidLow, out uint phys)
		{
			luidHigh = 0;
			luidLow = 0u;
			phys = 0u;
			if (string.IsNullOrEmpty(instanceName))
			{
				return false;
			}
			string text = instanceName.ToLowerInvariant();
			int num = text.IndexOf("_luid_", StringComparison.Ordinal);
			if (num < 0)
			{
				return false;
			}
			int num2 = num + 6;
			int num3 = text.IndexOf('_', num2);
			if (num3 <= num2)
			{
				return false;
			}
			int num4 = text.IndexOf('_', num3 + 1);
			if (num4 <= num3 + 1)
			{
				return false;
			}
			if (!TryParseHex(text.Substring(num2, num3 - num2), out luidLow))
			{
				return false;
			}
			luidHigh = (int)luidLow;
			uint value;
			if (!TryParseHex(text.Substring(num3 + 1, num4 - num3 - 1), out value))
			{
				return false;
			}
			luidLow = value;
			int num5 = text.IndexOf("_phys_", num4, StringComparison.Ordinal);
			if (num5 < 0)
			{
				return false;
			}
			int num6 = num5 + 6;
			int num7 = text.IndexOf('_', num6);
			if (num7 <= num6)
			{
				return false;
			}
			uint result;
			if (!uint.TryParse(text.Substring(num6, num7 - num6), out result))
			{
				return false;
			}
			phys = result;
			return true;
		}

		private static bool TryParseHex(string token, out uint value)
		{
			value = 0u;
			if (string.IsNullOrEmpty(token))
			{
				return false;
			}
			string text = (token.StartsWith("0x", StringComparison.Ordinal) ? token.Substring(2) : token);
			if (text.Length == 0 || text.Length > 8)
			{
				return false;
			}
			uint num = 0u;
			string text2 = text;
			foreach (char c in text2)
			{
				int num2;
				if (c >= '0' && c <= '9')
				{
					num2 = c - 48;
				}
				else
				{
					if (c < 'a' || c > 'f')
					{
						return false;
					}
					num2 = c - 97 + 10;
				}
				num = (num << 4) | (uint)num2;
			}
			value = num;
			return true;
		}

		public static RenderAdapter ResolveRenderAdapter(int pid, int intervalMs)
		{
			if (pid <= 0)
			{
				return null;
			}
			IntPtr query = IntPtr.Zero;
			try
			{
				if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
				{
					return null;
				}
				IntPtr counter;
				if (PdhAddEnglishCounterW(query, "\\GPU Engine(*engtype_3D)\\Utilization Percentage", IntPtr.Zero, out counter) != 0)
				{
					return null;
				}
				if (PdhCollectQueryData(query) != 0)
				{
					return null;
				}
				Thread.Sleep(intervalMs);
				if (PdhCollectQueryData(query) != 0)
				{
					return null;
				}
				return PickAdapter(counter, pid);
			}
			catch
			{
				return null;
			}
			finally
			{
				if (query != IntPtr.Zero)
				{
					try
					{
						PdhCloseQuery(query);
					}
					catch
					{
					}
				}
			}
		}

		private static RenderAdapter PickAdapter(IntPtr counter, int pid, bool strict = false)
		{
			uint bufferSize = 0u;
			uint itemCount = 0u;
			uint num = PdhGetFormattedCounterArrayW(counter, 512u, ref bufferSize, ref itemCount, IntPtr.Zero);
			if (num != 2147485650u || bufferSize == 0)
			{
				return null;
			}
			IntPtr intPtr = Marshal.AllocHGlobal((int)bufferSize);
			try
			{
				if (PdhGetFormattedCounterArrayW(counter, 512u, ref bufferSize, ref itemCount, intPtr) != 0)
				{
					return null;
				}
				Dictionary<long, RenderAdapter> dictionary = new Dictionary<long, RenderAdapter>();
				int num2 = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
				for (uint num3 = 0u; num3 < itemCount; num3++)
				{
					PdhFmtCounterValueItem pdhFmtCounterValueItem = (PdhFmtCounterValueItem)Marshal.PtrToStructure(new IntPtr(intPtr.ToInt64() + num3 * num2), typeof(PdhFmtCounterValueItem));
					if (pdhFmtCounterValueItem.Value.CStatus > 1)
					{
						continue;
					}
					string instanceName = Marshal.PtrToStringUni(pdhFmtCounterValueItem.Name);
					int luidHigh;
					uint luidLow;
					uint phys;
					if (ParsePid(instanceName) != pid || !ParseAdapter(instanceName, out luidHigh, out luidLow, out phys))
					{
						continue;
					}
					double num4 = pdhFmtCounterValueItem.Value.DoubleValue;
					if (!double.IsNaN(num4) && !double.IsInfinity(num4))
					{
						if (num4 < 0.0)
						{
							num4 = 0.0;
						}
						long key = ((long)luidHigh << 32) | luidLow;
						RenderAdapter value;
						if (!dictionary.TryGetValue(key, out value))
						{
							RenderAdapter renderAdapter = new RenderAdapter();
							renderAdapter.LuidHigh = luidHigh;
							renderAdapter.LuidLow = luidLow;
							renderAdapter.PhysIndex = phys;
							value = (dictionary[key] = renderAdapter);
						}
						value.Util += num4;
					}
				}
				if (dictionary.Count == 0)
				{
					return null;
				}
				RenderAdapter renderAdapter3 = null;
				RenderAdapter renderAdapter4 = null;
				foreach (RenderAdapter value2 in dictionary.Values)
				{
					if (renderAdapter3 == null || value2.Util > renderAdapter3.Util)
					{
						renderAdapter4 = renderAdapter3;
						renderAdapter3 = value2;
					}
					else if (renderAdapter4 == null || value2.Util > renderAdapter4.Util)
					{
						renderAdapter4 = value2;
					}
				}
				if (strict)
				{
					return UniqueActiveAdapter(dictionary.Values);
				}
				renderAdapter3.Ambiguous = renderAdapter4 != null && renderAdapter4.Util >= 10.0;
				return renderAdapter3;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		internal static RenderAdapter UniqueActiveAdapter(IEnumerable<RenderAdapter> adapters)
		{
			RenderAdapter renderAdapter = null;
			foreach (RenderAdapter adapter in adapters)
			{
				if (adapter != null && !double.IsNaN(adapter.Util) && !double.IsInfinity(adapter.Util) && !(adapter.Util <= 0.0))
				{
					if (renderAdapter != null)
					{
						RenderAdapter renderAdapter2 = new RenderAdapter();
						renderAdapter2.Ambiguous = true;
						return renderAdapter2;
					}
					renderAdapter = adapter;
				}
			}
			return renderAdapter;
		}

		[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
		private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

		[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
		private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

		[DllImport("pdh.dll")]
		private static extern uint PdhCollectQueryData(IntPtr query);

		[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
		private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

		[DllImport("pdh.dll")]
		private static extern uint PdhCloseQuery(IntPtr query);
	}
}
