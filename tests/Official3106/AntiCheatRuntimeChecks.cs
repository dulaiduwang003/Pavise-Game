using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // Compile against production sources with -main:PaviseApp.AntiCheatRuntimeChecks.
    // All native process access, topology, admission and durable storage are injected.
    internal static class AntiCheatRuntimeChecks
    {
        private static int checks;
        private static void Check(bool value, string message)
        { checks++; if (!value) throw new Exception(message); }

        private sealed class MemoryStore : IAntiCheatAffinityStore
        {
            internal string Text = "";
            internal int Saves;
            internal readonly List<string> Events;
            internal MemoryStore(List<string> events) { Events = events; }
            public bool Load(out string text) { text = Text; return true; }
            public bool Save(string text) { Saves++; Events.Add("save"); Text = text; return true; }
        }

        private sealed class SimulatedProcess
        {
            internal AntiCheatAffinityIdentity Identity;
            internal ushort Group;
            internal ulong Mask = 4095, Allowed = 4095;
            internal AntiCheatAffinityRead State = AntiCheatAffinityRead.Known;
            internal bool CpuSetsKnown = true, CpuSetsEmpty = true;
        }

        private sealed class MemoryPlatform : IAntiCheatAffinityPlatform, IAntiCheatAffinityRestorePlatform
        {
            internal long Clock;
            internal int Reads, Writes, Attempts, Restores;
            internal bool WriteAllowed = true;
            internal readonly Dictionary<int, SimulatedProcess> Processes = new Dictionary<int, SimulatedProcess>();
            internal readonly List<string> Events = new List<string>();
            public string DeviceIdentity { get { return "runtime-test-machine"; } }
            public long NowMilliseconds { get { return Clock; } }
            public AntiCheatAffinityObservation Read(AntiCheatAffinityIdentity identity)
            {
                Reads++;
                SimulatedProcess process;
                if (!Processes.TryGetValue(identity.ProcessId, out process))
                    return new AntiCheatAffinityObservation(AntiCheatAffinityRead.Gone, 0, 0, 0, "gone");
                AntiCheatAffinityRead state = process.Identity.Key == identity.Key
                    ? process.State : AntiCheatAffinityRead.DifferentProcess;
                return new AntiCheatAffinityObservation(state, process.Group, process.Mask, process.Allowed, state.ToString())
                    { CpuSetsKnown = process.CpuSetsKnown, CpuSetsEmpty = process.CpuSetsEmpty };
            }
            public bool Write(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target,
                out AntiCheatAffinityObservation observed)
            { return Change(identity, group, expected, target, true, out observed); }
            public bool Restore(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target,
                out AntiCheatAffinityObservation observed)
            { Restores++; return Change(identity, group, expected, target, false, out observed); }
            private bool Change(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, bool admission,
                out AntiCheatAffinityObservation observed)
            {
                Attempts++; observed = Read(identity);
                if (!WriteAllowed || observed.Read != AntiCheatAffinityRead.Known || observed.Group != group
                    || observed.Mask != expected || (target & ~observed.AllowedMask) != 0
                    || (admission && (!observed.CpuSetsKnown || !observed.CpuSetsEmpty))) return false;
                Writes++; Events.Add((admission ? "bind:" : "restore:") + target.ToString("X"));
                Processes[identity.ProcessId].Mask = target;
                observed = Read(identity); return true;
            }
            public void Forget(AntiCheatAffinityIdentity identity) { Events.Add("forget:" + identity.ProcessId); }
        }

        private sealed class Fixture
        {
            internal readonly MemoryPlatform Platform = new MemoryPlatform();
            internal readonly MemoryStore Store;
            internal readonly AntiCheatAffinityLease Lease;
            internal readonly AntiCheatAffinityRuntime Runtime;
            internal AntiCheatAffinitySnapshot Topology = Machine(6, 0, 0);
            internal AntiCheatAffinityGameContext Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0, 0);
            internal readonly AntiCheatAffinityTarget Target = new AntiCheatAffinityTarget {
                Pid = 201, Birth = 20100, Name = "EasyAntiCheat.exe", Group = "EAC" };
            internal readonly SimulatedProcess Process;
            internal bool Alive = true, PrepareOk = true, IdentifyOk = true, InspectOk = true;
            internal string AdmissionFailure;
            internal int Captures, Identifies, Inspections, Prepares, Admissions;
            internal Fixture()
            {
                Process = new SimulatedProcess {
                    Identity = new AntiCheatAffinityIdentity(201, 20100, @"C:\Test\EasyAntiCheat.exe") };
                Platform.Processes.Add(201, Process);
                Store = new MemoryStore(Platform.Events);
                Lease = new AntiCheatAffinityLease(Platform, Store);
                Runtime = new AntiCheatAffinityRuntime(Lease, Capture, Identify, Inspect, Prepare,
                    delegate(AntiCheatAffinityGameContext game) { return Alive && game.Pid == 99 && game.Birth == 9900; }, Admit,
                    delegate { return Platform.Clock; });
            }
            private AntiCheatAffinitySnapshot Capture()
            { Captures++; Platform.Events.Add("capture"); return Topology; }
            private bool Identify(int pid, long birth, string name, out AntiCheatAffinityIdentity identity, out string reason)
            {
                Identifies++; Platform.Events.Add("identify"); SimulatedProcess process;
                identity = null; reason = "identity-refused";
                if (!IdentifyOk || !Platform.Processes.TryGetValue(pid, out process)
                    || process.Identity.CreationTime != birth) return false;
                identity = process.Identity; reason = ""; return true;
            }
            private bool Inspect(AntiCheatAffinityIdentity identity, out ushort group, out ulong mask, out string reason)
            {
                Inspections++; Platform.Events.Add("inspect"); SimulatedProcess process = Platform.Processes[identity.ProcessId];
                group = process.Group; mask = process.Mask; reason = InspectOk ? "" : "inspect-refused";
                return InspectOk;
            }
            private bool Prepare(int pid, long birth, string name)
            { Prepares++; Platform.Events.Add("prepare"); return PrepareOk; }
            private string Admit(AntiCheatAffinityTarget target, AntiCheatAffinityGameContext game)
            { Admissions++; Platform.Events.Add("admit"); return AdmissionFailure; }
            internal void Tick()
            { Platform.Clock += 1000; Runtime.Tick(Game, new List<AntiCheatAffinityTarget> { Target }); }
            internal void Empty()
            { Platform.Clock += 1000; Runtime.Tick(Game, new List<AntiCheatAffinityTarget>()); }
        }

        private static AntiCheatAffinitySnapshot Machine(int physical, ushort group, int split)
        {
            AntiCheatAffinityCore[] cores = new AntiCheatAffinityCore[physical];
            for (int i = 0; i < physical; i++) cores[i] = new AntiCheatAffinityCore {
                Group = group, Mask = 3UL << (i * 2), Efficiency = 0, L3Id = split == 0 ? 0 : i / split,
                L3Bytes = 32 * 1024 * 1024 };
            return new AntiCheatAffinitySnapshot { Valid = true, CpuSetMetadata = true, Reason = "ok", Cores = cores };
        }

        private static void EntryAndHandoff()
        {
            Fixture f = new Fixture(); f.Game = null; f.Tick();
            Check(f.Platform.Writes == 0 && f.Prepares == 0 && f.Captures == 0, "no game causes no admission or writes");
            Check(f.Runtime.Summary("EAC") == "", "no game no bound display");
            f = new Fixture(); f.Tick();
            Check(f.Process.Mask == 0xF00 && f.Platform.Writes == 1, "9750H whole two-core auxiliary pool hard bound");
            Check(f.Lease.OriginalAffinity(f.Process.Identity) == 0xFFF, "original mask captured before hard binding");
            Check(f.Platform.Events.IndexOf("prepare") < f.Platform.Events.IndexOf("inspect"), "legacy handoff precedes inspecting original mask");
            Check(f.Platform.Events.IndexOf("admit") < f.Platform.Events.IndexOf("prepare"), "admission precedes any placement handoff");
            Check(f.Platform.Events.IndexOf("save") < f.Platform.Events.IndexOf("bind:F00"), "receipt persisted before first hard write");
            Check(f.Runtime.Summary("EAC").Contains("Hard bound 1"), "successful readback published");
            Check(f.Runtime.Details("EAC").Contains("CPU 8,9,10,11"), "actual whole core target published");
            for (int i = 0; i < 5; i++) f.Tick();
            Check(f.Platform.Writes == 1 && f.Prepares == 1 && f.Inspections == 1, "stable target polls without reapplying or recapturing owned mask");
            Check(f.Runtime.Stop() && f.Process.Mask == 0xFFF, "explicit stop restores baseline");

            f = new Fixture(); f.PrepareOk = false; f.Tick();
            Check(f.Platform.Writes == 0 && f.Inspections == 0 && f.Prepares == 1, "handoff failure blocks inspection and ownership");
            Check(f.Runtime.Details("EAC").Contains("Waiting for earlier placement recovery"), "handoff debt visible");
            f.PrepareOk = true; f.Tick();
            Check(f.Process.Mask == 0xF00 && f.Prepares == 2, "handoff retries when preceding owner recovers");
            f.Runtime.Stop();
        }

        private static void ExitRemovalAndIdentity()
        {
            Fixture f = new Fixture(); f.Tick(); f.Empty();
            Check(f.Process.Mask == 0xFFF && f.Store.Text == "" && f.Platform.Restores == 1, "target removed while game alive restores baseline");
            Check(f.Runtime.Summary("EAC") == "", "removed target leaves no stale bound summary");
            f = new Fixture(); f.Tick(); f.Game = null; f.Tick();
            Check(f.Process.Mask == 0xFFF && f.Store.Text == "", "game exit restores baseline");
            f = new Fixture(); f.Tick(); f.Alive = false; f.Tick();
            Check(f.Process.Mask == 0xFFF && f.Runtime.Summary("EAC") == "", "failed game birth identity releases hard binding");

            f = new Fixture(); f.Alive = false; f.Tick();
            Check(f.Platform.Writes == 0 && f.Identifies == 0, "invalid game never admits target");
            f = new Fixture(); f.Target.Pid = 99; f.Target.Birth = 9900; f.Tick();
            Check(f.Platform.Writes == 0 && f.Identifies == 0 && f.Prepares == 0, "renderer never admitted under anti-cheat name");

            f = new Fixture(); f.Tick(); f.Platform.WriteAllowed = false; f.Game = null; f.Tick();
            Check(f.Process.Mask == 0xF00 && f.Runtime.PendingRecovery && f.Store.Text.Length > 0, "failed game exit restoration retains durable debt");
            f.Platform.WriteAllowed = true; f.Tick();
            Check(f.Process.Mask == 0xFFF && !f.Runtime.PendingRecovery && f.Store.Text == "", "next stopped tick recovers pending baseline");

            f = new Fixture(); f.Tick();
            f.Process.Identity = new AntiCheatAffinityIdentity(201, 999999, @"C:\Other\Different.exe"); f.Process.Mask = 0x155;
            int writes = f.Platform.Writes; f.Empty();
            Check(f.Platform.Writes == writes && f.Process.Mask == 0x155 && f.Store.Text == "", "PID reuse never restores into a different process");
        }

        private static void LayoutChanges()
        {
            Fixture f = new Fixture(); f.Tick();
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0xF00, 0); f.Tick();
            Check(f.Process.Mask == 0xF0 && f.Platform.Writes == 3, "changed game protection releases then reselects full original affinity");
            Check(f.Lease.OriginalAffinity(f.Process.Identity) == 0xFFF, "reselection never mistakes old bound mask for baseline");
            Check(f.Inspections == 2 && f.Captures == 2, "layout change refreshes inspection and topology");
            Check(f.Runtime.Stop() && f.Process.Mask == 0xFFF, "changed protection still restores full original baseline");

            f = new Fixture(); f.Topology = Machine(16, 0, 8); f.Process.Mask = f.Process.Allowed = 0xFFFFFFFF;
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0, 0xFFFF); f.Tick();
            Check(f.Process.Mask == 0xFFFF0000, "explicit game cache domain routes to other domain");
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0, 0xFFFF0000); f.Tick();
            Check(f.Process.Mask == 0xFFFF && f.Platform.Writes == 3, "changed game cache domain releases then selects other domain");
            Check(f.Lease.OriginalAffinity(f.Process.Identity) == 0xFFFFFFFF, "domain reselection baseline remains unbound original");
            f.Runtime.Stop();

            f = new Fixture(); f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0xFFF, 0); f.Tick();
            Check(f.Platform.Writes == 0, "fully reserved physical cores not used");
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0, 0); f.Tick();
            Check(f.Process.Mask == 0xF00, "reservation relaxation reconsiders previous selection failure");
            f.Runtime.Stop();
        }

        private static void AdmissionAndAncestry()
        {
            Fixture f = new Fixture(); f.AdmissionFailure = "not-session-zero-service"; f.Tick();
            Check(f.Admissions == 1 && f.Prepares == 0 && f.Inspections == 0 && f.Platform.Reads == 0 && f.Platform.Writes == 0,
                "refused service admission cannot prepare inspect or ensure");
            for (int i = 0; i < 5; i++) f.Tick();
            Check(f.Admissions == 1 && f.Platform.Writes == 0, "admission refusal stays stopped without repeated probes");

            f = new Fixture(); f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0, 0, new HashSet<int> { 201 }); f.Tick();
            Check(f.Prepares == 0 && f.Platform.Writes == 0 && f.Identifies == 0, "known game ancestor excluded before ownership");
            f = new Fixture(); f.Tick();
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0, 0, new HashSet<int> { 201 }); f.Tick();
            Check(f.Process.Mask == 0xFFF && f.Store.Text == "", "new ancestor evidence releases already-bound target");
            Check(f.Runtime.Details("EAC").Contains("avoiding affinity inheritance"), "inheritance exclusion visible");
            int writes = f.Platform.Writes;
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0, 0); f.Tick();
            Check(f.Platform.Writes == writes && f.Process.Mask == 0xFFF, "inheritance risk stays halted for current game");
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 2, 0, 0, 0); f.Tick();
            Check(f.Process.Mask == 0xF00, "new game session resets inheritance halt");
            f.Runtime.Stop();

            f = new Fixture(); f.Tick(); f.Platform.WriteAllowed = false;
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0, 0, new HashSet<int> { 201 }); f.Tick();
            Check(f.Process.Mask == 0xF00 && f.Runtime.PendingRecovery && f.Store.Text.Length > 0
                && f.Lease.HasOwnership(f.Process.Identity),
                "ancestor discovery with refused restoration retains halted ownership debt");
            f.Platform.WriteAllowed = true;
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0, 0); f.Tick();
            Check(f.Process.Mask == 0xFFF && !f.Runtime.PendingRecovery && f.Store.Text == ""
                && !f.Lease.HasOwnership(f.Process.Identity),
                "halted restoration continues after ancestor evidence disappears and clears debt");
            int recoveredWrites = f.Platform.Writes; f.Tick();
            Check(f.Process.Mask == 0xFFF && f.Platform.Writes == recoveredWrites && recoveredWrites == 2
                && f.Prepares == 1 && f.Runtime.Details("EAC").Contains("avoiding affinity inheritance"),
                "recovered ancestor halt never reacquires hard affinity during current game");
            f.Runtime.Stop();
        }

        private static void ConflictAndGroups()
        {
            Fixture f = new Fixture(); f.Tick();
            for (int correction = 0; correction < 3; correction++)
            {
                f.Process.Mask = (ulong)(0xFFF - correction); f.Tick();
                Check(f.Process.Mask == 0xF00, "foreign hard affinity corrected within rate budget");
            }
            f.Process.Mask = 0xFFF; f.Tick();
            Check(f.Process.Mask == 0xFFF && f.Runtime.Summary("EAC").Contains("Affinity conflict"), "fourth drift yields and publishes conflict");
            int writes = f.Platform.Writes;
            for (int i = 0; i < 90; i++) f.Tick();
            Check(f.Platform.Writes == writes && f.Process.Mask == 0xFFF && f.Store.Text == "", "conflict remains halted after sixty-second window passes");
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 3, 0); f.Tick();
            Check(f.Platform.Writes == writes, "layout change cannot reset repeated-conflict halt");
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 2, 0, 0, 0); f.Tick();
            Check(f.Process.Mask == 0xF00 && f.Platform.Writes == writes + 1, "new game session permits fresh hard binding");
            f.Runtime.Stop();

            f = new Fixture(); f.Topology = Machine(6, 1, 0); f.Process.Group = 1;
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 0, 0xFFF, 0xFFF); f.Tick();
            Check(f.Process.Mask == 0xF00, "game group-zero masks never transferred into target group one");
            Check(f.Runtime.Details("EAC").Contains("G1 CPU 8,9,10,11"), "group one target displayed explicitly");
            f.Runtime.Stop();

            f = new Fixture(); f.Topology = Machine(6, 1, 0); f.Process.Group = 1;
            f.Game = new AntiCheatAffinityGameContext(99, 9900, 1, 1, 0xF00, 0); f.Tick();
            Check(f.Process.Mask == 0xF0, "same group-one game reservations protect complete target physical cores");
            f.Runtime.Stop();

            f = new Fixture(); f.InspectOk = false; f.Tick();
            Check(f.Platform.Writes == 0 && f.Inspections == 1, "unknown group affinity never guessed");
            f.Tick(); Check(f.Inspections == 1, "inspection access failure stays stopped");
            f = new Fixture(); f.Process.CpuSetsEmpty = false; f.Tick();
            Check(f.Platform.Writes == 0 && f.Store.Text == "", "preexisting CPU Set restriction is read-only admission veto");
        }

        private static void PeriodicTopologyRefresh()
        {
            Fixture f = new Fixture();
            foreach (AntiCheatAffinityCore core in f.Topology.Cores) core.Parked = true;
            f.Tick();
            Check(f.Platform.Writes == 0 && f.Captures == 1 && f.Inspections == 1,
                "temporarily parked candidate pool rejected on initial snapshot");
            f.Topology = Machine(6, 0, 0); f.Platform.Clock = 29999; f.Tick();
            Check(f.Platform.Writes == 0 && f.Captures == 1 && f.Inspections == 1,
                "selection failure does not hammer topology before thirty seconds");
            f.Platform.Clock = 30000; f.Tick();
            Check(f.Process.Mask == 0xF00 && f.Platform.Writes == 1 && f.Captures == 2 && f.Inspections == 2,
                "temporary selection failure retried at exact thirty-second boundary");
            Check(f.Lease.OriginalAffinity(f.Process.Identity) == 0xFFF,
                "refreshed selection still snapshots full initial affinity");
            f.Runtime.Stop();

            f = new Fixture(); f.IdentifyOk = false; f.Tick();
            f.IdentifyOk = true; f.Platform.Clock += 30000; f.Tick();
            Check(f.Captures == 2 && f.Identifies == 1 && f.Prepares == 0 && f.Platform.Writes == 0,
                "identity failure remains latched across topology refresh");

            f = new Fixture(); f.Tick();
            f.Topology = Machine(6, 0, 0); f.Topology.Cores[4].Allocated = true;
            f.Platform.Clock += 30000; f.Tick();
            Check(f.Process.Mask == 0xCC0 && f.Platform.Writes == 3 && f.Platform.Restores == 1,
                "new exclusive allocation releases old pool and selects other complete cores");
            Check(f.Lease.OriginalAffinity(f.Process.Identity) == 0xFFF && f.Inspections == 2,
                "allocation refresh inspects restored original affinity rather than owned mask");
            f.Runtime.Stop();

            f = new Fixture(); f.Tick();
            f.Topology = Machine(6, 0, 0);
            f.Topology.Cores[4].Parked = f.Topology.Cores[5].Parked = true;
            f.Platform.Clock += 30000; f.Tick();
            Check(f.Process.Mask == 0xF00 && f.Platform.Writes == 1 && f.Platform.Restores == 0 && f.Inspections == 1,
                "parking already admitted idle cores does not churn hard placement");
            Check(f.Runtime.Summary("EAC").Contains("Hard bound 1"),
                "stable admitted parked pool still verified by affinity readback");
            f.Runtime.Stop();

            f = new Fixture(); f.Tick();
            f.Topology = new AntiCheatAffinitySnapshot { Valid = false, Reason = "invalid-refreshed-topology" };
            f.Platform.Clock += 30000; f.Tick();
            Check(f.Process.Mask == 0xFFF && f.Platform.Writes == 2 && f.Platform.Restores == 1 && f.Store.Text == "",
                "invalid refreshed snapshot releases ownership before refusing new binding");
            Check(!f.Runtime.Summary("EAC").Contains("Hard bound"),
                "invalid refreshed topology cannot retain stale bound display");
            int reads = f.Platform.Reads, writes = f.Platform.Writes;
            f.Tick();
            Check(f.Platform.Reads == reads && f.Platform.Writes == writes,
                "invalid snapshot failure never falls through to lease Ensure");

            f = new Fixture(); f.Tick();
            AntiCheatAffinitySnapshot reduced = Machine(6, 0, 0);
            AntiCheatAffinityCore[] remaining = new AntiCheatAffinityCore[5];
            Array.Copy(reduced.Cores, remaining, 5); reduced.Cores = remaining; f.Topology = reduced;
            f.Platform.Clock += 30000; f.Tick();
            Check(f.Process.Mask == 0xFFF && f.Platform.Restores == 1 && f.Store.Text == "",
                "missing bound physical core releases before considering reduced topology");
        }

        public static int Main()
        {
            try
            {
                Logger.WritesEnabled = false; Lang.Cur = 1;
                EntryAndHandoff(); ExitRemovalAndIdentity(); LayoutChanges(); AdmissionAndAncestry(); ConflictAndGroups(); PeriodicTopologyRefresh();
                Console.WriteLine("PASS anti-cheat runtime assertions=" + checks + " native_process_writes=0 product_workers=false");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
