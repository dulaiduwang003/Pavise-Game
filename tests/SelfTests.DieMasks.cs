// 文件用途 CCD 分组来源回归 纯判定 只注入拓扑快照 不读真实硬件
//   界面的 CCD 分带与快捷键都取 CpuTopology.DieMasks()
//   Windows 基本不报 RelationProcessorDie 实测两台机器都是 0 条：
//     Win10 19045 / i9-13900K   cores=24 dies=0 L3groups=1
//     Win11 26200 / Ryzen 9 8940HX cores=16 dies=0 L3groups=2（两块 32MiB）
//   所以 die 不足两块时要回落到 L3 分组 否则 AMD 双 CCD 上一个分带都画不出来
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunDieMaskRegressionTests()
        {
            Action[] tests =
            {
                DieMasksFallBackToL3WhenWindowsReportsNoDie,
                DieMasksPreferRealDiesWhenReported,
                DieMasksStayEmptyOnSingleL3,
                DieMasksAreSortedAndDeduplicated
            };
            CpuTopology.TopologySnapshot saved = CpuTopology.CaptureTopologyForTest();
            try
            {
                foreach (Action test in tests)
                {
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
            }
            finally { CpuTopology.RestoreTopologyForTest(saved); }
            return tests.Length;
        }

        private static KeyValuePair<uint, ulong>[] L3(params ulong[] masks)
        {
            var list = new List<KeyValuePair<uint, ulong>>();
            foreach (ulong m in masks) list.Add(new KeyValuePair<uint, ulong>(32u * 1024 * 1024, m));
            return list.ToArray();
        }

        private static ulong[] SixteenCores(ulong all)
        {
            var cores = new List<ulong>();
            for (int i = 0; i < 32; i += 2) cores.Add((3UL << i) & all);
            return cores.ToArray();
        }

        // 8940HX 的真实形状 32 个逻辑核 两块等容量 L3 各占一半 没有 die 记录
        private static void DieMasksFallBackToL3WhenWindowsReportsNoDie()
        {
            const ulong all = 0xFFFFFFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all), new ulong[0],
                0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0x0000FFFFUL, 0xFFFF0000UL));
            ulong[] dies = CpuTopology.DieMasks();
            Eq(2, dies.Length);
            Eq(0x0000FFFFUL, dies[0]);
            Eq(0xFFFF0000UL, dies[1]);
        }

        // 真报了 die 就用 die 别被 L3 顶掉 多 die 且每 die 多块 L3 的机器按 die 分带才对
        private static void DieMasksPreferRealDiesWhenReported()
        {
            const ulong all = 0xFFFFFFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all),
                new[] { 0x0000FFFFUL, 0xFFFF0000UL }, 0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0x000000FFUL, 0x0000FF00UL,
                0x00FF0000UL, 0xFF000000UL));
            ulong[] dies = CpuTopology.DieMasks();
            Eq(2, dies.Length);
            Eq(0x0000FFFFUL, dies[0]);
        }

        // Intel 消费级整颗共享一块 L3 回落之后仍是空 不能凭空多出分带
        private static void DieMasksStayEmptyOnSingleL3()
        {
            const ulong all = 0xFFFFFFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all), new ulong[0],
                0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0xFFFFFFFFUL));
            Eq(0, CpuTopology.DieMasks().Length);
            // 一条都没有的机器同样是空
            CpuTopology.InjectCacheDomainsForTest(L3());
            Eq(0, CpuTopology.DieMasks().Length);
        }

        // 枚举顺序不保证稳定 CCD 0 必须永远是编号最小的那组 否则按钮编号会跨次启动对调
        //   掩码还要先与 AllMask 求交 越界位和重复项都不能进
        private static void DieMasksAreSortedAndDeduplicated()
        {
            const ulong all = 0x0000FFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all), new ulong[0],
                0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0xFF00UL, 0x00FFUL, 0xFF00UL,
                0xFFFF0000UL));
            ulong[] dies = CpuTopology.DieMasks();
            // 高位那组整个落在 AllMask 之外 求交后为零被丢掉 重复的 0xFF00 只留一份
            Eq(2, dies.Length);
            Eq(0x00FFUL, dies[0]);
            Eq(0xFF00UL, dies[1]);
        }
    }
}
#endif
