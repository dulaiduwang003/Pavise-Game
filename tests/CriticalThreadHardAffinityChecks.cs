#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class CriticalThreadHardAffinityChecks
    {
        private static int checks;
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        private static void Refused(Action action, string message)
        { bool refused = false; try { action(); } catch (ArgumentException) { refused = true; } catch (InvalidOperationException) { refused = true; } catch (NotSupportedException) { refused = true; } catch (InvalidDataException) { refused = true; } catch (IOException) { refused = true; } Check(refused, message); }
        private sealed class Use : IDisposable
        {
            private readonly Gate gate;
            internal Use(Gate gate) { this.gate = gate; gate.Depth++; }
            public void Dispose() { gate.Depth--; }
        }
        private sealed class Gate : IHardThreadGate
        {
            internal bool Active = true, Recovery = true, Workers = true;
            internal int Depth, ActiveChecks, RecoveryChecks;
            internal HardThreadTarget Original = new HardThreadTarget(Guid.NewGuid(), 410, 12345, @"C:\PaviseFake\game.exe", 255);
            public HardThreadTarget Target { get { return Original; } }
            public bool AllowWorkers { get { return Workers; } }
            public IDisposable EnterUse() { return new Use(this); }
            public void RequireActive() { Check(Depth > 0, "forward outside the original coordination gate"); ActiveChecks++; if (!Active) throw new InvalidOperationException("original forward lease revoked"); }
            public void RequireRecovery() { Check(Depth > 0, "recovery outside the original coordination gate"); RecoveryChecks++; if (!Recovery) throw new InvalidOperationException("original recovery authority unknown"); }
        }
        private sealed class Store : IHardThreadStore
        {
            internal HardThreadJournal Saved;
            internal Action<HardThreadJournal> Before, After;
            internal int Saves;
            internal int Disposals, DisposeFailures;
            public HardThreadJournal Load(HardThreadTarget target)
            {
                if (Saved == null) return new HardThreadJournal { Target = target };
                if (!Saved.Target.Same(target)) throw new InvalidDataException("foreign original source");
                return Saved.Clone();
            }
            public void Save(long expected, HardThreadJournal journal)
            {
                Check((Saved == null ? 0 : Saved.Revision) == expected && journal.Revision == expected + 1, "source revision changed");
                Saves++; if (Before != null) Before(journal); Saved = journal.Clone(); if (After != null) After(journal);
            }
            public void Dispose() { Disposals++; if (DisposeFailures > 0) { DisposeFailures--; throw new IOException("mock ledger close failure"); } }
        }
        private sealed class Native : IHardThreadNative, IHardThreadTransientReads
        {
            internal readonly Gate Gate;
            internal readonly Store Store;
            internal readonly Dictionary<string, ulong> Masks = new Dictionary<string, ulong>();
            internal readonly HashSet<string> RetainedReads = new HashSet<string>();
            internal HardThreadRead ProcessRead = HardThreadRead.Known, ThreadRead = HardThreadRead.Known;
            internal bool ProcessSetsEmpty = true, ThreadSetsEmpty = true, FailWrite;
            internal int Groups = 1, Writes, Disposals, DisposeFailures;
            internal ulong ProcessMask = 255;
            internal ulong[] Cores = { 3, 12, 48, 192 };
            internal Action<HardThreadIdentity, ulong> BeforeSet, AfterSet;
            internal Func<HardThreadIdentity, HardThreadObserved> ReadOverride;
            internal Native(Gate gate, Store store) { Gate = gate; Store = store; }
            public HardThreadMachine ReadMachine()
            { return new HardThreadMachine { Read = ProcessRead, GroupCount = Groups, Group = 0, ProcessMask = ProcessMask, CpuSetsEmpty = ProcessSetsEmpty, PhysicalCores = (ulong[])Cores.Clone() }; }
            public HardThreadObserved ReadThread(HardThreadIdentity thread)
            {
                RetainedReads.Add(thread.Key);
                if (ReadOverride != null) return ReadOverride(thread);
                ulong mask; if (!Masks.TryGetValue(thread.Key, out mask)) mask = ProcessMask;
                return new HardThreadObserved { Read = ThreadRead, Group = 0, Mask = mask, CpuSetsEmpty = ThreadSetsEmpty };
            }
            public bool Set(HardThreadIdentity thread, ushort group, ulong mask, out ushort previousGroup, out ulong previous)
            {
                Check(Gate.Depth > 0, "native set outside original gate");
                HardThreadReceipt receipt = null;
                if (Store.Saved != null) foreach (var row in Store.Saved.Rows) if (!row.Terminal && row.Thread.Key == thread.Key) receipt = row;
                // A write-back over an external rewrite is durable by the owned receipt itself: only its
                // own bound value, with the original already recorded.
                Check(receipt != null && (receipt.Phase == HardThreadPhase.ApplyIntent && !receipt.PreviousKnown
                    || receipt.Phase == HardThreadPhase.RestoreIntent && !receipt.RestorePreviousKnown
                    || receipt.Phase == HardThreadPhase.Applied && receipt.PreviousKnown && group == receipt.Group && mask == receipt.Applied),
                    "native write without durable intent");
                Writes++; previousGroup = 0; previous = 0;
                if (FailWrite || mask == 0 || group != 0) return false;
                if (BeforeSet != null) BeforeSet(thread, mask);
                if (!Masks.TryGetValue(thread.Key, out previous)) previous = ProcessMask;
                Masks[thread.Key] = mask;
                if (AfterSet != null) AfterSet(thread, mask);
                return true;
            }
            public bool ReleaseUnowned(string[] retainedIdentities)
            { RetainedReads.IntersectWith(retainedIdentities); return true; }
            public void Dispose() { Disposals++; if (DisposeFailures > 0) { DisposeFailures--; throw new IOException("mock backend close failure"); } RetainedReads.Clear(); }
        }
        private sealed class Fixture : IDisposable
        {
            internal readonly Gate Gate = new Gate();
            internal readonly Store Store = new Store();
            internal readonly Native Native;
            internal readonly CriticalThreadHardAffinitySession Session;
            internal readonly HardThreadIdentity First = new HardThreadIdentity(51, 501), Second = new HardThreadIdentity(52, 502), Worker = new HardThreadIdentity(53, 503);
            internal int ExpectedNativeDisposals = 1, ExpectedStoreDisposals = 1;
            internal Fixture(IHardThreadGuardian guardian = null) { Native = new Native(Gate, Store); Session = CriticalThreadHardAffinitySession.OpenFake(Gate, Native, Store, guardian); }
            internal HardThreadPlan Plan() { return Session.CreatePlan(new[] { new HardThreadChoice(First, 1), new HardThreadChoice(Second, 4) }); }
            internal HardThreadPlan One() { return Session.CreatePlan(new[] { new HardThreadChoice(First, 1) }); }
            public void Dispose() { Session.Dispose(); Check(Gate.Depth == 0 && Native.Disposals == ExpectedNativeDisposals && Store.Disposals == ExpectedStoreDisposals, "original gate or handles leaked"); }
        }
        private sealed class Guardian : IHardThreadGuardian
        {
            internal bool Lost;
            internal int Disposals;
            internal int DisposeFailures;
            private bool closed;
            internal Action Callback;
            public HardThreadGuardianActor Actor { get { return new HardThreadGuardianActor(800, 900, @"C:\PaviseFake\guard.exe", new string('a', 64)); } }
            public void RequireForward() { if (Lost) throw new InvalidOperationException("original guardian is unavailable"); }
            public void OnLost(Action callback) { Callback = callback; }
            public bool ResourcesClosed { get { return closed; } }
            public void NoteSettled(HardThreadSettledSource original) { }
            public void Dispose() { Disposals++; if (DisposeFailures > 0) { DisposeFailures--; throw new IOException("mock guardian closure failure"); } closed = true; }
            internal void Lose() { Lost = true; if (Callback != null) Callback(); }
        }
        private static HardThreadObserved Observed(Fixture f, HardThreadIdentity thread, HardThreadRead read)
        {
            ulong mask; if (!f.Native.Masks.TryGetValue(thread.Key, out mask)) mask = 255;
            return new HardThreadObserved { Read = read, Group = 0, Mask = mask, CpuSetsEmpty = true };
        }
        private static void OwnershipAndChurn()
        {
            using (var f = new Fixture()) {
                Check(f.Session.ActiveReceipts.Length == 0, "an unmodified session published ownership facts");
                Check(f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "ownership fixture did not bind");
                var facts = f.Session.ActiveReceipts;
                Check(facts.Length == 1 && facts[0].Thread.Key == f.First.Key && facts[0].Group == 0 && facts[0].Mask == 1 && facts[0].VerifiedUtcTicks > 0,
                    "ownership facts were not backed by a successful native readback");
                long confirmed = facts[0].VerifiedUtcTicks; facts[0] = null;
                Check(f.Session.ActiveReceipts[0].VerifiedUtcTicks == confirmed, "caller array mutation or getter refreshed ownership facts");
                int writes = f.Native.Writes, saves = f.Store.Saves;
                f.Native.ReadOverride = delegate(HardThreadIdentity thread) { return Observed(f, thread, thread.Key == f.Worker.Key ? HardThreadRead.Unknown : HardThreadRead.Known); };
                Check(f.Session.AdmitNewThreads(new[] { f.Worker }, HardThreadCoverage.Complete), "new unknown worker rolled back the active plan");
                Check(f.Native.Writes == writes && f.Store.Saves == saves && f.Native.Masks[f.First.Key] == 1,
                    "new unknown worker caused native writes or recovery journal churn");
                Check(f.Session.Status == HardThreadStatus.CoveragePending && f.Session.LastFailureKind == HardThreadFailureKind.ObservationUnavailable
                    && !f.Session.LastOperationWriteIntent && !f.Session.LastOperationNativeAttempt, "new worker observation was presented as a write fault or complete coverage");
                Check(f.Session.ActiveReceipts.Length == 1 && f.Session.ActiveReceipts[0].Thread.Key == f.First.Key,
                    "unread new worker was published as successful ownership");
                Check(f.Native.RetainedReads.Count == 1 && f.Native.RetainedReads.Contains(f.First.Key),
                    "unmodified unread workers accumulated retained native query handles");
                f.Native.ReadOverride = delegate(HardThreadIdentity thread) { return Observed(f, thread, thread.Key == f.Worker.Key ? HardThreadRead.Gone : HardThreadRead.Known); };
                Check(f.Session.AdmitNewThreads(new[] { f.Worker }, HardThreadCoverage.Complete) && f.Native.Writes == writes && f.Store.Saves == saves,
                    "new exited worker caused rollback or journal creation");
                var excessive = new List<HardThreadIdentity>();
                for (int i = 0; i < 9; i++) excessive.Add(new HardThreadIdentity(200 + i, 2000 + i));
                Check(!f.Session.AdmitNewThreads(excessive.ToArray(), HardThreadCoverage.Partial)
                    && f.Session.LastFailureKind == HardThreadFailureKind.InvalidRequest && f.Native.Writes == writes
                    && f.Store.Saves == saves && f.Native.Masks[f.First.Key] == 1 && f.Session.ActiveReceipts.Length == 1,
                    "invalid worker budget restored existing owned threads");
                f.Native.ReadOverride = null;
                Check(f.Session.Restore() && f.Session.ActiveReceipts.Length == 0, "restored threads retained active ownership facts");
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "owned unknown fixture failed");
                int writes = f.Native.Writes, saves = f.Store.Saves;
                f.Native.ThreadRead = HardThreadRead.Unknown;
                Check(!f.Session.AdmitNewThreads(new HardThreadIdentity[0], HardThreadCoverage.Complete)
                    && f.Session.LastFailureKind == HardThreadFailureKind.RecoveryPending && f.Session.Status == HardThreadStatus.RecoveryPending,
                    "unread owned thread was not retained as recovery pending");
                Check(f.Native.Writes == writes && f.Store.Saves == saves && !f.Store.Saved.Rows[0].Terminal && f.Session.ActiveReceipts.Length == 0,
                    "Unknown owned thread became Gone or retained a verified active fact");
                Check(f.Native.RetainedReads.Contains(f.First.Key), "unknown owned thread lost its retained original handle");
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "post intent worker fixture failed");
                int writes = f.Native.Writes;
                f.Store.After = delegate(HardThreadJournal journal) {
                    var row = journal.Rows[journal.Rows.Count - 1];
                    if (row.Thread.Key == f.Worker.Key && row.Phase == HardThreadPhase.ApplyIntent)
                        f.Native.ReadOverride = delegate(HardThreadIdentity thread) { return Observed(f, thread, thread.Key == f.Worker.Key ? HardThreadRead.Unknown : HardThreadRead.Known); };
                };
                Check(f.Session.AdmitNewThreads(new[] { f.Worker }, HardThreadCoverage.Complete) && f.Native.Writes == writes
                    && f.Native.Masks[f.First.Key] == 1 && f.Store.Saved.Rows[1].Phase == HardThreadPhase.NotApplied,
                    "unmodified worker disappearing after intent restored candidate bindings");
                Check(f.Session.LastOperationWriteIntent && !f.Session.LastOperationNativeAttempt && f.Session.ActiveReceipts.Length == 1,
                    "an unwritten worker intent was counted as a native attempt or owned worker");
            }
            using (var f = new Fixture()) {
                f.Native.FailWrite = true;
                Check(!f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete)
                    && f.Session.LastFailureKind == HardThreadFailureKind.WriteOrDurabilityFault && f.Session.LastOperationWriteIntent
                    && f.Session.LastOperationNativeAttempt && f.Session.ActiveReceipts.Length == 0,
                    "a native write fault was mislabeled as normal thread churn");
            }
            using (var f = new Fixture()) {
                var secondWorker = new HardThreadIdentity(88, 888);
                Check(f.Session.Apply(f.One(), new[] { f.First, f.Worker, secondWorker }, HardThreadCoverage.Complete), "worker exit fixture failed");
                int writes = f.Native.Writes;
                f.Native.ReadOverride = delegate(HardThreadIdentity thread) { return Observed(f, thread, thread.Key == f.First.Key ? HardThreadRead.Known : HardThreadRead.Gone); };
                Check(f.Session.AdmitNewThreads(new HardThreadIdentity[0], HardThreadCoverage.Complete)
                    && f.Store.Saved.Rows[1].Phase == HardThreadPhase.Gone && f.Store.Saved.Rows[2].Phase == HardThreadPhase.Gone,
                    "journal replacement discarded a later exact worker exit");
                Check(f.Native.Writes == writes && f.Session.ActiveReceipts.Length == 1 && f.Native.Masks[f.First.Key] == 1,
                    "exact worker exits restored candidate ownership");
            }
            // Pavise leads: an owned mask someone else rewrote is written back on the next audit, the
            // receipt is untouched and the session restores the original at its end.
            using (var f = new Fixture()) {
                var secondWorker = new HardThreadIdentity(88, 888);
                Check(f.Session.Apply(f.One(), new[] { f.First, f.Worker, secondWorker }, HardThreadCoverage.Complete), "external change fixture failed");
                ulong workers = f.Native.Masks[f.Worker.Key]; int writes = f.Native.Writes, saves = f.Store.Saves;
                long revision = f.Store.Saved.Revision;
                f.Native.Masks[f.Worker.Key] = f.Native.ProcessMask;
                Check(f.Session.AdmitNewThreads(new HardThreadIdentity[0], HardThreadCoverage.Complete)
                    && f.Native.Writes == writes + 1 && f.Native.Masks[f.Worker.Key] == workers && f.Native.Masks[f.First.Key] == 1,
                    "an externally rewritten worker was not written back: " + f.Session.Detail);
                Check(f.Session.LastReassertion != null && f.Session.LastReassertion.Contains("worker TID 53 applied=0x")
                    && f.Session.LastReassertion.Contains("(process mask)") && f.Session.LastReassertion.Contains("1/3 owned threads changed")
                    && f.Session.LastReassertion.EndsWith("written back 1", StringComparison.Ordinal),
                    "a write-back did not name its thread, role and scope: " + f.Session.LastReassertion);
                Check(f.Store.Saves == saves && f.Store.Saved.Revision == revision && f.Store.Saved.Rows[1].Phase == HardThreadPhase.Applied
                    && f.Store.Saved.Rows[1].Previous == f.Native.ProcessMask && f.Session.ActiveReceipts.Length == 3,
                    "a write-back rewrote the receipt or dropped ownership");
                Check(f.Session.AdmitNewThreads(new HardThreadIdentity[0], HardThreadCoverage.Complete) && f.Native.Writes == writes + 1
                    && f.Session.LastReassertion == null, "an unchanged audit wrote again or kept a stale write-back note");
                Check(f.Session.Restore() && f.Native.Masks[f.Worker.Key] == f.Native.ProcessMask && f.Native.Masks[f.First.Key] == f.Native.ProcessMask,
                    "a written-back thread did not restore its original");
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First, f.Worker }, HardThreadCoverage.Complete), "process reset fixture failed");
                int writes = f.Native.Writes;
                f.Native.Masks[f.First.Key] = f.Native.ProcessMask; f.Native.Masks[f.Worker.Key] = f.Native.ProcessMask;
                Check(f.Session.AdmitNewThreads(new HardThreadIdentity[0], HardThreadCoverage.Complete) && f.Native.Writes == writes + 2
                    && f.Native.Masks[f.First.Key] == 1 && f.Session.LastReassertion.Contains("candidate TID 51")
                    && f.Session.LastReassertion.Contains("2/2 owned threads changed") && f.Session.LastReassertion.EndsWith("written back 2", StringComparison.Ordinal),
                    "a process-wide reset was not written back as one event: " + f.Session.LastReassertion);
            }
            // What is not overwritten: thread CPU Sets, a changed process scope, a refused write and a
            // write that does not read back. Each ends the binding and recovery keeps external values.
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First, f.Worker }, HardThreadCoverage.Complete), "external CPU Sets fixture failed");
                int writes = f.Native.Writes;
                f.Native.ReadOverride = delegate(HardThreadIdentity thread) {
                    var seen = Observed(f, thread, HardThreadRead.Known); if (thread.Key == f.Worker.Key) seen.CpuSetsEmpty = false; return seen; };
                Check(!f.Session.AdmitNewThreads(new HardThreadIdentity[0], HardThreadCoverage.Complete)
                    && f.Session.LastFailureKind == HardThreadFailureKind.ObservationUnavailable && f.Session.Detail.Contains("thread CPU Sets assigned")
                    && f.Native.Masks[f.First.Key] == f.Native.ProcessMask, "external thread CPU Sets were overwritten or kept the binding: " + f.Session.Detail);
                Check(f.Native.Writes == writes + 1, "external thread CPU Sets drew a write-back");
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First, f.Worker }, HardThreadCoverage.Complete), "process scope fixture failed");
                int writes = f.Native.Writes;
                f.Native.ProcessMask = 127; f.Native.Masks[f.Worker.Key] = 127;
                Check(!f.Session.AdmitNewThreads(new HardThreadIdentity[0], HardThreadCoverage.Complete) && f.Session.Detail.Contains("process scope changed")
                    && f.Native.Writes == writes && f.Native.Masks[f.Worker.Key] == 127, "a changed process scope drew a write: " + f.Session.Detail);
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First, f.Worker }, HardThreadCoverage.Complete), "refused write-back fixture failed");
                f.Native.Masks[f.Worker.Key] = f.Native.ProcessMask; f.Native.FailWrite = true;
                Check(!f.Session.AdmitNewThreads(new HardThreadIdentity[0], HardThreadCoverage.Complete)
                    && f.Session.LastFailureKind == HardThreadFailureKind.ObservationUnavailable && f.Session.Detail.Contains("write-back refused"),
                    "a refused write-back kept the binding or was reported as a durability fault: " + f.Session.Detail);
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First, f.Worker }, HardThreadCoverage.Complete), "contested write-back fixture failed");
                f.Native.Masks[f.Worker.Key] = f.Native.ProcessMask;
                f.Native.AfterSet = delegate(HardThreadIdentity thread, ulong mask) { if (thread.Key == f.Worker.Key) f.Native.Masks[thread.Key] = 7; };
                Check(!f.Session.AdmitNewThreads(new HardThreadIdentity[0], HardThreadCoverage.Complete)
                    && f.Session.LastFailureKind == HardThreadFailureKind.ObservationUnavailable && f.Session.Detail.Contains("write-back readback unconfirmed")
                    && f.Native.Masks[f.Worker.Key] == 7, "a contested write-back kept the binding or overwrote the external value on recovery: " + f.Session.Detail);
            }
        }
        private static void GuardianHealth()
        {
            var guardian = new Guardian();
            using (var f = new Fixture(guardian)) {
                var plan = f.One(); guardian.Lost = true;
                Check(!f.Session.Apply(plan, new[] { f.First }, HardThreadCoverage.Complete)
                    && f.Native.Writes == 0 && !f.Session.LastOperationWriteIntent && f.Session.LastFailureKind == HardThreadFailureKind.AuthorityUnavailable,
                    "dead original guardian permitted forward intent or native write");
            }
            guardian = new Guardian();
            using (var f = new Fixture(guardian)) {
                Check(f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "guardian loss fixture failed");
                guardian.Lose();
                Check(f.Native.Masks[f.First.Key] == 255 && f.Session.ActiveReceipts.Length == 0,
                    "guardian loss failed to recover original ownership");
                int writes = f.Native.Writes;
                f.Session.Dispose(); guardian.Lose();
                Check(f.Native.Writes == writes && guardian.Disposals == 1 && f.Native.Disposals == 1,
                    "disposed guardian callback performed native work or double closed resources");
            }
        }
        private sealed class CleanupFiles : IHardThreadCleanupFiles
        {
            internal HardThreadRead Child = HardThreadRead.Gone, Parent = HardThreadRead.Known;
            internal bool SpawnSucceeded = true, Terminal = true, Source = true, FileUnchanged = true, Certain = true;
            internal HardThreadCleanupPart? Failure;
            internal readonly int[] Deletes = new int[6], HeldQueries = new int[6];
            internal readonly bool[] Held = new bool[6];
            public bool OriginalSourceReady { get { return HardThreadCleanupPolicy.ChildSettled(SpawnSucceeded, Child) && Parent == HardThreadRead.Known && Terminal && Source && FileUnchanged; } }
            public bool ClosureCertain { get { return Certain; } }
            public bool DeleteExact(HardThreadCleanupPart part) { Deletes[(int)part]++; return Failure != part && !Held[(int)part]; }
            public bool HeldElsewhere(HardThreadCleanupPart part) { HeldQueries[(int)part]++; return Held[(int)part]; }
        }
        // An anti-cheat scanner keeping the guardian image open must not keep a settled session in debt.
        private static void SettledLeftovers()
        {
            var image = new CleanupFiles(); image.Held[(int)HardThreadCleanupPart.Executable] = true;
            var imageState = new HardThreadCleanupState();
            Check(imageState.Step(image), "a guardian image held by another process kept the settled session open");
            foreach (HardThreadCleanupPart part in new[] { HardThreadCleanupPart.Ledger, HardThreadCleanupPart.SessionLock, HardThreadCleanupPart.Configuration, HardThreadCleanupPart.Registration })
                Check(imageState.Removed(part) && image.Deletes[(int)part] == 1, "an unheld part was not removed beside a held image: " + part);
            Check(imageState.Deferred(HardThreadCleanupPart.Executable) && !imageState.Removed(HardThreadCleanupPart.Executable),
                "the held image was reported removed instead of deferred");
            Check(imageState.Deferred(HardThreadCleanupPart.Directory) && image.Deletes[(int)HardThreadCleanupPart.Directory] == 0,
                "the directory holding a deferred image was deleted or reported removed");
            int[] before = (int[])image.Deletes.Clone();
            Check(imageState.Step(image) && System.Linq.Enumerable.SequenceEqual(image.Deletes, before), "a completed cleanup touched deferred leftovers again");
            foreach (HardThreadCleanupPart settling in new[] { HardThreadCleanupPart.Ledger, HardThreadCleanupPart.SessionLock }) {
                var files = new CleanupFiles(); files.Held[(int)settling] = true; var state = new HardThreadCleanupState();
                Check(!state.Step(files) && !state.Removed(settling) && !state.Deferred(settling) && files.HeldQueries[(int)settling] == 0,
                    "a held settling part was deferred: " + settling);
                for (int i = (int)settling + 1; i < files.Deletes.Length; i++) Check(files.Deletes[i] == 0, "cleanup continued past a held settling part: " + settling);
            }
            var both = new CleanupFiles(); both.Held[(int)HardThreadCleanupPart.Configuration] = true; both.Held[(int)HardThreadCleanupPart.Registration] = true;
            var bothState = new HardThreadCleanupState();
            Check(bothState.Step(both) && bothState.Removed(HardThreadCleanupPart.Executable) && bothState.Deferred(HardThreadCleanupPart.Configuration)
                && bothState.Deferred(HardThreadCleanupPart.Registration) && bothState.Deferred(HardThreadCleanupPart.Directory), "several held leftovers were not all deferred");
            var heldAndFailed = new CleanupFiles { Failure = HardThreadCleanupPart.Registration }; heldAndFailed.Held[(int)HardThreadCleanupPart.Executable] = true;
            var failedState = new HardThreadCleanupState();
            Check(!failedState.Step(heldAndFailed) && failedState.Deferred(HardThreadCleanupPart.Executable) && !failedState.Removed(HardThreadCleanupPart.Registration),
                "an unexplained deletion fault beside a held image was accepted");
            heldAndFailed.Failure = null;
            Check(failedState.Step(heldAndFailed) && heldAndFailed.Deletes[(int)HardThreadCleanupPart.Executable] == 1,
                "cleanup could not resume after a fault beside a deferred image, or retried the deferred image");
            var uncertain = new CleanupFiles { Certain = false }; uncertain.Held[(int)HardThreadCleanupPart.Executable] = true;
            Check(!new HardThreadCleanupState().Step(uncertain), "deferral hid an unconfirmed native resource closure");
        }
        private static void ResourceClosure()
        {
            foreach (string fault in new[] { "native", "store", "guardian" }) {
                var guard = new Guardian();
                using (var f = new Fixture(guard)) {
                    Check(f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete) && f.Session.Restore(), "closure fault fixture failed");
                    if (fault == "native") { f.Native.DisposeFailures = 1; f.ExpectedNativeDisposals = 2; }
                    if (fault == "store") { f.Store.DisposeFailures = 1; f.ExpectedStoreDisposals = 2; }
                    if (fault == "guardian") guard.DisposeFailures = 1;
                    bool failed = false;
                    try { f.Session.Dispose(); } catch (AggregateException) { failed = true; }
                    Check(failed && !f.Session.ResourcesClosed && f.Native.Disposals == 1 && f.Store.Disposals == 1,
                        "a resource closure failure skipped independent resources or falsely completed");
                    Check(fault != "store" || guard.Disposals == 0, "guardian was detached before original ledger handles closed");
                    Refused(delegate { f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete); }, "partially disposed session reopened forward authority");
                    f.Session.Dispose();
                    bool completed = f.Session.ResourcesClosed && f.Native.Disposals == f.ExpectedNativeDisposals && f.Store.Disposals == f.ExpectedStoreDisposals
                        && guard.Disposals == (fault == "guardian" ? 2 : 1);
                    f.ExpectedNativeDisposals = f.Native.Disposals; f.ExpectedStoreDisposals = f.Store.Disposals;
                    Check(completed, "uncompleted resource closure could not be retried");
                }
            }
            foreach (string refusal in new[] { "child-alive", "child-unknown", "parent-unknown", "pending", "scope-changed", "file-changed" }) {
                var files = new CleanupFiles(); var state = new HardThreadCleanupState();
                if (refusal == "child-alive") files.Child = HardThreadRead.Known;
                if (refusal == "child-unknown") files.Child = HardThreadRead.Unknown;
                if (refusal == "parent-unknown") files.Parent = HardThreadRead.Unknown;
                if (refusal == "pending") files.Terminal = false;
                if (refusal == "scope-changed") files.Source = false;
                if (refusal == "file-changed") files.FileUnchanged = false;
                Check(!state.Step(files), "unverified original cleanup was accepted: " + refusal);
                foreach (int count in files.Deletes) Check(count == 0, "cleanup deleted data without original settled child proof: " + refusal);
            }
            foreach (HardThreadCleanupPart failure in Enum.GetValues(typeof(HardThreadCleanupPart))) {
                var files = new CleanupFiles { Failure = failure }; var state = new HardThreadCleanupState();
                Check(!state.Step(files) && !state.Removed(failure), "failed original deletion was reported complete");
                for (int i = 0; i < (int)failure; i++) Check(files.Deletes[i] == 1 && state.Removed((HardThreadCleanupPart)i), "completed deletion evidence was lost");
                files.Child = HardThreadRead.Unknown;
                int attempts = files.Deletes[(int)failure];
                Check(!state.Step(files) && files.Deletes[(int)failure] == attempts, "cleanup retried while original child state became unknown");
                files.Child = HardThreadRead.Gone; files.Failure = null;
                Check(state.Step(files), "original terminal cleanup could not resume after a deletion fault");
                for (int i = 0; i < files.Deletes.Length; i++) Check(files.Deletes[i] == (i == (int)failure ? 2 : 1), "cleanup repeated an already confirmed deletion");
            }
            var uncertain = new CleanupFiles { Certain = false }; var uncertainState = new HardThreadCleanupState();
            Check(!uncertainState.Step(uncertain), "unconfirmed native resource closure was reported complete");
            var failedSetup = new CleanupFiles { SpawnSucceeded = false, Child = HardThreadRead.Unknown };
            Check(new HardThreadCleanupState().Step(failedSetup), "original setup files remained orphaned after synchronous spawn failure");
            var unverifiedSpawn = new CleanupFiles { SpawnSucceeded = true, Child = HardThreadRead.Unknown };
            Check(!new HardThreadCleanupState().Step(unverifiedSpawn), "a created child was treated as an uncreated setup failure");
            unverifiedSpawn.Child = HardThreadRead.Gone;
            Check(new HardThreadCleanupState().Step(unverifiedSpawn), "original created child could not release setup files after its retained handle signaled");
            SettledLeftovers();
            Type controller = typeof(CriticalThreadGuardian).GetNestedType("ParentGuardian", BindingFlags.NonPublic);
            string controllerRoot = Path.Combine(Path.GetTempPath(), "Pavise-FakeController-" + Guid.NewGuid().ToString("N"));
            var originalController = (IHardThreadGuardian)Activator.CreateInstance(controller, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { controllerRoot }, null);
            MethodInfo register = typeof(CriticalThreadGuardian).GetMethod("RegisterController", BindingFlags.Static | BindingFlags.NonPublic);
            register.Invoke(null, new object[] { originalController });
            Check(HardThreadFileStore.CanonicalRootHasRecoveryDebt(controllerRoot), "an in memory setup cleanup controller was omitted from canonical debt");
            var nextController = (IHardThreadGuardian)Activator.CreateInstance(controller, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { Path.Combine(controllerRoot.ToUpperInvariant(), ".") }, null);
            bool admissionRefused = false;
            try { register.Invoke(null, new object[] { nextController }); }
            catch (TargetInvocationException error) {
                var failure = error.InnerException as HardThreadOperationException;
                admissionRefused = failure != null && failure.Kind == HardThreadFailureKind.RecoveryPending;
            }
            Check(admissionRefused && !originalController.ResourcesClosed && !Directory.Exists(controllerRoot),
                "a pending setup controller without a WAL permitted another original factory admission");
            originalController.Dispose();
            Check(originalController.ResourcesClosed && !HardThreadFileStore.CanonicalRootHasRecoveryDebt(controllerRoot),
                "a closed empty original setup controller retained phantom canonical debt");
            register.Invoke(null, new object[] { nextController });
            Check(HardThreadFileStore.CanonicalRootHasRecoveryDebt(controllerRoot), "a released controller prevented a new original factory admission");
            nextController.Dispose();
            Check(nextController.ResourcesClosed && !HardThreadFileStore.CanonicalRootHasRecoveryDebt(controllerRoot),
                "a replacement controller did not release its original root");
            var competitors = new IHardThreadGuardian[8]; var contenders = new Thread[competitors.Length];
            int admitted = 0, refused = 0; Exception unexpected = null;
            using (var start = new ManualResetEvent(false)) {
                for (int i = 0; i < contenders.Length; i++) {
                    int index = i;
                    competitors[index] = (IHardThreadGuardian)Activator.CreateInstance(controller, BindingFlags.Instance | BindingFlags.NonPublic, null,
                        new object[] { controllerRoot }, null);
                    contenders[index] = new Thread(delegate() {
                        start.WaitOne();
                        try { register.Invoke(null, new object[] { competitors[index] }); Interlocked.Increment(ref admitted); }
                        catch (TargetInvocationException error) {
                            var failure = error.InnerException as HardThreadOperationException;
                            if (failure != null && failure.Kind == HardThreadFailureKind.RecoveryPending) Interlocked.Increment(ref refused);
                            else Interlocked.CompareExchange(ref unexpected, error, null);
                        } catch (Exception error) { Interlocked.CompareExchange(ref unexpected, error, null); }
                    });
                    contenders[index].IsBackground = true; contenders[index].Start();
                }
                start.Set();
                foreach (var contender in contenders) Check(contender.Join(5000), "an original controller admission contender did not finish");
            }
            Check(unexpected == null && admitted == 1 && refused == competitors.Length - 1,
                "same root controller check and registration were not atomic");
            foreach (var competitor in competitors) competitor.Dispose();
            Check(!HardThreadFileStore.CanonicalRootHasRecoveryDebt(controllerRoot), "concurrent original controllers retained phantom canonical debt");
        }
        private static void Geometry()
        {
            using (var f = new Fixture()) {
                var plan = f.Plan(); Check(plan.Workers == 240, "workers must avoid both full physical cores");
                f.Native.Groups = 2; Refused(delegate { f.Plan(); }, "multi-group plan must refuse rather than truncate"); f.Native.Groups = 1;
                Refused(delegate { f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, 1), new HardThreadChoice(f.Second, 2) }); }, "SMT siblings must not become two key cores");
                Refused(delegate { f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, 3) }); }, "multi-logical candidate silently accepted");
                Refused(delegate { f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, 0) }); }, "empty mask accepted");
                Refused(delegate { f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, 1), new HardThreadChoice(f.First, 4) }); }, "duplicate candidate accepted");
                f.Native.Cores = new ulong[] { 3, 12 }; f.Native.ProcessMask = 15;
                f.Gate.Original = new HardThreadTarget(f.Gate.Original.SessionId, 410, 12345, @"C:\PaviseFake\game.exe", 15);
                Refused(delegate { f.One(); }, "insufficient worker capacity accepted");
            }
            using (var f = new Fixture()) {
                f.Gate.Original = new HardThreadTarget(f.Gate.Original.SessionId, 410, 12345, @"C:\PaviseFake\game.exe", 0x54);
                var plan = f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, 0x40) });
                Check(plan.Workers == 0x14, "single-thread physical cores were not retained as worker capacity");
            }
            using (var f = new Fixture()) {
                f.Gate.Original = new HardThreadTarget(f.Gate.Original.SessionId, 410, 12345, @"C:\PaviseFake\game.exe", 0xA8);
                var plan = f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, 0x80) });
                Check(plan.Workers == 0x28, "an odd-only candidate required its unselected sibling");
                Refused(delegate { f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, 0x40) }); },
                    "an unselected sibling outside the allowed range was accepted as a candidate");
                Refused(delegate { f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, 0x80), new HardThreadChoice(f.Second, 0x20) }); },
                    "two candidates left only one of three single-thread physical cores for workers");
            }
            using (var f = new Fixture()) {
                f.Gate.Original = new HardThreadTarget(f.Gate.Original.SessionId, 410, 12345, @"C:\PaviseFake\game.exe", 0xAA);
                var plan = f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, 0x80), new HardThreadChoice(f.Second, 0x20) });
                Check(plan.Workers == 0x0A, "two candidates could not use four single-thread physical cores");
            }
            using (var f = new Fixture()) {
                f.Gate.Original = new HardThreadTarget(f.Gate.Original.SessionId, 410, 12345, @"C:\PaviseFake\game.exe", 0x57);
                var plan = f.One();
                Check(plan.Workers == 0x54 && (plan.Workers & 3) == 0, "workers kept an allowed SMT sibling on the candidate physical core");
            }
            using (var f = new Fixture()) {
                f.Gate.Original = new HardThreadTarget(f.Gate.Original.SessionId, 410, 12345, @"C:\PaviseFake\game.exe", 7);
                Refused(delegate { f.One(); }, "three logical processors on two physical cores passed the worker capacity gate");
                f.Gate.Workers = false;
                Refused(delegate { f.One(); }, "candidate-only mode lost the existing two-other-physical-core minimum");
            }
            using (var f = new Fixture()) {
                var foreign = new HardThreadTarget(Guid.NewGuid(), 410, 12345, @"C:\PaviseFake\game.exe", 255);
                var plan = HardThreadPlan.Create(foreign, f.Native.ReadMachine(), new[] { new HardThreadChoice(f.First, 1) });
                Check(!f.Session.Apply(plan, new[] { f.First }, HardThreadCoverage.Complete) && f.Native.Writes == 0, "foreign session plan must not grant writes");
            }
            using (var f = new Fixture()) {
                var claimed = f.Native.ReadMachine(); claimed.PhysicalCores = new ulong[] { 1, 2, 4, 8, 16, 32, 64, 128 };
                var plan = HardThreadPlan.Create(f.Gate.Target, claimed, new[] { new HardThreadChoice(f.First, 1) });
                Check(!f.Session.Apply(plan, new[] { f.First }, HardThreadCoverage.Complete) && f.Native.Writes == 0, "caller topology cannot override original native facts");
            }
        }
        private static void HappyAndIncrement()
        {
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.Plan(), new[] { f.First, f.Second, f.Worker }, HardThreadCoverage.Complete), "happy apply requires durable intent before every native write");
                Check(f.Native.Masks[f.First.Key] == 1 && f.Native.Masks[f.Second.Key] == 4 && f.Native.Masks[f.Worker.Key] == 240, "wrong hard binding masks");
                var added = new HardThreadIdentity(54, 504);
                Check(f.Session.AdmitNewThreads(new[] { added }, HardThreadCoverage.Partial), "new original thread was not admitted");
                Check(f.Native.Masks[added.Key] == 240 && f.Session.Status == HardThreadStatus.CoveragePending, "incomplete future coverage presented as complete");
                int writes = f.Native.Writes;
                Check(f.Session.AdmitNewThreads(new[] { added }, HardThreadCoverage.Complete) && f.Native.Writes == writes, "same identity was rebound");
                f.Gate.Active = false;
                Check(f.Session.Restore(), "revocation must not disable original recovery");
                foreach (ulong mask in f.Native.Masks.Values) Check(mask == 255, "original group affinity not restored");
                Check(f.Gate.RecoveryChecks > 0 && f.Store.Saved.Rows.TrueForAll(x => x.Terminal), "recovery did not close exact receipts");
            }
            using (var f = new Fixture()) {
                var plan = f.One(); f.Gate.Active = false;
                Check(!f.Session.Apply(plan, new[] { f.First }, HardThreadCoverage.Complete) && f.Native.Writes == 0, "revoked forward authority issued native writes");
            }
        }
        private static void FaultWindows()
        {
            using (var f = new Fixture()) {
                bool fail = true;
                f.Store.Before = delegate(HardThreadJournal journal) { if (fail && journal.Rows.Count > 0 && journal.Rows[0].Phase == HardThreadPhase.PreviousRecorded) { fail = false; throw new IOException("crash before previous persisted"); } };
                Check(!f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "previous persistence fault accepted");
                Check(f.Native.Writes == 1, "unknown previous must never issue restore");
                int writes = f.Native.Writes;
                Check(!f.Session.Restore() && f.Native.Writes == writes && f.Native.Masks[f.First.Key] == 1, "unknown previous must never issue restore");
                Check(!f.Store.Saved.Rows[0].PreviousKnown, "uncertain original was manufactured from captured read");
                f.Native.ProcessRead = HardThreadRead.Gone;
                Check(f.Session.Restore() && f.Native.Writes == writes, "exact original process exit did not settle uncertainty");
            }
            using (var f = new Fixture()) {
                f.Native.BeforeSet = delegate(HardThreadIdentity thread, ulong mask) { if (mask == 1) f.Native.Masks[thread.Key] = 7; };
                Check(!f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "native previous race was accepted as applied");
                Check(f.Native.Masks[f.First.Key] == 7 && f.Store.Saved.Rows[0].Captured == 255 && f.Store.Saved.Rows[0].Previous == 7,
                    "apply race blindly restored old captured value");
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "restore crash fixture failed");
                f.Native.AfterSet = delegate { throw new IOException("crash after native restore before previous"); };
                Check(!f.Session.Restore(), "restore crash reported success");
                int writes = f.Native.Writes; f.Native.AfterSet = null;
                Check(!f.Session.Restore() && f.Native.Writes == writes, "unknown restore previous was guessed");
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "restore race fixture failed");
                f.Native.BeforeSet = delegate(HardThreadIdentity thread, ulong mask) { f.Native.Masks[thread.Key] = 7; };
                Check(!f.Session.Restore(), "restore race reported clean restoration");
                int writes = f.Native.Writes;
                Check(!f.Session.Restore() && f.Native.Writes == writes, "restore race caused repeated forced correction");
                f.Native.Masks[f.First.Key] = 7;
                Check(f.Session.Restore() && f.Native.Writes == writes, "external restore value was not recognized without overwrite");
            }
        }
        private static void Isolation()
        {
            using (var f = new Fixture()) {
                var plan = f.Plan();
                f.Native.ReadOverride = delegate(HardThreadIdentity thread) { return new HardThreadObserved { Read = thread.Key == f.Second.Key ? HardThreadRead.Unknown : HardThreadRead.Known,
                    Mask = f.Native.Masks.ContainsKey(thread.Key) ? f.Native.Masks[thread.Key] : 255, CpuSetsEmpty = true }; };
                Check(!f.Session.Apply(plan, new[] { f.First, f.Second }, HardThreadCoverage.Complete), "partial unknown thread input accepted");
                Check(!f.Native.Masks.ContainsKey(f.First.Key) && f.Native.Writes == 0 && !f.Session.LastOperationWriteIntent,
                    "unavailable candidate preflight must not partially apply an earlier candidate");
                Check(f.Session.LastFailureKind == HardThreadFailureKind.ObservationUnavailable, "candidate observation was classified as a write fault");
            }
            using (var f = new Fixture()) {
                var plan = f.One(); f.Native.ThreadSetsEmpty = false;
                Check(!f.Session.Apply(plan, new[] { f.First }, HardThreadCoverage.Complete) && f.Native.Writes == 0, "external CPU Sets were overwritten");
            }
            using (var f = new Fixture()) {
                var plan = f.One(); f.Native.ProcessSetsEmpty = false;
                Check(!f.Session.Apply(plan, new[] { f.First }, HardThreadCoverage.Complete) && f.Native.Writes == 0, "process CPU Sets ignored");
            }
            using (var f = new Fixture()) {
                var plan = f.One(); f.Native.ProcessMask = 15;
                Check(!f.Session.Apply(plan, new[] { f.First }, HardThreadCoverage.Complete) && f.Native.Writes == 0, "changed process mask ignored");
            }
            using (var f = new Fixture()) {
                f.Native.AfterSet = delegate(HardThreadIdentity thread, ulong mask) { f.Native.Masks[thread.Key] = 8; };
                Check(!f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "unverified readback must not report applied");
                Check(f.Native.Writes == 1 && f.Native.Masks[f.First.Key] == 8, "external post-write value was overwritten");
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "unknown restore fixture failed");
                f.Native.ThreadRead = HardThreadRead.Unknown; int writes = f.Native.Writes;
                Check(!f.Session.Restore() && f.Native.Writes == writes && !f.Store.Saved.Rows[0].Terminal, "Unknown was treated as Gone");
                f.Native.ThreadRead = HardThreadRead.Gone;
                Check(f.Session.Restore() && f.Native.Writes == writes, "held original thread exit not accepted");
            }
            using (var f = new Fixture()) {
                Check(f.Session.Apply(f.One(), new[] { f.First }, HardThreadCoverage.Complete), "external restore fixture failed");
                f.Native.Masks[f.First.Key] = 8; int writes = f.Native.Writes;
                Check(!f.Session.Restore() && f.Native.Writes == writes, "external current hard mask was overwritten");
                f.Native.Masks[f.First.Key] = 255;
                Check(f.Session.Restore() && f.Native.Writes == writes, "already restored original requires no write");
            }
        }
        private static void FileAndAbi(string root)
        {
            Directory.CreateDirectory(root);
            var gate = new Gate();
            using (var store = new HardThreadFileStore(root)) {
                var journal = store.Load(gate.Target); Check(journal.Revision == 0, "fresh original store not empty");
                journal.Revision = 1; journal.Rows.Add(new HardThreadReceipt { Thread = new HardThreadIdentity(61, 601), Captured = 255, Applied = 1, Phase = HardThreadPhase.ApplyIntent });
                store.Save(0, journal);
                var loaded = store.Load(gate.Target);
                Check(loaded.Revision == 1 && loaded.Rows.Count == 1 && !loaded.Rows[0].PreviousKnown, "WAL unknown previous did not roundtrip");
                Refused(delegate { store.Save(0, journal); }, "old revision overwrote original source");
                Refused(delegate { store.Load(new HardThreadTarget(Guid.NewGuid(), 410, 12345, @"C:\PaviseFake\game.exe", 255)); }, "foreign original source accepted");
                loaded.Revision = 2; loaded.Rows[0].PreviousKnown = true; loaded.Rows[0].Previous = 255; loaded.Rows[0].Phase = HardThreadPhase.Applied;
                store.Save(1, loaded); Check(store.Load(gate.Target).Rows[0].Previous == 255, "native returned previous did not roundtrip");
            }
            string path = Path.Combine(root, "hard-thread-" + gate.Target.SessionId.ToString("N") + ".xml");
            File.WriteAllText(path, "<!DOCTYPE hardThreads [<!ENTITY x SYSTEM 'file:///never'>]><hardThreads version='1'>&x;</hardThreads>");
            bool invalid = false; using (var store = new HardThreadFileStore(root)) { try { store.Load(gate.Target); } catch (System.Xml.XmlException) { invalid = true; } }
            Check(invalid, "DTD source was accepted");
            Refused(delegate { CriticalThreadHardAffinitySession.OpenNative(); }, "unbridged native factory grants write authority");
            Type backend = typeof(CriticalThreadHardAffinitySession).GetNestedType("CriticalThreadHardAffinityNative", BindingFlags.NonPublic);
            Type affinity = backend.GetNestedType("GroupAffinity", BindingFlags.NonPublic);
            Check(IntPtr.Size == 8 && Marshal.SizeOf(affinity) == 16 && Marshal.OffsetOf(affinity, "Group").ToInt32() == 8, "GROUP_AFFINITY x64 ABI layout mismatch");
            Type startup = typeof(CriticalThreadGuardian).GetNestedType("StartupInfo", BindingFlags.NonPublic);
            Type process = typeof(CriticalThreadGuardian).GetNestedType("ProcessInfo", BindingFlags.NonPublic);
            Type file = typeof(CriticalThreadGuardian).GetNestedType("FileIdentity", BindingFlags.NonPublic);
            Check(Marshal.SizeOf(startup) == 104 && Marshal.OffsetOf(startup, "Flags").ToInt32() == 60
                && Marshal.OffsetOf(startup, "Show").ToInt32() == 64 && Marshal.SizeOf(process) == 24 && Marshal.SizeOf(file) == 52,
                "native guardian startup process or source file ABI mismatch");
            Type disposition = typeof(CriticalThreadGuardian).GetNestedType("DeleteDisposition", BindingFlags.NonPublic);
            Check(Marshal.SizeOf(disposition) == 1 && Marshal.OffsetOf(disposition, "Delete").ToInt32() == 0, "FILE_DISPOSITION_INFO ABI mismatch");
        }
        private static void RootAdmission(string root)
        {
            string directory = Path.Combine(root, "root-admission"); Directory.CreateDirectory(directory);
            var oldTarget = new HardThreadTarget(Guid.NewGuid(), 601, 6010, @"C:\PaviseFake\game.exe", 255);
            var nextTarget = new HardThreadTarget(Guid.NewGuid(), 601, 6010, @"C:\PaviseFake\game.exe", 255);
            Check(!HardThreadFileStore.CanonicalRootHasRecoveryDebt(Path.Combine(root, "missing-canonical-root")), "a missing canonical root created recovery debt");
            Check(HardThreadFileStore.CanonicalRootHasRecoveryDebt(directory) && !File.Exists(Path.Combine(directory, "hard-thread-root.lock")),
                "read only historical inspection created a root writer or trusted an unverifiable existing root");
            using (var store = new HardThreadFileStore(directory)) {
                store.RequireForwardAdmission(oldTarget);
                var journal = store.Load(oldTarget); journal.Revision = 1;
                journal.Rows.Add(new HardThreadReceipt { Thread = new HardThreadIdentity(701, 7010), Captured = 255, Applied = 1, Phase = HardThreadPhase.ApplyIntent });
                store.Save(0, journal);
                Refused(delegate { store.CaptureSettled(oldTarget, 1); }, "pending WAL manufactured a settled cleanup source");
                Refused(delegate { using (var competing = new HardThreadFileStore(directory)) { competing.Load(nextTarget); } },
                    "another session bypassed the original root writer admission");
                Check(HardThreadFileStore.CanonicalRootHasRecoveryDebt(directory), "an active root writer was reported as recovery clean");
                string path = Path.Combine(directory, "hard-thread-" + oldTarget.SessionId.ToString("N") + ".xml");
                using (var unread = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    Refused(delegate { store.Load(oldTarget); }, "unreadable original ledger was treated as empty");
            }
            using (var store = new HardThreadFileStore(directory)) {
                bool refused = false;
                try { store.RequireForwardAdmission(nextTarget); }
                catch (HardThreadOperationException error) { refused = error.Kind == HardThreadFailureKind.RecoveryPending; }
                Check(refused, "pending previous session permitted new forward authority");
            }
            Check(HardThreadFileStore.CanonicalRootHasRecoveryDebt(directory), "a pending historical session was reported as clean");
            HardThreadSettledSource settled;
            using (var store = new HardThreadFileStore(directory)) {
                var journal = store.Load(oldTarget); Check(journal.Rows[0].Phase == HardThreadPhase.ApplyIntent, "forward refusal modified historical uncertainty");
                journal.Rows[0].Phase = HardThreadPhase.NotApplied; journal.Revision++;
                store.Save(1, journal);
                settled = store.CaptureSettled(oldTarget, 2);
            }
            Check(!HardThreadFileStore.CanonicalRootHasRecoveryDebt(directory), "settled canonical history remained recovery pending");
            string sourcePath = Path.Combine(directory, "hard-thread-" + oldTarget.SessionId.ToString("N") + ".xml");
            byte[] originalSource = File.ReadAllBytes(sourcePath);
            using (var store = new HardThreadFileStore(directory)) Check(store.MatchesSettled(settled, false), "original terminal source was not accepted for its cleanup record");
            File.WriteAllText(sourcePath, File.ReadAllText(sourcePath).Replace("detail=\"\"", "detail=\"changed\""));
            using (var store = new HardThreadFileStore(directory)) Check(!store.MatchesSettled(settled, false), "changed original terminal source retained cleanup permission");
            File.WriteAllBytes(sourcePath, originalSource);
            using (var store = new HardThreadFileStore(directory)) {
                store.RequireForwardAdmission(nextTarget);
                Check(store.Load(nextTarget).Rows.Count == 0, "settled historical source was reused as new ownership");
            }
            string historical = Path.Combine(directory, "hard-thread-" + oldTarget.SessionId.ToString("N") + ".xml");
            File.WriteAllText(historical, "<unverified/>");
            Check(HardThreadFileStore.CanonicalRootHasRecoveryDebt(directory), "malformed canonical history was reported as clean");
            using (var store = new HardThreadFileStore(directory)) {
                bool refused = false;
                try { store.RequireForwardAdmission(nextTarget); }
                catch (HardThreadOperationException error) { refused = error.Kind == HardThreadFailureKind.RecoveryPending; }
                Check(refused, "malformed historical source permitted new forward authority");
            }
        }
        private static void CandidateScope()
        {
            using (var f = new Fixture()) {
                f.Gate.Workers = false;
                var plan = f.Plan();
                Check(plan.WorkerMode == HardThreadWorkerMode.CandidatesOnly, "default original lease enabled workers");
                Check(f.Session.Apply(plan, new[] { f.First, f.Second }, HardThreadCoverage.Complete)
                    && f.Session.Status == HardThreadStatus.AppliedCandidates, "candidate-only scope misreported as worker coverage");
                Check(f.Native.Writes == 2 && !f.Native.Masks.ContainsKey(f.Worker.Key), "candidate-only scope touched worker mask");
                Check(f.Session.Restore(), "candidate-only restore failed");
            }
            using (var f = new Fixture()) {
                f.Gate.Workers = false;
                var plan = HardThreadPlan.Create(f.Gate.Target, f.Native.ReadMachine(), new[] { new HardThreadChoice(f.First, 1) }, HardThreadWorkerMode.AvoidWorkers);
                Check(!f.Session.Apply(plan, new[] { f.First, f.Worker }, HardThreadCoverage.Complete) && f.Native.Writes == 0,
                    "caller worker DTO overrode original candidate-only authority");
            }
            using (var f = new Fixture()) {
                f.Gate.Workers = false;
                Check(!f.Session.Apply(f.One(), new[] { f.First, f.Worker }, HardThreadCoverage.Complete) && f.Native.Writes == 0,
                    "candidate-only input validation happened after native writes");
            }
            using (var f = new Fixture()) {
                var input = new List<HardThreadIdentity> { f.First, f.Second };
                for (int i = 0; i < 9; i++) input.Add(new HardThreadIdentity(100 + i, 1000 + i));
                Check(!f.Session.Apply(f.Plan(), input.ToArray(), HardThreadCoverage.Complete) && f.Native.Writes == 0,
                    "worker budget accepted more than eight new workers");
            }
            var parent = new HardThreadGuardianActor(111, 222, @"C:\parent.exe", new string('a', 64));
            var child = new HardThreadGuardianActor(333, 444, @"C:\guard.exe", new string('b', 64));
            string nonce = new string('c', 64), source = new string('d', 64);
            var handshake = new HardThreadGuardianHandshake(parent, child, nonce, source);
            Check(!handshake.ForwardReady && !handshake.RecoveryEligible, "disk facts manufactured live guardian readiness");
            Refused(delegate { handshake.Accept(999, 333, parent, child, nonce, source); }, "wrong actual server peer accepted");
            Refused(delegate { handshake.Accept(111, 999, parent, child, nonce, source); }, "wrong actual spawned child accepted");
            Refused(delegate { handshake.Accept(111, 333, parent, child, new string('e', 64), source); }, "wrong original nonce accepted");
            Refused(delegate { handshake.Accept(111, 333, parent, child, nonce, new string('e', 64)); }, "wrong original scope bytes accepted");
            handshake.Accept(111, 333, parent, child, nonce, source);
            Check(handshake.ForwardReady && !handshake.RecoveryEligible, "valid original live chain not armed");
            handshake.ObserveParent(HardThreadRead.Unknown);
            Check(!handshake.RecoveryEligible, "unknown original parent treated as stopped");
            handshake.ObserveGuard(HardThreadRead.Gone);
            Check(!handshake.ForwardReady && !handshake.RecoveryEligible, "guardian death allowed forward or replaced parent stop proof");
            handshake.ObserveParent(HardThreadRead.Gone);
            Check(handshake.RecoveryEligible, "original stopped parent did not qualify protocol recovery");
            int exit;
            Check(CriticalThreadGuardian.TryRun(new[] { CriticalThreadGuardian.Command, "bad" }, @"C:\fake", out exit) && exit != 0,
                "malformed reserved guardian command fell through ordinary Main");
            Check(!CriticalThreadGuardian.TryRun(new[] { "--ordinary" }, @"C:\fake", out exit), "ordinary mode intercepted by guardian");
            Refused(delegate { CriticalThreadGuardian.StopProof.Issue(new object()); }, "an arbitrary object minted original recovery authority");
            Type registration = typeof(CriticalThreadGuardian).GetNestedType("NativeRegistration", BindingFlags.NonPublic);
            object forged = Activator.CreateInstance(registration, true);
            Refused(delegate { CriticalThreadGuardian.StopProof.Issue(forged); }, "a caller constructed registration minted original recovery authority");
            int attempts = 0;
            var waits = new List<int>();
            CriticalThreadGuardian.RetainRecoveryUntilSettled(delegate {
                attempts++;
                if (attempts == 2) throw new IOException("temporary unavailable original recovery store");
                return attempts == 12;
            }, delegate(int delay) { waits.Add(delay); });
            Check(attempts == 12 && waits.Count == 11, "guardian released recovery before original ownership settled");
            Check(waits[0] == 250 && waits[1] == 500 && waits[2] == 1000 && waits[7] == 30000
                && waits[waits.Count - 1] == 30000 && waits.TrueForAll(delay => delay > 0 && delay <= 30000),
                "guardian recovery retried without bounded backoff");
        }
        private sealed class OriginPlatform : IHardThreadOriginPlatform
        {
            internal HardThreadOriginContext Context;
            private readonly byte[] secret;
            internal OriginPlatform(string key, HardThreadOriginContext context) { secret = Encoding.UTF8.GetBytes(key + ":"); Context = context; }
            public HardThreadOriginContext Current() { return Context; }
            public byte[] Seal(byte[] original)
            { var result = new byte[secret.Length + original.Length]; Array.Copy(secret, result, secret.Length); Array.Copy(original, 0, result, secret.Length, original.Length); return result; }
            public byte[] Unseal(byte[] original)
            {
                if (original == null || original.Length < secret.Length) throw new IOException("fake Windows protection domain differs");
                for (int i = 0; i < secret.Length; i++) if (secret[i] != original[i]) throw new IOException("fake Windows protection domain differs");
                var result = new byte[original.Length - secret.Length]; Array.Copy(original, secret.Length, result, 0, result.Length); return result;
            }
        }
        private static void OriginBinding()
        {
            string machine = new Guid("00112233-4455-6677-8899-aabbccddeeff").ToString("N");
            string kernel = "boot2:" + new Guid("10112233-4455-6677-8899-aabbccddeeff").ToString("N") + ":100";
            var origin = new HardThreadOriginContext(machine, "producer", kernel);
            var platform = new OriginPlatform("machine-protection-a", origin);
            string root = @"C:\PaviseFake\CriticalThreadHardAffinityV1", nonce = new string('a', 64), sid = "S-1-5-21-1-2-3-1000";
            var target = new HardThreadTarget(Guid.NewGuid(), 410, 12345, @"C:\PaviseFake\game.exe", 255);
            var parent = new HardThreadGuardianActor(411, 12346, @"C:\PaviseFake\Pavise.exe", new string('b', 64));
            var child = new HardThreadGuardianActor(412, 12347, Path.Combine(root, "guards", target.SessionId.ToString("N"), "Pavise.HardThreadGuardian.exe"), new string('b', 64));
            byte[] bytes = CriticalThreadGuardian.EncodeOriginScopeForTest(target, parent, child, root, "original-root-file-id", sid, nonce, platform);
            CriticalThreadGuardian.ParseOriginScopeForTest(bytes, root, target.SessionId, nonce, sid, platform, true);
            Check(true, "valid machine and kernel bound original source did not parse");
            var anotherMachine = new OriginPlatform("machine-protection-b", origin);
            Refused(delegate { CriticalThreadGuardian.ParseOriginScopeForTest(bytes, root, target.SessionId, nonce, sid, anotherMachine, false); },
                "a shared SID and root fabricated the original Windows machine protection domain");
            platform.Context = new HardThreadOriginContext(new Guid("20112233-4455-6677-8899-aabbccddeeff").ToString("N"), "producer", kernel);
            Refused(delegate { CriticalThreadGuardian.ParseOriginScopeForTest(bytes, root, target.SessionId, nonce, sid, platform, false); },
                "a different Windows machine identity accepted a copied original scope");
            platform.Context = new HardThreadOriginContext(machine, "renamed-host", kernel);
            Refused(delegate { CriticalThreadGuardian.ParseOriginScopeForTest(bytes, root, target.SessionId, nonce, sid, platform, false); },
                "a different host name accepted the original protection domain");
            platform.Context = new HardThreadOriginContext(machine, "producer", kernel.Replace(":100", ":101"));
            Refused(delegate { CriticalThreadGuardian.ParseOriginScopeForTest(bytes, root, target.SessionId, nonce, sid, platform, true); },
                "live original registration crossed kernel domains");
            CriticalThreadGuardian.ParseOriginScopeForTest(bytes, root, target.SessionId, nonce, sid, platform, false);
            Check(true, "historical data parse incorrectly claimed to be a live same kernel registration");
            platform.Context = origin;
            string xml = Encoding.UTF8.GetString(bytes);
            foreach (string[] change in new[] {
                new[] { "version=\"2\"", "version=\"1\"" },
                new[] { "allowed=\"255\"", "allowed=\"127\"" },
                new[] { "birth=\"12345\"", "birth=\"12344\"" },
                new[] { "original-root-file-id", "another-root-file-id" },
                new[] { "parentBirth=\"12346\"", "parentBirth=\"12348\"" }
            }) {
                Check(xml.Contains(change[0]), "origin source mutation anchor missing");
                byte[] altered = Encoding.UTF8.GetBytes(xml.Replace(change[0], change[1]));
                Refused(delegate { CriticalThreadGuardian.ParseOriginScopeForTest(altered, root, target.SessionId, nonce, sid, platform, true); },
                    "changed or legacy source bypassed the original machine sealed binding");
            }
        }
        private static void HistoricalGoneFacts(string root)
        {
            var budget = new HardThreadHistoricalBudget();
            for (int i = 0; i < 8; i++) Check(budget.BeginSession(0), "bounded historical batch stopped before eight sessions");
            Check(!budget.BeginSession(0), "historical batch exceeded eight unsettled sessions");
            var late = new HardThreadHistoricalBudget();
            Check(late.BeginSession(5000) && !late.BeginSession(5001), "historical IO delay blocked all progress or ignored the soft time window");
            var imageBudget = new HardThreadHistoricalBudget();
            Check(imageBudget.ReadImage(33 * 1024 * 1024) && !imageBudget.ReadImage(33 * 1024 * 1024)
                && imageBudget.ReadImage(31 * 1024 * 1024) && !imageBudget.ReadImage(1)
                && !imageBudget.ReadImage(0) && !imageBudget.ReadImage(-1), "historical image reads exceeded the total bounded budget");
            var target = new HardThreadTarget(Guid.NewGuid(), 600, 6000, @"C:\PaviseFake\game.exe", 255);
            var alive = new HardThreadProcessFact(true, 600, 6000, true, 258, 0);
            var exited = new HardThreadProcessFact(true, 600, 6000, true, 0, 0);
            var reused = new HardThreadProcessFact(true, 600, 7000, true, 258, 0);
            var missing = new HardThreadProcessFact(false, 600, 0, false, uint.MaxValue, 87);
            var denied = new HardThreadProcessFact(false, 600, 0, false, uint.MaxValue, 5);
            Check(HardThreadHistoricalPolicy.Gone(target, alive, null) == HardThreadGoneReason.None, "live original process was marked historical gone");
            Check(HardThreadHistoricalPolicy.Gone(target, exited, null) == HardThreadGoneReason.OriginalExited, "held signaled original process did not establish exit");
            Check(HardThreadHistoricalPolicy.Gone(target, reused, null) == HardThreadGoneReason.ProcessIdReused, "positive different creation did not establish original PID reuse");
            Check(HardThreadHistoricalPolicy.Gone(target, missing, missing) == HardThreadGoneReason.RepeatedMissingProcess, "two specific missing PID results did not establish disappearance");
            foreach (var pair in new[] {
                new[] { denied, denied }, new[] { missing, denied }, new[] { missing, alive },
                new[] { new HardThreadProcessFact(true, 600, 7000, true, uint.MaxValue, 0), alive },
                new[] { new HardThreadProcessFact(true, 601, 7000, true, 0, 0), alive },
                new[] { new HardThreadProcessFact(false, 601, 0, false, uint.MaxValue, 87), missing },
                new[] { new HardThreadProcessFact(true, 600, 6000, false, 0, 0), alive },
                new[] { new HardThreadProcessFact(true, 600, 0, true, 0, 0), alive }
            }) Check(HardThreadHistoricalPolicy.Gone(target, pair[0], pair[1]) == HardThreadGoneReason.None,
                "unknown denied wrong-owner or wait failure became a historical gone fact");
            Refused(delegate { CriticalThreadGuardian.NativeGoneProof.Issue(new object()); }, "caller DTO minted native historical gone authority");
            Type registration = typeof(CriticalThreadGuardian).GetNestedType("HistoricalRegistration", BindingFlags.NonPublic);
            object forged = Activator.CreateInstance(registration, true);
            Refused(delegate { CriticalThreadGuardian.NativeGoneProof.Issue(forged); }, "a self constructed historical registration minted native gone authority");
            Refused(delegate { CriticalThreadGuardian.TrySettleHistoricalGone(root); }, "isolated checks executed native historical settlement");
            var previous = new HardThreadJournal { Target = target, Revision = 10, SourceNonce = new string('a', 64), SourceScopeHash = new string('b', 64) };
            previous.Rows.Add(new HardThreadReceipt { Thread = new HardThreadIdentity(700, 7000), Captured = 255, Applied = 1, Phase = HardThreadPhase.ApplyIntent, Detail = "original note" });
            previous.Rows.Add(new HardThreadReceipt { Thread = new HardThreadIdentity(701, 7001), Captured = 255, Applied = 4, PreviousKnown = true, Previous = 255, Phase = HardThreadPhase.Restored });
            var gone = HardThreadHistoricalPolicy.MarkGone(previous, HardThreadGoneReason.ProcessIdReused);
            Check(previous.Revision == 10 && previous.Rows[0].Phase == HardThreadPhase.ApplyIntent && gone.Revision == 11
                && gone.Rows[0].Phase == HardThreadPhase.Gone && !gone.Rows[0].PreviousKnown && gone.Rows[0].Previous == 0
                && gone.Rows[0].Captured == 255 && gone.Rows[0].Applied == 1 && gone.Rows[0].Detail.Contains("original note")
                && gone.Rows[0].Detail.Contains("prior phase=ApplyIntent") && gone.Rows[1].Phase == HardThreadPhase.Restored
                && gone.SourceNonce == previous.SourceNonce && gone.SourceScopeHash == previous.SourceScopeHash,
                "historical metadata projection guessed an original value lost evidence or mislabeled restoration");
            Refused(delegate { HardThreadHistoricalPolicy.MarkGone(previous, HardThreadGoneReason.None); }, "unknown target fact marked historical rows gone");
            string directory = Path.Combine(root, "scope-bound-journal"); Directory.CreateDirectory(directory);
            string nonce = new string('a', 64), source = new string('b', 64);
            using (var store = new HardThreadFileStore(directory)) {
                Refused(delegate { store.CommitHistoricalGone(null); }, "historical commit accepted caller facts without a private native proof");
                store.BindOriginalScope(target, nonce, source);
                var journal = store.Load(target);
                Check(journal.SourceNonce == nonce && journal.SourceScopeHash == source, "original live scope was not inherited by fresh WAL");
                journal.Revision = 1; journal.Rows.Add(new HardThreadReceipt { Thread = new HardThreadIdentity(700, 7000), Captured = 255, Applied = 1, Phase = HardThreadPhase.ApplyIntent });
                store.Save(0, journal);
                var current = store.Load(target);
                Check(current.SourceNonce == nonce && current.SourceScopeHash == source, "original scope binding did not survive durable WAL roundtrip");
                current.SourceNonce = new string('c', 64); current.Revision++;
                Refused(delegate { store.Save(1, current); }, "WAL write swapped its original source nonce");
            }
            using (var store = new HardThreadFileStore(directory)) {
                store.BindOriginalScope(target, nonce, new string('d', 64));
                Refused(delegate { store.Load(target); }, "historical or recovery source used a foreign scope hash");
            }
        }
        private static void StorageDomain()
        {
            foreach (string path in new[] { null, "", @"\\server\share\Pavise", @"\\?\UNC\server\share\Pavise", @"\\?\C:\Pavise",
                @"\??\C:\Pavise", @"\Pavise", @"C:Pavise", @"Pavise", "1:\\Pavise" }) {
                int calls = 0; bool refused = false;
                try { HardThreadStorageDomain.RequireLocal(path, delegate { calls++; return DriveType.Fixed; }); }
                catch (NotSupportedException error) { Check(error is HardThreadStorageDomainException, "a storage refusal lost its dedicated failure type"); refused = true; }
                Check(refused && calls == 0, "a UNC or unsupported source path reached the forward storage probe");
            }
            foreach (var type in new[] { DriveType.Fixed, DriveType.Removable, DriveType.Ram }) {
                int calls = 0;
                HardThreadStorageDomain.RequireLocal(@"C:\Pavise\CriticalThreadHardAffinityV1", delegate(string drive) {
                    calls++; Check(drive == @"C:\", "forward storage probe did not receive the explicit original drive root"); return type;
                });
                Check(calls == 1, "a supported local source domain was not checked exactly once");
            }
            foreach (var type in new[] { DriveType.Network, DriveType.Unknown, DriveType.NoRootDirectory, DriveType.CDRom, (DriveType)99 }) {
                int calls = 0; bool refused = false;
                try { HardThreadStorageDomain.RequireLocal(@"Z:\Pavise\CriticalThreadHardAffinityV1", delegate { calls++; return type; }); }
                catch (NotSupportedException error) { Check(error is HardThreadStorageDomainException, "a storage refusal lost its dedicated failure type"); refused = true; }
                Check(refused && calls == 1, "a mapped network or unknown data directory permitted new forward execution");
            }
            bool unknownRefused = false;
            try { HardThreadStorageDomain.RequireLocal(@"C:\Pavise", delegate { throw new IOException("fake drive query unavailable"); }); }
            catch (NotSupportedException error) { Check(error is HardThreadStorageDomainException, "a storage refusal lost its dedicated failure type"); unknownRefused = error.InnerException is IOException; }
            Check(unknownRefused, "a failed storage domain probe permitted new forward execution");
        }
        private static int Main(string[] args)
        {
            try { StorageDomain(); Geometry(); HappyAndIncrement(); FaultWindows(); Isolation(); CandidateScope(); OriginBinding(); HistoricalGoneFacts(args[0]); OwnershipAndChurn(); GuardianHealth(); ResourceClosure(); RootAdmission(args[0]); FileAndAbi(args[0]); SparseSession(); Console.WriteLine("PASS hard-thread checks=" + checks + " native=false windows=false user_threads=false settings_writes=false"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static void SparseSession()
        {
            foreach (ulong allowed in new[] { 0x54UL, 0xA8UL }) {
                using (var f = new Fixture()) {
                    ulong candidate = allowed == 0x54 ? 0x40UL : 0x80UL, workers = allowed == 0x54 ? 0x14UL : 0x28UL;
                    f.Gate.Original = new HardThreadTarget(f.Gate.Original.SessionId, 410, 12345, @"C:\PaviseFake\game.exe", allowed);
                    var plan = f.Session.CreatePlan(new[] { new HardThreadChoice(f.First, candidate) });
                    Check(f.Session.Apply(plan, new[] { f.First, f.Worker }, HardThreadCoverage.Complete)
                        && f.Native.Masks[f.First.Key] == candidate && f.Native.Masks[f.Worker.Key] == workers,
                        "the real session rejected three single-thread physical cores or added unselected siblings");
                    Check(f.Session.Restore() && f.Native.Masks[f.First.Key] == 0xFF && f.Native.Masks[f.Worker.Key] == 0xFF,
                        "a sparse single-thread selection did not restore both original thread masks");
                    Check(f.Store.Saved.Rows.TrueForAll(row => row.Terminal), "a sparse single-thread restoration retained an unsettled receipt");
                }
            }
        }
    }
}
#endif
