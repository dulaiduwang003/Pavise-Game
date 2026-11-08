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
			ulong cacheSnapshot = ((cache != 0) ? cache : ((performance != 0) ? performance : (all & ~background)));
			cacheSnapshot &= all;
			if (cacheSnapshot == 0)
			{
				return all;
			}
			return cacheSnapshot;
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
			int physicalCoresCount = physicalCores.Length;
			if (physicalCoresCount < 5)
			{
				return 0uL;
			}
			int squeezeMaskValue = ((physicalCoresCount <= 8) ? 1 : 2);
			List<ulong> list = new List<ulong>();
			if (hybrid && effMask != 0)
			{
				foreach (ulong numericValueEntry in physicalCores)
				{
					if ((numericValueEntry & allowedMask) == numericValueEntry && (numericValueEntry & effMask) == numericValueEntry)
					{
						list.Add(numericValueEntry);
					}
				}
			}
			if (list.Count < squeezeMaskValue)
			{
				list.Clear();
				foreach (ulong numericValueEntry2 in physicalCores)
				{
					if ((numericValueEntry2 & allowedMask) == numericValueEntry2)
					{
						list.Add(numericValueEntry2);
					}
				}
			}
			if (list.Count < squeezeMaskValue)
			{
				return 0uL;
			}
			List<ulong> list2 = PreferCleanCacheDomain(list, l3Domains, gameMask, squeezeMaskValue);
			list2.Sort();
			ulong squeezeMaskResult = 0uL;
			for (int k = list2.Count - squeezeMaskValue; k < list2.Count; k++)
			{
				if (k >= 0)
				{
					squeezeMaskResult |= list2[k];
				}
			}
			squeezeMaskResult &= allowedMask;
			if (squeezeMaskResult == 0 || squeezeMaskResult == allowedMask)
			{
				return 0uL;
			}
			if (BitCount(squeezeMaskResult) * 2 >= BitCount(allowedMask))
			{
				return 0uL;
			}
			return squeezeMaskResult;
		}

		internal static List<ulong> PreferCleanCacheDomain(List<ulong> pool, ulong[] l3Domains, ulong gameMask, int reserve)
		{
			if (l3Domains == null || l3Domains.Length < 2 || pool.Count <= reserve)
			{
				return pool;
			}
			List<ulong> list = null;
			foreach (ulong numericValueEntry in l3Domains)
			{
				if (numericValueEntry == 0 || (numericValueEntry & gameMask) != 0)
				{
					continue;
				}
				List<ulong> list2 = new List<ulong>();
				foreach (ulong item in pool)
				{
					if ((item & numericValueEntry) == item)
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
			int processedCount = 0;
			while (mask != 0)
			{
				mask &= mask - 1;
				processedCount++;
			}
			return processedCount;
		}

		public static double CoreInterruptRate(double[] rates, ulong coreMask)
		{
			if (rates == null || coreMask == 0)
			{
				return 0.0;
			}
			double coreInterruptRateResult = 0.0;
			for (int i = 0; i < rates.Length && i < 64; i++)
			{
				if (((coreMask >> i) & 1) != 0 && rates[i] > coreInterruptRateResult)
				{
					coreInterruptRateResult = rates[i];
				}
			}
			return coreInterruptRateResult;
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
			int maxValue = int.MaxValue;
			int minValue = int.MinValue;
			CoreDesc[] coresSnapshot = cores;
			for (int i = 0; i < coresSnapshot.Length; i++)
			{
				CoreDesc coreDesc = coresSnapshot[i];
				if (coreDesc.Eff < maxValue)
				{
					maxValue = coreDesc.Eff;
				}
				if (coreDesc.Eff > minValue)
				{
					minValue = coreDesc.Eff;
				}
			}
			if (minValue > maxValue)
			{
				ulong gameValue = 0uL;
				ulong backgroundValue = 0uL;
				for (int j = 0; j < cores.Length; j++)
				{
					CoreDesc coreDesc2 = cores[j];
					if (coreDesc2.Eff == minValue)
					{
						gameValue |= coreDesc2.Mask;
					}
					else
					{
						backgroundValue |= coreDesc2.Mask;
					}
				}
				if (gameValue != 0 && backgroundValue != 0)
				{
					result.Game = gameValue;
					result.Background = backgroundValue;
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
			ulong numericValue = 0uL;
			for (int l = 0; l < cores.Length; l++)
			{
				CoreDesc coreDesc4 = cores[l];
				if ((coreDesc4.Mask & 1) != 0)
				{
					numericValue = coreDesc4.Mask;
					break;
				}
			}
			if (dictionary.Count >= 2)
			{
				uint numericValue2 = 0u;
				uint maxValue2 = uint.MaxValue;
				foreach (uint value4 in dictionary2.Values)
				{
					if (value4 > numericValue2)
					{
						numericValue2 = value4;
					}
					if (value4 < maxValue2)
					{
						maxValue2 = value4;
					}
				}
				if (numericValue2 > maxValue2 && numericValue2 != 0)
				{
					ulong gameValue2 = 0uL;
					ulong backgroundValue2 = 0uL;
					foreach (KeyValuePair<int, ulong> item in dictionary)
					{
						if (dictionary2[item.Key] == numericValue2)
						{
							gameValue2 |= item.Value;
						}
						else
						{
							backgroundValue2 |= item.Value;
						}
					}
					if (gameValue2 != 0 && backgroundValue2 != 0)
					{
						result.Game = gameValue2;
						result.Background = backgroundValue2;
						result.AltGame = backgroundValue2;
						result.AltBackground = gameValue2;
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
				int gameDomainValue = -1;
				int decideValue = -1;
				foreach (KeyValuePair<int, int> item2 in dictionary4)
				{
					if (item2.Value > decideValue || (item2.Value == decideValue && (gameDomainValue < 0 || item2.Key < gameDomainValue)))
					{
						gameDomainValue = item2.Key;
						decideValue = item2.Value;
					}
				}
				ulong gameValue3 = ((gameDomainValue >= 0) ? (dictionary3[gameDomainValue] & allMask) : 0);
				ulong backgroundValue3 = allMask & ~gameValue3;
				if (gameValue3 != 0 && backgroundValue3 != 0 && decideValue >= 6)
				{
					result.Game = gameValue3;
					result.Background = backgroundValue3;
					result.Partitioned = true;
					result.Tag = "symmetric-ccd";
					result.GameDomain = gameDomainValue;
					int altDomainValue = -1;
					int decideValue2 = -1;
					foreach (KeyValuePair<int, int> item3 in dictionary4)
					{
						if (item3.Key != gameDomainValue && (item3.Value > decideValue2 || (item3.Value == decideValue2 && (altDomainValue < 0 || item3.Key < altDomainValue))))
						{
							altDomainValue = item3.Key;
							decideValue2 = item3.Value;
						}
					}
					if (altDomainValue >= 0 && decideValue2 >= 6)
					{
						ulong altGameValue = dictionary3[altDomainValue] & allMask;
						ulong altBackgroundValue = allMask & ~altGameValue;
						if (altGameValue != 0 && altBackgroundValue != 0)
						{
							result.AltGame = altGameValue;
							result.AltBackground = altBackgroundValue;
							result.AltDomain = altDomainValue;
						}
					}
					return result;
				}
			}
			if (cores.Length >= 8 && numericValue != 0)
			{
				int backgroundCoreCountLimit = Math.Max(1, BackgroundCoreCount(cores.Length));
				List<ulong> list = new List<ulong>();
				coresSnapshot = cores;
				for (int i = 0; i < coresSnapshot.Length; i++)
				{
					CoreDesc coreDesc6 = coresSnapshot[i];
					if (coreDesc6.Mask != numericValue)
					{
						list.Add(coreDesc6.Mask);
					}
				}
				list.Sort();
				ulong backgroundValue4 = numericValue;
				for (int n = 0; n < backgroundCoreCountLimit - 1 && list.Count - n - 1 >= 0; n++)
				{
					backgroundValue4 |= list[list.Count - n - 1];
				}
				ulong gameValue4 = allMask & ~backgroundValue4;
				if (gameValue4 != 0)
				{
					result.Game = gameValue4;
					result.Background = backgroundValue4;
					result.Partitioned = true;
					result.Tag = "pool-iso-core0";
					return result;
				}
			}
			int backgroundCoreCountResult = BackgroundCoreCount(cores.Length);
			if (backgroundCoreCountResult > 0)
			{
				List<ulong> list2 = new List<ulong>();
				coresSnapshot = cores;
				for (int i = 0; i < coresSnapshot.Length; i++)
				{
					CoreDesc coreDesc7 = coresSnapshot[i];
					list2.Add(coreDesc7.Mask);
				}
				list2.Sort();
				ulong backgroundValue5 = 0uL;
				for (int list2Index = list2.Count - backgroundCoreCountResult; list2Index < list2.Count; list2Index++)
				{
					if (list2Index >= 0)
					{
						backgroundValue5 |= list2[list2Index];
					}
				}
				ulong gameValue5 = allMask & ~backgroundValue5;
				if (backgroundValue5 != 0 && gameValue5 != 0)
				{
					result.Game = gameValue5;
					result.Background = backgroundValue5;
					result.Partitioned = true;
					result.Tag = "tail-reserve";
					return result;
				}
			}
			return result;
		}
	}
}
