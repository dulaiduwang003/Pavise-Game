#if PAVISE_SELFTEST && PAVISE_OVERNIGHT_FEATURE_BOUNDARY
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace PaviseApp
{
    internal static class OvernightFeatureBoundaryRunner
    {
        private static int suites, failed, assertions;
        private static int Main()
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 20000, Timeout.Infinite))
            {
                Run("CPU coordination remains active without new page queries or writes", NoNewPageWork);
                Run("historical page receipts restore exactly and preserve external changes", HistoricalPageRestore);
                Run("unverifiable historical page receipts remain available for retry", HistoricalPageRetry);
                Run("production PowerShell launcher no longer binds helper CPU Sets", LauncherBoundary);
                Run("existing owned helper scopes still restore without touching Pavise settings", HistoricalHelperRestore);
                Console.WriteLine("RESULT suites=" + suites + " failed=" + failed + " assertions=" + assertions
                    + " settings=transient native_writes=0 runtime_started=false powershell_started=false");
                return failed == 0 ? 0 : 1;
            }
        }
        private static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException(message); }
        private static void Run(string name, Action test)
        {
            suites++;
            try { Settings.UseTransientStoreForCurrentProcess(); test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        private static FrameCoordinationSample Sample()
        {
            var result = new FrameCoordinationSample { Valid = true, Pid = 505, Birth = 999, PresentTid = 101, WorkerTid = 102 };
            result.Threads[101] = 1001; result.Threads[102] = 1002; return result;
        }
        private static void NoNewPageWork()
        {
            Check(!OvernightPerformancePolicy.Enabled, "The stability comparison build did not disable the overnight experiments");
            var platform = new FramePlatform(); FrameCoordination.SetPlatformForTest(platform);
            try
            {
                Check(FrameCoordination.Apply(Sample(), delegate { return true; }), "Existing CPU coordination no longer applies");
                Check(platform.Cpu[101] == 1 && platform.Cpu[102] == 1 && platform.CpuWrites == 2, "Existing CPU priority promotion changed");
                Check(platform.PressureReads == 0 && platform.MemoryReads == 0 && platform.MemoryWrites == 0,
                    "The disabled page experiment still queried pressure or thread memory");
                Check(platform.Memory[101] == 1 && platform.Memory[102] == 4 && !FrameMemoryProtection.HasResidue,
                    "New page priority changes or receipts were created");
                Check(FrameMemoryProtection.Healthy() && platform.MemoryReads == 0, "An empty page receipt still polled threads");
                Check(FrameCoordination.Restore(), "Existing CPU coordination no longer restores");
                Check(platform.Cpu[101] == 0 && platform.Cpu[102] == -2 && platform.CpuWrites == 4, "Original CPU priorities were not restored");
                Check(platform.MemoryReads == 0 && platform.MemoryWrites == 0, "CPU restoration introduced new page work");
            }
            finally { FrameCoordination.Restore(); FrameCoordination.SetPlatformForTest(null); }
        }
        private static void SeedPageReceipt(FramePlatform platform)
        {
            FrameCoordination.SetPlatformForTest(platform);
            platform.Memory[101] = 5; platform.Memory[102] = 5;
            Check(Settings.SaveStr(FrameMemoryProtection.JournalKey, "v1;505|999|101|1001|1;505|999|102|1002|4"), "Historical receipt fixture did not persist");
        }
        private static void HistoricalPageRestore()
        {
            var platform = new FramePlatform(); SeedPageReceipt(platform);
            try
            {
                Check(FrameCoordination.Restore(), "Disabling page application also disabled historical restoration");
                Check(platform.Memory[101] == 1 && platform.Memory[102] == 4 && platform.MemoryWrites == 2, "Historical page priorities were not restored exactly");
                Check(!FrameMemoryProtection.HasResidue && platform.PressureReads == 0, "Historical restoration retained its receipt or queried pressure");
                Check(FrameCoordination.Restore() && platform.MemoryWrites == 2, "Historical restoration is not idempotent");
                SeedPageReceipt(platform); platform.Memory[101] = 3;
                Check(FrameCoordination.Restore(), "An externally changed page priority blocked cleanup");
                Check(platform.Memory[101] == 3 && platform.Memory[102] == 4 && platform.MemoryWrites == 3, "Historical restoration overwrote an external page priority");
            }
            finally { FrameCoordination.Restore(); FrameCoordination.SetPlatformForTest(null); }
        }
        private static void HistoricalPageRetry()
        {
            var platform = new FramePlatform(); SeedPageReceipt(platform); platform.DenyRestore = true;
            string receipt = Settings.LoadStr(FrameMemoryProtection.JournalKey, "");
            try
            {
                Check(!FrameCoordination.Restore(), "Denied historical page restoration was incorrectly reported as successful");
                Check(Settings.LoadStr(FrameMemoryProtection.JournalKey, "") == receipt && FrameMemoryProtection.HasResidue,
                    "A denied restore discarded its original receipt");
                platform.DenyRestore = false;
                Check(FrameCoordination.Restore() && platform.Memory[101] == 1 && platform.Memory[102] == 4,
                    "The retained page receipt could not restore on retry");
                Check(!FrameMemoryProtection.HasResidue, "The confirmed retry retained a stale receipt");
                Settings.SaveStr(FrameMemoryProtection.JournalKey, "corrupt");
                Check(!FrameCoordination.Restore() && Settings.LoadStr(FrameMemoryProtection.JournalKey, "") == "corrupt",
                    "Disabling page application silently deleted an invalid historical receipt");
                Settings.SaveStr(FrameMemoryProtection.JournalKey, "");
            }
            finally { platform.DenyRestore = false; FrameCoordination.Restore(); FrameCoordination.SetPlatformForTest(null); }
        }
        private static void LauncherBoundary()
        {
            MethodInfo method = typeof(PsRunner).GetMethod("RunCore", BindingFlags.Static | BindingFlags.NonPublic);
            Check(method != null, "The real PowerShell execution method was not found");
            int ownHelperCalls = 0, waits = 0;
            foreach (MethodBase call in Calls(method))
            {
                if (call.DeclaringType == typeof(SelfYield) && call.Name == "OwnHelperScope") ownHelperCalls++;
                if (call.DeclaringType == typeof(System.Diagnostics.Process) && call.Name == "WaitForExit"
                    && call.GetParameters().Length == 1) waits++;
            }
            Check(ownHelperCalls == 0, "The production PowerShell path still enters the experimental helper CPU Sets scope");
            Check(waits >= 2, "The original bounded process wait or post-kill wait was removed");
        }
        private static IEnumerable<MethodBase> Calls(MethodInfo method)
        {
            var codes = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public))
            { var code = (OpCode)field.GetValue(null); codes[code.Value] = code; }
            byte[] il = method.GetMethodBody().GetILAsByteArray();
            for (int i = 0; i < il.Length;)
            {
                short value = il[i++] == 0xfe ? (short)(0xfe00 | il[i++]) : (short)il[i - 1];
                OpCode code; if (!codes.TryGetValue(value, out code)) throw new FormatException("Invalid execution IL");
                int count;
                switch (code.OperandType)
                {
                    case OperandType.InlineNone: count = 0; break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: count = 1; break;
                    case OperandType.InlineVar: count = 2; break;
                    case OperandType.InlineI8: case OperandType.InlineR: count = 8; break;
                    case OperandType.InlineSwitch: count = 4 + BitConverter.ToInt32(il, i) * 4; break;
                    default: count = 4; break;
                }
                if (code.OperandType == OperandType.InlineMethod)
                    yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, i));
                i += count;
            }
        }
        private static void HistoricalHelperRestore()
        {
            var child = new YieldChild();
            IDisposable scope = SelfYield.HelperScopeForTest(child, new uint[] { 17, 18 });
            Check(child.Writes == 1 && SelfYield.PendingHelpersForTest == 1, "The historical owned helper fixture was not admitted");
            scope.Dispose();
            Check(child.Ids.Length == 0 && child.Writes == 2 && child.Disposed && SelfYield.PendingHelpersForTest == 0,
                "A live historical helper did not restore its original CPU Sets");
            child = new YieldChild(); scope = SelfYield.HelperScopeForTest(child, new uint[] { 17, 18 });
            child.DenyRestore = true; scope.Dispose();
            Check(!child.Disposed && SelfYield.PendingHelpersForTest == 1, "A denied helper restoration discarded its live stable handle");
            child.DenyRestore = false; scope.Dispose();
            Check(child.Ids.Length == 0 && child.Disposed && SelfYield.PendingHelpersForTest == 0, "A retained helper could not restore on retry");
            child = new YieldChild(); scope = SelfYield.HelperScopeForTest(child, new uint[] { 17, 18 });
            child.Ids = new uint[] { 44 }; scope.Dispose();
            Check(child.Writes == 1 && child.Ids[0] == 44 && child.Disposed, "Helper restoration overwrote an external CPU Sets change");
            Check(SelfYield.PendingHelpersForTest == 0 && !SelfYield.Engaged, "Historical helper fixtures left a scope or engaged native Pavise tuning");
        }
        private sealed class FramePlatform : IFrameThreads, IFrameThreadMemory
        {
            internal readonly Dictionary<int, int> Cpu = new Dictionary<int, int> { { 101, 0 }, { 102, -2 } };
            internal readonly Dictionary<int, int> Memory = new Dictionary<int, int> { { 101, 1 }, { 102, 4 } };
            internal int CpuWrites, PressureReads, MemoryReads, MemoryWrites;
            internal bool DenyRestore;
            public int Read(FrameThreadReceipt id, out int value) { value = Cpu[id.Tid]; return 0; }
            public bool Set(FrameThreadReceipt id, int expected, int desired)
            { if (Cpu[id.Tid] != expected) return false; CpuWrites++; Cpu[id.Tid] = desired; return true; }
            public bool UnderPressure(int pid, long birth) { PressureReads++; return true; }
            public int ReadMemory(FrameThreadReceipt id, out int value) { MemoryReads++; value = Memory[id.Tid]; return 0; }
            public bool SetMemory(FrameThreadReceipt id, int expected, int desired)
            {
                if (Memory[id.Tid] != expected || (DenyRestore && desired < 5)) return false;
                Check(Settings.LoadStr(FrameMemoryProtection.JournalKey, "").Length > 0, "A memory write lacked a durable restore receipt");
                MemoryWrites++; Memory[id.Tid] = desired; return true;
            }
        }
        private sealed class YieldChild : IYieldChild
        {
            internal uint[] Ids = new uint[0];
            internal int Writes; internal bool DenyRestore, Disposed;
            public bool Owned { get { return !Disposed; } }
            public int Read(out uint[] ids) { ids = (uint[])Ids.Clone(); return Disposed ? 1 : 0; }
            public bool Write(uint[] expected, uint[] desired)
            {
                if (!NativeYieldChild.Same(Ids, expected) || (DenyRestore && desired.Length == 0)) return false;
                Writes++; Ids = (uint[])desired.Clone(); return true;
            }
            public void Dispose() { Disposed = true; }
        }
    }
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Boundary checks cannot start runtime"); } }
}
#endif
