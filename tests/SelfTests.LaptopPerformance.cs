#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int laptopChecks;
        private static void LaptopCheck(bool ok, string message)
        { laptopChecks++; if (!ok) throw new InvalidOperationException("Laptop mode: " + message); }

        private sealed class LaptopDevice : LaptopPerfMode.IBackend, IDisposable
        {
            internal int Vendor = 1, Mode = 2, Original = 2, Writes, Reads;
            internal bool ReadOk = true, WriteOk = true, PartialWrite, Ac = true, ThrowRead, ThrowWrite;
            internal Action AfterWrite, BeforeRead;
            internal int Target { get { return Vendor == 1 ? 3 : 1; } }
            public bool Read(int vendor, out int mode)
            {
                Reads++;
                if (BeforeRead != null) BeforeRead();
                if (ThrowRead) throw new InvalidOperationException("read fault");
                mode = Mode;
                return ReadOk && vendor == Vendor;
            }
            public bool Write(int vendor, int mode)
            {
                LaptopCheck(vendor == Vendor && (mode == Original || mode == Target), "write selected an unsupported firmware mode");
                LaptopCheck(!string.IsNullOrEmpty(Settings.LoadStr(LaptopPerfMode.SnapKey, "")), "write preceded recovery evidence");
                Writes++;
                if (ThrowWrite) throw new InvalidOperationException("write fault");
                if (WriteOk || PartialWrite) Mode = mode;
                if (AfterWrite != null) AfterWrite();
                return WriteOk;
            }
            internal LaptopDevice()
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                LaptopPerfMode.ResetForTest(); LaptopPerfMode.BackendForTest = this;
                LaptopPerfMode.OnAcPowerForTest = delegate { return Ac; };
            }
            internal void Seed(string raw)
            { LaptopCheck(Settings.SaveStrDurable(LaptopPerfMode.SnapKey, raw), "fixture receipt failed"); }
            public void Dispose()
            { LaptopPerfMode.ResetForTest(); Settings.UseTransientStoreForCurrentProcess(); }
        }

        internal static void RunLaptopPerformanceTests()
        {
            laptopChecks = 0;
            LaptopCheck(LaptopPerfMode.PreferenceKey == "AutoLaptopPerformanceV2", "new opt-in reuses a retired preference");
            foreach (int vendor in new[] { 1, 2 })
            using (var device = new LaptopDevice { Vendor = vendor, Mode = vendor == 1 ? 2 : 0, Original = vendor == 1 ? 2 : 0 })
            {
                LaptopCheck(LaptopPerfMode.SupportedCached(), "supported standard firmware was rejected");
                int reads = device.Reads;
                for (int i = 0; i < 25; i++) LaptopCheck(LaptopPerfMode.SupportedCached(), "cached support was lost");
                LaptopCheck(device.Reads == reads && device.Writes == 0, "support probe repeatedly read or wrote firmware");
                LaptopCheck(LaptopPerfMode.Activate() && device.Mode == device.Target && device.Writes == 1
                    && LaptopPerfMode.HasResidue && LaptopPerfMode.LastStatus == "applied", "opt-in activation failed");
                for (int i = 0; i < 8; i++) LaptopCheck(LaptopPerfMode.Activate(), "owned target audit failed");
                LaptopCheck(device.Writes == 1, "audit repeatedly rewrote the vendor mode");
                LaptopCheck(LaptopPerfMode.Restore() && device.Mode == device.Original && device.Writes == 2 && !LaptopPerfMode.HasResidue, "owned mode did not restore");
                LaptopCheck(LaptopPerfMode.Restore() && device.Writes == 2, "settled recovery repeated firmware write");
            }

            foreach (int vendor in new[] { 1, 2 })
            using (var device = new LaptopDevice { Vendor = vendor, Mode = vendor == 1 ? 3 : 1, Original = vendor == 1 ? 2 : 0 })
            {
                LaptopCheck(LaptopPerfMode.Activate() && LaptopPerfMode.LastStatus == "already" && device.Writes == 0 && !LaptopPerfMode.HasResidue, "already-active mode was claimed");
                device.Mode = device.Original;
                LaptopCheck(LaptopPerfMode.Activate() && LaptopPerfMode.LastStatus == "external" && device.Writes == 0, "external change after already-active was overridden");
                LaptopCheck(LaptopPerfMode.Activate() && device.Writes == 0, "external choice was retaken in the same session");
                LaptopPerfMode.EndSession();
                LaptopCheck(LaptopPerfMode.Activate() && device.Mode == device.Target && device.Writes == 1, "new session could not opt in again");
                LaptopCheck(LaptopPerfMode.Restore(), "new session could not restore");
            }

            foreach (int external in new[] { 1, 2, 255 })
            using (var device = new LaptopDevice())
            {
                LaptopCheck(LaptopPerfMode.Activate(), "external-change fixture activation failed");
                int writes = device.Writes;
                device.Mode = external;
                LaptopCheck(LaptopPerfMode.Activate() && LaptopPerfMode.LastStatus == "external" && !LaptopPerfMode.HasResidue, "external selection was not yielded");
                int reads = device.Reads;
                for (int i = 0; i < 8; i++) LaptopCheck(LaptopPerfMode.Activate(), "yield audit failed");
                LaptopCheck(device.Mode == external && device.Writes == writes && device.Reads == reads, "yielded session touched firmware again");
                LaptopCheck(LaptopPerfMode.Restore() && device.Writes == writes, "external selection was reverted");
            }

            foreach (int vendor in new[] { 1, 2 }) foreach (bool legacy in new[] { false, true })
            using (var device = new LaptopDevice { Vendor = vendor, Mode = vendor == 1 ? 3 : 1, Original = vendor == 1 ? 2 : 0 })
            {
                device.Seed(vendor + "|" + device.Original + (legacy ? "" : "|" + device.Mode));
                LaptopCheck(!LaptopPerfMode.Activate() && device.Writes == 0 && LaptopPerfMode.HasResidue, "old debt authorized a new apply");
                LaptopCheck(LaptopPerfMode.Restore() && device.Mode == device.Original && device.Writes == 1 && !LaptopPerfMode.HasResidue, "legacy/current receipt did not restore");
            }
            foreach (string raw in new[] { "broken", "0|2|3", "1|255|3", "1|2|1", "2|0|2", "1|2|3|extra" })
            using (var device = new LaptopDevice())
            {
                device.Seed(raw);
                LaptopCheck(!LaptopPerfMode.Activate() && !LaptopPerfMode.Restore() && device.Writes == 0 && LaptopPerfMode.HasResidue, "malformed debt was used or discarded");
            }
            using (var device = new LaptopDevice { Mode = 3 })
            {
                device.Seed("1|3|3");
                LaptopCheck(LaptopPerfMode.Restore() && device.Writes == 0 && !LaptopPerfMode.HasResidue, "already-original legacy receipt rewrote firmware");
            }
            using (var device = new LaptopDevice())
            {
                LaptopCheck(LaptopPerfMode.Restore() && device.Reads == 0 && device.Writes == 0, "empty recovery touched firmware");
                device.Seed("1|2|3"); device.Mode = 3; device.ReadOk = false;
                LaptopCheck(!LaptopPerfMode.Restore() && device.Writes == 0 && LaptopPerfMode.HasResidue, "unreadable firmware lost debt");
                device.ReadOk = true; device.WriteOk = false;
                LaptopCheck(!LaptopPerfMode.Restore() && device.Writes == 1 && LaptopPerfMode.HasResidue, "failed restore discarded debt");
                device.WriteOk = true; LaptopPerfMode.SaveSnapshotForTest = delegate { return false; };
                LaptopCheck(!LaptopPerfMode.Restore() && device.Mode == 2 && LaptopPerfMode.HasResidue, "failed cleanup declared complete");
                int writes = device.Writes; LaptopPerfMode.SaveSnapshotForTest = null;
                LaptopCheck(LaptopPerfMode.Restore() && device.Writes == writes && !LaptopPerfMode.HasResidue, "cleanup retry rewrote firmware");
            }
            using (var device = new LaptopDevice { Mode = 3, WriteOk = false, PartialWrite = true })
            {
                device.Seed("1|2|3");
                LaptopCheck(!LaptopPerfMode.Restore() && LaptopPerfMode.HasResidue && device.Mode == 2, "partial restore was forgotten");
                LaptopCheck(LaptopPerfMode.Restore() && device.Writes == 1 && !LaptopPerfMode.HasResidue, "partial restore was repeated");
            }
            using (var device = new LaptopDevice())
            {
                LaptopPerfMode.ReadSnapshotForTest = delegate { throw new InvalidOperationException("unreadable journal"); };
                LaptopCheck(!LaptopPerfMode.Activate() && !LaptopPerfMode.Restore() && LaptopPerfMode.HasResidue
                    && device.Reads == 0 && device.Writes == 0, "unreadable journal allowed firmware access");
            }
            foreach (bool lieAboutSave in new[] { false, true })
            using (var device = new LaptopDevice())
            {
                LaptopPerfMode.SaveSnapshotForTest = delegate { return lieAboutSave; };
                LaptopCheck(!LaptopPerfMode.Activate() && device.Writes == 0 && device.Mode == 2, "failed/unverified journal authorized firmware write");
            }
            using (var device = new LaptopDevice { Ac = false })
            {
                LaptopCheck(!LaptopPerfMode.Activate() && device.Reads == 0 && device.Writes == 0, "battery activation queried or wrote firmware");
            }
            using (var device = new LaptopDevice())
            {
                int admission = 0;
                LaptopCheck(!LaptopPerfMode.Activate(delegate { return ++admission == 1; }) && device.Writes == 0 && !LaptopPerfMode.HasResidue, "cancel after journal touched firmware or lost settlement");
            }
            using (var device = new LaptopDevice())
            {
                LaptopPerfMode.SaveSnapshotForTest = delegate(string value) { bool ok = Settings.SaveStrDurable(LaptopPerfMode.SnapKey, value); device.Ac = false; return ok; };
                LaptopCheck(!LaptopPerfMode.Activate() && device.Writes == 0 && !LaptopPerfMode.HasResidue, "AC loss after journal applied firmware");
            }
            using (var device = new LaptopDevice())
            {
                device.BeforeRead = delegate { if (device.Reads == 3) device.Mode = 1; };
                LaptopCheck(LaptopPerfMode.Activate() && LaptopPerfMode.LastStatus == "external" && device.Mode == 1
                    && device.Writes == 0 && !LaptopPerfMode.HasResidue, "external change before write was overwritten");
            }
            foreach (bool partial in new[] { false, true })
            using (var device = new LaptopDevice { WriteOk = false, PartialWrite = partial })
            {
                LaptopCheck(!LaptopPerfMode.Activate() && LaptopPerfMode.HasResidue, "failed apply discarded debt");
                int writes = device.Writes;
                LaptopCheck(!LaptopPerfMode.Activate() && device.Writes == writes, "unconfirmed partial apply was promoted or repeated");
                device.WriteOk = true;
                LaptopCheck(LaptopPerfMode.Restore() && device.Mode == 2 && !LaptopPerfMode.HasResidue, "failed apply could not recover");
            }
            using (var device = new LaptopDevice())
            {
                device.AfterWrite = delegate { device.ReadOk = false; };
                LaptopCheck(!LaptopPerfMode.Activate() && LaptopPerfMode.HasResidue, "failed apply readback accepted");
                device.ReadOk = true; device.AfterWrite = null;
                LaptopCheck(LaptopPerfMode.Restore() && !LaptopPerfMode.HasResidue, "unconfirmed apply could not restore");
            }
            using (var device = new LaptopDevice())
            {
                LaptopCheck(LaptopPerfMode.Activate(), "receipt tamper fixture failed");
                device.Seed(""); int writes = device.Writes;
                LaptopCheck(!LaptopPerfMode.Activate() && !LaptopPerfMode.Restore() && device.Writes == writes, "lost current receipt allowed further control");
            }
            using (var device = new LaptopDevice())
            {
                LaptopCheck(LaptopPerfMode.Activate(), "end session fixture failed");
                LaptopPerfMode.EndSession();
                LaptopCheck(!LaptopPerfMode.Activate() && LaptopPerfMode.HasResidue, "new session bypassed old recovery debt");
                LaptopCheck(LaptopPerfMode.Restore() && !LaptopPerfMode.HasResidue, "end session erased recovery debt");
            }
            foreach (int invalid in new[] { -1, 0, 4, 255 })
            using (var device = new LaptopDevice { Mode = invalid })
                LaptopCheck(!LaptopPerfMode.Activate() && device.Writes == 0, "unknown/custom initial mode was taken over");
            using (var device = new LaptopDevice { ThrowRead = true })
                LaptopCheck(!LaptopPerfMode.Activate() && device.Writes == 0, "read exception reached firmware write");
            using (var device = new LaptopDevice { ThrowWrite = true })
                LaptopCheck(!LaptopPerfMode.Activate() && LaptopPerfMode.HasResidue, "write exception discarded journal");
            using (var device = new LaptopDevice { Mode = 3 })
            {
                device.Seed("1|2"); LaptopPerfMode.HealFromCrash();
                LaptopCheck(device.Mode == 2 && device.Writes == 1 && !LaptopPerfMode.HasResidue, "startup legacy restoration failed");
            }
            Console.WriteLine("PASS laptop vendor mode assertions=" + laptopChecks + " firmware=mocked settings=transient application_started=false");
        }
#if PAVISE_LAPTOP_TEST_RUNNER
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Laptop regression runner cannot start the product"); }
        private static int Main()
        {
            Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
            try { RunLaptopPerformanceTests(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
#endif
    }
}
#endif