using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class CpuTopology
	{
		private sealed class SqueezeCache
		{
			public uint[] Ids;
		}

		private sealed class CpuSetRec
		{
			public uint Id;

			public short Group;

			public byte Logical;

			public byte Core;

			public byte Efficiency;

			public byte Scheduling;
		}

		private static volatile SqueezeCache squeezeCache;

		private static uint[] backgroundIds;

		private static uint[] allIds;

		private static uint[] partitionGameIds;

		private static uint[] altBackgroundIds;

		private static readonly List<ulong> physicalCoreMasks;

		private static List<ulong> parsedCoreMasks;

		public static bool Hybrid;

		public static bool AsymCache;

		public static bool MultiGroup;

		public static ulong PerfMask;

		public static ulong EffMask;

		public static ulong BigL3Mask;

		public static ulong SmallL3Mask;

		public static ulong FavoredMask;

		public static string FavoredDetail;

		public static ulong AllMask;

		public static ulong ThrottleMask;

		public static ulong BoostMask;

		public static ulong StrictBoostMask;

		public static ulong AltThrottleMask;

		public static string PartitionTag;

		private static List<KeyValuePair<uint, ulong>> cacheDomains;

		private static List<ulong> processorDieDomains;

		public static bool CpuSetPartitionRejected;

		public static bool StrictMaskUnsafe;

		public static bool AllMaskReconciled;

		public static int PhysicalCoreCount
		{
			get
			{
				return physicalCoreMasks.Count;
			}
		}

		internal static ulong ParsedUnion
		{
			get
			{
				ulong num = 0uL;
				foreach (ulong parsedCoreMask in parsedCoreMasks)
				{
					num |= parsedCoreMask;
				}
				return num;
			}
		}

		public static bool TopologySourcesAgree
		{
			get
			{
				if (parsedCoreMasks.Count == physicalCoreMasks.Count)
				{
					return UnionOf(parsedCoreMasks) == UnionOf(physicalCoreMasks);
				}
				return false;
			}
		}

		public static string TopologySourceCounts
		{
			get
			{
				return parsedCoreMasks.Count + " / " + physicalCoreMasks.Count;
			}
		}

		public static ulong InactiveThrottleMask
		{
			get
			{
				return AltThrottleMask;
			}
		}

		public static ulong BackgroundAllowedMask()
		{
			if (!HasSafeBackgroundPartition())
			{
				return AllMask;
			}
			return ThrottleMask;
		}

		public static ulong BackgroundSqueezeMask()
		{
			return CpuPartitionPolicy.SqueezeMask(physicalCoreMasks.ToArray(), BackgroundAllowedMask(), EffMask, Hybrid, L3Masks(), StrictBoostMask);
		}

		public static int L3CacheMb()
		{
			try
			{
				uint num = 0u;
				foreach (KeyValuePair<uint, ulong> cacheDomain in cacheDomains)
				{
					if (cacheDomain.Key > num)
					{
						num = cacheDomain.Key;
					}
				}
				return (int)(num / 1048576);
			}
			catch
			{
				return 0;
			}
		}

		public static ulong[] L3Masks()
		{
			List<ulong> list = new List<ulong>();
			foreach (KeyValuePair<uint, ulong> cacheDomain in cacheDomains)
			{
				ulong num = cacheDomain.Value & AllMask;
				if (num != 0)
				{
					list.Add(num);
				}
			}
			return list.ToArray();
		}

		public static uint[] BackgroundYieldCpuSetIds()
		{
			if (HasSafeBackgroundPartition())
			{
				return backgroundIds;
			}
			SqueezeCache squeezeCache = CpuTopology.squeezeCache;
			if (squeezeCache == null)
			{
				squeezeCache = new SqueezeCache();
				if (!MultiGroup)
				{
					ulong num = BackgroundSqueezeMask();
					if (num != 0)
					{
						try
						{
							squeezeCache.Ids = CpuSetIdsFor(num);
						}
						catch
						{
							squeezeCache.Ids = null;
						}
					}
				}
				CpuTopology.squeezeCache = squeezeCache;
			}
			return squeezeCache.Ids;
		}

		public static ulong[] PhysicalCoreMasks()
		{
			return physicalCoreMasks.ToArray();
		}

		private static ulong ComputeFavoredMask(List<CpuSetRec> rows)
		{
			List<int> list = new List<int>(rows.Count);
			List<byte> list2 = new List<byte>(rows.Count);
			List<byte> list3 = new List<byte>(rows.Count);
			foreach (CpuSetRec row in rows)
			{
				if (row.Group == 0 && row.Logical < 64)
				{
					list.Add(row.Logical);
					list2.Add(row.Efficiency);
					list3.Add(row.Scheduling);
				}
			}
			FavoredDetail = DescribeCoreClasses(list.ToArray(), list2.ToArray(), list3.ToArray());
			return FavoredMaskOf(list.ToArray(), list2.ToArray(), list3.ToArray());
		}

		internal static string DescribeCoreClasses(int[] logical, byte[] efficiency, byte[] scheduling)
		{
			if (logical == null || efficiency == null || scheduling == null || logical.Length == 0 || efficiency.Length != logical.Length || scheduling.Length != logical.Length)
			{
				return "";
			}
			int[] array = new int[logical.Length];
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = i;
			}
			Array.Sort(array, (int a, int b) => logical[a].CompareTo(logical[b]));
			List<string> list = new List<string>();
			int num = 0;
			for (int j = 1; j <= array.Length; j++)
			{
				if (j == array.Length || efficiency[array[j]] != efficiency[array[num]] || scheduling[array[j]] != scheduling[array[num]] || logical[array[j]] != logical[array[j - 1]] + 1)
				{
					int num2 = logical[array[num]];
					int num3 = logical[array[j - 1]];
					list.Add(((num2 == num3) ? num2.ToString() : (num2 + "-" + num3)) + ":" + efficiency[array[num]] + "/" + scheduling[array[num]]);
					num = j;
				}
			}
			return string.Join(" ", list.ToArray());
		}

		internal static ulong FavoredMaskOf(int[] logical, byte[] efficiency, byte[] scheduling)
		{
			if (logical == null || efficiency == null || scheduling == null || logical.Length == 0 || efficiency.Length != logical.Length || scheduling.Length != logical.Length)
			{
				return 0uL;
			}
			byte b = 0;
			foreach (byte b2 in efficiency)
			{
				if (b2 > b)
				{
					b = b2;
				}
			}
			byte b3 = byte.MaxValue;
			byte b4 = 0;
			for (int j = 0; j < logical.Length; j++)
			{
				if (efficiency[j] == b)
				{
					if (scheduling[j] < b3)
					{
						b3 = scheduling[j];
					}
					if (scheduling[j] > b4)
					{
						b4 = scheduling[j];
					}
				}
			}
			if (b4 <= b3)
			{
				return 0uL;
			}
			ulong num = 0uL;
			for (int k = 0; k < logical.Length; k++)
			{
				if (efficiency[k] == b && scheduling[k] == b4 && logical[k] >= 0 && logical[k] < 64)
				{
					num |= (ulong)(1L << logical[k]);
				}
			}
			return num;
		}

		private static void BuildCpuSetPolicies()
		{
			try
			{
				MultiGroup = GetActiveProcessorGroupCount() > 1;
			}
			catch
			{
				MultiGroup = Environment.ProcessorCount > 64;
			}
			int returned;
			GetSystemCpuSetInformation(IntPtr.Zero, 0, out returned, IntPtr.Zero, 0u);
			if (returned <= 0)
			{
				return;
			}
			int num = returned;
			IntPtr intPtr = Marshal.AllocHGlobal(num);
			try
			{
				if (!GetSystemCpuSetInformation(intPtr, num, out returned, IntPtr.Zero, 0u) || returned <= 0 || returned > num)
				{
					return;
				}
				List<CpuSetRec> list = new List<CpuSetRec>();
				int num3;
				for (long num2 = 0L; num2 + 20 <= returned; num2 += num3)
				{
					IntPtr ptr = (IntPtr)((long)intPtr + num2);
					num3 = Marshal.ReadInt32(ptr, 0);
					int num4 = Marshal.ReadInt32(ptr, 4);
					if (num3 <= 0 || num2 + num3 > returned)
					{
						break;
					}
					if (num4 == 0 && num3 >= 20)
					{
						list.Add(new CpuSetRec
						{
							Id = (uint)Marshal.ReadInt32(ptr, 8),
							Group = Marshal.ReadInt16(ptr, 12),
							Logical = Marshal.ReadByte(ptr, 14),
							Core = Marshal.ReadByte(ptr, 15),
							Efficiency = Marshal.ReadByte(ptr, 18),
							Scheduling = (byte)((num3 >= 21) ? Marshal.ReadByte(ptr, 20) : 0)
						});
					}
				}
				if (list.Count == 0)
				{
					return;
				}
				List<uint> list2 = new List<uint>();
				byte b2 = byte.MaxValue;
				byte b3 = 0;
				foreach (CpuSetRec item2 in list)
				{
					list2.Add(item2.Id);
					if (item2.Efficiency < b2)
					{
						b2 = item2.Efficiency;
					}
					if (item2.Efficiency > b3)
					{
						b3 = item2.Efficiency;
					}
				}
				allIds = list2.ToArray();
				FavoredMask = ComputeFavoredMask(list);
				Dictionary<string, CpuSetRec> dictionary = new Dictionary<string, CpuSetRec>();
				Dictionary<string, ulong> dictionary2 = new Dictionary<string, ulong>();
				foreach (CpuSetRec item3 in list)
				{
					string key = item3.Group + ":" + item3.Core;
					CpuSetRec value;
					if (!dictionary.TryGetValue(key, out value) || item3.Logical < value.Logical)
					{
						dictionary[key] = item3;
					}
					if (item3.Group == 0 && item3.Logical < 64)
					{
						ulong value2;
						dictionary2.TryGetValue(key, out value2);
						dictionary2[key] = value2 | (ulong)(1L << (int)item3.Logical);
					}
				}
				foreach (ulong value3 in dictionary2.Values)
				{
					if (value3 != 0)
					{
						physicalCoreMasks.Add(value3);
					}
				}
				List<uint> list3 = new List<uint>();
				List<uint> list4 = new List<uint>();
				HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
				if (b3 > b2)
				{
					int num5 = 0;
					foreach (CpuSetRec item4 in list)
					{
						if (item4.Efficiency == b3)
						{
							num5++;
						}
					}
					if (num5 >= 8)
					{
						foreach (CpuSetRec value4 in dictionary.Values)
						{
							if (value4.Efficiency != b3)
							{
								hashSet.Add(value4.Group + ":" + value4.Core);
							}
						}
					}
				}
				else if (!AsymCache && !MultiGroup)
				{
					List<CpuPartitionPolicy.CoreDesc> list5 = new List<CpuPartitionPolicy.CoreDesc>();
					foreach (ulong value5 in dictionary2.Values)
					{
						int l = -1;
						int die = -1;
						uint l3Size = 0u;
						for (int i = 0; i < cacheDomains.Count; i++)
						{
							if ((cacheDomains[i].Value & value5) != 0)
							{
								l = i;
								l3Size = cacheDomains[i].Key;
								break;
							}
						}
						for (int j = 0; j < processorDieDomains.Count; j++)
						{
							if ((processorDieDomains[j] & value5) != 0)
							{
								die = j;
								break;
							}
						}
						list5.Add(new CpuPartitionPolicy.CoreDesc
						{
							Mask = value5,
							L3 = l,
							Die = die,
							Eff = 0,
							L3Size = l3Size
						});
					}
					CpuPartitionPolicy.CorePlan corePlan = CpuPartitionPolicy.Decide(list5.ToArray(), AllMask);
					if (corePlan.Partitioned)
					{
						uint[] array = CpuSetIdsFor(corePlan.Background);
						uint[] array2 = CpuSetIdsFor(corePlan.Game);
						if (array != null && array.Length > 0 && array2 != null && array2.Length > 0)
						{
							backgroundIds = array;
							partitionGameIds = array2;
							ThrottleMask = corePlan.Background;
							StrictBoostMask = corePlan.Game;
							PartitionTag = corePlan.Tag;
							if (corePlan.AltBackground != 0)
							{
								uint[] array3 = CpuSetIdsFor(corePlan.AltBackground);
								if (array3 != null && array3.Length > 0)
								{
									altBackgroundIds = array3;
									AltThrottleMask = corePlan.AltBackground;
								}
							}
						}
					}
				}
				else if (!AsymCache)
				{
					List<CpuSetRec> list6 = new List<CpuSetRec>(dictionary.Values);
					list6.Sort(delegate(CpuSetRec a, CpuSetRec b)
					{
						int num9 = a.Group.CompareTo(b.Group);
						return (num9 == 0) ? a.Core.CompareTo(b.Core) : num9;
					});
					int num6 = CpuPartitionPolicy.BackgroundCoreCount(list6.Count);
					for (int k = Math.Max(0, list6.Count - num6); k < list6.Count; k++)
					{
						hashSet.Add(list6[k].Group + ":" + list6[k].Core);
					}
				}
				ulong num7 = 0uL;
				ulong num8 = 0uL;
				foreach (CpuSetRec item5 in list)
				{
					string item = item5.Group + ":" + item5.Core;
					bool flag = hashSet.Contains(item);
					if (flag)
					{
						list3.Add(item5.Id);
					}
					else if (hashSet.Count > 0 && (b3 == b2 || item5.Efficiency == b3))
					{
						list4.Add(item5.Id);
					}
					if (item5.Group == 0 && item5.Logical < 64)
					{
						if (flag)
						{
							num7 |= (ulong)(1L << (int)item5.Logical);
						}
						else if (hashSet.Count > 0 && (b3 == b2 || item5.Efficiency == b3))
						{
							num8 |= (ulong)(1L << (int)item5.Logical);
						}
					}
				}
				if (AsymCache && !MultiGroup)
				{
					uint[] array4 = CpuSetIdsFor(ThrottleMask);
					uint[] array5 = CpuSetIdsFor(BigL3Mask);
					if (array4 != null && array4.Length > 0)
					{
						backgroundIds = array4;
					}
					if (array5 != null && array5.Length > 0)
					{
						partitionGameIds = array5;
					}
					uint[] array6 = CpuSetIdsFor(BigL3Mask);
					if (array6 != null && array6.Length > 0)
					{
						altBackgroundIds = array6;
						AltThrottleMask = BigL3Mask;
					}
				}
				else if (list3.Count > 0 && list4.Count > 0)
				{
					backgroundIds = list3.ToArray();
					partitionGameIds = list4.ToArray();
				}
				if (!MultiGroup && !AsymCache && num7 != 0 && num8 != 0)
				{
					if (PartitionAgreesWithEfficiency(num8, num7))
					{
						ThrottleMask = num7;
						StrictBoostMask = num8;
					}
					else
					{
						CpuSetPartitionRejected = true;
						backgroundIds = null;
						partitionGameIds = null;
					}
				}
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		private static void Parse()
		{
			if (IntPtr.Size != 8)
			{
				return;
			}
			int length = 0;
			GetLogicalProcessorInformationEx(65535, IntPtr.Zero, ref length);
			if (length <= 0)
			{
				return;
			}
			int num = length;
			IntPtr intPtr = Marshal.AllocHGlobal(num);
			try
			{
				if (!GetLogicalProcessorInformationEx(65535, intPtr, ref length) || length <= 0 || length > num)
				{
					return;
				}
				Dictionary<int, ulong> dictionary = new Dictionary<int, ulong>();
				List<ulong> list = new List<ulong>();
				List<KeyValuePair<uint, ulong>> list2 = new List<KeyValuePair<uint, ulong>>();
				List<ulong> list3 = new List<ulong>();
				bool flag = false;
				long num2 = 0L;
				while (num2 + 8 <= length)
				{
					IntPtr intPtr2 = (IntPtr)((long)intPtr + num2);
					int num3 = Marshal.ReadInt32(intPtr2, 0);
					int num4 = Marshal.ReadInt32(intPtr2, 4);
					if (num4 <= 0 || num2 + num4 > length)
					{
						break;
					}
					IntPtr intPtr3 = (IntPtr)((long)intPtr2 + 8);
					switch (num3)
					{
					case 0:
					{
						if (!RecordFits(num4, 30, 2))
						{
							num2 += num4;
							continue;
						}
						int key2 = Marshal.ReadByte(intPtr3, 1);
						int num9 = Marshal.ReadInt16(intPtr3, 22);
						if (!RecordArrayFits(num4, 32, num9, 16))
						{
							num2 += num4;
							continue;
						}
						ulong num10 = 0uL;
						for (int k = 0; k < num9; k++)
						{
							IntPtr ptr4 = (IntPtr)((long)intPtr3 + 24 + k * 16);
							if (Marshal.ReadInt16(ptr4, 8) != 0)
							{
								flag = true;
								continue;
							}
							ulong num11 = (ulong)Marshal.ReadInt64(ptr4, 0);
							ulong value;
							dictionary.TryGetValue(key2, out value);
							dictionary[key2] = value | num11;
							num10 |= num11;
						}
						if (num10 != 0)
						{
							list.Add(num10);
						}
						break;
					}
					case 5:
					{
						if (!RecordFits(num4, 30, 2))
						{
							num2 += num4;
							continue;
						}
						int num7 = Marshal.ReadInt16(intPtr3, 22);
						if (!RecordArrayFits(num4, 32, num7, 16))
						{
							num2 += num4;
							continue;
						}
						ulong num8 = 0uL;
						for (int j = 0; j < num7; j++)
						{
							IntPtr ptr3 = (IntPtr)((long)intPtr3 + 24 + j * 16);
							if (Marshal.ReadInt16(ptr3, 8) != 0)
							{
								flag = true;
							}
							else
							{
								num8 |= (ulong)Marshal.ReadInt64(ptr3, 0);
							}
						}
						if (num8 != 0)
						{
							list3.Add(num8);
						}
						break;
					}
					case 2:
					{
						if (!RecordFits(num4, 8, 1))
						{
							num2 += num4;
							continue;
						}
						if (Marshal.ReadByte(intPtr3, 0) != 3)
						{
							break;
						}
						if (!RecordFits(num4, 38, 2))
						{
							num2 += num4;
							continue;
						}
						uint key = (uint)Marshal.ReadInt32(intPtr3, 4);
						int num5 = Marshal.ReadInt16(intPtr3, 30);
						ulong num6 = 0uL;
						if (num5 == 0)
						{
							if (!RecordFits(num4, 40, 16))
							{
								num2 += num4;
								continue;
							}
							IntPtr ptr = (IntPtr)((long)intPtr3 + 32);
							if (Marshal.ReadInt16(ptr, 8) != 0)
							{
								flag = true;
							}
							else
							{
								num6 = (ulong)Marshal.ReadInt64(ptr, 0);
							}
						}
						else
						{
							if (!RecordArrayFits(num4, 40, num5, 16))
							{
								num2 += num4;
								continue;
							}
							for (int i = 0; i < num5; i++)
							{
								IntPtr ptr2 = (IntPtr)((long)intPtr3 + 32 + i * 16);
								if (Marshal.ReadInt16(ptr2, 8) != 0)
								{
									flag = true;
								}
								else
								{
									num6 |= (ulong)Marshal.ReadInt64(ptr2, 0);
								}
							}
						}
						if (num6 != 0)
						{
							list2.Add(new KeyValuePair<uint, ulong>(key, num6));
						}
						break;
					}
					}
					num2 += num4;
				}
				if (flag)
				{
					return;
				}
				parsedCoreMasks = list;
				cacheDomains = list2;
				processorDieDomains = list3;
				if (dictionary.Count >= 2)
				{
					int num12 = int.MinValue;
					foreach (KeyValuePair<int, ulong> item in dictionary)
					{
						if (item.Key > num12)
						{
							num12 = item.Key;
						}
					}
					ulong num13 = 0uL;
					ulong num14 = 0uL;
					foreach (KeyValuePair<int, ulong> item2 in dictionary)
					{
						if (item2.Key == num12)
						{
							num13 |= item2.Value;
						}
						else
						{
							num14 |= item2.Value;
						}
					}
					if (num13 != 0 && num14 != 0)
					{
						PerfMask = num13;
						EffMask = num14;
						Hybrid = true;
					}
				}
				if (Hybrid || list2.Count < 2)
				{
					return;
				}
				uint num15 = 0u;
				uint num16 = uint.MaxValue;
				foreach (KeyValuePair<uint, ulong> item3 in list2)
				{
					if (item3.Key > num15)
					{
						num15 = item3.Key;
					}
					if (item3.Key < num16)
					{
						num16 = item3.Key;
					}
				}
				if (num15 <= num16)
				{
					return;
				}
				ulong num17 = 0uL;
				ulong num18 = 0uL;
				foreach (KeyValuePair<uint, ulong> item4 in list2)
				{
					if (item4.Key == num15)
					{
						num17 |= item4.Value;
					}
					else
					{
						num18 |= item4.Value;
					}
				}
				if (num17 != 0 && num18 != 0)
				{
					BigL3Mask = num17;
					SmallL3Mask = num18;
					AsymCache = true;
				}
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		private static bool RecordFits(int recordSize, int offset, int bytes)
		{
			if (recordSize >= 0 && offset >= 0 && bytes >= 0 && offset <= recordSize)
			{
				return bytes <= recordSize - offset;
			}
			return false;
		}

		private static bool RecordArrayFits(int recordSize, int offset, int count, int stride)
		{
			if (count > 0 && count <= 64 && stride > 0 && count <= int.MaxValue / stride)
			{
				return RecordFits(recordSize, offset, count * stride);
			}
			return false;
		}

		internal static ulong[] ParsedCoreMasksForTest()
		{
			return parsedCoreMasks.ToArray();
		}

		internal static void SetParsedCoreMasksForTest(ulong[] cores)
		{
			parsedCoreMasks = new List<ulong>(cores ?? new ulong[0]);
		}

		private static ulong UnionOf(List<ulong> masks)
		{
			ulong num = 0uL;
			foreach (ulong mask in masks)
			{
				num |= mask;
			}
			return num;
		}

		public static uint[] BackgroundCpuSetIds()
		{
			return backgroundIds;
		}

		public static bool HasSafeBackgroundPartition()
		{
			if (backgroundIds != null && backgroundIds.Length > 0 && partitionGameIds != null)
			{
				return partitionGameIds.Length > 0;
			}
			return false;
		}

		public static uint[] InactiveBackgroundCpuSetIds()
		{
			return altBackgroundIds;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetSystemCpuSetInformation(IntPtr buffer, int length, out int returned, IntPtr process, uint flags);

		[DllImport("kernel32.dll")]
		private static extern ushort GetActiveProcessorGroupCount();

		static CpuTopology()
		{
			physicalCoreMasks = new List<ulong>();
			parsedCoreMasks = new List<ulong>();
			FavoredDetail = "";
			PartitionTag = "";
			cacheDomains = new List<KeyValuePair<uint, ulong>>();
			processorDieDomains = new List<ulong>();
			try
			{
				Parse();
			}
			catch
			{
				Hybrid = false;
				AsymCache = false;
			}
			DeriveMasks();
			try
			{
				BuildCpuSetPolicies();
			}
			catch
			{
			}
			ValidateMasks();
		}

		internal static bool PartitionAgreesWithEfficiency(ulong gamePartition, ulong background)
		{
			if (!Hybrid || PerfMask == 0 || EffMask == 0)
			{
				return true;
			}
			if ((gamePartition & EffMask) != 0)
			{
				return false;
			}
			if ((background & PerfMask) != 0)
			{
				return false;
			}
			return true;
		}

		internal static ulong ReconcileAllMask(ulong fromCount, ulong fromEnum, out bool mismatch)
		{
			mismatch = fromEnum != 0 && fromEnum != fromCount;
			if (!mismatch)
			{
				return fromCount;
			}
			ulong num = fromCount & fromEnum;
			if (num == 0)
			{
				return fromCount;
			}
			return num;
		}

		private static void DeriveMasks()
		{
			int processorCount = Environment.ProcessorCount;
			ulong fromCount = (ulong)((processorCount >= 64) ? (-1) : ((1L << processorCount) - 1));
			AllMask = ReconcileAllMask(fromCount, ParsedUnion, out AllMaskReconciled);
			if (Hybrid)
			{
				ThrottleMask = EffMask;
				BoostMask = AllMask;
			}
			else if (AsymCache)
			{
				ThrottleMask = SmallL3Mask;
				BoostMask = AllMask;
			}
			else
			{
				ThrottleMask = (ulong)((processorCount >= 2 && processorCount <= 64) ? (3L << processorCount - 2) : ((processorCount >= 2) ? 0 : 1));
				BoostMask = AllMask;
			}
			StrictBoostMask = CpuPartitionPolicy.StrictMask(AllMask, ThrottleMask, Hybrid ? PerfMask : 0, AsymCache ? BigL3Mask : 0);
		}

		internal static ulong SafeStrictMask(ulong strict, ulong throttle, ulong all, ulong eff, bool hybrid)
		{
			if (strict == 0 || (strict & all) == 0)
			{
				return all;
			}
			if ((strict & throttle) != 0)
			{
				return all;
			}
			if (hybrid && eff != 0 && (strict & eff) != 0)
			{
				return all;
			}
			return strict;
		}

		private static void ValidateMasks()
		{
			ulong num = SafeStrictMask(StrictBoostMask, ThrottleMask, AllMask, EffMask, Hybrid);
			if (num != StrictBoostMask)
			{
				StrictMaskUnsafe = true;
				StrictBoostMask = num;
				partitionGameIds = null;
				backgroundIds = null;
			}
		}

		public static uint[] CpuSetIdsFor(ulong mask)
		{
			int returned;
			GetSystemCpuSetInformation(IntPtr.Zero, 0, out returned, IntPtr.Zero, 0u);
			if (returned <= 0)
			{
				return null;
			}
			int num = returned;
			IntPtr intPtr = Marshal.AllocHGlobal(num);
			try
			{
				if (!GetSystemCpuSetInformation(intPtr, num, out returned, IntPtr.Zero, 0u) || returned <= 0 || returned > num)
				{
					return null;
				}
				List<uint> list = new List<uint>();
				int num3;
				for (long num2 = 0L; num2 + 8 <= returned; num2 += num3)
				{
					IntPtr ptr = (IntPtr)((long)intPtr + num2);
					num3 = Marshal.ReadInt32(ptr, 0);
					int num4 = Marshal.ReadInt32(ptr, 4);
					if (num3 <= 0 || num2 + num3 > returned)
					{
						break;
					}
					if (num4 == 0 && num3 >= 16)
					{
						uint item = (uint)Marshal.ReadInt32(ptr, 8);
						short num5 = Marshal.ReadInt16(ptr, 12);
						byte b = Marshal.ReadByte(ptr, 14);
						if (num5 == 0 && b < 64 && ((mask >> (int)b) & 1) != 0)
						{
							list.Add(item);
						}
					}
				}
				return (list.Count > 0) ? list.ToArray() : null;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		public static ulong[] DieMasks()
		{
			ulong[] array = DomainMasks(processorDieDomains);
			if (array.Length >= 2)
			{
				return array;
			}
			List<ulong> list = new List<ulong>();
			foreach (KeyValuePair<uint, ulong> cacheDomain in cacheDomains)
			{
				list.Add(cacheDomain.Value);
			}
			return DomainMasks(list);
		}

		private static ulong[] DomainMasks(List<ulong> domains)
		{
			List<ulong> list = new List<ulong>();
			foreach (ulong domain in domains)
			{
				ulong num = domain & AllMask;
				if (num != 0 && !list.Contains(num))
				{
					list.Add(num);
				}
			}
			if (list.Count < 2)
			{
				return new ulong[0];
			}
			list.Sort();
			return list.ToArray();
		}

		public static ulong CacheHeavyMask()
		{
			if (!AsymCache)
			{
				return 0uL;
			}
			return BigL3Mask & AllMask;
		}

		internal static int CountSetBits(ulong v)
		{
			int num = 0;
			while (v != 0)
			{
				num += (int)(v & 1);
				v >>= 1;
			}
			return num;
		}

		public static string DescribeMask(ulong mask)
		{
			if (mask == 0)
			{
				return Lang.T("t.cputopology.1");
			}
			List<string> list = new List<string>();
			int i = 0;
			while (i < 64)
			{
				if ((mask & (ulong)(1L << i)) == 0)
				{
					i++;
					continue;
				}
				int num = i;
				for (; i + 1 < 64 && (mask & (ulong)(1L << i + 1)) != 0; i++)
				{
				}
				list.Add((i > num + 1) ? (num + "-" + i) : ((i == num + 1) ? (num + "," + i) : num.ToString()));
				i++;
			}
			return CountSetBits(mask) + Lang.T("t.cputopology.2") + string.Join(",", list.ToArray());
		}
	}
}
