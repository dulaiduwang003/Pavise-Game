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
            int preferredCoreCount = ((physicalCoresCount <= 8) ? 1 : 2);
            List<ulong> maskValues = new List<ulong>();
            if (hybrid && effMask != 0)
            {
                foreach (ulong physicalCore in physicalCores)
                {
                    if ((physicalCore & allowedMask) == physicalCore && (physicalCore & effMask) == physicalCore)
                    {
                        maskValues.Add(physicalCore);
                    }
                }
            }
            if (maskValues.Count < preferredCoreCount)
            {
                maskValues.Clear();
                foreach (ulong currentPhysicalCore in physicalCores)
                {
                    if ((currentPhysicalCore & allowedMask) == currentPhysicalCore)
                    {
                        maskValues.Add(currentPhysicalCore);
                    }
                }
            }
            if (maskValues.Count < preferredCoreCount)
            {
                return 0uL;
            }
            List<ulong> preferredCoreMasks = PreferCleanCacheDomain(maskValues, l3Domains, gameMask, preferredCoreCount);
            preferredCoreMasks.Sort();
            ulong squeezeMaskResult = 0uL;
            for (int k = preferredCoreMasks.Count - preferredCoreCount; k < preferredCoreMasks.Count; k++)
            {
                if (k >= 0)
                {
                    squeezeMaskResult |= preferredCoreMasks[k];
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
            List<ulong> cleanDomainCandidates = null;
            foreach (ulong l3Domain in l3Domains)
            {
                if (l3Domain == 0 || (l3Domain & gameMask) != 0)
                {
                    continue;
                }
                List<ulong> maskValues = new List<ulong>();
                foreach (ulong currentPool in pool)
                {
                    if ((currentPool & l3Domain) == currentPool)
                    {
                        maskValues.Add(currentPool);
                    }
                }
                if (maskValues.Count >= reserve && (cleanDomainCandidates == null || maskValues.Count < cleanDomainCandidates.Count))
                {
                    cleanDomainCandidates = maskValues;
                }
            }
            return cleanDomainCandidates ?? pool;
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
            CorePlan result = default(CorePlan);
            result.Game = allMask;
            result.Background = 0uL;
            result.GameDomain = -1;
            result.AltDomain = -1;
            result.Partitioned = false;
            result.Tag = "flat";
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
                    CoreDesc core = cores[j];
                    if (core.Eff == minValue)
                    {
                        gameValue |= core.Mask;
                    }
                    else
                    {
                        backgroundValue |= core.Mask;
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
            Dictionary<int, ulong> coreMasksByL3 = new Dictionary<int, ulong>();
            Dictionary<int, uint> l3SizesByL3 = new Dictionary<int, uint>();
            for (int k = 0; k < cores.Length; k++)
            {
                CoreDesc currentCore = cores[k];
                if (currentCore.L3 >= 0 && currentCore.L3Size != 0)
                {
                    ulong accumulatedL3Mask;
                    coreMasksByL3.TryGetValue(currentCore.L3, out accumulatedL3Mask);
                    coreMasksByL3[currentCore.L3] = accumulatedL3Mask | currentCore.Mask;
                    l3SizesByL3[currentCore.L3] = currentCore.L3Size;
                }
            }
            ulong lowestCoreMask = 0uL;
            for (int l = 0; l < cores.Length; l++)
            {
                CoreDesc candidateCore = cores[l];
                if ((candidateCore.Mask & 1) != 0)
                {
                    lowestCoreMask = candidateCore.Mask;
                    break;
                }
            }
            if (coreMasksByL3.Count >= 2)
            {
                uint largestL3Size = 0u;
                uint maxValue2 = uint.MaxValue;
                foreach (uint l3Size in l3SizesByL3.Values)
                {
                    if (l3Size > largestL3Size)
                    {
                        largestL3Size = l3Size;
                    }
                    if (l3Size < maxValue2)
                    {
                        maxValue2 = l3Size;
                    }
                }
                if (largestL3Size > maxValue2 && largestL3Size != 0)
                {
                    ulong gameValue2 = 0uL;
                    ulong backgroundValue2 = 0uL;
                    foreach (KeyValuePair<int, ulong> coreMaskEntry in coreMasksByL3)
                    {
                        if (l3SizesByL3[coreMaskEntry.Key] == largestL3Size)
                        {
                            gameValue2 |= coreMaskEntry.Value;
                        }
                        else
                        {
                            backgroundValue2 |= coreMaskEntry.Value;
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
            Dictionary<int, ulong> coreMasksByDie = new Dictionary<int, ulong>();
            Dictionary<int, int> coreCountsByDie = new Dictionary<int, int>();
            for (int m = 0; m < cores.Length; m++)
            {
                CoreDesc resolvedCore = cores[m];
                if (resolvedCore.Die >= 0)
                {
                    ulong coreMask;
                    coreMasksByDie.TryGetValue(resolvedCore.Die, out coreMask);
                    coreMasksByDie[resolvedCore.Die] = coreMask | resolvedCore.Mask;
                    int dieCoreCount;
                    coreCountsByDie.TryGetValue(resolvedCore.Die, out dieCoreCount);
                    coreCountsByDie[resolvedCore.Die] = dieCoreCount + 1;
                }
            }
            if (coreMasksByDie.Count >= 2)
            {
                int gameDomainValue = -1;
                int primaryCoreCount = -1;
                foreach (KeyValuePair<int, int> coreCountEntry in coreCountsByDie)
                {
                    if (coreCountEntry.Value > primaryCoreCount || (coreCountEntry.Value == primaryCoreCount && (gameDomainValue < 0 || coreCountEntry.Key < gameDomainValue)))
                    {
                        gameDomainValue = coreCountEntry.Key;
                        primaryCoreCount = coreCountEntry.Value;
                    }
                }
                ulong gameValue3 = ((gameDomainValue >= 0) ? (coreMasksByDie[gameDomainValue] & allMask) : 0);
                ulong backgroundValue3 = allMask & ~gameValue3;
                if (gameValue3 != 0 && backgroundValue3 != 0 && primaryCoreCount >= 6)
                {
                    result.Game = gameValue3;
                    result.Background = backgroundValue3;
                    result.Partitioned = true;
                    result.Tag = "symmetric-ccd";
                    result.GameDomain = gameDomainValue;
                    int altDomainValue = -1;
                    int alternateCoreCount = -1;
                    foreach (KeyValuePair<int, int> currentCoreCountEntry in coreCountsByDie)
                    {
                        if (currentCoreCountEntry.Key != gameDomainValue && (currentCoreCountEntry.Value > alternateCoreCount || (currentCoreCountEntry.Value == alternateCoreCount && (altDomainValue < 0 || currentCoreCountEntry.Key < altDomainValue))))
                        {
                            altDomainValue = currentCoreCountEntry.Key;
                            alternateCoreCount = currentCoreCountEntry.Value;
                        }
                    }
                    if (altDomainValue >= 0 && alternateCoreCount >= 6)
                    {
                        ulong altGameValue = coreMasksByDie[altDomainValue] & allMask;
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
            if (cores.Length >= 8 && lowestCoreMask != 0)
            {
                int backgroundCoreCountLimit = Math.Max(1, BackgroundCoreCount(cores.Length));
                List<ulong> masks = new List<ulong>();
                coresSnapshot = cores;
                for (int i = 0; i < coresSnapshot.Length; i++)
                {
                    CoreDesc selectedCore = coresSnapshot[i];
                    if (selectedCore.Mask != lowestCoreMask)
                    {
                        masks.Add(selectedCore.Mask);
                    }
                }
                masks.Sort();
                ulong backgroundValue4 = lowestCoreMask;
                for (int n = 0; n < backgroundCoreCountLimit - 1 && masks.Count - n - 1 >= 0; n++)
                {
                    backgroundValue4 |= masks[masks.Count - n - 1];
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
                List<ulong> currentMasks = new List<ulong>();
                coresSnapshot = cores;
                for (int i = 0; i < coresSnapshot.Length; i++)
                {
                    CoreDesc workingCore = coresSnapshot[i];
                    currentMasks.Add(workingCore.Mask);
                }
                currentMasks.Sort();
                ulong backgroundValue5 = 0uL;
                for (int list2Index = currentMasks.Count - backgroundCoreCountResult; list2Index < currentMasks.Count; list2Index++)
                {
                    if (list2Index >= 0)
                    {
                        backgroundValue5 |= currentMasks[list2Index];
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
