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
                uint largestCacheSize = 0u;
                foreach (KeyValuePair<uint, ulong> cacheDomain in cacheDomains)
                {
                    if (cacheDomain.Key > largestCacheSize)
                    {
                        largestCacheSize = cacheDomain.Key;
                    }
                }
                return (int)(largestCacheSize / 1048576);
            }
            catch
            {
                return 0;
            }
        }

        public static ulong[] L3Masks()
        {
            List<ulong> coreMasks = new List<ulong>();
            foreach (KeyValuePair<uint, ulong> cacheDomain in cacheDomains)
            {
                ulong valueMask = cacheDomain.Value & AllMask;
                if (valueMask != 0)
                {
                    coreMasks.Add(valueMask);
                }
            }
            return coreMasks.ToArray();
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
            List<int> logicals = new List<int>(rows.Count);
            List<byte> efficiencies = new List<byte>(rows.Count);
            List<byte> schedulings = new List<byte>(rows.Count);
            foreach (CpuSetRec row in rows)
            {
                if (row.Group == 0 && row.Logical < 64)
                {
                    logicals.Add(row.Logical);
                    efficiencies.Add(row.Efficiency);
                    schedulings.Add(row.Scheduling);
                }
            }
            FavoredDetail = DescribeCoreClasses(logicals.ToArray(), efficiencies.ToArray(), schedulings.ToArray());
            return FavoredMaskOf(logicals.ToArray(), efficiencies.ToArray(), schedulings.ToArray());
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
            List<string> classDescriptions = new List<string>();
            int rangeStartIndex = 0;
            for (int j = 1; j <= integerValues.Length; j++)
            {
                if (j == integerValues.Length || efficiency[integerValues[j]] != efficiency[integerValues[rangeStartIndex]] || scheduling[integerValues[j]] != scheduling[integerValues[rangeStartIndex]] || logical[integerValues[j]] != logical[integerValues[j - 1]] + 1)
                {
                    int firstLogicalCpu = logical[integerValues[rangeStartIndex]];
                    int lastLogicalCpu = logical[integerValues[j - 1]];
                    classDescriptions.Add(((firstLogicalCpu == lastLogicalCpu) ? firstLogicalCpu.ToString() : (firstLogicalCpu + "-" + lastLogicalCpu)) + ":" + efficiency[integerValues[rangeStartIndex]] + "/" + scheduling[integerValues[rangeStartIndex]]);
                    rangeStartIndex = j;
                }
            }
            return string.Join(" ", classDescriptions.ToArray());
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
            IntPtr nativeBuffer = Marshal.AllocHGlobal(returnedSnapshot);
            try
            {
                if (!GetSystemCpuSetInformation(nativeBuffer, returnedSnapshot, out returned, IntPtr.Zero, 0u) || returned <= 0 || returned > returnedSnapshot)
                {
                    return;
                }
                byte[] cpuSetBytes = new byte[returned];
                Marshal.Copy(nativeBuffer, cpuSetBytes, 0, returned);
                CpuTopologyEvidence evidence = CpuTopologyEvidence.FromCpuSets(cpuSetBytes);
                MultiGroup |= evidence.MultiGroup;
                if (!evidence.Valid) { CoreTypeDetail = evidence.Reason; return; }
                List<CpuSetRec> cpuSetRecs = new List<CpuSetRec>();
                int recordSize;
                for (long iterationIndex = 0L; iterationIndex + 20 <= returned; iterationIndex += recordSize)
                {
                    IntPtr ptr = (IntPtr)((long)nativeBuffer + iterationIndex);
                    recordSize = Marshal.ReadInt32(ptr, 0);
                    int entryType = Marshal.ReadInt32(ptr, 4);
                    if (recordSize <= 0 || iterationIndex + recordSize > returned)
                    {
                        break;
                    }
                    if (entryType == 0 && recordSize >= 20)
                    {
                        cpuSetRecs.Add(new CpuSetRec
                        {
                            Id = (uint)Marshal.ReadInt32(ptr, 8),
                            Group = Marshal.ReadInt16(ptr, 12),
                            Logical = Marshal.ReadByte(ptr, 14),
                            Core = Marshal.ReadByte(ptr, 15),
                            Efficiency = Marshal.ReadByte(ptr, 18),
                            Scheduling = (byte)((recordSize >= 21) ? Marshal.ReadByte(ptr, 20) : 0)
                        });
                    }
                }
                if (cpuSetRecs.Count == 0)
                {
                    return;
                }
                List<uint> ids = new List<uint>();
                foreach (CpuSetRec cpuSetRec in cpuSetRecs)
                {
                    ids.Add(cpuSetRec.Id);
                }
                allIds = ids.ToArray();
                FavoredMask = ComputeFavoredMask(cpuSetRecs);
                Dictionary<string, CpuSetRec> cpuSetRecsByKey = new Dictionary<string, CpuSetRec>();
                Dictionary<string, ulong> siblingMasksByCore = new Dictionary<string, ulong>();
                foreach (CpuSetRec currentCpuSetRec in cpuSetRecs)
                {
                    string key = currentCpuSetRec.Group + ":" + currentCpuSetRec.Core;
                    CpuSetRec candidateCpuSetRec;
                    if (!cpuSetRecsByKey.TryGetValue(key, out candidateCpuSetRec) || currentCpuSetRec.Logical < candidateCpuSetRec.Logical)
                    {
                        cpuSetRecsByKey[key] = currentCpuSetRec;
                    }
                    if (currentCpuSetRec.Group == 0 && currentCpuSetRec.Logical < 64)
                    {
                        ulong siblingMask;
                        siblingMasksByCore.TryGetValue(key, out siblingMask);
                        siblingMasksByCore[key] = siblingMask | (ulong)(1L << (int)currentCpuSetRec.Logical);
                    }
                }
                foreach (ulong siblingMask in siblingMasksByCore.Values)
                {
                    if (siblingMask != 0)
                    {
                        physicalCoreMasks.Add(siblingMask);
                    }
                }
                ResolveCoreTypes(evidence.Classes);
                // 类别校正后重新计算默认值，再推导 CPU Set 池。
                DeriveMasks();
                if (MultiGroup || AllMaskReconciled || !TopologySourcesAgree) return;
                List<uint> currentIds = new List<uint>();
                List<uint> candidateIds = new List<uint>();
                HashSet<string> seenCoreKeys = new HashSet<string>(StringComparer.Ordinal);
                if (Hybrid)
                {
                    if (CountSetBits(PerfMask) >= 8)
                    {
                        foreach (CpuSetRec row in cpuSetRecsByKey.Values)
                            if (row.Group == 0 && row.Logical < 64 && (EffMask & (1UL << row.Logical)) != 0)
                                seenCoreKeys.Add(row.Group + ":" + row.Core);
                    }
                }
                else if (!AsymCache && !MultiGroup)
                {
                    List<CpuPartitionPolicy.CoreDesc> cores = new List<CpuPartitionPolicy.CoreDesc>();
                    foreach (ulong physicalCoreMask in siblingMasksByCore.Values)
                    {
                        int l = -1;
                        int die = -1;
                        uint l3Size = 0u;
                        for (int i = 0; i < cacheDomains.Count; i++)
                        {
                            if ((cacheDomains[i].Value & physicalCoreMask) != 0)
                            {
                                l = i;
                                l3Size = cacheDomains[i].Key;
                                break;
                            }
                        }
                        for (int j = 0; j < processorDieDomains.Count; j++)
                        {
                            if ((processorDieDomains[j] & physicalCoreMask) != 0)
                            {
                                die = j;
                                break;
                            }
                        }
                        cores.Add(new CpuPartitionPolicy.CoreDesc
                        {
                            Mask = physicalCoreMask,
                            L3 = l,
                            Die = die,
                            Eff = 0,
                            L3Size = l3Size
                        });
                    }
                    CpuPartitionPolicy.CorePlan corePlan = CpuPartitionPolicy.Decide(cores.ToArray(), AllMask);
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
                    List<CpuSetRec> valuesSnapshot = new List<CpuSetRec>(cpuSetRecsByKey.Values);
                    valuesSnapshot.Sort(delegate(CpuSetRec a, CpuSetRec b)
                    {
                        int compareToResult = a.Group.CompareTo(b.Group);
                        return (compareToResult == 0) ? a.Core.CompareTo(b.Core) : compareToResult;
                    });
                    int backgroundCoreCountResult = CpuPartitionPolicy.BackgroundCoreCount(valuesSnapshot.Count);
                    for (int k = Math.Max(0, valuesSnapshot.Count - backgroundCoreCountResult); k < valuesSnapshot.Count; k++)
                    {
                        seenCoreKeys.Add(valuesSnapshot[k].Group + ":" + valuesSnapshot[k].Core);
                    }
                }
                ulong performanceCoreMask = 0uL;
                ulong efficiencyCoreMask = 0uL;
                foreach (CpuSetRec resolvedCpuSetRec in cpuSetRecs)
                {
                    string coreKey = resolvedCpuSetRec.Group + ":" + resolvedCpuSetRec.Core;
                    bool isInHashSet = seenCoreKeys.Contains(coreKey);
                    if (isInHashSet)
                    {
                        currentIds.Add(resolvedCpuSetRec.Id);
                    }
                    else if (seenCoreKeys.Count > 0 && (!Hybrid || (resolvedCpuSetRec.Group == 0 && resolvedCpuSetRec.Logical < 64 && (PerfMask & (1UL << resolvedCpuSetRec.Logical)) != 0)))
                    {
                        candidateIds.Add(resolvedCpuSetRec.Id);
                    }
                    if (resolvedCpuSetRec.Group == 0 && resolvedCpuSetRec.Logical < 64)
                    {
                        if (isInHashSet)
                        {
                            performanceCoreMask |= (ulong)(1L << (int)resolvedCpuSetRec.Logical);
                        }
                        else if (seenCoreKeys.Count > 0 && (!Hybrid || (resolvedCpuSetRec.Group == 0 && resolvedCpuSetRec.Logical < 64 && (PerfMask & (1UL << resolvedCpuSetRec.Logical)) != 0)))
                        {
                            efficiencyCoreMask |= (ulong)(1L << (int)resolvedCpuSetRec.Logical);
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
                else if (currentIds.Count > 0 && candidateIds.Count > 0)
                {
                    backgroundIds = currentIds.ToArray();
                    partitionGameIds = candidateIds.ToArray();
                }
                if (!MultiGroup && !AsymCache && performanceCoreMask != 0 && efficiencyCoreMask != 0)
                {
                    if (PartitionAgreesWithEfficiency(efficiencyCoreMask, performanceCoreMask))
                    {
                        ThrottleMask = performanceCoreMask;
                        StrictBoostMask = efficiencyCoreMask;
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
                Marshal.FreeHGlobal(nativeBuffer);
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
            IntPtr nativeBuffer = Marshal.AllocHGlobal(lengthSnapshot);
            try
            {
                if (!GetLogicalProcessorInformationEx(65535, nativeBuffer, ref length) || length <= 0 || length > lengthSnapshot)
                {
                    return;
                }
                byte[] topologyBytes = new byte[length];
                Marshal.Copy(nativeBuffer, topologyBytes, 0, length);
                CpuTopologyEvidence evidence = CpuTopologyEvidence.FromTopology(topologyBytes);
                MultiGroup |= evidence.MultiGroup;
                if (!evidence.Valid) { CoreTypeDetail = evidence.Reason; return; }
                topologyClasses = evidence.Classes;
                Dictionary<int, ulong> coreMasksByEfficiencyClass = new Dictionary<int, ulong>();
                List<ulong> currentPhysicalCoreMasks = new List<ulong>();
                List<KeyValuePair<uint, ulong>> l3CacheEntries = new List<KeyValuePair<uint, ulong>>();
                List<ulong> numaMasks = new List<ulong>();
                bool ready = false;
                long recordOffset = 0L;
                while (recordOffset + 8 <= length)
                {
                    IntPtr recordPointer = (IntPtr)((long)nativeBuffer + recordOffset);
                    int relationship = Marshal.ReadInt32(recordPointer, 0);
                    int recordSize = Marshal.ReadInt32(recordPointer, 4);
                    if (recordSize <= 0 || recordOffset + recordSize > length)
                    {
                        break;
                    }
                    IntPtr relationshipPointer = (IntPtr)((long)recordPointer + 8);
                    switch (relationship)
                    {
                    case 0:
                    {
                        if (!RecordFits(recordSize, 30, 2))
                        {
                            recordOffset += recordSize;
                            continue;
                        }
                        int efficiencyClass = Marshal.ReadByte(relationshipPointer, 1);
                        int groupCount = Marshal.ReadInt16(relationshipPointer, 22);
                        if (!RecordArrayFits(recordSize, 32, groupCount, 16))
                        {
                            recordOffset += recordSize;
                            continue;
                        }
                        ulong processorMask = 0uL;
                        for (int k = 0; k < groupCount; k++)
                        {
                            IntPtr ptr4 = (IntPtr)((long)relationshipPointer + 24 + k * 16);
                            if (Marshal.ReadInt16(ptr4, 8) != 0)
                            {
                                ready = true;
                                continue;
                            }
                            ulong groupMask = (ulong)Marshal.ReadInt64(ptr4, 0);
                            ulong coreMask;
                            coreMasksByEfficiencyClass.TryGetValue(efficiencyClass, out coreMask);
                            coreMasksByEfficiencyClass[efficiencyClass] = coreMask | groupMask;
                            processorMask |= groupMask;
                        }
                        if (processorMask != 0)
                        {
                            currentPhysicalCoreMasks.Add(processorMask);
                        }
                        break;
                    }
                    case 5:
                    {
                        if (!RecordFits(recordSize, 30, 2))
                        {
                            recordOffset += recordSize;
                            continue;
                        }
                        int cacheGroupCount = Marshal.ReadInt16(relationshipPointer, 22);
                        if (!RecordArrayFits(recordSize, 32, cacheGroupCount, 16))
                        {
                            recordOffset += recordSize;
                            continue;
                        }
                        ulong cacheMask = 0uL;
                        for (int j = 0; j < cacheGroupCount; j++)
                        {
                            IntPtr ptr3 = (IntPtr)((long)relationshipPointer + 24 + j * 16);
                            if (Marshal.ReadInt16(ptr3, 8) != 0)
                            {
                                ready = true;
                            }
                            else
                            {
                                cacheMask |= (ulong)Marshal.ReadInt64(ptr3, 0);
                            }
                        }
                        if (cacheMask != 0)
                        {
                            numaMasks.Add(cacheMask);
                        }
                        break;
                    }
                    case 2:
                    {
                        if (!RecordFits(recordSize, 8, 1))
                        {
                            recordOffset += recordSize;
                            continue;
                        }
                        if (Marshal.ReadByte(relationshipPointer, 0) != 3)
                        {
                            break;
                        }
                        if (!RecordFits(recordSize, 38, 2))
                        {
                            recordOffset += recordSize;
                            continue;
                        }
                        uint key = (uint)Marshal.ReadInt32(relationshipPointer, 4);
                        int numaGroupCount = Marshal.ReadInt16(relationshipPointer, 30);
                        ulong numaMask = 0uL;
                        if (numaGroupCount == 0)
                        {
                            if (!RecordFits(recordSize, 40, 16))
                            {
                                recordOffset += recordSize;
                                continue;
                            }
                            IntPtr ptr = (IntPtr)((long)relationshipPointer + 32);
                            if (Marshal.ReadInt16(ptr, 8) != 0)
                            {
                                ready = true;
                            }
                            else
                            {
                                numaMask = (ulong)Marshal.ReadInt64(ptr, 0);
                            }
                        }
                        else
                        {
                            if (!RecordArrayFits(recordSize, 40, numaGroupCount, 16))
                            {
                                recordOffset += recordSize;
                                continue;
                            }
                            for (int i = 0; i < numaGroupCount; i++)
                            {
                                IntPtr ptr2 = (IntPtr)((long)relationshipPointer + 32 + i * 16);
                                if (Marshal.ReadInt16(ptr2, 8) != 0)
                                {
                                    ready = true;
                                }
                                else
                                {
                                    numaMask |= (ulong)Marshal.ReadInt64(ptr2, 0);
                                }
                            }
                        }
                        if (numaMask != 0)
                        {
                            l3CacheEntries.Add(new KeyValuePair<uint, ulong>(key, numaMask));
                        }
                        break;
                    }
                    }
                    recordOffset += recordSize;
                }
                if (ready)
                {
                    return;
                }
                parsedCoreMasks = currentPhysicalCoreMasks;
                cacheDomains = l3CacheEntries;
                processorDieDomains = numaMasks;
                if (coreMasksByEfficiencyClass.Count >= 2)
                {
                    int minValue = int.MinValue;
                    foreach (KeyValuePair<int, ulong> coreMaskEntry in coreMasksByEfficiencyClass)
                    {
                        if (coreMaskEntry.Key > minValue)
                        {
                            minValue = coreMaskEntry.Key;
                        }
                    }
                    ulong performanceCoreMask = 0uL;
                    ulong efficiencyCoreMask = 0uL;
                    foreach (KeyValuePair<int, ulong> currentCoreMaskEntry in coreMasksByEfficiencyClass)
                    {
                        if (currentCoreMaskEntry.Key == minValue)
                        {
                            performanceCoreMask |= currentCoreMaskEntry.Value;
                        }
                        else
                        {
                            efficiencyCoreMask |= currentCoreMaskEntry.Value;
                        }
                    }
                    if (performanceCoreMask != 0 && efficiencyCoreMask != 0)
                    {
                        PerfMask = performanceCoreMask;
                        EffMask = efficiencyCoreMask;
                        Hybrid = true;
                    }
                }
                if (Hybrid || l3CacheEntries.Count < 2)
                {
                    return;
                }
                uint largestCacheSize = 0u;
                uint maxValue = uint.MaxValue;
                foreach (KeyValuePair<uint, ulong> l3CacheEntryEntry in l3CacheEntries)
                {
                    if (l3CacheEntryEntry.Key > largestCacheSize)
                    {
                        largestCacheSize = l3CacheEntryEntry.Key;
                    }
                    if (l3CacheEntryEntry.Key < maxValue)
                    {
                        maxValue = l3CacheEntryEntry.Key;
                    }
                }
                if (largestCacheSize <= maxValue)
                {
                    return;
                }
                ulong performanceL3Mask = 0uL;
                ulong efficiencyL3Mask = 0uL;
                foreach (KeyValuePair<uint, ulong> currentL3CacheEntryEntry in l3CacheEntries)
                {
                    if (currentL3CacheEntryEntry.Key == largestCacheSize)
                    {
                        performanceL3Mask |= currentL3CacheEntryEntry.Value;
                    }
                    else
                    {
                        efficiencyL3Mask |= currentL3CacheEntryEntry.Value;
                    }
                }
                if (performanceL3Mask != 0 && efficiencyL3Mask != 0)
                {
                    BigL3Mask = performanceL3Mask;
                    SmallL3Mask = efficiencyL3Mask;
                    AsymCache = true;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuffer);
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
            // Windows 的单一类别不能排除关闭 SMT 的混合 CPU；备用探测只操作自身临时线程，不重新分配游戏。

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
            // 数量不能确定处理器编号；即使禁用的 CPU 造成编号空洞，也保留已核验的活动掩码。

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
            IntPtr nativeBuffer = Marshal.AllocHGlobal(returnedSnapshot);
            try
            {
                if (!GetSystemCpuSetInformation(nativeBuffer, returnedSnapshot, out returned, IntPtr.Zero, 0u) || returned <= 0 || returned > returnedSnapshot)
                {
                    return null;
                }
                List<uint> unsignedValues = new List<uint>();
                int recordSize;
                for (long iterationIndex = 0L; iterationIndex + 8 <= returned; iterationIndex += recordSize)
                {
                    IntPtr ptr = (IntPtr)((long)nativeBuffer + iterationIndex);
                    recordSize = Marshal.ReadInt32(ptr, 0);
                    int entryType = Marshal.ReadInt32(ptr, 4);
                    if (recordSize <= 0 || iterationIndex + recordSize > returned)
                    {
                        break;
                    }
                    if (entryType == 0 && recordSize >= 16)
                    {
                        uint cpuSetId = (uint)Marshal.ReadInt32(ptr, 8);
                        short processorGroup = Marshal.ReadInt16(ptr, 12);
                        byte b = Marshal.ReadByte(ptr, 14);
                        if (processorGroup == 0 && b < 64 && ((mask >> (int)b) & 1) != 0)
                        {
                            unsignedValues.Add(cpuSetId);
                        }
                    }
                }
                return (unsignedValues.Count > 0) ? unsignedValues.ToArray() : null;
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuffer);
            }
        }

        public static ulong[] DieMasks()
        {
            ulong[] domainMasksResult = DomainMasks(processorDieDomains);
            if (domainMasksResult.Length >= 2)
            {
                return domainMasksResult;
            }
            List<ulong> cacheMasks = new List<ulong>();
            foreach (KeyValuePair<uint, ulong> cacheDomain in cacheDomains)
            {
                cacheMasks.Add(cacheDomain.Value);
            }
            return DomainMasks(cacheMasks);
        }

        private static ulong[] DomainMasks(List<ulong> domains)
        {
            List<ulong> domainMasks = new List<ulong>();
            foreach (ulong domain in domains)
            {
                ulong domainMask = domain & AllMask;
                if (domainMask != 0 && !domainMasks.Contains(domainMask))
                {
                    domainMasks.Add(domainMask);
                }
            }
            if (domainMasks.Count < 2)
            {
                return new ulong[0];
            }
            domainMasks.Sort();
            return domainMasks.ToArray();
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
            List<string> texts = new List<string>();
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
                texts.Add((i > iSnapshot + 1) ? (iSnapshot + "-" + i) : ((i == iSnapshot + 1) ? (iSnapshot + "," + i) : iSnapshot.ToString()));
                i++;
            }
            return CountSetBits(mask) + Lang.T("t.cputopology.2") + string.Join(",", texts.ToArray());
        }
    }
}
