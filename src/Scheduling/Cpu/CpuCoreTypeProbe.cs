using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // 物理核及同核线程关系来自 Windows 探测只报告实际采样逻辑处理器的类型
    internal static class CpuCoreTypeProbe
    {
        internal static int[] Read(ulong allMask)
        {
            string reason;
            return Read(allMask, out reason);
        }

        internal static int[] Read(ulong allMask, out string reason)
        {
            return ReadInternal(allMask, null, out reason);
        }

#if PAVISE_CORE_TYPE_TESTS
        internal static int[] ReadForTest(ulong allMask, Func<uint, uint, uint[]> query, out string reason)
        {
            if (query == null) throw new ArgumentNullException("query");
            return ReadInternal(allMask, query, out reason);
        }
#endif

        private static int[] ReadInternal(ulong allMask, Func<uint, uint, uint[]> query, out string reason)
        {
            int[] result = Unknown();
            string outcome = "cpuid-unavailable";
            if (IntPtr.Size != 8 || allMask == 0)
            {
                reason = "cpuid-unsupported-scope";
                return result;
            }
            try
            {
                // 现有调度模型只管理处理器组 0
                SystemInfo system;
                GetNativeSystemInfo(out system);
                if (system.Architecture != 9)
                {
                    reason = "cpuid-architecture-unsupported";
                    return result;
                }
                if (GetActiveProcessorGroupCount() != 1)
                {
                    reason = "cpuid-multiple-groups";
                    return result;
                }
                Thread worker = new Thread(delegate()
                {
                    try { result = ReadWorker(allMask, query, out outcome); }
                    catch (Exception) { result = Unknown(); outcome = "cpuid-probe-failed"; }
                });
                worker.IsBackground = true;
                worker.Name = "Pavise CPU type inspection";
                worker.Start();
                // 返回前等待有界工作线程结束 确保亲和性恢复不会超出其生命周期
                while (true)
                {
                    try { worker.Join(); break; }
                    catch (ThreadInterruptedException) { /* 返回前完成本方工作线程的清理 */ }
                }
            }
            catch (Exception) { result = Unknown(); outcome = "cpuid-worker-failed"; }
            reason = outcome;
            return result;
        }

        private static int[] ReadWorker(ulong allMask, Func<uint, uint, uint[]> query, out string reason)
        {
            reason = "cpuid-unavailable";
            int[] result = Unknown();
            IntPtr thread = new IntPtr(-2);
            GroupAffinity original;
            if (!GetThreadGroupAffinity(thread, out original) || original.Group != 0
                || (allMask & ~original.Mask.ToUInt64()) != 0)
            {
                reason = "cpuid-affinity-scope-unavailable";
                return result;
            }
            bool changed = false, complete = false, restored = true;
            Stopwatch budget = Stopwatch.StartNew();
            CpuIdReader reader = null;
            if (query == null)
            {
                // 原生导出取不到时结果记为未知 不用别的办法执行 CPUID
                try { reader = new CpuIdReader(); }
                catch (InvalidOperationException) { reason = "cpuid-native-unavailable"; return result; }
                query = reader.Read;
            }
            using (reader)
            {
                uint[] identity = query(0, 0);
                if (!IntelVendor(identity)) { reason = "cpuid-vendor-unsupported"; return result; }
                if (identity[0] < 0x1a) { reason = "cpuid-leaf-unavailable"; return result; }
                try
                {
                    for (int logical = 0; logical < 64; logical++)
                    {
                        if ((allMask & (1UL << logical)) == 0) continue;
                        if (budget.ElapsedMilliseconds > 2000)
                        { reason = "cpuid-budget-exceeded"; return Unknown(); }
                        GroupAffinity target = new GroupAffinity { Mask = new UIntPtr(1UL << logical), Group = 0 };
                        GroupAffinity previous;
                        if (!SetThreadGroupAffinity(thread, ref target, out previous))
                        { reason = "cpuid-affinity-failed"; return Unknown(); }
                        changed = true;
                        ProcessorNumber before, after;
                        GetCurrentProcessorNumberEx(out before);
                        // 在已绑定的处理器上也核对 CPUID 叶是否可用
                        identity = query(0, 0);
                        uint[] type = identity[0] >= 0x1a && IntelVendor(identity) ? query(0x1a, 0) : null;
                        GetCurrentProcessorNumberEx(out after);
                        if (before.Group != 0 || after.Group != 0 || before.Number != logical || after.Number != logical)
                        { reason = "cpuid-processor-mismatch"; return Unknown(); }
                        int value = type == null ? -1 : (int)(type[0] >> 24);
                        // 核型标识不表示性能排名
                        if (value != 0x20 && value != 0x40)
                        { reason = "cpuid-core-type-unavailable"; return Unknown(); }
                        result[logical] = value;
                    }
                    complete = true;
                }
                finally
                {
                    if (changed)
                    {
                        GroupAffinity ignored, observed;
                        restored = SetThreadGroupAffinity(thread, ref original, out ignored)
                            && GetThreadGroupAffinity(thread, out observed)
                            && observed.Group == original.Group && observed.Mask.Equals(original.Mask);
                        if (!restored) { reason = "cpuid-affinity-restore-failed"; }
                    }
                }
            }
            if (!complete || !restored) return Unknown();
            reason = "cpuid-1a";
            return result;
        }

        private static bool IntelVendor(uint[] registers)
        {
            return registers[1] == 0x756e6547 && registers[3] == 0x49656e69 && registers[2] == 0x6c65746e;
        }

        private static int[] Unknown()
        {
            int[] result = new int[64];
            for (int i = 0; i < result.Length; i++) result[i] = -1;
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity
        {
            internal UIntPtr Mask;
            internal ushort Group;
            internal ushort Reserved0, Reserved1, Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorNumber
        {
            internal ushort Group;
            internal byte Number, Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemInfo
        {
            internal ushort Architecture, Reserved;
            internal uint PageSize;
            internal IntPtr MinimumAddress, MaximumAddress;
            internal UIntPtr ActiveProcessorMask;
            internal uint ProcessorCount, ProcessorType, AllocationGranularity;
            internal ushort ProcessorLevel, ProcessorRevision;
        }

        // CPUID 指令由原生组件执行 见 NativeCpu 和 src/Native/PaviseCpu.cpp
        private sealed class CpuIdReader : IDisposable
        {
            private NativeCpu.CpuidFunction invoke;

            internal CpuIdReader()
            {
                invoke = NativeCpu.Cpuid;
                if (invoke == null) throw new InvalidOperationException("CPUID native export unavailable: " + NativeCpu.Status);
            }

            internal uint[] Read(uint leaf, uint subleaf)
            {
                uint[] registers = new uint[4];
                invoke(leaf, subleaf, registers);
                return registers;
            }

            public void Dispose() { invoke = null; }
        }

        [DllImport("kernel32.dll")] private static extern void GetNativeSystemInfo(out SystemInfo system);
        [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);
        [DllImport("kernel32.dll")] private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber processor);
    }
}
