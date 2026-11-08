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

        internal static bool CoreTypesConfirmed;
        internal static string CoreTypeDetail = "core-class-unavailable";
        private static int[] topologyClasses;

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
				ulong getParsedUnionResult = 0uL;
				foreach (ulong parsedCoreMask in parsedCoreMasks)
				{
					getParsedUnionResult |= parsedCoreMask;
				}
				return getParsedUnionResult;
			}
		}

        public static bool TopologySourcesAgree
        {
            get { return CpuCoreClassification.SamePhysicalCores(parsedCoreMasks.ToArray(), physicalCoreMasks.ToArray()); }
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
				uint numericValue = 0u;
				foreach (KeyValuePair<uint, ulong> cacheDomain in cacheDomains)
				{
					if (cacheDomain.Key > numericValue)
					{
						numericValue = cacheDomain.Key;
					}
				}
				return (int)(numericValue / 1048576);
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
				ulong valueMask = cacheDomain.Value & AllMask;
				if (valueMask != 0)
				{
					list.Add(valueMask);
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
					ulong backgroundSqueezeMaskResult = BackgroundSqueezeMask();
					if (backgroundSqueezeMaskResult != 0)
					{
						try
						{
							squeezeCache.Ids = CpuSetIdsFor(backgroundSqueezeMaskResult);
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
			int[] integerValues = new int[logical.Length];
			for (int i = 0; i < integerValues.Length; i++)
			{
				integerValues[i] = i;
			}
			Array.Sort(integerValues, (int a, int b) => logical[a].CompareTo(logical[b]));
			List<string> list = new List<string>();
			int describeCoreClassesValue = 0;
			for (int j = 1; j <= integerValues.Length; j++)
			{
				if (j == integerValues.Length || efficiency[integerValues[j]] != efficiency[integerValues[describeCoreClassesValue]] || scheduling[integerValues[j]] != scheduling[integerValues[describeCoreClassesValue]] || logical[integerValues[j]] != logical[integerValues[j - 1]] + 1)
				{
					int describeCoreClassesValue2 = logical[integerValues[describeCoreClassesValue]];
					int describeCoreClassesValue3 = logical[integerValues[j - 1]];
					list.Add(((describeCoreClassesValue2 == describeCoreClassesValue3) ? describeCoreClassesValue2.ToString() : (describeCoreClassesValue2 + "-" + describeCoreClassesValue3)) + ":" + efficiency[integerValues[describeCoreClassesValue]] + "/" + scheduling[integerValues[describeCoreClassesValue]]);
					describeCoreClassesValue = j;
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
			ulong favoredMaskOfResult = 0uL;
			for (int k = 0; k < logical.Length; k++)
			{
				if (efficiency[k] == b && scheduling[k] == b4 && logical[k] >= 0 && logical[k] < 64)
				{
					favoredMaskOfResult |= (ulong)(1L << logical[k]);
				}
			}
			return favoredMaskOfResult;
		}

		private static void BuildCpuSetPolicies()
		{
			try
			{
				MultiGroup |= GetActiveProcessorGroupCount() > 1;
			}
			catch
			{
				MultiGroup |= Environment.ProcessorCount > 64;
			}
			int returned;
			GetSystemCpuSetInformation(IntPtr.Zero, 0, out returned, IntPtr.Zero, 0u);
			if (returned <= 0 || returned > 4194304)
			{
				return;
			}
			int returnedSnapshot = returned;
			IntPtr intPtr = Marshal.AllocHGlobal(returnedSnapshot);
			try
			{
				if (!GetSystemCpuSetInformation(intPtr, returnedSnapshot, out returned, IntPtr.Zero, 0u) || returned <= 0 || returned > returnedSnapshot)
				{
					return;
				}
                byte[] cpuSetBytes = new byte[returned];
                Marshal.Copy(intPtr, cpuSetBytes, 0, returned);
                CpuTopologyEvidence evidence = CpuTopologyEvidence.FromCpuSets(cpuSetBytes);
                MultiGroup |= evidence.MultiGroup;
                if (!evidence.Valid) { CoreTypeDetail = evidence.Reason; return; }
				List<CpuSetRec> list = new List<CpuSetRec>();
				int cpuSetPoliciesValue;
				for (long iterationIndex = 0L; iterationIndex + 20 <= returned; iterationIndex += cpuSetPoliciesValue)
				{
					IntPtr ptr = (IntPtr)((long)intPtr + iterationIndex);
					cpuSetPoliciesValue = Marshal.ReadInt32(ptr, 0);
					int int32 = Marshal.ReadInt32(ptr, 4);
					if (cpuSetPoliciesValue <= 0 || iterationIndex + cpuSetPoliciesValue > returned)
					{
						break;
					}
					if (int32 == 0 && cpuSetPoliciesValue >= 20)
					{
						list.Add(new CpuSetRec
						{
							Id = (uint)Marshal.ReadInt32(ptr, 8),
							Group = Marshal.ReadInt16(ptr, 12),
							Logical = Marshal.ReadByte(ptr, 14),
							Core = Marshal.ReadByte(ptr, 15),
							Efficiency = Marshal.ReadByte(ptr, 18),
							Scheduling = (byte)((cpuSetPoliciesValue >= 21) ? Marshal.ReadByte(ptr, 20) : 0)
						});
					}
				}
				if (list.Count == 0)
				{
					return;
				}
				List<uint> list2 = new List<uint>();
				foreach (CpuSetRec item2 in list)
				{
					list2.Add(item2.Id);
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
                ResolveCoreTypes(evidence.Classes);
                // Recompute defaults after class reconciliation, before deriving CPU Set pools.
                DeriveMasks();
                if (MultiGroup || AllMaskReconciled || !TopologySourcesAgree) return;
				List<uint> list3 = new List<uint>();
				List<uint> list4 = new List<uint>();
				HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
                if (Hybrid)
                {
                    if (CountSetBits(PerfMask) >= 8)
                    {
                        foreach (CpuSetRec row in dictionary.Values)
                            if (row.Group == 0 && row.Logical < 64 && (EffMask & (1UL << row.Logical)) != 0)
                                hashSet.Add(row.Group + ":" + row.Core);
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
						uint[] cpuSetIdsForResult = CpuSetIdsFor(corePlan.Background);
						uint[] cpuSetIdsForResult2 = CpuSetIdsFor(corePlan.Game);
						if (cpuSetIdsForResult != null && cpuSetIdsForResult.Length > 0 && cpuSetIdsForResult2 != null && cpuSetIdsForResult2.Length > 0)
						{
							backgroundIds = cpuSetIdsForResult;
							partitionGameIds = cpuSetIdsForResult2;
							ThrottleMask = corePlan.Background;
							StrictBoostMask = corePlan.Game;
							PartitionTag = corePlan.Tag;
							if (corePlan.AltBackground != 0)
							{
								uint[] cpuSetIdsForResult3 = CpuSetIdsFor(corePlan.AltBackground);
								if (cpuSetIdsForResult3 != null && cpuSetIdsForResult3.Length > 0)
								{
									altBackgroundIds = cpuSetIdsForResult3;
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
						int compareToResult = a.Group.CompareTo(b.Group);
						return (compareToResult == 0) ? a.Core.CompareTo(b.Core) : compareToResult;
					});
					int backgroundCoreCountResult = CpuPartitionPolicy.BackgroundCoreCount(list6.Count);
					for (int k = Math.Max(0, list6.Count - backgroundCoreCountResult); k < list6.Count; k++)
					{
						hashSet.Add(list6[k].Group + ":" + list6[k].Core);
					}
				}
				ulong numericValue = 0uL;
				ulong numericValue2 = 0uL;
				foreach (CpuSetRec item5 in list)
				{
					string item = item5.Group + ":" + item5.Core;
					bool isInHashSet = hashSet.Contains(item);
					if (isInHashSet)
					{
						list3.Add(item5.Id);
					}
					else if (hashSet.Count > 0 && (!Hybrid || (item5.Group == 0 && item5.Logical < 64 && (PerfMask & (1UL << item5.Logical)) != 0)))
					{
						list4.Add(item5.Id);
					}
					if (item5.Group == 0 && item5.Logical < 64)
					{
						if (isInHashSet)
						{
							numericValue |= (ulong)(1L << (int)item5.Logical);
						}
						else if (hashSet.Count > 0 && (!Hybrid || (item5.Group == 0 && item5.Logical < 64 && (PerfMask & (1UL << item5.Logical)) != 0)))
						{
							numericValue2 |= (ulong)(1L << (int)item5.Logical);
						}
					}
				}
				if (AsymCache && !MultiGroup)
				{
					uint[] cpuSetIdsForResult4 = CpuSetIdsFor(ThrottleMask);
					uint[] cpuSetIdsForResult5 = CpuSetIdsFor(BigL3Mask);
					if (cpuSetIdsForResult4 != null && cpuSetIdsForResult4.Length > 0)
					{
						backgroundIds = cpuSetIdsForResult4;
					}
					if (cpuSetIdsForResult5 != null && cpuSetIdsForResult5.Length > 0)
					{
						partitionGameIds = cpuSetIdsForResult5;
					}
					uint[] cpuSetIdsForResult6 = CpuSetIdsFor(BigL3Mask);
					if (cpuSetIdsForResult6 != null && cpuSetIdsForResult6.Length > 0)
					{
						altBackgroundIds = cpuSetIdsForResult6;
						AltThrottleMask = BigL3Mask;
					}
				}
				else if (list3.Count > 0 && list4.Count > 0)
				{
					backgroundIds = list3.ToArray();
					partitionGameIds = list4.ToArray();
				}
				if (!MultiGroup && !AsymCache && numericValue != 0 && numericValue2 != 0)
				{
					if (PartitionAgreesWithEfficiency(numericValue2, numericValue))
					{
						ThrottleMask = numericValue;
						StrictBoostMask = numericValue2;
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
			if (length <= 0 || length > 4194304)
			{
				return;
			}
			int lengthSnapshot = length;
			IntPtr intPtr = Marshal.AllocHGlobal(lengthSnapshot);
			try
			{
				if (!GetLogicalProcessorInformationEx(65535, intPtr, ref length) || length <= 0 || length > lengthSnapshot)
				{
					return;
				}
                byte[] topologyBytes = new byte[length];
                Marshal.Copy(intPtr, topologyBytes, 0, length);
                CpuTopologyEvidence evidence = CpuTopologyEvidence.FromTopology(topologyBytes);
                MultiGroup |= evidence.MultiGroup;
                if (!evidence.Valid) { CoreTypeDetail = evidence.Reason; return; }
                topologyClasses = evidence.Classes;
				Dictionary<int, ulong> dictionary = new Dictionary<int, ulong>();
				List<ulong> list = new List<ulong>();
				List<KeyValuePair<uint, ulong>> list2 = new List<KeyValuePair<uint, ulong>>();
				List<ulong> list3 = new List<ulong>();
				bool ready = false;
				long numericValue = 0L;
				while (numericValue + 8 <= length)
				{
					IntPtr intPtr2 = (IntPtr)((long)intPtr + numericValue);
					int int32 = Marshal.ReadInt32(intPtr2, 0);
					int int322 = Marshal.ReadInt32(intPtr2, 4);
					if (int322 <= 0 || numericValue + int322 > length)
					{
						break;
					}
					IntPtr intPtr3 = (IntPtr)((long)intPtr2 + 8);
					switch (int32)
					{
					case 0:
					{
						if (!RecordFits(int322, 30, 2))
						{
							numericValue += int322;
							continue;
						}
						int key2 = Marshal.ReadByte(intPtr3, 1);
						int int16 = Marshal.ReadInt16(intPtr3, 22);
						if (!RecordArrayFits(int322, 32, int16, 16))
						{
							numericValue += int322;
							continue;
						}
						ulong numericValue2 = 0uL;
						for (int k = 0; k < int16; k++)
						{
							IntPtr ptr4 = (IntPtr)((long)intPtr3 + 24 + k * 16);
							if (Marshal.ReadInt16(ptr4, 8) != 0)
							{
								ready = true;
								continue;
							}
							ulong int64 = (ulong)Marshal.ReadInt64(ptr4, 0);
							ulong value;
							dictionary.TryGetValue(key2, out value);
							dictionary[key2] = value | int64;
							numericValue2 |= int64;
						}
						if (numericValue2 != 0)
						{
							list.Add(numericValue2);
						}
						break;
					}
					case 5:
					{
						if (!RecordFits(int322, 30, 2))
						{
							numericValue += int322;
							continue;
						}
						int int162 = Marshal.ReadInt16(intPtr3, 22);
						if (!RecordArrayFits(int322, 32, int162, 16))
						{
							numericValue += int322;
							continue;
						}
						ulong numericValue3 = 0uL;
						for (int j = 0; j < int162; j++)
						{
							IntPtr ptr3 = (IntPtr)((long)intPtr3 + 24 + j * 16);
							if (Marshal.ReadInt16(ptr3, 8) != 0)
							{
								ready = true;
							}
							else
							{
								numericValue3 |= (ulong)Marshal.ReadInt64(ptr3, 0);
							}
						}
						if (numericValue3 != 0)
						{
							list3.Add(numericValue3);
						}
						break;
					}
					case 2:
					{
						if (!RecordFits(int322, 8, 1))
						{
							numericValue += int322;
							continue;
						}
						if (Marshal.ReadByte(intPtr3, 0) != 3)
						{
							break;
						}
						if (!RecordFits(int322, 38, 2))
						{
							numericValue += int322;
							continue;
						}
						uint key = (uint)Marshal.ReadInt32(intPtr3, 4);
						int int163 = Marshal.ReadInt16(intPtr3, 30);
						ulong numericValue4 = 0uL;
						if (int163 == 0)
						{
							if (!RecordFits(int322, 40, 16))
							{
								numericValue += int322;
								continue;
							}
							IntPtr ptr = (IntPtr)((long)intPtr3 + 32);
							if (Marshal.ReadInt16(ptr, 8) != 0)
							{
								ready = true;
							}
							else
							{
								numericValue4 = (ulong)Marshal.ReadInt64(ptr, 0);
							}
						}
						else
						{
							if (!RecordArrayFits(int322, 40, int163, 16))
							{
								numericValue += int322;
								continue;
							}
							for (int i = 0; i < int163; i++)
							{
								IntPtr ptr2 = (IntPtr)((long)intPtr3 + 32 + i * 16);
								if (Marshal.ReadInt16(ptr2, 8) != 0)
								{
									ready = true;
								}
								else
								{
									numericValue4 |= (ulong)Marshal.ReadInt64(ptr2, 0);
								}
							}
						}
						if (numericValue4 != 0)
						{
							list2.Add(new KeyValuePair<uint, ulong>(key, numericValue4));
						}
						break;
					}
					}
					numericValue += int322;
				}
				if (ready)
				{
					return;
				}
				parsedCoreMasks = list;
				cacheDomains = list2;
				processorDieDomains = list3;
				if (dictionary.Count >= 2)
				{
					int minValue = int.MinValue;
					foreach (KeyValuePair<int, ulong> item in dictionary)
					{
						if (item.Key > minValue)
						{
							minValue = item.Key;
						}
					}
					ulong numericValue5 = 0uL;
					ulong numericValue6 = 0uL;
					foreach (KeyValuePair<int, ulong> item2 in dictionary)
					{
						if (item2.Key == minValue)
						{
							numericValue5 |= item2.Value;
						}
						else
						{
							numericValue6 |= item2.Value;
						}
					}
					if (numericValue5 != 0 && numericValue6 != 0)
					{
						PerfMask = numericValue5;
						EffMask = numericValue6;
						Hybrid = true;
					}
				}
				if (Hybrid || list2.Count < 2)
				{
					return;
				}
				uint numericValue7 = 0u;
				uint maxValue = uint.MaxValue;
				foreach (KeyValuePair<uint, ulong> item3 in list2)
				{
					if (item3.Key > numericValue7)
					{
						numericValue7 = item3.Key;
					}
					if (item3.Key < maxValue)
					{
						maxValue = item3.Key;
					}
				}
				if (numericValue7 <= maxValue)
				{
					return;
				}
				ulong numericValue8 = 0uL;
				ulong numericValue9 = 0uL;
				foreach (KeyValuePair<uint, ulong> item4 in list2)
				{
					if (item4.Key == numericValue7)
					{
						numericValue8 |= item4.Value;
					}
					else
					{
						numericValue9 |= item4.Value;
					}
				}
				if (numericValue8 != 0 && numericValue9 != 0)
				{
					BigL3Mask = numericValue8;
					SmallL3Mask = numericValue9;
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
			ulong unionOfResult = 0uL;
			foreach (ulong mask in masks)
			{
				unionOfResult |= mask;
			}
			return unionOfResult;
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
            Hybrid = false;
            PerfMask = EffMask = 0;
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

        private static void ResolveCoreTypes(int[] cpuSetClasses)
        {
            bool trusted = !MultiGroup && !AllMaskReconciled && TopologySourcesAgree;
            CpuCoreClassificationResult result = CpuCoreClassification.Resolve(AllMask,
                physicalCoreMasks.ToArray(), topologyClasses, cpuSetClasses, null, trusted);
            // A flat Windows class does not rule out a hybrid CPU with SMT disabled.
            // The fallback samples only its own temporary worker and never reassigns a game.
            string probeReason = "";
            if (trusted && result.State == CpuCoreClassificationState.Unknown)
            {
                int[] coreTypes = CpuCoreTypeProbe.Read(AllMask, out probeReason);
                result = CpuCoreClassification.Resolve(AllMask, physicalCoreMasks.ToArray(),
                    topologyClasses, cpuSetClasses, coreTypes, trusted);
            }
            Hybrid = result.State == CpuCoreClassificationState.Hybrid;
            CoreTypesConfirmed = Hybrid || result.State == CpuCoreClassificationState.Uniform;
            PerfMask = Hybrid ? result.PerformanceMask : 0;
            EffMask = Hybrid ? result.EfficiencyMask : 0;
            CoreTypeDetail = result.Source + ":" + result.Reason + (probeReason.Length == 0 ? "" : ";" + probeReason);
            if (Hybrid)
            {
                AsymCache = false;
                FavoredMask &= PerfMask;
            }
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
            // A count does not identify processor indexes. Preserve the validated
            // active mask even when disabled CPUs leave gaps in the numbering.
            mismatch = fromEnum != 0 && CountSetBits(fromEnum) != CountSetBits(fromCount);
            return fromEnum != 0 ? fromEnum : fromCount;
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
                ThrottleMask = 0;
                int remaining = 2;
                for (int logical = 63; logical >= 0 && remaining > 0; logical--)
                    if ((AllMask & (1UL << logical)) != 0) { ThrottleMask |= 1UL << logical; remaining--; }
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
			ulong safeStrictMaskResult = SafeStrictMask(StrictBoostMask, ThrottleMask, AllMask, EffMask, Hybrid);
			if (safeStrictMaskResult != StrictBoostMask)
			{
				StrictMaskUnsafe = true;
				StrictBoostMask = safeStrictMaskResult;
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
			int returnedSnapshot = returned;
			IntPtr intPtr = Marshal.AllocHGlobal(returnedSnapshot);
			try
			{
				if (!GetSystemCpuSetInformation(intPtr, returnedSnapshot, out returned, IntPtr.Zero, 0u) || returned <= 0 || returned > returnedSnapshot)
				{
					return null;
				}
				List<uint> list = new List<uint>();
				int cpuSetIdsForValue;
				for (long iterationIndex = 0L; iterationIndex + 8 <= returned; iterationIndex += cpuSetIdsForValue)
				{
					IntPtr ptr = (IntPtr)((long)intPtr + iterationIndex);
					cpuSetIdsForValue = Marshal.ReadInt32(ptr, 0);
					int int32 = Marshal.ReadInt32(ptr, 4);
					if (cpuSetIdsForValue <= 0 || iterationIndex + cpuSetIdsForValue > returned)
					{
						break;
					}
					if (int32 == 0 && cpuSetIdsForValue >= 16)
					{
						uint item = (uint)Marshal.ReadInt32(ptr, 8);
						short int16 = Marshal.ReadInt16(ptr, 12);
						byte b = Marshal.ReadByte(ptr, 14);
						if (int16 == 0 && b < 64 && ((mask >> (int)b) & 1) != 0)
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
			ulong[] domainMasksResult = DomainMasks(processorDieDomains);
			if (domainMasksResult.Length >= 2)
			{
				return domainMasksResult;
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
				ulong domainMask = domain & AllMask;
				if (domainMask != 0 && !list.Contains(domainMask))
				{
					list.Add(domainMask);
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
			int countSetBitsResult = 0;
			while (v != 0)
			{
				countSetBitsResult += (int)(v & 1);
				v >>= 1;
			}
			return countSetBitsResult;
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
				int iSnapshot = i;
				for (; i + 1 < 64 && (mask & (ulong)(1L << i + 1)) != 0; i++)
				{
				}
				list.Add((i > iSnapshot + 1) ? (iSnapshot + "-" + i) : ((i == iSnapshot + 1) ? (iSnapshot + "," + i) : iSnapshot.ToString()));
				i++;
			}
			return CountSetBits(mask) + Lang.T("t.cputopology.2") + string.Join(",", list.ToArray());
		}
	}
}
