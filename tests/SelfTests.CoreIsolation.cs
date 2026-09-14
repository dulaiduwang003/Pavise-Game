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

        // An unreadable placement is not the same as an ineffective placement, anti-cheats commonly revoke handles mid-match
        //   Withdrawing effective isolation on a single read failure shows up as isolation vanishing mid-play and never coming back this match
        private static void IsolationWithdrawalNeedsRepeatedReadableMismatches()
        {
            const int max = 3;
            // Write not effective, below the limit: count a retry, do not withdraw
            Eq(GameMode.IsolationVerdict.Retry, GameMode.IsolationVerdictOf(true, 1, max));
            Eq(GameMode.IsolationVerdict.Retry, GameMode.IsolationVerdictOf(false, max - 1, max));
            // At the limit with the game's affinity still covering the exclusive range: just stop correcting, isolation stays
            Eq(GameMode.IsolationVerdict.StopCorrecting, GameMode.IsolationVerdictOf(true, max, max));
            Eq(GameMode.IsolationVerdict.StopCorrecting, GameMode.IsolationVerdictOf(true, max + 1, max));
            // Only withdraw isolation at the limit when the exclusive range is not covered
            Eq(GameMode.IsolationVerdict.Withdraw, GameMode.IsolationVerdictOf(false, max, max));
            Eq(GameMode.IsolationVerdict.Withdraw, GameMode.IsolationVerdictOf(false, max + 1, max));
            // Coverage check: all cores covers, a subset covers, one missing exclusive core does not, an unreadable 0 does not
            Eq(true, GameMode.CoversIsolation(0xFFFFUL, 0x0FF0UL));
            Eq(true, GameMode.CoversIsolation(0x0FF0UL, 0x0FF0UL));
            Eq(false, GameMode.CoversIsolation(0x0FE0UL, 0x0FF0UL));
            Eq(false, GameMode.CoversIsolation(0UL, 0x0FF0UL));
            Eq(false, GameMode.CoversIsolation(0xFFFFUL, 0UL));
            Console.WriteLine("PASS CoreIsolation: relapses are corrected in place; isolation is withdrawn only when the game can no longer reach the isolated cores");
        }

        internal static void RunCoreIsolationTests()
        {
            IsolationWithdrawalNeedsRepeatedReadableMismatches();
            var f = new IsolationFixture { Allowed = 48 };
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12)); Eq(true, e.Active); Eq(12UL, f.Allocated); Eq(60UL, f.Allowed);
                Eq("journal", f.Operations[0]); Eq("journal", f.Operations[1]);
                Eq("process:60", f.Operations[2]); Eq("journal", f.Operations[3]); Eq("system:243", f.Operations[4]);
                Eq(true, e.Audit()); Eq(true, e.Allow(7, 100, 12));
                Eq(false, e.Allow(7, 101, 12)); // PID reuse cannot inherit admission
                Eq(true, e.Restore()); Eq(0UL, f.Allocated); Eq(48UL, f.Allowed); Eq("", f.Receipt);
                Eq("system:255", f.Operations[5]); Eq("process:48", f.Operations[6]);
            }

            f = new IsolationFixture { Allocated = 48 };
            using (var e = f.Engine()) { Eq(false, e.Begin(12, 7, 100, 12)); Eq(0, f.Writes); Eq("", f.Receipt); }
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
            Console.WriteLine("PASS CoreIsolation: receipt before writes, exact admission, flags, failure rollback, existing allocations");

            f = new IsolationFixture();
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12));
                f.Allocated = 60; Eq(false, e.Audit()); int writes = f.Writes;
                Eq(false, e.Restore()); Eq(60UL, f.Allocated); Eq(writes + 1, f.Writes); // only process grant restored
                Eq(true, f.Receipt.Length > 0);
                f.Allocated = 12; Eq(true, e.Restore());
            }
            f = new IsolationFixture();
            using (var e = f.Engine())
            {
                Eq(true, e.Begin(12, 7, 100, 12)); f.Allowed = 192;
                Eq(false, e.Audit()); Eq(false, e.Restore()); Eq(192UL, f.Allowed); Eq(0UL, f.Allocated);
                f.Dead = true; Eq(true, e.Restore()); Eq("", f.Receipt);
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
            Eq(before, f.Writes); // Dispose must not restore with unknown boot identity
            f.Boot = "223456781234123412341234567890ab"; f.Allocated = 192;
            using (var recovered = f.Engine()) { Eq(true, recovered.Recover()); }
            Eq(before, f.Writes); Eq(192UL, f.Allocated); Eq("", f.Receipt);
            foreach (string corrupt in new[] { "broken", "1|123456781234123412341234567890ab|FF|C|1|7,100,0,4|7,100,0,4",
                "1|123456781234123412341234567890ab|FF|C|1|7,100,0,C0" })
            { f = new IsolationFixture { Receipt = corrupt }; using (var e = f.Engine()) Eq(false, e.Recover()); Eq(0, f.Writes); Eq(corrupt, f.Receipt); }
            Console.WriteLine("PASS CoreIsolation: crash recovery, PID reuse, access denied, unknown boot, reboot and corrupt receipts");

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
