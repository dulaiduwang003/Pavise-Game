using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class CpuPartitionPolicy
	{
		public struct CoreDesc
		{
			public ulong Mask;

			public int L3;

			public int Die;

			public int Eff;

			public uint L3Size;
		}

		public struct CorePlan
		{
			public ulong Game;

			public ulong Background;

			public ulong AltGame;

			public ulong AltBackground;

			public int GameDomain;

			public int AltDomain;

			public bool Partitioned;

			public string Tag;
		}

		public const int SqueezeMinPhysical = 5;

		public const int IsolateCore0MinPhysical = 8;

		public const int MinGameDomainCores = 6;

		public static ulong StrictMask(ulong all, ulong background, ulong performance, ulong cache)
		{
			ulong num = ((cache != 0) ? cache : ((performance != 0) ? performance : (all & ~background)));
			num &= all;
			if (num == 0)
			{
				return all;
			}
			return num;
		}

		public static int BackgroundCoreCount(int physicalCoreCount)
		{
			if (physicalCoreCount <= 6)
			{
				return 0;
			}
			if (physicalCoreCount <= 10)
			{
				return 1;
			}
			return Math.Min(4, Math.Max(2, physicalCoreCount / 8));
		}

		public static ulong SqueezeMask(ulong[] physicalCores, ulong allowedMask, ulong effMask, bool hybrid)
		{
			return SqueezeMask(physicalCores, allowedMask, effMask, hybrid, null, 0uL);
		}

		public static ulong SqueezeMask(ulong[] physicalCores, ulong allowedMask, ulong effMask, bool hybrid, ulong[] l3Domains, ulong gameMask)
		{
			if (physicalCores == null || allowedMask == 0)
			{
				return 0uL;
			}
			int num = physicalCores.Length;
			if (num < 5)
			{
				return 0uL;
			}
			int num2 = ((num <= 8) ? 1 : 2);
			List<ulong> list = new List<ulong>();
			if (hybrid && effMask != 0)
			{
				foreach (ulong num3 in physicalCores)
				{
					if ((num3 & allowedMask) == num3 && (num3 & effMask) == num3)
					{
						list.Add(num3);
					}
				}
			}
			if (list.Count < num2)
			{
				list.Clear();
				foreach (ulong num4 in physicalCores)
				{
					if ((num4 & allowedMask) == num4)
					{
						list.Add(num4);
					}
				}
			}
			if (list.Count < num2)
			{
				return 0uL;
			}
			List<ulong> list2 = PreferCleanCacheDomain(list, l3Domains, gameMask, num2);
			list2.Sort();
			ulong num5 = 0uL;
			for (int k = list2.Count - num2; k < list2.Count; k++)
			{
				if (k >= 0)
				{
					num5 |= list2[k];
				}
			}
			num5 &= allowedMask;
			if (num5 == 0 || num5 == allowedMask)
			{
				return 0uL;
			}
			if (BitCount(num5) * 2 >= BitCount(allowedMask))
			{
				return 0uL;
			}
			return num5;
		}

		internal static List<ulong> PreferCleanCacheDomain(List<ulong> pool, ulong[] l3Domains, ulong gameMask, int reserve)
		{
			if (l3Domains == null || l3Domains.Length < 2 || pool.Count <= reserve)
			{
				return pool;
			}
			List<ulong> list = null;
			foreach (ulong num in l3Domains)
			{
				if (num == 0 || (num & gameMask) != 0)
				{
					continue;
				}
				List<ulong> list2 = new List<ulong>();
				foreach (ulong item in pool)
				{
					if ((item & num) == item)
					{
						list2.Add(item);
					}
				}
				if (list2.Count >= reserve && (list == null || list2.Count < list.Count))
				{
					list = list2;
				}
			}
			return list ?? pool;
		}

		internal static int BitCount(ulong mask)
		{
			int num = 0;
			while (mask != 0)
			{
				mask &= mask - 1;
				num++;
			}
			return num;
		}

		public static double CoreInterruptRate(double[] rates, ulong coreMask)
		{
			if (rates == null || coreMask == 0)
			{
				return 0.0;
			}
			double num = 0.0;
			for (int i = 0; i < rates.Length && i < 64; i++)
			{
				if (((coreMask >> i) & 1) != 0 && rates[i] > num)
				{
					num = rates[i];
				}
			}
			return num;
		}

		public static CorePlan Decide(CoreDesc[] cores, ulong allMask)
		{
			CorePlan corePlan = default(CorePlan);
			corePlan.Game = allMask;
			corePlan.Background = 0uL;
			corePlan.GameDomain = -1;
			corePlan.AltDomain = -1;
			corePlan.Partitioned = false;
			corePlan.Tag = "flat";
			CorePlan result = corePlan;
			if (cores == null || cores.Length == 0 || allMask == 0)
			{
				return result;
			}
			int num = int.MaxValue;
			int num2 = int.MinValue;
			CoreDesc[] array = cores;
			for (int i = 0; i < array.Length; i++)
			{
				CoreDesc coreDesc = array[i];
				if (coreDesc.Eff < num)
				{
					num = coreDesc.Eff;
				}
				if (coreDesc.Eff > num2)
				{
					num2 = coreDesc.Eff;
				}
			}
			if (num2 > num)
			{
				ulong num3 = 0uL;
				ulong num4 = 0uL;
				for (int j = 0; j < cores.Length; j++)
				{
					CoreDesc coreDesc2 = cores[j];
					if (coreDesc2.Eff == num2)
					{
						num3 |= coreDesc2.Mask;
					}
					else
					{
						num4 |= coreDesc2.Mask;
					}
				}
				if (num3 != 0 && num4 != 0)
				{
					result.Game = num3;
					result.Background = num4;
					result.Partitioned = true;
					result.Tag = "hybrid-pe";
					return result;
				}
			}
			Dictionary<int, ulong> dictionary = new Dictionary<int, ulong>();
			Dictionary<int, uint> dictionary2 = new Dictionary<int, uint>();
			for (int k = 0; k < cores.Length; k++)
			{
				CoreDesc coreDesc3 = cores[k];
				if (coreDesc3.L3 >= 0 && coreDesc3.L3Size != 0)
				{
					ulong value;
					dictionary.TryGetValue(coreDesc3.L3, out value);
					dictionary[coreDesc3.L3] = value | coreDesc3.Mask;
					dictionary2[coreDesc3.L3] = coreDesc3.L3Size;
				}
			}
			ulong num5 = 0uL;
			for (int l = 0; l < cores.Length; l++)
			{
				CoreDesc coreDesc4 = cores[l];
				if ((coreDesc4.Mask & 1) != 0)
				{
					num5 = coreDesc4.Mask;
					break;
				}
			}
			if (dictionary.Count >= 2)
			{
				uint num6 = 0u;
				uint num7 = uint.MaxValue;
				foreach (uint value4 in dictionary2.Values)
				{
					if (value4 > num6)
					{
						num6 = value4;
					}
					if (value4 < num7)
					{
						num7 = value4;
					}
				}
				if (num6 > num7 && num6 != 0)
				{
					ulong num8 = 0uL;
					ulong num9 = 0uL;
					foreach (KeyValuePair<int, ulong> item in dictionary)
					{
						if (dictionary2[item.Key] == num6)
						{
							num8 |= item.Value;
						}
						else
						{
							num9 |= item.Value;
						}
					}
					if (num8 != 0 && num9 != 0)
					{
						result.Game = num8;
						result.Background = num9;
						result.AltGame = num9;
						result.AltBackground = num8;
						result.Partitioned = true;
						result.Tag = "x3d-cache";
						return result;
					}
				}
			}
			Dictionary<int, ulong> dictionary3 = new Dictionary<int, ulong>();
			Dictionary<int, int> dictionary4 = new Dictionary<int, int>();
			for (int m = 0; m < cores.Length; m++)
			{
				CoreDesc coreDesc5 = cores[m];
				if (coreDesc5.Die >= 0)
				{
					ulong value2;
					dictionary3.TryGetValue(coreDesc5.Die, out value2);
					dictionary3[coreDesc5.Die] = value2 | coreDesc5.Mask;
					int value3;
					dictionary4.TryGetValue(coreDesc5.Die, out value3);
					dictionary4[coreDesc5.Die] = value3 + 1;
				}
			}
			if (dictionary3.Count >= 2)
			{
				int num10 = -1;
				int num11 = -1;
				foreach (KeyValuePair<int, int> item2 in dictionary4)
				{
					if (item2.Value > num11 || (item2.Value == num11 && (num10 < 0 || item2.Key < num10)))
					{
						num10 = item2.Key;
						num11 = item2.Value;
					}
				}
				ulong num12 = ((num10 >= 0) ? (dictionary3[num10] & allMask) : 0);
				ulong num13 = allMask & ~num12;
				if (num12 != 0 && num13 != 0 && num11 >= 6)
				{
					result.Game = num12;
					result.Background = num13;
					result.Partitioned = true;
					result.Tag = "symmetric-ccd";
					result.GameDomain = num10;
					int num14 = -1;
					int num15 = -1;
					foreach (KeyValuePair<int, int> item3 in dictionary4)
					{
						if (item3.Key != num10 && (item3.Value > num15 || (item3.Value == num15 && (num14 < 0 || item3.Key < num14))))
						{
							num14 = item3.Key;
							num15 = item3.Value;
						}
					}
					if (num14 >= 0 && num15 >= 6)
					{
						ulong num16 = dictionary3[num14] & allMask;
						ulong num17 = allMask & ~num16;
						if (num16 != 0 && num17 != 0)
						{
							result.AltGame = num16;
							result.AltBackground = num17;
							result.AltDomain = num14;
						}
					}
					return result;
				}
			}
			if (cores.Length >= 8 && num5 != 0)
			{
				int num18 = Math.Max(1, BackgroundCoreCount(cores.Length));
				List<ulong> list = new List<ulong>();
				array = cores;
				for (int i = 0; i < array.Length; i++)
				{
					CoreDesc coreDesc6 = array[i];
					if (coreDesc6.Mask != num5)
					{
						list.Add(coreDesc6.Mask);
					}
				}
				list.Sort();
				ulong num19 = num5;
				for (int n = 0; n < num18 - 1 && list.Count - n - 1 >= 0; n++)
				{
					num19 |= list[list.Count - n - 1];
				}
				ulong num20 = allMask & ~num19;
				if (num20 != 0)
				{
					result.Game = num20;
					result.Background = num19;
					result.Partitioned = true;
					result.Tag = "pool-iso-core0";
					return result;
				}
			}
			int num21 = BackgroundCoreCount(cores.Length);
			if (num21 > 0)
			{
				List<ulong> list2 = new List<ulong>();
				array = cores;
				for (int i = 0; i < array.Length; i++)
				{
					CoreDesc coreDesc7 = array[i];
					list2.Add(coreDesc7.Mask);
				}
				list2.Sort();
				ulong num22 = 0uL;
				for (int num23 = list2.Count - num21; num23 < list2.Count; num23++)
				{
					if (num23 >= 0)
					{
						num22 |= list2[num23];
					}
				}
				ulong num24 = allMask & ~num22;
				if (num22 != 0 && num24 != 0)
				{
					result.Game = num24;
					result.Background = num22;
					result.Partitioned = true;
					result.Tag = "tail-reserve";
					return result;
				}
			}
			return result;
		}
	}
}
