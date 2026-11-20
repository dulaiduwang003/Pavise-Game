using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class HagsTweak
    {
        private struct AdapterInfo
        {
            public uint HAdapter;

            public uint LuidLow;

            public int LuidHigh;

            public uint NumOfSources;

            public int PresentMoveRegionsPreferred;
        }

        private struct EnumAdapters
        {
            public uint NumAdapters;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public AdapterInfo[] Adapters;
        }

        private struct CloseAdapter
        {
            public uint HAdapter;
        }

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTEnumAdapters(ref EnumAdapters args);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTCloseAdapter(ref CloseAdapter args);

#if PAVISE_SELFTEST
        internal delegate bool StateQuery(out bool supported, out bool enabled);
        // 自检构建不枚举真实显卡查硬件调度 交给替身 没注入就当场拒绝
        internal static StateQuery QueryStateForTest = null;
#endif

        public static bool TryQueryState(out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
#if PAVISE_SELFTEST
            if (QueryStateForTest == null) throw new System.InvalidOperationException("Unmocked hardware scheduling query");
            return QueryStateForTest(out supported, out enabled);
#elif PAVISE_PERFLAB
            return false;
#endif
            EnumAdapters enumAdapters = default(EnumAdapters);
            enumAdapters.Adapters = new AdapterInfo[16];
            EnumAdapters args = enumAdapters;
            List<HardwareSchedulingObservation> list = new List<HardwareSchedulingObservation>();
            HashSet<uint> hashSet = new HashSet<uint>();
            bool inventoryComplete = false;
            try
            {
                if (D3DKMTEnumAdapters(ref args) != 0 || args.NumAdapters == 0 || args.NumAdapters > 16)
                {
                    return false;
                }
                inventoryComplete = true;
                for (int i = 0; i < args.NumAdapters && i < 16; i++)
                {
                    AdapterInfo adapterInfo = args.Adapters[i];
                    if (adapterInfo.HAdapter == 0 || !hashSet.Add(adapterInfo.HAdapter))
                    {
                        inventoryComplete = false;
                        continue;
                    }
                    uint luidHigh = (uint)adapterInfo.LuidHigh;
                    string verifiedRendererLuid = luidHigh.ToString("x8", CultureInfo.InvariantCulture) + adapterInfo.LuidLow.ToString("x8", CultureInfo.InvariantCulture);
                    list.Add(HardwareSchedulingProbe.Query(verifiedRendererLuid));
                }
            }
            catch
            {
                inventoryComplete = false;
            }
            finally
            {
                HashSet<uint> hashSet2 = new HashSet<uint>();
                for (int j = 0; j < args.NumAdapters && j < 16; j++)
                {
                    uint hAdapter = args.Adapters[j].HAdapter;
                    if (hAdapter == 0)
                    {
                        inventoryComplete = false;
                        continue;
                    }
                    if (!hashSet2.Add(hAdapter))
                    {
                        inventoryComplete = false;
                        continue;
                    }
                    try
                    {
                        CloseAdapter closeAdapter = default(CloseAdapter);
                        closeAdapter.HAdapter = hAdapter;
                        CloseAdapter args2 = closeAdapter;
                        if (D3DKMTCloseAdapter(ref args2) != 0)
                        {
                            inventoryComplete = false;
                        }
                    }
                    catch
                    {
                        inventoryComplete = false;
                    }
                }
            }
            return AggregateCurrentState(list, inventoryComplete, out supported, out enabled);
        }

        internal static bool AggregateCurrentState(IEnumerable<HardwareSchedulingObservation> observations, bool inventoryComplete, out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            bool inventoryCompleteSnapshot = inventoryComplete;
            int processedCount = 0;
            if (observations == null)
            {
                return false;
            }
            foreach (HardwareSchedulingObservation observation in observations)
            {
                processedCount++;
                if (observation == null || !observation.IsKnown)
                {
                    inventoryCompleteSnapshot = false;
                    continue;
                }
                supported |= observation.SupportedByDriver == true;
                enabled |= observation.State == HardwareSchedulingState.Enabled;
            }
            if (inventoryCompleteSnapshot)
            {
                return processedCount > 0;
            }
            return false;
        }
    }
}
