using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace PaviseApp
{
    internal static class ManualMemoryGameChecks
    {
        private static int checks;
        private static void Check(bool ok, string why) { checks++; if (!ok) throw new InvalidOperationException(why); }
        private sealed class Memory : IManualMemoryNative
        {
            internal readonly List<ManualMemoryStep> Calls = new List<ManualMemoryStep>();
            internal Action<ManualMemoryStep> OnExecute;
            public bool TryQuery(out ManualMemorySnapshot value)
            { value = new ManualMemorySnapshot { TotalBytes = 16UL << 30, AvailableBytes = 8UL << 30 }; return true; }
            public int Execute(ManualMemoryStep step) { Calls.Add(step); if (OnExecute != null) OnExecute(step); return 0; }
        }
        private sealed class Api : IManualMemoryApi
        {
            internal int Writes, Acquires, Releases;
            internal Action OnAcquire;
            public bool QueryPerformance(ref StandbyPerformanceInformation information, uint size) { throw new InvalidOperationException("No real sampling"); }
            public int QueryLists(out StandbyMemoryListInformation information, out int size) { information = default(StandbyMemoryListInformation); size = 0; return -1; }
            public int AcquirePrivilege(ref uint privilege, out IntPtr state) { Acquires++; state = new IntPtr(1); if (OnAcquire != null) OnAcquire(); return 0; }
            public void ReleasePrivilege(IntPtr state) { Releases++; }
            public int MemoryCommand(int kind, ref int command, int size) { Writes++; return 0; }
            public bool FileCache(UIntPtr min, UIntPtr max, uint flags, out int error) { Writes++; error = 0; return true; }
            public int EmptyInformation(int kind) { Writes++; return 0; }
            public int Combine(int kind, ref ManualMemoryCombineInformation information, int size) { Writes++; return 0; }
        }
        private static void Scopes()
        {
            var memory = new Memory();
            var report = new ManualMemoryCleaner(memory, _ => { }, () => false).Run(() => true, null);
            Check(memory.Calls.SequenceEqual(ManualMemoryCleaner.OrderedSteps) && report.Completed && !report.GameProtected, "outside-game plan changed");
            memory = new Memory();
            report = new ManualMemoryCleaner(memory, _ => { }, () => true).Run(() => true, null);
            Check(memory.Calls.SequenceEqual(new[] { ManualMemoryStep.PaviseMemory }), "game session reached a global operation");
            Check(report.GameProtected && report.Completed && report.ExpectedSteps == 1, "game plan reported six-step or false incomplete result");
            Check(ManualMemoryText.Summary(report).Contains("Pavise"), "game scope was hidden in summary");
            memory = new Memory();
            report = new ManualMemoryCleaner(memory, _ => { }, () => { throw new Exception(); }).Run(() => true, null);
            Check(report.GameProtected && memory.Calls.All(x => x == ManualMemoryStep.PaviseMemory), "unknown game state allowed global cleanup");
        }
        private static void Transitions()
        {
            bool game = false; var memory = new Memory();
            var report = new ManualMemoryCleaner(memory, _ => { }, () => game).Run(() => true, (n, s) => game = true);
            Check(memory.Calls.SequenceEqual(new[] { ManualMemoryStep.PaviseMemory }), "game starting in progress callback was not protected");
            game = false; memory = new Memory(); memory.OnExecute = s => { game = s != ManualMemoryStep.PaviseMemory; };
            report = new ManualMemoryCleaner(memory, _ => { }, () => game).Run(() => true, null);
            Check(memory.Calls.SequenceEqual(new[] { ManualMemoryStep.CombinePages, ManualMemoryStep.PaviseMemory }), "later global steps ran after game entry");
            Check(report.Completed && report.EscalatedToGameProtection, "transition did not retain game protection");
            game = true; memory = new Memory();
            report = new ManualMemoryCleaner(memory, _ => { }, () => game).Run(() => true, (n, s) => game = false);
            Check(memory.Calls.SequenceEqual(new[] { ManualMemoryStep.PaviseMemory }), "switch-out/exit upgraded an in-progress light cleanup");
        }
        private static void NativeBoundary()
        {
            var api = new Api(); var native = new WindowsManualMemoryNative(api, () => true);
            foreach (ManualMemoryStep step in ManualMemoryCleaner.OrderedSteps)
                Check(native.Execute(step) == ManualMemoryCleaner.StatusGameProtected, "global native operation admitted in game: " + step);
            Check(api.Writes == 0 && api.Acquires == 0, "protected native operations requested global privileges or writes");
            Check(native.Execute(ManualMemoryStep.PaviseMemory) == 0 && api.Writes == 0 && api.Acquires == 0, "own-memory collection used a kernel cleanup API");
            foreach (ManualMemoryStep step in ManualMemoryCleaner.OrderedSteps)
            {
                bool game = false; api = new Api { OnAcquire = () => game = true };
                native = new WindowsManualMemoryNative(api, () => game);
                Check(native.Execute(step) == ManualMemoryCleaner.StatusGameProtected && api.Writes == 0 && api.Releases == 1,
                    "game entering during privilege acquisition reached native write: " + step);
            }
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(GameMode).GetField("sync", flags).SetValue(mode, new object());
            typeof(GameMode).GetField("active", flags).SetValue(mode, true);
            typeof(GameMode).GetField("smartSwitchedOut", flags).SetValue(mode, true);
            Check(mode.MemoryCleanupGamePresent(), "Alt-Tab dropped session protection");
        }
        private static int Main()
        {
            try { Logger.WritesEnabled = false; Settings.UseTransientStoreForCurrentProcess(); Lang.Cur = 0;
                Scopes(); Transitions(); NativeBoundary();
                Check(StandbyMemoryControl.ManualCleanupActive == 0, "cleanup gate leaked");
                Console.WriteLine("PASS manual memory game protection assertions=" + checks + "; kernel writes=0; game launch=none"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Memory checks cannot start Pavise"); }
    }
}
