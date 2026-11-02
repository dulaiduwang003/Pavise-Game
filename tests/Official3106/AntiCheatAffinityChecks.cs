using System;
using System.Collections.Generic;

namespace PaviseApp
{
#if PAVISE_AC_AFFINITY_CHECKS
    internal static class Settings
    {
        internal static bool TryLoadStr(string key, out string value) { value = ""; throw new Exception("production store must not run"); }
        internal static bool SaveStrDurable(string key, string value) { throw new Exception("production store must not run"); }
    }
#endif
    internal static class AntiCheatAffinityChecks
    {
        private static int checks;
        private static void Check(bool yes, string detail) { checks++; if (!yes) throw new Exception(detail); }
        private sealed class Store : IAntiCheatAffinityStore
        {
            internal string Text = "";
            internal bool LoadOk = true, SaveOk = true;
            internal int Saves;
            internal Action<string> AfterSave;
            internal readonly List<string> Events;
            internal Store(List<string> events) { Events = events; }
            public bool Load(out string value) { value = Text; return LoadOk; }
            public bool Save(string value)
            {
                Saves++; Events.Add("save:" + value);
                if (!SaveOk) return false;
                Text = value; if (AfterSave != null) AfterSave(value); return true;
            }
        }
        private sealed class Process
        {
            internal ulong Mask = 255, Allowed = 255;
            internal ushort Group = 1;
            internal AntiCheatAffinityRead State;
            internal bool CpuSetsKnown = true, CpuSetsEmpty = true;
        }
        private sealed class Platform : IAntiCheatAffinityPlatform, IAntiCheatAffinityRestorePlatform
        {
            internal string Device = "test-machine";
            internal long Clock;
            internal bool WriteOk = true, WriteThenFail, UnknownAfterWrite;
            internal int Reads, Writes, Forgets;
            internal Action BeforeWrite, AfterWrite;
            internal readonly Dictionary<string, Process> Processes = new Dictionary<string, Process>();
            internal readonly List<string> Events = new List<string>();
            public string DeviceIdentity { get { return Device; } }
            public long NowMilliseconds { get { return Clock; } }
            public AntiCheatAffinityObservation Read(AntiCheatAffinityIdentity identity)
            {
                Reads++; Process p;
                if (!Processes.TryGetValue(identity.Key, out p)) return new AntiCheatAffinityObservation(AntiCheatAffinityRead.Gone, 0, 0, 0, "gone");
                return new AntiCheatAffinityObservation(p.State, p.Group, p.Mask, p.Allowed, p.State.ToString())
                    { CpuSetsKnown = p.CpuSetsKnown, CpuSetsEmpty = p.CpuSetsEmpty };
            }
            public bool Write(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, out AntiCheatAffinityObservation after)
            { return WriteMask(identity, group, expected, target, true, out after); }
            public bool Restore(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, out AntiCheatAffinityObservation after)
            { return WriteMask(identity, group, expected, target, false, out after); }
            private bool WriteMask(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, bool admission, out AntiCheatAffinityObservation after)
            {
                if (BeforeWrite != null) BeforeWrite();
                after = Read(identity);
                if (after.Read != AntiCheatAffinityRead.Known || after.Mask != expected || after.Group != group) return false;
                if (admission && (!after.CpuSetsKnown || !after.CpuSetsEmpty)) return false;
                Writes++; Events.Add("write:" + target);
                if (!WriteOk) return false;
                Processes[identity.Key].Mask = target;
                if (AfterWrite != null) AfterWrite();
                after = Read(identity);
                if (UnknownAfterWrite) after.Read = AntiCheatAffinityRead.Unavailable;
                return !WriteThenFail && !UnknownAfterWrite && after.Mask == target;
            }
            public void Forget(AntiCheatAffinityIdentity identity) { Forgets++; }
        }
        private sealed class Fixture
        {
            internal readonly AntiCheatAffinityIdentity Id = new AntiCheatAffinityIdentity(1234, 100000, @"C:\Games\AntiCheat.exe");
            internal readonly Platform Native = new Platform();
            internal readonly Store Store;
            internal readonly Process Process = new Process();
            internal AntiCheatAffinityLease Lease;
            internal Fixture()
            {
                Native.Processes.Add(Id.Key, Process); Store = new Store(Native.Events);
                Lease = new AntiCheatAffinityLease(Native, Store);
            }
            internal AntiCheatAffinityResult Ensure(ulong target)
            { return Lease.Ensure(Id, Process.Group, target); }
            internal void Tick() { Native.Clock += 1000; }
            internal void CrashAndReopen() { Lease = new AntiCheatAffinityLease(Native, Store); }
        }
        private static void BasicAndReadOnlyGuard()
        {
            Fixture f = new Fixture();
            Check(f.Lease.Recover(), "empty recovery");
            Check(f.Ensure(48).Bound && f.Process.Mask == 48, "group 1 hard binding");
            Check(f.Lease.HasOwnership(f.Id) && f.Lease.OriginalAffinity(f.Id) == 255, "ownership original captured");
            Check(f.Native.Events[0].StartsWith("save:ACHA1|"), "durable intent precedes first write");
            Check(f.Native.Events[1] == "write:48", "only hard mask write");
            Check(!f.Lease.PendingRecovery, "normal lease has no recovery debt");
            int reads = f.Native.Reads, writes = f.Native.Writes, saves = f.Store.Saves;
            for (int i = 0; i < 20; i++) Check(f.Ensure(48).Bound, "poll cache active");
            Check(f.Native.Reads == reads && f.Native.Writes == writes && f.Store.Saves == saves, "subsecond calls do no platform work");
            for (int i = 0; i < 10; i++) { f.Tick(); Check(f.Ensure(48).Bound, "one second read check"); }
            Check(f.Native.Writes == writes && f.Store.Saves == saves, "matching guard never replays writes or receipt");
            Check(f.Lease.Recover() && f.Process.Mask == 48, "Recover does not release active ownership");
            Check(f.Lease.Release(f.Id) && f.Process.Mask == 255 && f.Store.Text == "", "release original");
            Check(!f.Lease.HasOwnership(f.Id), "released ownership clear");
            Check(f.Ensure(48).Bound, "released process can be acquired later");
            Check(f.Lease.ReleaseAll() && f.Process.Mask == 255, "release all");
        }
        private static void ExternalChangesAndRateLimit()
        {
            Fixture f = new Fixture(); f.Ensure(48);
            for (int i = 0; i < 3; i++)
            {
                f.Process.Mask = (ulong)(240 - i); f.Tick();
                Check(f.Ensure(48).Code == "reasserted" && f.Process.Mask == 48, "external drift corrected");
                Check(f.Lease.OriginalAffinity(f.Id) == (ulong)(240 - i), "latest foreign baseline captured");
            }
            f.Process.Mask = 15; f.Tick();
            Check(f.Ensure(48).Code == "conflict" && f.Process.Mask == 15, "fourth conflict yields and preserves external mask");
            int writes = f.Native.Writes;
            for (int i = 0; i < 80; i++) { f.Tick(); Check(f.Ensure(48).Code == "conflict", "conflict stays stopped for this session"); }
            Check(f.Native.Writes == writes && f.Store.Text == "", "no endless reassertions");
            Check(f.Lease.ReleaseAll(), "session reset after conflict");
            Check(f.Ensure(48).Bound, "next session can acquire");
            f.Process.Mask = 7;
            Check(f.Lease.Release(f.Id) && f.Process.Mask == 7, "exit never overwrites a new external value");

            f = new Fixture(); f.Ensure(48); f.Process.Mask = 63; f.Tick(); f.Ensure(48);
            Check(f.Lease.ReleaseAll() && f.Process.Mask == 63, "restore external latest value rather than initial mask");
            f = new Fixture(); f.Ensure(48);
            for (int i = 0; i < 5; i++) { f.Native.Clock += 60001; f.Process.Mask = 127; Check(f.Ensure(48).Bound, "sparse drift gets fresh correction window"); }
            f.Lease.ReleaseAll();
        }
        private static void TargetChanges()
        {
            Fixture f = new Fixture(); f.Ensure(48);
            Check(f.Ensure(192).Bound && f.Process.Mask == 192, "changed topology target bypasses read throttle");
            Check(f.Lease.OriginalAffinity(f.Id) == 255, "target change does not snapshot own affinity as original");
            Check(f.Lease.ReleaseAll() && f.Process.Mask == 255, "changed target restores initial foreign baseline");

            f = new Fixture(); f.Ensure(48);
            f.Native.BeforeWrite = delegate { throw new InvalidOperationException("simulated crash before changed target write"); };
            try { f.Ensure(192); } catch (InvalidOperationException) { }
            Check(f.Process.Mask == 48 && f.Store.Text.Contains("|C0|30|P"), "prepared target journal includes previous own mask");
            f.Native.BeforeWrite = null; f.CrashAndReopen();
            Check(f.Lease.Recover() && f.Process.Mask == 255, "prepared target change crash restores previous owned mask");

            f = new Fixture(); f.Ensure(48);
            f.Native.BeforeWrite = delegate { throw new InvalidOperationException("target preparation crash"); };
            try { f.Ensure(192); } catch (InvalidOperationException) { }
            f.Native.BeforeWrite = null; f.Native.WriteOk = false; f.CrashAndReopen();
            Check(!f.Lease.Recover() && f.Process.Mask == 48 && f.Store.Text.EndsWith("|30|R\n"), "restore failure keeps previous owned mask in restore intent");
            f.Native.WriteOk = true;
            Check(f.Lease.Recover() && f.Process.Mask == 255, "restore intent recognizes previous owned target on retry");
        }
        private static void CrashIntents()
        {
            Fixture f = new Fixture();
            f.Native.BeforeWrite = delegate { throw new InvalidOperationException("before write"); };
            try { f.Ensure(48); } catch (InvalidOperationException) { }
            Check(f.Store.Text.EndsWith("|0|P\n") && f.Process.Mask == 255, "prepared acquisition survives crash");
            f.Native.BeforeWrite = null; f.CrashAndReopen();
            Check(f.Lease.Recover() && f.Native.Writes == 0 && f.Store.Text == "", "prepared but unwritten clears without write");

            f = new Fixture();
            f.Native.AfterWrite = delegate { throw new InvalidOperationException("after write"); };
            try { f.Ensure(48); } catch (InvalidOperationException) { }
            Check(f.Process.Mask == 48 && f.Store.Text.EndsWith("|0|P\n"), "write before confirmation still prepared durable");
            f.Native.AfterWrite = null; f.CrashAndReopen();
            Check(f.Lease.Recover() && f.Process.Mask == 255, "prepared applied crash restores baseline");

            f = new Fixture(); f.Ensure(48); f.Process.Mask = 127; f.Tick();
            f.Native.AfterWrite = delegate { throw new InvalidOperationException("after drift write"); };
            try { f.Ensure(48); } catch (InvalidOperationException) { }
            f.Native.AfterWrite = null; f.CrashAndReopen();
            Check(f.Lease.Recover() && f.Process.Mask == 127, "drift prepared crash restores updated external baseline");

            f = new Fixture(); f.Ensure(48);
            f.Native.AfterWrite = delegate { throw new InvalidOperationException("after restore write"); };
            try { f.Lease.ReleaseAll(); } catch (InvalidOperationException) { }
            Check(f.Process.Mask == 255 && f.Store.Text.EndsWith("|30|R\n"), "restore intent durable before restore");
            f.Native.AfterWrite = null; f.CrashAndReopen();
            Check(f.Lease.Recover() && f.Store.Text == "", "crash after successful restore clears without undoing restore");
        }
        private static void FailureDebtsAndIdentity()
        {
            Fixture f = new Fixture(); f.Store.SaveOk = false;
            Check(f.Ensure(48).Code == "journal-failed" && f.Native.Writes == 0 && f.Process.Mask == 255, "no write before durable receipt");
            f = new Fixture(); f.Store.LoadOk = false;
            Check(f.Ensure(48).PendingRecovery && f.Native.Writes == 0, "unreadable receipt blocks acquisition");
            f = new Fixture(); f.Native.Device = "";
            Check(f.Ensure(48).PendingRecovery && f.Native.Writes == 0, "unknown machine blocks writes");

            f = new Fixture(); f.Ensure(48); f.Native.WriteOk = false;
            Check(!f.Lease.ReleaseAll() && f.Lease.HasOwnership(f.Id) && f.Lease.PendingRecovery, "failed restore retains recoverable debt");
            Check(f.Store.Text.EndsWith("|30|R\n"), "restore intent remains durable");
            f.Native.WriteOk = true;
            Check(f.Lease.Recover() && f.Process.Mask == 255 && f.Store.Text == "", "failed restore retry succeeds");

            f = new Fixture(); f.Ensure(48); f.Native.WriteOk = false;
            Check(!f.Lease.Release(f.Id) && f.Lease.PendingRecovery, "individual release failure sets recovery debt");
            f.Native.WriteOk = true;
            Check(f.Lease.Release(f.Id) && f.Process.Mask == 255 && f.Store.Text == "", "individual release retry restores and removes final receipt");
            Check(!f.Lease.PendingRecovery && !f.Lease.HasOwnership(f.Id), "successful final individual release clears stale recovery flag");
            Check(f.Lease.Release(f.Id) && !f.Lease.PendingRecovery, "already released identity stays free of recovery debt");

            f = new Fixture(); f.Ensure(48);
            AntiCheatAffinityIdentity second = new AntiCheatAffinityIdentity(4321, 200000, @"C:\Games\OtherAntiCheat.exe");
            f.Native.Processes.Add(second.Key, new Process());
            Check(f.Lease.Ensure(second, 1, 192).Bound, "second active process acquired");
            Check(f.Lease.Release(f.Id) && !f.Lease.PendingRecovery && f.Lease.HasOwnership(second), "other normal active receipt is not mistaken for recovery debt");
            Check(f.Lease.Release(second) && !f.Lease.PendingRecovery && f.Store.Text == "", "last normal receipt releases cleanly");

            f = new Fixture(); f.Ensure(48); f.Process.State = AntiCheatAffinityRead.Unavailable;
            Check(!f.Lease.ReleaseAll() && f.Lease.HasOwnership(f.Id) && f.Store.Text.Length > 0, "permission denial never clears receipt");
            int writes = f.Native.Writes;
            Check(f.Ensure(192).PendingRecovery && f.Native.Writes == writes, "pending debt blocks new target writes");
            f.Process.State = AntiCheatAffinityRead.Known;
            Check(f.Lease.Recover() && f.Process.Mask == 255, "permission recovers and restores");

            f = new Fixture(); f.Ensure(48); f.Process.State = AntiCheatAffinityRead.DifferentProcess;
            writes = f.Native.Writes;
            Check(f.Lease.ReleaseAll() && f.Native.Writes == writes && f.Store.Text == "", "PID reused never writes other process");
            f = new Fixture(); f.Ensure(48); f.Native.Processes.Remove(f.Id.Key);
            Check(f.Lease.ReleaseAll() && f.Store.Text == "", "gone process clears safe receipt");
            f = new Fixture(); f.Ensure(48); f.CrashAndReopen(); f.Native.Device = "different-machine";
            writes = f.Native.Writes;
            Check(!f.Lease.Recover() && f.Native.Writes == writes && f.Store.Text.Length > 0, "foreign machine receipt untouched");

            f = new Fixture(); f.Native.WriteThenFail = true;
            Check(f.Ensure(48).PendingRecovery && f.Process.Mask == 48, "unverified setter may have written");
            f.Native.WriteThenFail = false;
            Check(f.Lease.Recover() && f.Process.Mask == 255, "unverified applied write recovered");
            f = new Fixture(); f.Native.UnknownAfterWrite = true;
            Check(f.Ensure(48).PendingRecovery && f.Store.Text.EndsWith("|0|P\n"), "readback unknown keeps intent");
            f.Native.UnknownAfterWrite = false;
            Check(f.Lease.Recover() && f.Process.Mask == 255, "unknown readback later restores");

            f = new Fixture(); f.Store.AfterSave = delegate(string text) { if (text.EndsWith("|0|P\n")) f.Process.Mask = 127; };
            Check(f.Ensure(48).PendingRecovery && f.Process.Mask == 127 && f.Native.Writes == 0, "external change after intent prevents compare-write");
            f.Store.AfterSave = null;
            Check(f.Lease.Recover() && f.Process.Mask == 127, "foreign race value preserved during recovery");

            f = new Fixture(); f.Ensure(48); f.Store.SaveOk = false;
            Check(!f.Lease.ReleaseAll() && f.Process.Mask == 48 && f.Lease.HasOwnership(f.Id), "restore does not write without durable restore intent");
            f.Store.SaveOk = true; Check(f.Lease.Recover() && f.Process.Mask == 255, "journal write failure debt recovers");
        }
        private static void UnsupportedAndNoTakeover()
        {
            Fixture f = new Fixture();
            Check(f.Lease.Ensure(f.Id, 2, 48).Code == "unsupported" && f.Native.Writes == 0, "wrong group never guessed as group zero");
            f = new Fixture(); f.Process.State = AntiCheatAffinityRead.Unsupported;
            Check(f.Ensure(48).Code == "unsupported" && f.Native.Writes == 0, "multi group or CPU Sets restriction skip");
            f = new Fixture();
            Check(f.Ensure(256).Code == "unsupported" && f.Native.Writes == 0, "outside group capacity skip");
            f = new Fixture(); f.Process.Mask = 48;
            Check(f.Ensure(48).Code == "already-matched" && !f.Lease.HasOwnership(f.Id), "existing matching mask never becomes owned");
            Check(f.Lease.ReleaseAll() && f.Process.Mask == 48 && f.Native.Writes == 0, "existing matching mask never restored to invented baseline");
            f = new Fixture(); Check(f.Ensure(0).Code == "invalid-target", "empty target rejected");
            f = new Fixture(); f.Store.Text = "ACHA1|test-machine\nmalformed";
            Check(!f.Lease.Recover() && f.Native.Writes == 0 && f.Store.Text.EndsWith("malformed"), "malformed receipt preserved and blocked");
            f = new Fixture(); f.Ensure(48); f.Process.Group = 2;
            Check(!f.Lease.ReleaseAll() && f.Store.Text.Length > 0, "group changed during ownership retains debt");
        }
        private static void ForeignCpuSetsAndStoppedState()
        {
            Fixture f = new Fixture(); f.Process.CpuSetsEmpty = false;
            Check(f.Ensure(48).Code == "unsupported" && f.Native.Writes == 0 && !f.Lease.HasOwnership(f.Id), "nonempty CPU Sets blocks initial admission");
            Check(f.Native.Forgets > 0, "unadmitted native query handle released");
            f = new Fixture(); f.Process.CpuSetsKnown = false;
            Check(f.Ensure(48).Code == "unsupported" && f.Native.Writes == 0, "unreadable CPU Sets blocks initial admission");

            f = new Fixture(); f.Ensure(48); f.Process.CpuSetsEmpty = false; f.Tick();
            AntiCheatAffinityResult result = f.Ensure(48);
            Check(result.Code == "unsupported" && !result.PendingRecovery && !result.Bound && f.Process.Mask == 255, "CPU Sets added after binding causes hard mask restoration and stop");
            Check(!f.Process.CpuSetsEmpty && f.Store.Text == "", "foreign CPU Sets remains untouched and debt clears");
            Check(!f.Ensure(48).Bound && f.Native.Writes == 2, "stopped result never reports bound or replays setter");

            f = new Fixture(); f.Ensure(48); f.Process.CpuSetsKnown = false; f.Tick();
            result = f.Ensure(48);
            Check(!result.PendingRecovery && f.Process.Mask == 255 && !f.Process.CpuSetsKnown, "hard restoration does not require readable unrelated CPU Sets");

            f = new Fixture(); f.Ensure(48); f.Process.CpuSetsEmpty = false; f.CrashAndReopen();
            Check(f.Lease.Recover() && f.Process.Mask == 255 && !f.Process.CpuSetsEmpty, "startup crash recovery ignores foreign CPU Sets for owned hard mask");
            f = new Fixture(); f.Ensure(48); f.Process.CpuSetsEmpty = false;
            Check(f.Lease.ReleaseAll() && f.Process.Mask == 255 && !f.Process.CpuSetsEmpty, "shutdown restore leaves foreign CPU Sets intact");

            f = new Fixture(); f.Store.AfterSave = delegate(string text) { if (text.EndsWith("|0|P\n")) f.Process.CpuSetsEmpty = false; };
            Check(f.Ensure(48).PendingRecovery && f.Native.Writes == 0 && f.Process.Mask == 255, "CPU Sets appearing between admission and setter prevents binding");
            f.Store.AfterSave = null;
            Check(f.Lease.Recover() && !f.Process.CpuSetsEmpty && f.Process.Mask == 255, "admission race no-op recovery preserves CPU Sets");
            result = f.Ensure(48);
            Check(!result.PendingRecovery && !result.Bound && result.Code == "stopped", "recovered stopped guard cannot keep a stale pending/bound result");

            f = new Fixture(); f.Ensure(48); f.Process.CpuSetsEmpty = false; f.Native.WriteOk = false; f.Tick();
            Check(f.Ensure(48).PendingRecovery && f.Lease.HasOwnership(f.Id), "failed hard restore with foreign sets retains debt");
            Check(f.Native.Forgets > 0, "failed restoration closes retained native query handle");
            f.Native.WriteOk = true;
            Check(f.Lease.Recover() && f.Process.Mask == 255 && !f.Process.CpuSetsEmpty, "foreign sets do not block retrying owned hard restoration");
        }
        private static int Main()
        {
            try
            {
                BasicAndReadOnlyGuard(); ExternalChangesAndRateLimit(); TargetChanges(); CrashIntents(); FailureDebtsAndIdentity(); UnsupportedAndNoTakeover(); ForeignCpuSetsAndStoppedState();
                Console.WriteLine("PASS anti-cheat hard affinity ownership assertions=" + checks + " native_process_writes=false cpuset_setters=false"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
        }
    }
}
