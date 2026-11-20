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

            // 系统给的调度评级 取这个物理核各逻辑核里最高的一个 越大越是首选核 读不到时全为 0
            public int Rank;
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
            return SqueezeMask(physicalCores, allowedMask, effMask, hybrid, l3Domains, gameMask, null);
        }

        // ranks 与 physicalCores 一一对应 评级有高低时收到评级最低的核 评级全相同或缺失时沿用末尾的核
        public static ulong SqueezeMask(ulong[] physicalCores, ulong allowedMask, ulong effMask, bool hybrid, ulong[] l3Domains, ulong gameMask, int[] ranks)
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
            ulong squeezeMaskResult = 0uL;
            Dictionary<ulong, int> rankOf = RankLookup(physicalCores, ranks);
            if (rankOf != null && RanksDiffer(list2, rankOf))
            {
                list2.Sort((a, b) => rankOf[a] != rankOf[b] ? rankOf[a].CompareTo(rankOf[b]) : b.CompareTo(a));
                for (int k = 0; k < squeezeMaskValue && k < list2.Count; k++)
                {
                    squeezeMaskResult |= list2[k];
                }
            }
            else
            {
                list2.Sort();
                for (int k = list2.Count - squeezeMaskValue; k < list2.Count; k++)
                {
                    if (k >= 0)
                    {
                        squeezeMaskResult |= list2[k];
                    }
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

        private static Dictionary<ulong, int> RankLookup(ulong[] physicalCores, int[] ranks)
        {
            if (physicalCores == null || ranks == null || ranks.Length != physicalCores.Length) return null;
            var rankOf = new Dictionary<ulong, int>();
            for (int i = 0; i < physicalCores.Length; i++) rankOf[physicalCores[i]] = ranks[i];
            return rankOf;
        }

        private static bool RanksDiffer(List<ulong> cores, Dictionary<ulong, int> rankOf)
        {
            int low = int.MaxValue, high = int.MinValue, rank;
            foreach (ulong core in cores)
            {
                if (!rankOf.TryGetValue(core, out rank)) return false;
                low = Math.Min(low, rank); high = Math.Max(high, rank);
            }
            return high > low;
        }

        // 评级全相同的老平台没有首选核 调用方这时按核的序号挑选
        internal static bool RanksDiffer(CoreDesc[] cores)
        {
            int low = int.MaxValue, high = int.MinValue;
            foreach (CoreDesc core in cores) { low = Math.Min(low, core.Rank); high = Math.Max(high, core.Rank); }
            return cores.Length > 1 && high > low;
        }

        // 评级最高的那个核所在的 CCD 评级全相同或首选核不在任何 CCD 时返回 -1
        internal static int FavoredDie(CoreDesc[] cores)
        {
            if (cores == null || !RanksDiffer(cores)) return -1;
            CoreDesc best = cores[0];
            foreach (CoreDesc core in cores)
                if (core.Rank > best.Rank || (core.Rank == best.Rank && core.Mask < best.Mask)) best = core;
            return best.Die;
        }

        private static bool PlaceLowestRanked(CoreDesc[] cores, ulong allMask, int count, bool preferCore0, ref CorePlan result)
        {
            ulong background = LowestRanked(cores, count, preferCore0) & allMask;
            ulong game = allMask & ~background;
            if (background == 0 || game == 0) return false;
            result.Game = game;
            result.Background = background;
            result.Partitioned = true;
            result.Tag = "pool-low-rank";
            return true;
        }

        // 后台取评级最低的 count 个物理核 不碰首选核
        // 有多组三级缓存时先避开首选核所在的那组 让游戏的缓存干净 同一档评级里优先末尾的核
        // preferCore0 时同一档里先取 0 号核 把中断多的 0 号核让给后台 0 号核评级高时自然不会被选
        internal static ulong LowestRanked(CoreDesc[] cores, int count, bool preferCore0)
        {
            if (cores == null || count <= 0 || cores.Length <= count) return 0uL;
            CoreDesc best = cores[0];
            var domains = new HashSet<int>();
            foreach (CoreDesc core in cores)
            {
                if (core.Rank > best.Rank || (core.Rank == best.Rank && core.Mask < best.Mask)) best = core;
                if (core.L3 >= 0) domains.Add(core.L3);
            }
            bool splitCache = domains.Count >= 2 && best.L3 >= 0;
            var order = new List<CoreDesc>(cores);
            order.Sort((a, b) =>
            {
                if (splitCache)
                {
                    int sharesA = a.L3 == best.L3 ? 1 : 0, sharesB = b.L3 == best.L3 ? 1 : 0;
                    if (sharesA != sharesB) return sharesA.CompareTo(sharesB);
                }
                if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
                if (preferCore0)
                {
                    int zeroA = (a.Mask & 1) != 0 ? 0 : 1, zeroB = (b.Mask & 1) != 0 ? 0 : 1;
                    if (zeroA != zeroB) return zeroA.CompareTo(zeroB);
                }
                return b.Mask.CompareTo(a.Mask);
            });
            ulong chosen = 0uL;
            for (int i = 0; i < count; i++) chosen |= order[i].Mask;
            return chosen;
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
                // 首选核不一定在 0 号 CCD 评级有高低时游戏放在首选核所在的那组 那组核数不够才按核数挑
                int favoredDie = FavoredDie(cores);
                int favoredCount;
                if (favoredDie >= 0 && favoredDie != gameDomainValue && dictionary4.TryGetValue(favoredDie, out favoredCount) && favoredCount >= MinGameDomainCores)
                {
                    gameDomainValue = favoredDie;
                    decideValue = favoredCount;
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
                // 0 号核常常就是评级最高的首选核 评级有高低时后台改取评级最低的核 首选核留给游戏
                if (RanksDiffer(cores) && PlaceLowestRanked(cores, allMask, backgroundCoreCountLimit, true, ref result)) return result;
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
            if (backgroundCoreCountResult > 0 && RanksDiffer(cores) && PlaceLowestRanked(cores, allMask, backgroundCoreCountResult, false, ref result)) return result;
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
