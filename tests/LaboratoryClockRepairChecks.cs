using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PaviseApp
{
    internal static class LaboratoryClockRepairChecks
    {
        private static int assertions;
        private static readonly string Entry = "{2d948227-3c94-44e2-868d-fba639c9d135}";
        private static readonly string Boot = "boot2:2d9482273c9444e2868dfba639c9d135:1";
        private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
        private sealed class Store : ILaboratoryManualStore
        {
            internal string Value = "";
            internal int Writes, FailWriteAt;
            internal bool FailRead;
            public bool Read(out string value) { value = Value; return !FailRead; }
            public bool Write(string value) { Writes++; if (Writes == FailWriteAt) return false; Value = value; return true; }
        }
        private sealed class Native : IClockRepairNative
        {
            internal readonly ClockRepairSnapshot Snapshot = new ClockRepairSnapshot { EntryId = Entry, BootIdentity = Boot };
            internal readonly List<string> Changes = new List<string>();
            internal int Deletes, Sets, FailDeleteAt, FailSetAt;
            internal bool FailRead, LieDelete, LieSet;
            internal Action BeforeRead;
            public bool Read(out ClockRepairSnapshot value, out string error)
            {
                if (BeforeRead != null) BeforeRead();
                value = new ClockRepairSnapshot { EntryId = Snapshot.EntryId, BootIdentity = Snapshot.BootIdentity };
                foreach (var item in Snapshot.Values) value.Values.Add(item.Key, item.Value);
                error = FailRead ? "read_failure" : ""; return !FailRead;
            }
            public bool Delete(string id, uint type, out string error)
            {
                Check(id == Snapshot.EntryId, "wrong boot entry written"); Check(ClockRepairNative.Allowed(type), "non-whitelisted delete");
                Deletes++; Changes.Add("delete:" + type.ToString("X8")); error = Deletes == FailDeleteAt ? "delete_failure" : "";
                if (error.Length != 0) return false;
                if (!LieDelete) Snapshot.Values.Remove(type);
                return true;
            }
            public bool Set(string id, uint type, ulong value, out string error)
            {
                Check(id == Snapshot.EntryId, "wrong boot entry restored"); Check(ClockRepairNative.ValidNumber(type, value), "non-whitelisted restore");
                Sets++; Changes.Add("set:" + type.ToString("X8")); error = Sets == FailSetAt ? "set_failure" : "";
                if (error.Length != 0) return false;
                if (!LieSet) Snapshot.Values[type] = value;
                return true;
            }
        }
        private static ClockRepairController Controller(Native native, Store store, Func<bool> conflicting = null)
        { return new ClockRepairController(native, store, conflicting ?? delegate { return false; }); }
        private static ClockRepairReceipt Receipt(string phase, int attempted, params uint[] types)
        {
            var value = new ClockRepairReceipt { Phase = phase, Attempted = attempted, EntryId = Entry, Boot = Boot };
            foreach (uint type in types) value.Values.Add(new KeyValuePair<uint, ulong>(type, 1));
            return value;
        }
        private static void Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--native-readonly")
            {
                ClockRepairSnapshot snapshot; string error;
                if (!new ClockRepairNative().Read(out snapshot, out error)) { Console.WriteLine("READ_FAILED " + error); Environment.ExitCode = 2; return; }
                Console.WriteLine("READ_OK Entry=" + snapshot.EntryId + " Boot=" + snapshot.BootIdentity);
                foreach (var item in snapshot.Values) Console.WriteLine(item.Key.ToString("X8") + "=" + item.Value.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("No mutations performed."); return;
            }
            Settings.UseTransientStoreForCurrentProcess(); Lang.Cur = 1;
            string failure;
            {
                var n = new Native(); var s = new Store(); var c = Controller(n, s);
                Check(!c.HasReceipt && !c.HasUnfinishedRecovery, "default off");
                Check(!c.Inspect().CanApply && c.Inspect().Reason.Length > 0, "no forced values should be unavailable");
                Check(!c.Apply(out failure) && n.Changes.Count == 0 && s.Writes == 0, "clean config must be zero write");
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 0;
                n.Snapshot.Values[ClockRepairNative.UsePlatformTick] = 0;
                n.Snapshot.Values[ClockRepairNative.TscSyncPolicy] = 0;
                Check(!c.Apply(out failure) && n.Changes.Count == 0 && s.Writes == 0, "explicit defaults must be untouched");
            }
            {
                var n = new Native(); var s = new Store(); var c = Controller(n, s);
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1;
                n.Snapshot.Values[ClockRepairNative.UsePlatformTick] = 0;
                n.Snapshot.Values[ClockRepairNative.TscSyncPolicy] = 2;
                n.Snapshot.Values[0x260000A5] = 1;
                Check(c.Inspect().CanApply, "forced candidates");
                Check(c.Apply(out failure), "apply " + failure);
                Check(n.Deletes == 2 && n.Sets == 0, "only forced values removed");
                Check(n.Snapshot.Values[ClockRepairNative.UsePlatformTick] == 0 && n.Snapshot.Values[0x260000A5] == 1, "explicit false and dynamic tick preserved");
                Check(c.HasReceipt && !c.HasUnfinishedRecovery, "applied is durable, not interrupted debt");
                Check(c.Inspect().Applied && c.Inspect().RestartRequired && !c.Inspect().CanApply && c.Inspect().CanRestore, "awaiting restart presentation");
                int deletes = n.Deletes;
                Check(!c.Apply(out failure) && n.Deletes == deletes, "no duplicate apply");
                Check(c.RecoverInterrupted(out failure) && n.Deletes == deletes && n.Sets == 0 && c.HasReceipt, "successful apply survives app restart");
                n.Snapshot.BootIdentity = "boot2:2d9482273c9444e2868dfba639c9d135:2";
                Check(c.Inspect().Applied && !c.Inspect().RestartRequired, "reboot recognized");
                Check(c.Restore(out failure), "restore " + failure);
                Check(n.Snapshot.Values[ClockRepairNative.UsePlatformClock] == 1 && n.Snapshot.Values[ClockRepairNative.TscSyncPolicy] == 2, "original values exact");
                Check(n.Snapshot.Values[ClockRepairNative.UsePlatformTick] == 0 && n.Snapshot.Values[0x260000A5] == 1, "other values unchanged after restore");
                Check(!c.HasReceipt && !c.HasUnfinishedRecovery, "receipt cleared");
            }
            {
                var n = new Native(); var s = new Store { FailWriteAt = 1 }; var c = Controller(n, s);
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1;
                Check(!c.Apply(out failure) && n.Changes.Count == 0, "save failure must precede native writes");
            }
            for (int fail = 2; fail <= 4; fail++)
            {
                var n = new Native(); var s = new Store { FailWriteAt = fail }; var c = Controller(n, s);
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1;
                n.Snapshot.Values[ClockRepairNative.TscSyncPolicy] = 2;
                Check(!c.Apply(out failure), "intent/completion save failure should fail apply");
                Check(n.Snapshot.Values[ClockRepairNative.UsePlatformClock] == 1 && n.Snapshot.Values[ClockRepairNative.TscSyncPolicy] == 2, "save failure rolled back originals");
            }
            {
                var n = new Native { FailDeleteAt = 2 }; var s = new Store(); var c = Controller(n, s);
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1; n.Snapshot.Values[ClockRepairNative.UsePlatformTick] = 1;
                Check(!c.Apply(out failure), "partial native failure");
                Check(n.Snapshot.Values[ClockRepairNative.UsePlatformClock] == 1 && n.Snapshot.Values[ClockRepairNative.UsePlatformTick] == 1 && !c.HasReceipt, "partial failure rollback");
            }
            {
                var n = new Native { LieDelete = true }; var s = new Store(); var c = Controller(n, s);
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1;
                Check(!c.Apply(out failure) && n.Sets == 0 && !c.HasReceipt, "false-success delete rejected");
            }
            {
                var n = new Native(); var s = new Store(); var c = Controller(n, s); int reads = 0;
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1; n.Snapshot.Values[ClockRepairNative.UsePlatformTick] = 1;
                n.BeforeRead = delegate { if (++reads == 4) n.Snapshot.Values[ClockRepairNative.UsePlatformTick] = 0; };
                Check(!c.Apply(out failure), "external edit between deletes must abort apply");
                Check(n.Deletes == 1 && n.Sets == 1 && n.Snapshot.Values[ClockRepairNative.UsePlatformClock] == 1, "only completed own mutation rolls back");
                Check(n.Snapshot.Values[ClockRepairNative.UsePlatformTick] == 0 && !c.HasReceipt, "unattempted external change is preserved");
            }
            {
                var n = new Native(); var s = new Store(); var c = Controller(n, s, delegate { return true; });
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1;
                Check(!c.Inspect().CanApply && !c.Apply(out failure) && n.Changes.Count == 0 && s.Writes == 0, "other timer owner blocked");
            }
            {
                var n = new Native(); var s = new Store(); bool occupied = false;
                var c = Controller(n, s, delegate { return occupied; });
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1;
                Check(c.Apply(out failure), "setup for late owner");
                int count = n.Changes.Count; occupied = true;
                Check(!c.Inspect().CanRestore && !c.Restore(out failure) && n.Changes.Count == count && c.HasReceipt, "late owner must not be overwritten");
                occupied = false;
                Check(c.Restore(out failure) && !c.HasReceipt, "late owner can relinquish before restore");
            }
            {
                var n = new Native(); var s = new Store { Value = Receipt("intent", 1, ClockRepairNative.UsePlatformClock, ClockRepairNative.UsePlatformTick).Encode() };
                n.Snapshot.Values[ClockRepairNative.UsePlatformTick] = 1;
                var c = Controller(n, s);
                Check(c.HasUnfinishedRecovery && c.RecoverInterrupted(out failure), "interrupted intent recovered");
                Check(n.Sets == 1 && n.Snapshot.Values[ClockRepairNative.UsePlatformClock] == 1 && n.Snapshot.Values[ClockRepairNative.UsePlatformTick] == 1, "only attempted key restored");
            }
            {
                var n = new Native(); var s = new Store { Value = Receipt("intent", 0, ClockRepairNative.UsePlatformClock).Encode() };
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1;
                Check(Controller(n, s).RecoverInterrupted(out failure) && n.Changes.Count == 0 && s.Value == "", "unattempted intent cleared without write");
            }
            {
                var n = new Native(); var s = new Store { Value = Receipt("applied", 1, ClockRepairNative.UsePlatformClock).Encode() }; var c = Controller(n, s);
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 0;
                Check(!c.Restore(out failure) && n.Changes.Count == 0 && c.HasReceipt && c.HasUnfinishedRecovery, "external scalar edit preserved with recovery record");
            }
            {
                var n = new Native(); var s = new Store { Value = Receipt("applied", 1, ClockRepairNative.UsePlatformClock).Encode() }; var c = Controller(n, s);
                n.Snapshot.EntryId = "{6d390e0e-8648-44f7-a4d5-7ea45bc22013}";
                Check(!c.Restore(out failure) && n.Changes.Count == 0 && c.HasReceipt, "boot entry mismatch must never write current alias");
            }
            {
                var n = new Native { FailSetAt = 1 }; var s = new Store { Value = Receipt("intent", 1, ClockRepairNative.UsePlatformClock).Encode() }; var c = Controller(n, s);
                Check(!c.RecoverInterrupted(out failure) && c.HasUnfinishedRecovery, "restore failure retains debt");
                n.FailSetAt = 0;
                Check(c.RecoverInterrupted(out failure) && !c.HasReceipt, "restore can retry");
            }
            {
                var n = new Native { LieSet = true }; var s = new Store { Value = Receipt("intent", 1, ClockRepairNative.UsePlatformClock).Encode() }; var c = Controller(n, s);
                Check(!c.RecoverInterrupted(out failure) && c.HasReceipt, "false-success restore rejected");
            }
            {
                var n = new Native(); var s = new Store { Value = Receipt("applied", 1, ClockRepairNative.UsePlatformClock).Encode(), FailWriteAt = 2 }; var c = Controller(n, s);
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1;
                Check(!c.Restore(out failure) && c.HasUnfinishedRecovery && n.Changes.Count == 0, "record clear failure retains recovery without native write");
                s.FailWriteAt = 0;
                Check(c.RecoverInterrupted(out failure) && !c.HasReceipt && n.Changes.Count == 0, "record cleanup can retry without reapplying state");
            }
            foreach (string corrupt in new[] { "broken", "clock1|intent", Receipt("applied", 1, ClockRepairNative.UsePlatformClock).Encode().Replace("260000A2", "260000A5"), Receipt("applied", 1, ClockRepairNative.TscSyncPolicy).Encode().Replace("|1", "|9") })
            {
                var n = new Native(); var s = new Store { Value = corrupt }; var c = Controller(n, s);
                Check(c.HasReceipt && c.HasUnfinishedRecovery, "corrupt debt visible");
                Check(!c.Apply(out failure) && !c.Restore(out failure) && !c.RecoverInterrupted(out failure) && n.Changes.Count == 0 && s.Value == corrupt, "corrupt record retained without write");
            }
            {
                var n = new Native { FailRead = true }; var s = new Store(); var c = Controller(n, s);
                Check(!c.Inspect().CanApply && !c.Apply(out failure) && n.Changes.Count == 0 && s.Writes == 0, "native read failure is not clean absence");
                s.FailRead = true;
                Check(c.HasReceipt && c.HasUnfinishedRecovery && !c.Restore(out failure), "store read failure preserved");
            }
            {
                var n = new Native(); var s = new Store(); var c = Controller(n, s);
                n.Snapshot.Values[ClockRepairNative.UsePlatformClock] = 1; n.Snapshot.BootIdentity = null;
                Check(!c.Inspect().CanApply && !c.Apply(out failure) && n.Changes.Count == 0 && s.Writes == 0, "unknown boot identity cannot admit apply");
                s.Value = Receipt("applied", 1, ClockRepairNative.UsePlatformClock).Encode();
                n.Snapshot.Values.Clear();
                Check(c.Inspect().RestartRequired && c.Inspect().Summary.IndexOf("not yet verified", StringComparison.Ordinal) >= 0, "unknown reboot status must not claim reboot completed");
            }
            Check(!ClockRepairNative.Allowed(0x260000A5) && !ClockRepairNative.Allowed(0x250000F0), "dynamic tick and hypervisor are not allowed");
            Console.WriteLine("PASS LaboratoryClockRepairChecks assertions=" + assertions);
        }
    }
}
