// @author bdth 2074055628@qq.com
// 文件用途 拓扑快照测试钩子 掩码描述与自定义核心选择
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class CpuTopology
    {
#if PAVISE_SELFTEST
        internal sealed class TopologySnapshot
        {
            public ulong All, Perf, Eff, LowPower, BigL3, SmallL3;
            public bool Hybrid, Asym;
            public ulong[] Cores, Dies, Parsed;
            public bool Reconciled;
            public KeyValuePair<uint, ulong>[] L3;
        }

        internal static TopologySnapshot CaptureTopologyForTest()
        {
            return new TopologySnapshot
            {
                All = AllMask, Perf = PerfMask, Eff = EffMask, LowPower = LowPowerEffMask,
                BigL3 = BigL3Mask, SmallL3 = SmallL3Mask,
                Hybrid = Hybrid, Asym = AsymCache,
                Cores = physicalCoreMasks.ToArray(),
                Parsed = ParsedCoreMasksForTest(),
                Reconciled = AllMaskReconciled,
                Dies = processorDieDomains.ToArray(),
                L3 = cacheDomains.ToArray(),
            };
        }

        internal static void RestoreTopologyForTest(TopologySnapshot s)
        {
            if (s == null) return;
            InjectTopologyForTest(s.All, s.Cores, s.Dies,
                s.Perf, s.Eff, s.BigL3, s.SmallL3, s.Hybrid, s.Asym);
            InjectCacheDomainsForTest(s.L3);
            LowPowerEffMask = s.LowPower;
            SetParsedCoreMasksForTest(s.Parsed);
            AllMaskReconciled = s.Reconciled;
        }

        internal static void InjectTopologyForTest(ulong all, ulong[] cores, ulong[] dies,
            ulong perf, ulong eff, ulong bigL3, ulong smallL3, bool hybrid, bool asym)
        {
            AllMask = all;
            physicalCoreMasks.Clear();
            if (cores != null) physicalCoreMasks.AddRange(cores);
            // 物理核有两套来源 注入只改一路会让一致性校验假阳性 台架截图上会挂告警
            SetParsedCoreMasksForTest(cores);
            AllMaskReconciled = false;
            processorDieDomains = new List<ulong>(dies ?? new ulong[0]);
            PerfMask = perf; EffMask = eff; LowPowerEffMask = 0;
            BigL3Mask = bigL3; SmallL3Mask = smallL3;
            Hybrid = hybrid; AsymCache = asym;
            customSet = null;
            squeezeCache = null;
        }

        // L3 分组不进 InjectTopologyForTest 的参数表 那个签名有十三处调用点
        //   注入过的必须由 RestoreTopologyForTest 还原 否则会漏到别的套件
        internal static void InjectCacheDomainsForTest(KeyValuePair<uint, ulong>[] domains)
        {
            cacheDomains = new List<KeyValuePair<uint, ulong>>(
                domains ?? new KeyValuePair<uint, ulong>[0]);
        }
#endif

        // Windows 基本不报 RelationProcessorDie 实测 Win10 19045 的 13900K 与
        //   Win11 26200 的 Ryzen 9 8940HX 都是 0 条 die 记录
        //   AMD 的 CCD 体现为每块 CCD 一组 L3 所以 die 不足两块时回落到 L3 分组
        //   Intel 消费级整颗共享一块 L3 回落后仍是 0 组 不会凭空多出分带和快捷键
        //   后台落点不走这里 它另有一路直接读 cacheDomains 的 L3 下标 这里只服务界面
        public static ulong[] DieMasks()
        {
            ulong[] fromDies = DomainMasks(processorDieDomains);
            if (fromDies.Length >= 2) return fromDies;
            var l3 = new List<ulong>();
            foreach (KeyValuePair<uint, ulong> kv in cacheDomains) l3.Add(kv.Value);
            return DomainMasks(l3);
        }

        // 掩码互不相交 直接按值升序就是按最低位升序 CCD 0 永远是编号最小的那组
        //   枚举顺序不保证稳定 不排序的话按钮编号可能跨次启动对调
        private static ulong[] DomainMasks(List<ulong> domains)
        {
            var list = new List<ulong>();
            foreach (ulong d in domains)
            {
                ulong m = d & AllMask;
                if (m != 0 && !list.Contains(m)) list.Add(m);
            }
            if (list.Count < 2) return new ulong[0];
            list.Sort();
            return list.ToArray();
        }

        public static ulong CacheHeavyMask()
        {
            return AsymCache ? BigL3Mask & AllMask : 0;
        }

        internal static ulong SanitizeCustomMask(ulong wanted, ulong all)
        {
            ulong m = wanted & all;
            return CountSetBits(m) >= MinCustomCores ? m : 0;
        }

        internal static int CountSetBits(ulong v)
        {
            int n = 0;
            while (v != 0) { n += (int)(v & 1UL); v >>= 1; }
            return n;
        }

        public static string DescribeMask(ulong mask)
        {
            if (mask == 0) return Lang.T("t.cputopology.1");
            var parts = new List<string>();
            int i = 0;
            while (i < 64)
            {
                if ((mask & (1UL << i)) == 0) { i++; continue; }
                int start = i;
                while (i + 1 < 64 && (mask & (1UL << (i + 1))) != 0) i++;
                parts.Add(i > start + 1 ? start + "-" + i
                    : i == start + 1 ? start + "," + i : start.ToString());
                i++;
            }
            return CountSetBits(mask) + Lang.T("t.cputopology.2") + string.Join(",", parts.ToArray());
        }

        public const int MinCustomBackgroundCores = 2;

        private sealed class CustomCoreSet
        {
            public ulong Mask;
            public uint[] Ids;
            public ulong BackgroundMask;
            public uint[] BackgroundIds;
        }

        private static volatile CustomCoreSet customSet;

        public static ulong CustomMask
        {
            get { CustomCoreSet c = customSet; return c != null ? c.Mask : 0; }
        }

        public static ulong CustomBackgroundMask
        {
            get { CustomCoreSet c = customSet; return c != null ? c.BackgroundMask : 0; }
        }

        internal static ulong BackgroundRemainderFor(ulong game, ulong all)
        {
            ulong rest = all & ~game;
            return CountSetBits(rest) >= MinCustomBackgroundCores ? rest : 0;
        }

        public static bool SetCustomMask(ulong wanted)
        {
            ulong clean = SanitizeCustomMask(wanted, AllMask);
            CustomCoreSet prev = customSet;
            if (clean == (prev != null ? prev.Mask : 0)) return clean != 0;
            if (clean == 0)
            {
                customSet = null;
                squeezeCache = null;
                return false;
            }
            uint[] ids;
            try { ids = CpuSetIdsFor(clean); }
            catch { ids = null; }
            if (ids == null || ids.Length == 0)
            {
                customSet = null;
                squeezeCache = null;
                return false;
            }
            var next = new CustomCoreSet { Mask = clean, Ids = ids };
            ulong rest = BackgroundRemainderFor(clean, AllMask);
            if (rest != 0)
            {
                uint[] bg;
                try { bg = CpuSetIdsFor(rest); }
                catch { bg = null; }
                if (bg != null && bg.Length > 0)
                {
                    next.BackgroundIds = bg;
                    next.BackgroundMask = rest;
                }
            }
            customSet = next;
            squeezeCache = null;
            return true;
        }
    }
}
