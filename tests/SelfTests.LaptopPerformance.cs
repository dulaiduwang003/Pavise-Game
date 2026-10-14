#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private sealed class LaptopDevice : LaptopPerfMode.IBackend, IDisposable
        {
            internal int Vendor = 1, Mode = 2, Writes;
            internal bool Ac = true, ReadOk = true, WriteOk = true, PartialWrite;
            internal Action<int> AfterWrite;
            internal readonly List<string> ReceiptsAtWrite = new List<string>();
            public int DetectVendor() { return Vendor; }
            public bool OnAcPower { get { return Ac; } }
            public bool Read(int vendor, out int mode) { mode = Mode; return ReadOk && vendor == Vendor; }
            public bool Write(int vendor, int mode)
            {
                Writes++;
                ReceiptsAtWrite.Add(Settings.LoadStr(LaptopPerfMode.SnapKey, ""));
                if (vendor != Vendor) return false;
                if (WriteOk || PartialWrite) Mode = mode;
                if (AfterWrite != null) AfterWrite(mode);
                return WriteOk;
            }
            internal LaptopDevice()
            {
                Settings.UseTransientStoreForCurrentProcess();
                LaptopPerfMode.BackendForTest = this;
                LaptopPerfMode.SaveSnapshotForTest = null;
                Settings.BeforeStrictStringReadForTest = null;
            }
            public void Dispose()
            {
                LaptopPerfMode.BackendForTest = null;
                LaptopPerfMode.SaveSnapshotForTest = null;
                Settings.BeforeStrictStringReadForTest = null;
                Settings.UseTransientStoreForCurrentProcess();
            }
        }

        internal static void RunLaptopPerformanceTests()
        {
            foreach (int vendor in new[] { 1, 2 })
            using (var device = new LaptopDevice { Vendor = vendor, Mode = vendor == 1 ? 2 : 0 })
            {
                int original = device.Mode, target = vendor == 1 ? 3 : 1;
                Eq(true, LaptopPerfMode.Activate(delegate { return true; }));
                Eq(target, device.Mode); Eq(1, device.Writes);
                Eq(vendor + "|" + original + "|" + target, device.ReceiptsAtWrite[0]);
                Eq(true, LaptopPerfMode.Restore()); Eq(original, device.Mode);
                Eq(false, LaptopPerfMode.HasResidue); Eq(2, device.Writes);
                Eq(true, LaptopPerfMode.Restore()); Eq(2, device.Writes);
            }
            using (var device = new LaptopDevice { Mode = 3 })
            {
                Eq(true, LaptopPerfMode.Activate(delegate { return true; }));
                Eq(false, LaptopPerfMode.HasResidue); Eq(true, LaptopPerfMode.Restore()); Eq(0, device.Writes);
            }
            using (var device = new LaptopDevice())
            {
                Eq(false, LaptopPerfMode.Activate(delegate { return false; })); Eq(0, device.Writes);
                device.Ac = false;
                Eq(false, LaptopPerfMode.CanActivate);
                Eq(false, LaptopPerfMode.Activate(delegate { return true; })); Eq(0, device.Writes);
                device.Ac = true; device.ReadOk = false;
                Eq(false, LaptopPerfMode.Activate(delegate { return true; })); Eq(0, device.Writes);
                device.ReadOk = true; device.Vendor = 0;
                Eq(false, LaptopPerfMode.CanActivate);
                Eq(false, LaptopPerfMode.Activate(delegate { return true; })); Eq(0, device.Writes);
                device.Vendor = 1; device.Mode = 255;
                Eq(false, LaptopPerfMode.Activate(delegate { return true; })); Eq(0, device.Writes);
            }
            using (var device = new LaptopDevice())
            {
                Settings.BeforeStrictStringReadForTest = delegate(string key)
                { if (key == LaptopPerfMode.SnapKey) throw new IOException("Unreadable receipt"); };
                Eq(false, LaptopPerfMode.Activate(delegate { return true; }));
                Eq(false, LaptopPerfMode.Restore()); Eq(true, LaptopPerfMode.HasResidue); Eq(0, device.Writes);
            }
            using (var device = new LaptopDevice())
            {
                LaptopPerfMode.SaveSnapshotForTest = delegate(string value) { return false; };
                Eq(false, LaptopPerfMode.Activate(delegate { return true; })); Eq(0, device.Writes);
                Eq(false, LaptopPerfMode.HasResidue);
            }
            using (var device = new LaptopDevice())
            {
                int admissions = 0;
                Eq(false, LaptopPerfMode.Activate(delegate { return ++admissions == 1; }));
                Eq(0, device.Writes); Eq(false, LaptopPerfMode.HasResidue);
            }
            using (var device = new LaptopDevice())
            {
                LaptopPerfMode.SaveSnapshotForTest = delegate(string value)
                { device.Ac = false; return Settings.SaveStrDurable(LaptopPerfMode.SnapKey, value); };
                Eq(false, LaptopPerfMode.Activate(delegate { return true; }));
                Eq(0, device.Writes); Eq(false, LaptopPerfMode.HasResidue);
            }
            foreach (int external in new[] { 1, 2, 255 })
            using (var device = new LaptopDevice())
            {
                Eq(true, LaptopPerfMode.Activate(delegate { return true; }));
                device.Mode = external;
                Eq(true, LaptopPerfMode.Restore()); Eq(external, device.Mode); Eq(1, device.Writes);
                Eq(false, LaptopPerfMode.HasResidue);
            }
            using (var device = new LaptopDevice())
            {
                Eq(true, LaptopPerfMode.Activate(delegate { return true; }));
                device.ReadOk = false;
                Eq(false, LaptopPerfMode.Restore()); Eq(true, LaptopPerfMode.HasResidue); Eq(1, device.Writes);
                device.ReadOk = true; device.WriteOk = false;
                Eq(false, LaptopPerfMode.Restore()); Eq(true, LaptopPerfMode.HasResidue);
                device.WriteOk = true;
                LaptopPerfMode.SaveSnapshotForTest = delegate(string value) { return value.Length > 0; };
                Eq(false, LaptopPerfMode.Restore()); Eq(2, device.Mode); Eq(true, LaptopPerfMode.HasResidue);
                int writes = device.Writes;
                LaptopPerfMode.SaveSnapshotForTest = null;
                Eq(true, LaptopPerfMode.Restore()); Eq(writes, device.Writes); Eq(false, LaptopPerfMode.HasResidue);
            }
            using (var device = new LaptopDevice { WriteOk = false, PartialWrite = true })
            {
                Eq(false, LaptopPerfMode.Activate(delegate { return true; }));
                Eq(true, LaptopPerfMode.HasResidue);
                device.WriteOk = true; device.PartialWrite = false;
                Eq(true, LaptopPerfMode.Restore()); Eq(false, LaptopPerfMode.HasResidue); Eq(2, device.Mode);
            }
            using (var device = new LaptopDevice())
            {
                device.AfterWrite = delegate(int written) { if (written == 3) device.ReadOk = false; };
                Eq(false, LaptopPerfMode.Activate(delegate { return true; }));
                Eq(3, device.Mode); Eq(true, LaptopPerfMode.HasResidue); Eq(1, device.Writes);
                device.AfterWrite = null; device.ReadOk = true;
                Eq(true, LaptopPerfMode.Restore()); Eq(2, device.Mode); Eq(false, LaptopPerfMode.HasResidue);
            }
            using (var device = new LaptopDevice { Mode = 3 })
            {
                Settings.SaveStr(LaptopPerfMode.SnapKey, "1|2");
                LaptopPerfMode.HealFromCrash(); Eq(2, device.Mode); Eq(false, LaptopPerfMode.HasResidue);
                Settings.SaveStr(LaptopPerfMode.SnapKey, "broken");
                Eq(false, LaptopPerfMode.Restore()); Eq(true, LaptopPerfMode.HasResidue); Eq(1, device.Writes);
            }
            RunLaptopRuntimeTests();
        }

        private static void RunLaptopRuntimeTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PaviseLaptopTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (var device = new LaptopDevice())
                {
                    Settings.Save(PolicyCatalog.KeyLaptopPerf, true);
                    var mode = new GameMode(dir, new SuppressionCore());
                    Eq(false, mode.LaptopPerformanceEnabled);
                    const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    Action<bool> syncPower = delegate(bool ready)
                    { typeof(GameMode).GetMethod("SyncLaptopPerformance", flags).Invoke(mode, new object[] { ready }); };
                    Action sync = delegate { syncPower(true); };
                    Action<string, bool> set = delegate(string field, bool value)
                    { typeof(GameMode).GetField(field, flags).SetValue(mode, value); };
                    set("enabled", true); set("active", true);
                    foreach (string preset in new[] { "0", "1", "2", "3" })
                    {
                        Settings.SaveStr(PolicyCatalog.KeyPreset, preset); sync(); Eq(0, device.Writes);
                    }
                    Eq(true, mode.TrySetLaptopPerformanceEnabled(true));
                    syncPower(false); Eq(0, device.Writes);
                    sync(); Eq(3, device.Mode);
                    sync(); Eq(1, device.Writes);
                    device.Ac = false; syncPower(false); Eq(2, device.Mode); Eq(false, LaptopPerfMode.HasResidue);
                    device.Ac = true; sync(); Eq(3, device.Mode);
                    Eq(true, mode.TrySetLaptopPerformanceEnabled(false)); sync(); Eq(2, device.Mode);
                    Eq(true, mode.TrySetLaptopPerformanceEnabled(true)); sync(); Eq(3, device.Mode);
                    set("enabled", false); sync(); Eq(2, device.Mode);
                    set("enabled", true); sync(); Eq(3, device.Mode);
                    set("active", false); sync(); Eq(2, device.Mode);
                    set("active", true); sync(); Eq(3, device.Mode);
                    set("stopping", true); sync(); Eq(2, device.Mode);
                    Eq(false, mode.TrySetLaptopPerformanceEnabled(false));
                    set("stopping", false);
                    Settings.SuspendWritesForReset();
                    Eq(false, mode.TrySetLaptopPerformanceEnabled(false)); Eq(true, mode.LaptopPerformanceEnabled);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

#if PAVISE_LAPTOP_TEST_RUNNER
        private static void Eq<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", actual " + actual);
        }
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("The laptop regression runner cannot start the app"); }
        private static int Main()
        {
            Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
            try
            {
                RunLaptopPerformanceTests();
                Console.WriteLine("PASS laptop performance: mocked firmware, transient settings, lifecycle and recovery");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
#endif
    }
}
#endif
