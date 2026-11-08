using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // Physical/sibling topology comes from Windows; probes report only the sampled logical CPU's type.
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
                // The existing scheduling model only addresses group 0.
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
                // Join the bounded worker before returning so executable memory and affinity restoration cannot outlive it.
                while (true)
                {
                    try { worker.Join(); break; }
                    catch (ThreadInterruptedException) { /* Finish owned worker cleanup before returning. */ }
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
            using (CpuIdReader reader = query == null ? new CpuIdReader() : null)
            {
                if (query == null) query = reader.Read;
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
                        // Query leaf availability on the pinned processor as well.
                        identity = query(0, 0);
                        uint[] type = identity[0] >= 0x1a && IntelVendor(identity) ? query(0x1a, 0) : null;
                        GetCurrentProcessorNumberEx(out after);
                        if (before.Group != 0 || after.Group != 0 || before.Number != logical || after.Number != logical)
                        { reason = "cpuid-processor-mismatch"; return Unknown(); }
                        int value = type == null ? -1 : (int)(type[0] >> 24);
                        // These are identifiers, not a numeric performance ranking.
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

        private sealed class CpuIdReader : IDisposable
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void ReadRegisters(uint leaf, uint subleaf, IntPtr output);
            private IntPtr code, output;
            private ReadRegisters invoke;

            internal CpuIdReader()
            {
                // Windows x64 ABI: RCX=leaf, RDX=subleaf, R8=output; CPUID clobbers
                // EAX/EBX/ECX/EDX. Preserve nonvolatile RBX; R8 keeps the output pointer.
                byte[] bytes = { 0x53, 0x8b, 0xc1, 0x8b, 0xca, 0x0f, 0xa2,
                    0x41, 0x89, 0x00, 0x41, 0x89, 0x58, 0x04,
                    0x41, 0x89, 0x48, 0x08, 0x41, 0x89, 0x50, 0x0c, 0x5b, 0xc3 };
                try
                {
                    code = VirtualAlloc(IntPtr.Zero, new UIntPtr((uint)bytes.Length), 0x3000, 0x04);
                    if (code == IntPtr.Zero) throw new InvalidOperationException("CPUID memory unavailable");
                    Marshal.Copy(bytes, 0, code, bytes.Length);
                    uint oldProtection;
                    if (!VirtualProtect(code, new UIntPtr((uint)bytes.Length), 0x20, out oldProtection)
                        || !FlushInstructionCache(new IntPtr(-1), code, new UIntPtr((uint)bytes.Length)))
                        throw new InvalidOperationException("CPUID code unavailable");
                    output = Marshal.AllocHGlobal(16);
                    invoke = (ReadRegisters)Marshal.GetDelegateForFunctionPointer(code, typeof(ReadRegisters));
                }
                catch { Dispose(); throw; }
            }

            internal uint[] Read(uint leaf, uint subleaf)
            {
                invoke(leaf, subleaf, output);
                uint[] registers = new uint[4];
                for (int i = 0; i < registers.Length; i++) registers[i] = unchecked((uint)Marshal.ReadInt32(output, i * 4));
                return registers;
            }

            public void Dispose()
            {
                invoke = null;
                if (output != IntPtr.Zero) { Marshal.FreeHGlobal(output); output = IntPtr.Zero; }
                if (code != IntPtr.Zero) { VirtualFree(code, UIntPtr.Zero, 0x8000); code = IntPtr.Zero; }
            }
        }

        [DllImport("kernel32.dll")] private static extern void GetNativeSystemInfo(out SystemInfo system);
        [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);
        [DllImport("kernel32.dll")] private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber processor);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint oldProtection);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
    }
}
