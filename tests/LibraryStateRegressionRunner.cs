#if PAVISE_SELFTEST && PAVISE_LIBRARY_STATE_REGRESSION
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class LibraryStateRegressionRunner
    {
        private static int assertions, suites, failed;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        [STAThread]
        private static int Main()
        {
            using (var watchdog = new System.Threading.Timer(delegate { Environment.Exit(124); }, null, 30000, Timeout.Infinite))
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                Run("normal library scan holds the slot through UI consumption", NormalRunning);
                Run("library additions coalesce one fresh snapshot without stale replacement", CoalescedRunning);
                Run("rebuilt library rejects the old list snapshot", RebuiltRunning);
                Run("inactive and disposed lists reject late snapshots and release busy", InactiveRunning);
                Run("library queue dispatch and query failures remain retryable", FailedRunning);
                Run("whitelist operation commits before one fresh UI result", NormalWhitelist);
                Run("whitelist queue failure unlocks the next user operation", WhitelistQueueFailure);
                Run("whitelist dispatch failure preserves mutation and remains retryable", WhitelistPostFailure);
                Run("whitelist operation failure refreshes actual state and unlocks", WhitelistOperationFailure);
                Run("whitelist completion refreshes rebuilt controls", RebuiltWhitelist);
                Run("existing async UI refresh regression cases", delegate {
                    Check(SelfTests.RunUiAsyncStateRegressionTests() == 12, "Existing async refresh fixtures did not all execute");
                });
                Console.WriteLine("RESULT suites=" + suites + " failed=" + failed + " assertions=" + assertions
                    + " legacy_async_cases=12 settings=transient handles_created=0 runtime_started=false probes=mocked");
                return failed == 0 ? 0 : 1;
            }
        }
        private static void Run(string title, Action test)
        {
            suites++;
            try { test(); Console.WriteLine("PASS " + title); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failed++; Console.Error.WriteLine("FAIL " + title + ": " + error);
            }
        }
        private static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException(message); }
        private static void NormalRunning()
        {
            using (var f = new Fixture())
            {
                f.SetGames(true, "alpha"); f.NextRunning = false;
                f.RefreshRunning(); f.RunWork();
                Check(f.Games.Items[0].Running && f.Int("runningBusy") == 1, "Worker changed the UI or released its slot before dispatch");
                f.RunPost();
                Check(!f.Games.Items[0].Running && f.Int("runningBusy") == 0 && f.RunningReads == 1, "Normal scan did not publish one result");
            }
        }
        private static void CoalescedRunning()
        {
            using (var f = new Fixture())
            {
                f.SetGames(true, "alpha"); f.NextRunning = false;
                f.RefreshRunning(); f.RunWork();
                f.SetGames(true, "alpha", "beta"); f.RefreshRunning(); f.RefreshRunning();
                Check(f.Work.Count == 0 && f.Int("runningBusy") == 1, "New library state overlapped a queued older result");
                f.RunPost();
                Check(f.Games.Items[0].Running && f.Games.Items[1].Running, "Old paths snapshot replaced newer library state");
                Check(f.Work.Count == 1, "Multiple forced refreshes were not coalesced into one follow-up");
                f.NextRunning = false; f.Drain();
                Check(f.RunningReads == 2 && f.LastPaths.Count == 2 && !f.Games.Items[0].Running && !f.Games.Items[1].Running,
                    "Fresh follow-up missed the added game or ran duplicate scans");
            }
        }
        private static void RebuiltRunning()
        {
            using (var f = new Fixture())
            {
                f.SetGames(true, "alpha"); f.NextRunning = false;
                f.RefreshRunning(); f.RunWork();
                GameLibraryList old = f.Games; f.ReplaceGames(); old.Dispose();
                f.SetGamesWithoutRevision(true, "alpha"); f.RefreshRunning(); f.RunPost();
                Check(f.Games.Items[0].Running && f.Work.Count == 1, "Old result wrote into rebuilt controls or lost the follow-up");
                f.NextRunning = false; f.Drain();
                Check(!f.Games.Items[0].Running && f.Int("runningBusy") == 0, "Rebuilt list could not acquire a new refresh slot");
            }
        }
        private static void InactiveRunning()
        {
            foreach (bool dispose in new[] { false, true })
                using (var f = new Fixture())
                {
                    f.SetGames(false, "alpha"); f.NextRunning = true;
                    f.RefreshRunning(); f.RunWork(); f.Set("uiActive", false);
                    if (dispose) f.Games.Dispose();
                    f.RunPost();
                    Check(!f.Games.Items[0].Running && f.Int("runningBusy") == 0 && f.Work.Count == 0,
                        "Hidden or disposed controls accepted a late snapshot or held a stale slot");
                    if (!dispose) { f.Set("uiActive", true); f.RefreshRunning(); f.Drain(); Check(f.Games.Items[0].Running, "Reopened library could not refresh"); }
                }
        }
        private static void FailedRunning()
        {
            foreach (int failure in new[] { 0, 1, 2 })
                using (var f = new Fixture())
                {
                    f.SetGames(true, "alpha"); f.NextRunning = false;
                    if (failure == 0) f.Form.UiStateWorkQueueForTest = delegate { throw new InvalidOperationException("Queue rejected"); };
                    if (failure == 1) f.Form.UiStatePostForTest = delegate { throw new InvalidOperationException("Dispatch rejected"); };
                    if (failure == 2) f.FailRunningQuery = true;
                    f.RefreshRunning(); f.Drain();
                    Check(f.Int("runningBusy") == 0 && f.Games.Items[0].Running, "A failed scan stranded busy or published unknown state");
                    f.ConnectQueues(); f.FailRunningQuery = false; f.RefreshRunning(); f.Drain();
                    Check(!f.Games.Items[0].Running && f.Int("runningBusy") == 0, "Failed scan permanently prevented a fresh scan");
                }
        }
        private static void NormalWhitelist()
        {
            using (var f = new Fixture())
            {
                f.Form.RefreshWhitelist(false); f.Op(delegate { f.SetWhite("beta"); return null; });
                f.RunWork();
                Check(f.Int("whiteOpBusy") == 1 && f.WhiteKey() == "alpha", "Whitelist operation released before UI dispatch or wrote controls from its worker");
                f.RunPost(); f.Drain();
                Check(f.WhiteKey() == "beta" && f.Int("whiteOpBusy") == 0 && f.Warnings == 0, "Whitelist completion did not echo the committed model");
            }
        }
        private static void WhitelistQueueFailure()
        {
            using (var f = new Fixture())
            {
                int mutations = 0;
                f.Form.UiStateWorkQueueForTest = delegate { throw new InvalidOperationException("Queue rejected"); };
                f.Op(delegate { mutations++; return null; });
                Check(mutations == 0 && f.Int("whiteOpBusy") == 0 && f.Warnings == 1, "Rejected whitelist queue remained busy, mutated or lost the warning");
                f.ConnectQueues(); f.Op(delegate { mutations++; f.SetWhite("beta"); return null; }); f.Drain();
                Check(mutations == 1 && f.WhiteKey() == "beta" && f.Int("whiteOpBusy") == 0, "Queue failure wedged the next whitelist click");
            }
        }
        private static void WhitelistPostFailure()
        {
            using (var f = new Fixture())
            {
                int mutations = 0;
                f.Form.UiStatePostForTest = delegate { throw new InvalidOperationException("Dispatch rejected"); };
                f.Op(delegate { mutations++; f.SetWhite("beta"); return null; }); f.Drain();
                Check(mutations == 1 && f.Int("whiteOpBusy") == 0, "Lost UI dispatch discarded a committed mutation or held the operation gate");
                f.ConnectQueues(); f.Form.RefreshWhitelist(false);
                Check(f.WhiteKey() == "beta", "A later fast refresh did not echo the mutation after dispatch failure");
                f.Op(delegate { mutations++; f.SetWhite("gamma"); return null; }); f.Drain();
                Check(mutations == 2 && f.WhiteKey() == "gamma", "Dispatch failure blocked the next whitelist operation");
            }
        }
        private static void WhitelistOperationFailure()
        {
            using (var f = new Fixture())
            {
                f.Op(delegate { throw new InvalidOperationException("Mutation failed"); }); f.Drain();
                Check(f.Warnings == 1 && f.WhiteKey() == "alpha" && f.Int("whiteOpBusy") == 0, "Failed operation left optimistic UI or held the busy gate");
                f.Op(delegate { f.SetWhite("beta"); return null; }); f.Drain();
                Check(f.WhiteKey() == "beta", "Failed operation blocked a later valid click");
            }
        }
        private static void RebuiltWhitelist()
        {
            using (var f = new Fixture())
            {
                f.Op(delegate { f.SetWhite("beta"); return null; }); f.RunWork();
                ListBox old = f.White; EmptyStatePanel oldPanel = f.WhitePanel;
                f.ReplaceWhite(); old.Dispose(); oldPanel.Dispose(); f.RunPost(); f.Drain();
                Check(f.WhiteKey() == "beta" && f.Int("whiteOpBusy") == 0, "Whitelist completion wrote disposed controls or failed to refresh the replacement");
            }
        }
        private sealed class Fixture : IDisposable
        {
            internal readonly PanelForm Form;
            internal GameLibraryList Games;
            internal ListBox White;
            internal EmptyStatePanel WhitePanel;
            internal readonly Queue<Action> Work = new Queue<Action>(), Posts = new Queue<Action>();
            internal List<string> LastPaths;
            internal bool NextRunning, FailRunningQuery;
            internal int RunningReads, Warnings;
            private List<WhitelistRuleView> whiteRows;
            private readonly List<Control> controls = new List<Control>();
            private readonly Dictionary<FieldInfo, object> vendor = new Dictionary<FieldInfo, object>();
            internal Fixture()
            {
                Settings.UseTransientStoreForCurrentProcess(); Lang.Cur = 0;
                foreach (string name in new[] { "tokens", "stamp", "scanned" })
                {
                    FieldInfo field = typeof(PeripheralVendorProbe).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                    vendor.Add(field, field.GetValue(null));
                    field.SetValue(null, name == "tokens" ? (object)new string[0] : name == "stamp" ? (object)Environment.TickCount : true);
                }
                Form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm)); GC.SuppressFinalize(Form);
                Set("uiActive", true);
                var page = new DBPanel(); controls.Add(page); Set("curPage", page); Set("pageLibrary", page);
                ReplaceGames(); ReplaceWhite(); SetWhite("alpha"); ConnectQueues();
                Form.RunningQueryForTest = delegate(List<string> paths)
                {
                    RunningReads++; LastPaths = new List<string>(paths);
                    if (FailRunningQuery) throw new InvalidOperationException("Snapshot failed");
                    var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (string path in paths) state[path] = NextRunning; return state;
                };
                Form.WhitelistQueryForTest = delegate { return new List<WhitelistRuleView>(whiteRows); };
                Form.WhitelistWarningForTest = delegate { Warnings++; };
            }
            internal void Set(string name, object value) { typeof(PanelForm).GetField(name, Hidden).SetValue(Form, value); }
            internal int Int(string name) { return (int)typeof(PanelForm).GetField(name, Hidden).GetValue(Form); }
            internal void Call(string name, params object[] args) { typeof(PanelForm).GetMethod(name, Hidden).Invoke(Form, args); }
            internal void ConnectQueues()
            {
                Form.UiStateWorkQueueForTest = delegate(Action action) { Work.Enqueue(action); };
                Form.UiStatePostForTest = delegate(Action action) { Posts.Enqueue(action); };
            }
            internal void ReplaceGames() { Games = new GameLibraryList(); controls.Add(Games); Set("lstGames", Games); }
            internal void ReplaceWhite()
            {
                White = new ListBox(); WhitePanel = new EmptyStatePanel(); controls.Add(White); controls.Add(WhitePanel);
                Set("lstWhite", White); Set("whitePanel", WhitePanel);
            }
            internal void SetGames(bool running, params string[] names)
            { Set("runningVersion", Int("runningVersion") + 1); SetGamesWithoutRevision(running, names); }
            internal void SetGamesWithoutRevision(bool running, params string[] names)
            {
                var items = new List<GameLibraryItem>();
                foreach (string name in names) items.Add(new GameLibraryItem(new GameProfile {
                    Id = name, Name = name, ExecutablePath = @"C:\InertFixture\" + name + ".exe" }, running, false));
                Games.SetItems(items);
            }
            internal void SetWhite(string name)
            {
                WhitelistRule rule; Check(WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, name, out rule), "Invalid mock whitelist rule");
                whiteRows = new List<WhitelistRuleView> { new WhitelistRuleView(rule, -1) };
            }
            internal string WhiteKey()
            {
                foreach (object item in White.Items)
                {
                    var view = item.GetType().GetField("View").GetValue(item) as WhitelistRuleView;
                    if (view != null) return view.Rule.Value;
                }
                return "";
            }
            internal void RefreshRunning() { Call("RefreshGameRunningStates", true); }
            internal void Op(Func<string> op) { Call("RunWhitelistOp", op); }
            internal void RunWork() { Check(Work.Count != 0, "No mock worker queued"); Work.Dequeue()(); }
            internal void RunPost() { Check(Posts.Count != 0, "No mock UI result queued"); Posts.Dequeue()(); }
            internal void Drain()
            {
                for (int i = 0; i < 20 && (Work.Count != 0 || Posts.Count != 0); i++)
                { if (Work.Count != 0) RunWork(); if (Posts.Count != 0) RunPost(); }
                Check(Work.Count == 0 && Posts.Count == 0, "Mock UI refresh did not settle");
            }
            public void Dispose()
            {
                Set("uiActive", false); Work.Clear(); Posts.Clear();
                foreach (Control control in controls) { Check(!control.IsHandleCreated, "Fixture created a native control handle"); control.Dispose(); }
                foreach (var prior in vendor) prior.Key.SetValue(null, prior.Value);
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
    }
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Library state fixtures cannot start the optimization runtime"); }
    }
}
#endif
