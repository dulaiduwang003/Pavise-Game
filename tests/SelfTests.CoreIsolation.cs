#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private sealed class IsolationFixture : ICoreIsolationPlatform, ICoreIsolationStore
        {
            internal string Boot = "123456781234123412341234567890ab", Receipt = "";
            internal ulong All = 255, Allocated, Allowed, AllocateDuringGrant;
            internal long Creation = 100;
            internal bool Dead, BlockOpen, ReadFailure, WriteSystemFailure, GrantFailure, PartialSystemWrite, NoAdmissionFlag;
            internal int Writes, SaveCalls, FailSaveAt;
            internal readonly List<string> Operations = new List<string>();
            public string BootIdentity() { return Boot; }
            public IsolationCpuState ReadSystem(IntPtr p)
            {
                return ReadFailure ? null : new IsolationCpuState { All = All, Allocated = Allocated,
                    Admitted = p == IntPtr.Zero || NoAdmissionFlag ? 0 : Allocated & Allowed,
                    Physical = new ulong[] { 3, 12, 48, 192 } };
            }
            public bool SetSystemAllowed(ulong mask)
            {
                Writes++; Operations.Add("system:" + mask);
                if (!WriteSystemFailure || PartialSystemWrite) Allocated = All & ~mask;
                return !WriteSystemFailure;
            }
            public IntPtr Open(int pid, long creation) { return !BlockOpen && !Dead && pid == 7 && creation == Creation ? new IntPtr(7) : IntPtr.Zero; }
            public bool Exited(IntPtr h) { return Dead; }
            public bool Gone(int pid, long creation) { return Dead || pid != 7 || creation != Creation; }
            public bool ReadAllowed(IntPtr h, out ulong mask) { mask = Allowed; return !ReadFailure; }
            public bool SetAllowed(IntPtr h, ulong mask)
            { Writes++; Operations.Add("process:" + mask); if (GrantFailure && mask != 0) return false;
                Allowed = mask; if (mask != 0 && AllocateDuringGrant != 0) Allocated = AllocateDuringGrant; return true; }
            public void Close(IntPtr h) { }
            public bool Read(out string value) { value = Receipt; return true; }
            public bool Write(string value)
            { SaveCalls++; Operations.Add("journal"); if (SaveCalls == FailSaveAt) return false; Receipt = value; return true; }
            internal CoreIsolationEngine Engine() { return new CoreIsolationEngine(this, this); }
        }

        internal static void RunCoreIsolationTests()
        {
            var single = new IsolationFixture();
            using (var e = single.Engine())
            {
                Eq(false, e.Begin(12, 7, 100, 4)); Eq(0, single.Writes);
                Eq(true, e.Begin(12, 7, 100, 4, true));
                Eq(12UL, single.Allocated); Eq(4UL, single.Allowed); Eq(true, e.Audit());
                var crashedSingle = new IsolationFixture { Receipt = single.Receipt, Allocated = 12, Allowed = 4 };
                using (var recovery = crashedSingle.Engine())
                { Eq(true, recovery.Recover()); Eq(0UL, crashedSingle.Allocated); Eq(0UL, crashedSingle.Allowed); }
                Eq(true, e.Restore()); Eq(0UL, single.Allocated); Eq(0UL, single.Allowed); Eq("", single.Receipt);
            }
            var manual = new IsolationFixture();
            using (var e = manual.Engine())
            {
                Eq(false, e.Begin(3, 7, 100, 3)); Eq(0, manual.Writes);
                Eq(true, e.Begin(3, 7, 100, 3, true)); Eq(3UL, manual.Allocated);
                Eq(true, manual.Receipt.StartsWith("3|"));
                var crashed = new IsolationFixture { Receipt = manual.Receipt, Allocated = 3, Allowed = 3 };
                using (var recovery = crashed.Engine())
                { Eq(true, recovery.Recover()); Eq(0UL, crashed.Allocated); Eq(0UL, crashed.Allowed); Eq("", crashed.Receipt); }
                Eq(true, e.Restore());
            }
            manual = new IsolationFixture { Allocated = 48 };
            using (var e = manual.Engine())
            { Eq(false, e.Begin(3, 7, 100, 3, true)); Eq("external-allocation", e.Error); Eq(0, manual.Writes); }
            manual = new IsolationFixture();
            using (var e = manual.Engine())
            { Eq(false, e.Begin(0x3F, 7, 100, 0x3F, true)); Eq(0, manual.Writes); }
            manual = new IsolationFixture();
            using (var e = manual.Engine())
            {
                var execute = typeof(CoreIsolationWorker).GetMethod("Execute", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Eq(true, (bool)execute.Invoke(null, new object[] { e, "MANUAL 3 7 100 3" }));
                Eq(true, e.Active); Eq(3UL, manual.Allocated); Eq(true, e.Restore());
            }
            var f = new IsolationFixture { Allowed = 48 };
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12)); Eq(true, e.Active); Eq(12UL, f.Allocated); Eq(60UL, f.Allowed);
                Eq("journal", f.Operations[0]); Eq("journal", f.Operations[1]);
                Eq("process:60", f.Operations[2]); Eq("journal", f.Operations[3]); Eq("system:243", f.Operations[4]);
                Eq(true, e.Audit()); Eq(true, e.Allow(7, 100, 12));
                Eq(false, e.Allow(7, 101, 12));
                Eq(true, e.Restore()); Eq(0UL, f.Allocated); Eq(48UL, f.Allowed); Eq("", f.Receipt);
                Eq("system:255", f.Operations[5]); Eq("process:48", f.Operations[6]);
            }

            f = new IsolationFixture { Allocated = 0xC0 };
            using (var e = f.Engine()) { Eq(false, e.Begin(0x3C, 7, 100, 0x3C)); Eq(0, f.Writes); Eq("invalid-range", e.Error); Eq("", f.Receipt); }
            f = new IsolationFixture { Allocated = 12 };
            using (var e = f.Engine()) { Eq(false, e.Begin(12, 7, 100, 12)); Eq(0, f.Writes); Eq("", f.Receipt); Eq("external-allocation", e.Error); }
            f = new IsolationFixture { Allocated = 48 };
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12)); Eq(60UL, f.Allocated); Eq("system:195", f.Operations[4]);
                Eq(true, f.Receipt.StartsWith("2|" + f.Boot + "|FF|C|30|1|"));
                Eq(true, e.Audit());
                Eq(true, e.Restore()); Eq(48UL, f.Allocated); Eq("system:207", f.Operations[5]); Eq("", f.Receipt);
            }
            f = new IsolationFixture { Allocated = 48 };
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12)); f.Allocated = 12;
                Eq(false, e.Audit()); Eq(true, e.Restore()); Eq(0UL, f.Allocated); Eq(0UL, f.Allowed); Eq("", f.Receipt);
            }
            f = new IsolationFixture { FailSaveAt = 1 };
            using (var e = f.Engine()) { Eq(false, e.Begin(12, 7, 100, 12)); Eq(0, f.Writes); }
            f = new IsolationFixture { FailSaveAt = 2 };
            using (var e = f.Engine()) { Eq(false, e.Begin(12, 7, 100, 12)); Eq(0, f.Writes); Eq("", f.Receipt); }
            f = new IsolationFixture { GrantFailure = true };
            using (var e = f.Engine()) { Eq(false, e.Begin(12, 7, 100, 12)); Eq(0UL, f.Allocated); Eq("", f.Receipt); }
            f = new IsolationFixture { AllocateDuringGrant = 12 };
            using (var e = f.Engine()) { Eq(false, e.Begin(12, 7, 100, 12)); Eq(12UL, f.Allocated); Eq(0UL, f.Allowed); Eq("", f.Receipt); }
            f = new IsolationFixture { FailSaveAt = 3 };
            using (var e = f.Engine()) { Eq(false, e.Begin(12, 7, 100, 12)); Eq(0UL, f.Allocated); Eq(0UL, f.Allowed); Eq("", f.Receipt); }
            f = new IsolationFixture { NoAdmissionFlag = true };
            using (var e = f.Engine()) { Eq(false, e.Begin(12, 7, 100, 12)); Eq(0UL, f.Allocated); Eq(0UL, f.Allowed); Eq("", f.Receipt); }
            f = new IsolationFixture { WriteSystemFailure = true, PartialSystemWrite = true };
            using (var e = f.Engine())
            {
                Eq(false, e.Begin(12, 7, 100, 12)); Eq(false, e.Active); Eq(0UL, f.Allocated);
                f.WriteSystemFailure = false; Eq(true, e.Restore()); Eq("", f.Receipt);
            }
            Console.WriteLine("PASS CoreIsolation: receipt before writes, exact admission, flags, failure rollback, existing allocations kept as found");

            f = new IsolationFixture();
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12));
                f.Allocated = 60; Eq(false, e.Audit()); int writes = f.Writes;
                Eq(true, e.Restore()); Eq(48UL, f.Allocated); Eq(writes + 2, f.Writes);
                Eq(0UL, f.Allowed); Eq("", f.Receipt);
            }
            f = new IsolationFixture();
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12)); f.Allowed = 192;
                Eq(false, e.Audit()); Eq(false, e.Restore()); Eq(192UL, f.Allowed); Eq(0UL, f.Allocated);
                f.Dead = true; Eq(true, e.Restore()); Eq("", f.Receipt);
            }
            f = new IsolationFixture();
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12)); f.Allocated = 4;
                int writes = f.Writes;
                Eq(false, e.Restore()); Eq(writes, f.Writes);
                Eq(12UL, f.Allowed); Eq(true, f.Receipt.Length > 0);
                f.Allocated = 0; Eq(true, e.Restore()); Eq(0UL, f.Allowed);
            }
            f = new IsolationFixture();
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12)); f.Allocated = 60; f.Allowed = 192;
                int writes = f.Writes;
                Eq(false, e.Restore()); Eq(writes, f.Writes); Eq(60UL, f.Allocated);
                f.Allocated = 48; f.Dead = true; Eq(true, e.Restore());
            }
            Console.WriteLine("PASS CoreIsolation: external changes are not overwritten; unfinished restoration retains receipt");

            f = new IsolationFixture(); var abandoned = f.Engine();
            Eq(true, abandoned.Begin(12, 7, 100, 12));
            using (var recovered = f.Engine()) { Eq(true, recovered.Recover()); Eq(0UL, f.Allocated); Eq(0UL, f.Allowed); }
            f = new IsolationFixture(); abandoned = f.Engine();
            Eq(true, abandoned.Begin(12, 7, 100, 12)); f.Creation = 200; f.Allowed = 192;
            using (var recovered = f.Engine()) { Eq(true, recovered.Recover()); Eq(192UL, f.Allowed); Eq(0UL, f.Allocated); }
            f = new IsolationFixture(); abandoned = f.Engine();
            Eq(true, abandoned.Begin(12, 7, 100, 12)); f.BlockOpen = true;
            using (var recovered = f.Engine()) { Eq(false, recovered.Recover()); Eq(true, f.Receipt.Length > 0); }
            f.BlockOpen = false;
            using (var recovered = f.Engine()) { Eq(true, recovered.Recover()); Eq("", f.Receipt); }
            f = new IsolationFixture(); abandoned = f.Engine();
            Eq(true, abandoned.Begin(12, 7, 100, 12)); int before = f.Writes;
            f.ReadFailure = true;
            using (var recovered = f.Engine()) { Eq(false, recovered.Recover()); }
            Eq(before, f.Writes); Eq(true, f.Receipt.Length > 0);
            f.ReadFailure = false;
            f.Boot = null;
            using (var recovered = f.Engine()) { Eq(false, recovered.Recover()); }
            Eq(before, f.Writes);
            f.Boot = "223456781234123412341234567890ab"; f.Allocated = 192;
            using (var recovered = f.Engine()) { Eq(true, recovered.Recover()); }
            Eq(before, f.Writes); Eq(192UL, f.Allocated); Eq("", f.Receipt);
            f = new IsolationFixture { Allocated = 48 }; abandoned = f.Engine();
            Eq(true, abandoned.Begin(12, 7, 100, 12)); Eq(60UL, f.Allocated);
            using (var recovered = f.Engine()) { Eq(true, recovered.Recover()); Eq(48UL, f.Allocated); Eq(0UL, f.Allowed); Eq("", f.Receipt); }
            f = new IsolationFixture { Allocated = 12, Allowed = 0x3C, Receipt = "1|123456781234123412341234567890ab|FF|C|1|7,100,30,3C" };
            using (var recovered = f.Engine()) { Eq(true, recovered.Recover()); Eq(0UL, f.Allocated); Eq(0x30UL, f.Allowed); Eq("", f.Receipt); }
            foreach (string corrupt in new[] { "broken", "1|123456781234123412341234567890ab|FF|C|1|7,100,0,4|7,100,0,4",
                "1|123456781234123412341234567890ab|FF|C|1|7,100,0,C0",
                "2|123456781234123412341234567890ab|FF|C|C|1", "2|123456781234123412341234567890ab|FF|C|100|1",
                "2|123456781234123412341234567890ab|FF|C|30|2", "2|123456781234123412341234567890ab|FF|C|1",
                "2|123456781234123412341234567890ab|FF|3|0|1", "4|123456781234123412341234567890ab|FF|C|0|1",
                "3|123456781234123412341234567890ab|FF|3|30|1" })
            { f = new IsolationFixture { Receipt = corrupt }; using (var e = f.Engine()) Eq(false, e.Recover()); Eq(0, f.Writes); Eq(corrupt, f.Receipt); }
            Console.WriteLine("PASS CoreIsolation: crash recovery, PID reuse, access denied, unknown boot, reboot, pre-existing allocations, v1 and v2 receipts");

            var data = new byte[64];
            for (int i = 0; i < 2; i++)
            {
                Array.Copy(BitConverter.GetBytes(32), 0, data, i * 32, 4);
                Array.Copy(BitConverter.GetBytes(9123 + i * 19), 0, data, i * 32 + 8, 4);
                data[i * 32 + 14] = (byte)(i == 0 ? 0 : 63); data[i * 32 + 15] = (byte)i;
            }
            data[51] = 6;
            IsolationCpuState decoded = CoreIsolationNative.Decode(data, 64);
            Eq(1UL | (1UL << 63), decoded.All); Eq(1UL << 63, decoded.Allocated); Eq(1UL << 63, decoded.Admitted);
            data[46] = 0; Eq(null, CoreIsolationNative.Decode(data, 64));
            data[46] = 63; data[44] = 1; Eq(null, CoreIsolationNative.Decode(data, 64));
            data[44] = 0; data[32] = 0; Eq(null, CoreIsolationNative.Decode(data, 64));
            Eq(null, CoreIsolationNative.Decode(new byte[31], 31));
            Console.WriteLine("PASS CoreIsolation: CPU Set IDs, group rejection, bit 63, duplicate CPU and truncated native records");
        }
    }
}
#endif
