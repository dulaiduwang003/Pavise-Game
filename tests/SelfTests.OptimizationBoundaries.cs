// File purpose Boundary regression using only synthetic process snapshots, fake GPU preferences and temporary settings
// Does not run the game loop, write real GPU or power state, or tune real processes
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static object BoundaryField(object target, string name)
        { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static void BoundarySet(object target, string name, object value)
        { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        private static object BoundaryCall(object target, string name, params object[] args)
        { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }

        private static void AutoGpuRevalidatesUnderCommitLock()
        {
            for (int repeat = 0; repeat < 5; repeat++)
            using (var fixture = new AppGpuFixture())
            using (var requestHold = new ManualResetEvent(false))
            using (var held = new ManualResetEvent(false))
            using (var validated = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                int eligible = 1, validations = 0;
                Exception holderError = null, writerError = null;
                AppGpuPreferenceResult result = AppGpuPreferenceResult.ReadFailed;
                var holder = new Thread(delegate()
                {
                    try
                    {
                        Eq(true, requestHold.WaitOne(5000));
                        lock (GpuPrefStage.MutationGate)
                        { held.Set(); Eq(true, release.WaitOne(5000)); }
                    }
                    catch (Exception ex) { holderError = ex; }
                });
                var writer = new Thread(delegate()
                {
                    try
                    {
                        result = GameMode.AutoGpuEnroll(fixture.Manager, AppGpuPath, delegate
                        {
                            int call = Interlocked.Increment(ref validations);
                            if (call == 1)
                            {
                                requestHold.Set(); Eq(true, held.WaitOne(5000));
                                bool answer = Volatile.Read(ref eligible) != 0;
                                validated.Set(); return answer;
                            }
                            Eq(true, Monitor.IsEntered(GpuPrefStage.MutationGate));
                            return Volatile.Read(ref eligible) != 0;
                        });
                    }
                    catch (Exception ex) { writerError = ex; }
                });
                holder.IsBackground = writer.IsBackground = true;
                holder.Start(); writer.Start();
                try
                {
                    Eq(true, validated.WaitOne(5000));
                    Volatile.Write(ref eligible, 0); // window/session eligibility lapses while waiting on the lock
                }
                finally
                {
                    requestHold.Set(); release.Set();
                    bool writerDone = writer.Join(5000), holderDone = holder.Join(5000);
                    Eq(true, writerDone && holderDone);
                }
                if (holderError != null) throw holderError;
                if (writerError != null) throw writerError;
                Eq(2, validations); Eq(AppGpuPreferenceResult.Changed, result);
                Eq(0, fixture.Control.StageReleases); Eq(0, fixture.Ledger.Writes); Eq(0, fixture.Control.Writes);
            }
            using (var fixture = new AppGpuFixture())
            {
                int validations = 0;
                GameMode.AutoGpuEnroll(fixture.Manager, AppGpuPath, delegate
                { if (++validations == 2) throw new InvalidOperationException("read unavailable"); return true; });
                Eq(2, validations); Eq(0, fixture.Control.StageReleases);
                Eq(0, fixture.Ledger.Writes); Eq(0, fixture.Control.Writes);
            }
        }

        private static void AutoGpuSharedHostsAreNotFamilies()
        {
            foreach (string host in new[] { "explorer", "cmd", "powershell", "python", "wscript" })
            {
                string hostPath = @"C:\Fixture\" + host + ".exe";
                Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(hostPath));
                var entries = new[] {
                    new ProcEntry { Pid = 10, Creation = 100, Session = 1, Path = hostPath },
                    new ProcEntry { Pid = 20, ParentPid = 10, Creation = 200, Session = 1, Path = AppGpuPath },
                    new ProcEntry { Pid = 30, ParentPid = 20, Creation = 300, Session = 1, Path = @"C:\Fixture\helper.exe" },
                    new ProcEntry { Pid = 40, ParentPid = 10, Creation = 400, Session = 1, Path = @"C:\Fixture\sibling.exe" } };
                var snapshot = new ProcessSnapshot(entries);
                var visible = new HashSet<int> { 10 };
                foreach (ProcEntry entry in entries)
                    Eq(entry.Pid != 10, GameMode.AutoGpuVisibilityAllows(
                        new GameProcessSnapshot { Pid = entry.Pid, Creation = entry.Creation, Path = entry.Path }, snapshot, visible, 1));
                visible.Add(20); // normal visible apps and their own helper processes still need protection
                foreach (ProcEntry entry in entries)
                    Eq(entry.Pid == 40, GameMode.AutoGpuVisibilityAllows(
                        new GameProcessSnapshot { Pid = entry.Pid, Creation = entry.Creation, Path = entry.Path }, snapshot, visible, 1));
                entries[0].Path = @"C:\Fixture\visibleapp.exe";
                entries[1].Path = hostPath; visible.Remove(20);
                // Must not walk through a windowless shared runtime and charge the standalone app it launched to a more distant ancestor
                Eq(true, GameMode.AutoGpuVisibilityAllows(new GameProcessSnapshot {
                    Pid = 30, Creation = 300, Path = entries[2].Path }, snapshot, visible, 1));
            }
        }

        private static GameMode BoundaryAutoMode(string folder)
        {
            var mode = new GameMode(folder, new SuppressionCore());
            BoundarySet(mode, "active", true); BoundarySet(mode, "autoGpuOn", true);
            BoundaryCall(mode, "SetAutoGpuSessionStamp", 100L);
            return mode;
        }

        private static AppGpuPreferenceResult BoundaryCommit(GameMode mode, AppGpuFixture fixture, long stamp)
        {
            return (AppGpuPreferenceResult)BoundaryCall(mode, "CommitAutoGpu", fixture.Manager,
                AppGpuPath, stamp, (Func<bool>)delegate { return true; });
        }

        private static void AutoGpuCommitSkipsContendedLocks()
        {
            SafetyFolder(delegate(string folder)
            {
                using (var fixture = new AppGpuFixture())
                {
                    GameMode mode = BoundaryAutoMode(folder);
                    foreach (object gate in new[] { GpuPrefStage.MutationGate, BoundaryField(mode, "sync") })
                    {
                        // autoGpuOn=false forces the old implementation to read ActivePreset, which needs sync
                        BoundarySet(mode, "autoGpuOn", false);
                        AppGpuPreferenceResult result = AppGpuPreferenceResult.ReadFailed;
                        Exception failure = null;
                        var writer = new Thread(delegate()
                        { try { result = BoundaryCommit(mode, fixture, 100); } catch (Exception ex) { failure = ex; } });
                        writer.IsBackground = true;
                        bool done;
                        lock (gate) { writer.Start(); done = writer.Join(1500); }
                        Eq(true, writer.Join(5000));
                        if (failure != null) throw failure;
                        Eq(true, done); Eq(AppGpuPreferenceResult.Busy, result);
                    }
                    Eq(0, fixture.Control.Reads); Eq(0, fixture.Ledger.Writes); Eq(0, fixture.Control.Writes);
                }
            });
        }

        private static void AutoGpuCommitDrainsAcrossBoundaries()
        {
            foreach (string boundary in new[] { "session", "disable", "stop" })
            SafetyFolder(delegate(string folder)
            {
                using (var fixture = new AppGpuFixture())
                using (var writing = new ManualResetEvent(false))
                using (var release = new ManualResetEvent(false))
                using (var transitionStarted = new ManualResetEvent(false))
                using (var transitionDone = new ManualResetEvent(false))
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    GameMode mode = BoundaryAutoMode(folder);
                    AppGpuPreferenceResult result = AppGpuPreferenceResult.ReadFailed;
                    Exception writerError = null, transitionError = null;
                    fixture.Control.BeforeWrite = delegate { writing.Set(); Eq(true, release.WaitOne(5000)); };
                    var writer = new Thread(delegate()
                    { try { result = BoundaryCommit(mode, fixture, 100); } catch (Exception ex) { writerError = ex; } });
                    var transition = new Thread(delegate()
                    {
                        try
                        {
                            transitionStarted.Set();
                            if (boundary == "session") BoundaryCall(mode, "SetAutoGpuSessionStamp", 200L);
                            else if (boundary == "disable") mode.AutoGpuPreference = false;
                            else Eq(true, (bool)BoundaryCall(mode, "DrainAsyncShutdown", 4000));
                            // The success-return boundary must come after the preference commit and the handled bookkeeping
                            Eq(1, fixture.Control.Writes); Eq(true, GameMode.AutoGpuAlreadyHandled(AppGpuPath));
                        }
                        catch (Exception ex) { transitionError = ex; }
                        finally { transitionDone.Set(); }
                    });
                    writer.IsBackground = transition.IsBackground = true;
                    writer.Start();
                    bool started = false;
                    try
                    {
                        Eq(true, writing.WaitOne(5000));
                        if (boundary == "stop")
                        {
                            BoundarySet(mode, "stopping", true);
                            Eq(false, (bool)BoundaryCall(mode, "DrainAsyncShutdown", 0));
                        }
                        transition.Start(); started = true;
                        Eq(true, transitionStarted.WaitOne(3000)); Eq(false, transitionDone.WaitOne(100));
                    }
                    finally
                    {
                        release.Set(); bool writerDone = writer.Join(5000);
                        bool boundaryDone = !started || transition.Join(5000);
                        Eq(true, writerDone && boundaryDone);
                    }
                    if (writerError != null) throw writerError;
                    if (transitionError != null) throw transitionError;
                    Eq(AppGpuPreferenceResult.Success, result);
                    fixture.Control.BeforeWrite = null;
                    Eq(AppGpuPreferenceResult.Changed, BoundaryCommit(mode, fixture, 100));
                    Eq(1, fixture.Control.Writes);
                }
            });
        }

        private static object BoundaryBoostPass(GameMode mode, int pid, long creation)
        {
            Type type = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
            object pass = Activator.CreateInstance(type, true);
            type.GetField("DesiredMask").SetValue(pass, BoundaryField(mode, "allMask"));
            type.GetField("RendererPid").SetValue(pass, pid);
            type.GetField("RendererCreation").SetValue(pass, creation);
            BoundaryCall(mode, "ResolvePriorityTarget", pass);
            // Input is synthetic unsaturated data so the test does not follow the test machine's live load at the time
            type.GetField("CpuSaturated").SetValue(pass, false);
            BoundaryCall(mode, "RefreshBoostPriority", pass);
            return pass;
        }

        private static void BoundaryPriority(object pass, uint expected)
        { Eq(expected, (uint)pass.GetType().GetField("PriorityTarget").GetValue(pass)); }

        private static void BoostPriorityIsSessionAndIdentityBound()
        {
            SafetyFolder(delegate(string folder)
            {
                RenderLane.ResetShutdownForTest(); VramSpillProbe.ResetForTest();
                var mode = new GameMode(folder, new SuppressionCore());
                Eq(false, IrqSessionProbe.EnabledSetting);
                BoundaryCall(mode, "ReportBegin", "A");
                object a = BoundaryBoostPass(mode, 101, 1000);
                BoundaryPriority(a, Native.HIGH_PRIORITY_CLASS);
                var verified = (HashSet<int>)BoundaryField(mode, "boostStateVerified");
                verified.Add(101);
                long generation = (long)BoundaryField(mode, "boostIdentityGeneration");

                BoundaryCall(mode, "ReportFinish"); BoundaryCall(mode, "BeginSessionPolicy");
                BoundaryCall(mode, "ReportBegin", "B"); // real direct match-switch entry, no Deactivate
                Eq(true, (long)BoundaryField(mode, "boostIdentityGeneration") > generation);
                object b = BoundaryBoostPass(mode, 101, 1000); // a new match under the same identity must re-audit, old pass rejected
                Eq(false, verified.Contains(101));
                BoundaryPriority(b, Native.HIGH_PRIORITY_CLASS);
                a.GetType().GetField("CpuSaturated").SetValue(a, true);
                Eq(false, (bool)BoundaryCall(mode, "RefreshBoostPriority", a));
                Eq(Native.HIGH_PRIORITY_CLASS, (uint)BoundaryField(mode, "boostPriorityTarget"));
                b.GetType().GetField("CpuSaturated").SetValue(b, true);
                Eq(true, (bool)BoundaryCall(mode, "RefreshBoostPriority", b));
                BoundaryPriority(b, Native.NORMAL_PRIORITY_CLASS);

                object reused = BoundaryBoostPass(mode, 101, 2000);
                BoundaryPriority(reused, Native.HIGH_PRIORITY_CLASS);
                Eq(false, (bool)BoundaryCall(mode, "RefreshBoostPriority", b));
                Eq(Native.HIGH_PRIORITY_CLASS, (uint)BoundaryField(mode, "boostPriorityTarget"));
                object other = BoundaryBoostPass(mode, 102, 2000);
                BoundaryPriority(other, Native.HIGH_PRIORITY_CLASS);
                Eq(false, (bool)BoundaryCall(mode, "RefreshBoostPriority", reused));
                object unknown = BoundaryBoostPass(mode, 102, 0);
                Eq(false, (bool)BoundaryCall(mode, "RefreshBoostPriority", unknown));
                BoundaryPriority(unknown, Native.NORMAL_PRIORITY_CLASS);
                Eq(false, (bool)BoundaryCall(mode, "RefreshBoostPriority", other));
                BoundaryCall(mode, "ReportFinish");
                RenderLane.ResetShutdownForTest(); VramSpillProbe.ResetForTest();
            });
        }
    }
}
#endif
