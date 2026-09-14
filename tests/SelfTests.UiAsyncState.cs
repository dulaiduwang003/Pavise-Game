// File purpose Deterministic page refresh races, data source, scheduling and UI dispatch are all mocked
// Real controls are never shown and never acquire native handles
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunUiAsyncStateRegressionTests()
        {
            Action[] cases = { UiAsyncStartupSnapshotKeepsUserChoice,
                UiAsyncWhitelistOldFullCannotReplaceNewFast,
                UiAsyncNormalSettingsRefreshAndToggle, UiAsyncStartupFailureKeepsFreshReadback,
                UiAsyncSettingsRefreshesCoalesceWithoutLoss, UiAsyncSettingsInactiveAndRebuilt,
                UiAsyncNormalWhitelistRefreshKeepsSelection, UiAsyncWhitelistRefreshesCoalesceWithoutLoss,
                UiAsyncWhitelistInactiveAndRebuilt, UiAsyncDispatchFailuresReleaseSlots,
                UiAsyncFailedReadsKeepKnownState, UiAsyncUpdateHintStateAndRebuild };
            int failed = 0;
            foreach (Action test in cases)
            {
                try { test(); Console.WriteLine("PASS " + test.Method.Name); }
                catch (Exception error)
                {
                    while (error.InnerException != null) error = error.InnerException;
                    Console.WriteLine("FAIL " + test.Method.Name + ": " + error.Message);
                    failed++;
                }
            }
            if (failed != 0) throw new InvalidOperationException("Async UI state failures=" + failed);
            Console.WriteLine("PASS async_ui_state tasks=mocked shaders=mocked whitelist=mocked handles_created=0");
            return cases.Length;
        }

        private static void UiAsyncCheck(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private static void UiAsyncStartupSnapshotKeepsUserChoice()
        {
            foreach (bool initial in new[] { false, true })
                using (var f = new UiAsyncFixture())
                {
                    f.TaskState = initial;
                    f.Auto.SetSilently(initial);
                    f.Shader.Value = " ";
                    f.ShaderBytes = 8192;
                    f.OnMeasure = delegate { f.ToggleStartup(!initial); };
                    f.RefreshSettings();
                    f.RunWork();
                    f.RunPost();
                    UiAsyncCheck(f.TaskState == !initial && f.TaskChanges == 1,
                        "mock startup mutation did not retain the user's choice");
                    UiAsyncCheck(f.Auto.Checked == !initial,
                        "late startup snapshot reversed the user's current switch");
                    UiAsyncCheck(f.Shader.Value == CacheSweep.FmtBytes(f.ShaderBytes),
                        "startup toggle discarded the independently measured shader cache count");
                    UiAsyncCheck(f.ShaderReads == 1 && f.TaskReads == 1 && f.WorkCount == 1
                        && f.Work.Count == 0 && f.Posts.Count == 0,
                        "startup toggle restarted or lost the completed settings scan");
                }
        }

        private static void UiAsyncWhitelistOldFullCannotReplaceNewFast()
        {
            foreach (bool adding in new[] { false, true })
                using (var f = new UiAsyncFixture())
                {
                    List<WhitelistRuleView> oldRows = UiAsyncRows(adding ? new[] { "alpha" } : new[] { "alpha", "beta" }, 0);
                    List<WhitelistRuleView> newRows = UiAsyncRows(adding ? new[] { "alpha", "beta" } : new[] { "alpha" }, -1);
                    f.FastRows = oldRows;
                    f.DeepRows = oldRows;
                    f.OnDeepRead = delegate
                    {
                        f.FastRows = newRows;
                        f.RefreshWhitelist(false);
                        f.White.SelectedIndex = 0;
                    };
                    f.RefreshWhitelist(true);
                    f.RunWork();
                    f.RunPost();
                    UiAsyncCheck(f.UserKeys() == (adding ? "alpha,beta" : "alpha"),
                        "late full whitelist snapshot replaced a newer fast refresh");
                    UiAsyncCheck(f.White.SelectedItem != null && f.White.SelectedItem.ToString() == "alpha",
                        "latest whitelist selection was not preserved");
                }
        }

        private static List<WhitelistRuleView> UiAsyncRows(string[] names, int matches)
        {
            var rows = new List<WhitelistRuleView>();
            foreach (string name in names)
            {
                WhitelistRule rule;
                UiAsyncCheck(WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, name, out rule), "invalid mock rule");
                rows.Add(new WhitelistRuleView(rule, matches));
            }
            return rows;
        }

        private static void UiAsyncNormalSettingsRefreshAndToggle()
        {
            using (var f = new UiAsyncFixture())
            {
                f.TaskState = true; f.ShaderBytes = 8192;
                f.RefreshSettings(); f.Drain();
                UiAsyncCheck(f.Auto.Checked && f.Shader.Value == CacheSweep.FmtBytes(8192),
                    "uncontended settings refresh did not update both controls");
                f.ToggleStartup(false); f.ToggleStartup(true);
                UiAsyncCheck(f.TaskState && f.Auto.Checked && f.TaskChanges == 2 && f.TaskReads == 1 && f.Warnings == 0,
                    "normal startup enable/disable did not preserve command behavior");
            }
        }

        private static void UiAsyncStartupFailureKeepsFreshReadback()
        {
            foreach (bool initial in new[] { false, true })
                using (var f = new UiAsyncFixture())
                {
                    f.TaskState = initial; f.Auto.SetSilently(initial);
                    f.OnTaskChange = delegate(bool enabled)
                    {
                        // Even after a failed command the external change is still visible
                        // the fresh query wins, not the earlier snapshot
                        f.TaskState = !initial;
                        return 1;
                    };
                    f.OnMeasure = delegate { f.ToggleStartup(!initial); };
                    f.RefreshSettings(); f.Drain();
                    UiAsyncCheck(f.Auto.Checked == !initial && f.TaskState == !initial
                        && f.TaskChanges == 1 && f.TaskReads == 2 && f.Warnings == 1,
                        "failed startup command's fresh readback was overwritten or warning lost");
                }
        }

        private static void UiAsyncSettingsRefreshesCoalesceWithoutLoss()
        {
            using (var f = new UiAsyncFixture())
            {
                f.RefreshSettings(); f.RefreshSettings(); f.RefreshSettings();
                UiAsyncCheck(f.Work.Count == 1, "settings refreshes started overlapping workers");
                f.OnMeasure = delegate { f.TaskState = true; f.ShaderBytes = 16384; };
                f.RunWork();
                f.RefreshSettings();
                UiAsyncCheck(f.Work.Count == 0, "settings slot was released before its UI result");
                f.RunPost(); f.Drain();
                UiAsyncCheck(f.WorkCount == 2 && f.TaskReads == 2 && f.Auto.Checked
                    && f.Shader.Value == CacheSweep.FmtBytes(16384), "coalesced settings refresh lost the latest state");
            }
        }

        private static void UiAsyncSettingsInactiveAndRebuilt()
        {
            using (var f = new UiAsyncFixture())
            {
                f.Auto.SetSilently(true);
                f.RefreshSettings(); f.RunWork();
                f.Set("uiActive", false); f.RunPost();
                UiAsyncCheck(f.Auto.Checked && f.Shader.Value == "untouched", "inactive settings accepted a late result");
                f.Set("uiActive", true);
                f.RefreshSettings(); f.RunWork();
                Toggle oldAuto = f.Auto; SettingCard oldShader = f.Shader;
                f.ReplaceSettingsControls(); oldAuto.Dispose(); oldShader.Dispose();
                f.Auto.SetSilently(true);
                f.RefreshSettings(); // Requests a fresh read for the replacement view
                f.RunPost();
                UiAsyncCheck(f.Auto.Checked && f.Shader.Value == "untouched", "old snapshot wrote into rebuilt settings");
                f.TaskState = true; f.Drain();
                UiAsyncCheck(f.Auto.Checked && f.Shader.Value == CacheSweep.FmtBytes(f.ShaderBytes),
                    "rebuilt settings did not receive their coalesced refresh");
                f.RefreshSettings(); f.RunWork();
                f.Set("uiActive", false); f.Auto.Dispose(); f.Shader.Dispose(); f.RunPost();
                UiAsyncCheck(f.Work.Count == 0, "closed settings scheduled new background reads");
            }
        }

        private static void UiAsyncNormalWhitelistRefreshKeepsSelection()
        {
            using (var f = new UiAsyncFixture())
            {
                f.FastRows = UiAsyncRows(new[] { "alpha", "beta" }, -1);
                f.DeepRows = UiAsyncRows(new[] { "alpha", "beta" }, 3);
                f.RefreshWhitelist(true); f.White.SelectedIndex = 1; f.Drain();
                UiAsyncCheck(f.UserKeys() == "alpha,beta" && f.UserViews()[1].CurrentMatches == 3
                    && f.White.SelectedItem.ToString() == "beta", "uncontended whitelist refresh lost data or selection");
                UiAsyncCheck(f.DeepReads == 1 && f.WorkCount == 1, "normal whitelist refresh repeated unexpectedly");
            }
        }

        private static void UiAsyncWhitelistRefreshesCoalesceWithoutLoss()
        {
            using (var f = new UiAsyncFixture())
            {
                f.FastRows = UiAsyncRows(new[] { "alpha" }, -1);
                f.DeepRows = UiAsyncRows(new[] { "alpha" }, 0);
                f.OnDeepRead = delegate
                {
                    f.FastRows = UiAsyncRows(new[] { "alpha", "beta" }, -1);
                    f.DeepRows = UiAsyncRows(new[] { "alpha", "beta" }, 7);
                    f.RefreshWhitelist(true); f.RefreshWhitelist(true);
                    f.White.SelectedIndex = 1;
                };
                f.RefreshWhitelist(true); f.RunWork();
                UiAsyncCheck(f.Work.Count == 0 && f.UserKeys() == "alpha,beta", "busy whitelist did not retain the immediate fast result");
                f.RunPost();
                UiAsyncCheck(f.UserKeys() == "alpha,beta", "obsolete full result replaced latest rules before the follow-up");
                f.Drain();
                UiAsyncCheck(f.WorkCount == 2 && f.DeepReads == 2 && f.UserViews()[1].CurrentMatches == 7
                    && f.White.SelectedItem.ToString() == "beta", "coalesced whitelist refresh lost latest counts or selection");
            }
        }

        private static void UiAsyncWhitelistInactiveAndRebuilt()
        {
            using (var f = new UiAsyncFixture())
            {
                f.FastRows = UiAsyncRows(new[] { "alpha" }, -1);
                f.DeepRows = UiAsyncRows(new[] { "alpha" }, 4);
                f.RefreshWhitelist(true); f.RunWork();
                f.Set("uiActive", false); f.RunPost();
                UiAsyncCheck(f.UserViews()[0].CurrentMatches == -1, "inactive whitelist accepted a late result");
                f.Set("uiActive", true);
                f.RefreshWhitelist(true); f.RunWork();
                ListBox oldList = f.White; EmptyStatePanel oldPanel = f.WhitePanel;
                f.ReplaceWhitelistControls(); oldList.Dispose(); oldPanel.Dispose();
                // Fill the replacement items directly and verify control identity separately
                // without advancing the RefreshWhitelist generation
                f.FastRows = UiAsyncRows(new[] { "beta" }, -1);
                f.Call("FillWhitelist", f.FastRows);
                f.RunPost();
                UiAsyncCheck(f.UserKeys() == "beta", "old full snapshot wrote into rebuilt whitelist");
                f.DeepRows = UiAsyncRows(new[] { "beta" }, 8);
                f.RefreshWhitelist(true); f.Drain();
                UiAsyncCheck(f.UserViews()[0].CurrentMatches == 8, "rebuilt whitelist could not refresh normally");
                f.RefreshWhitelist(true); f.RunWork();
                f.Set("uiActive", false); f.White.Dispose(); f.WhitePanel.Dispose(); f.RunPost();
                UiAsyncCheck(f.Work.Count == 0, "closed whitelist scheduled another scan");
            }
        }

        private static void UiAsyncDispatchFailuresReleaseSlots()
        {
            foreach (bool whitelist in new[] { false, true })
                foreach (bool postFails in new[] { false, true })
                    using (var f = new UiAsyncFixture())
                    {
                        f.TaskState = true;
                        f.FastRows = UiAsyncRows(new[] { "alpha" }, -1);
                        f.DeepRows = UiAsyncRows(new[] { "alpha" }, 2);
                        if (postFails) f.Form.UiStatePostForTest = delegate { throw new InvalidOperationException("mock handle destroyed"); };
                        else f.Form.UiStateWorkQueueForTest = delegate { throw new InvalidOperationException("mock queue rejection"); };
                        if (whitelist) f.RefreshWhitelist(true); else f.RefreshSettings();
                        if (postFails) f.RunWork();
                        f.Form.UiStateWorkQueueForTest = delegate(Action action) { f.WorkCount++; f.Work.Enqueue(action); };
                        f.Form.UiStatePostForTest = delegate(Action action) { f.Posts.Enqueue(action); };
                        if (whitelist) f.RefreshWhitelist(true); else f.RefreshSettings();
                        f.Drain();
                        UiAsyncCheck(whitelist ? f.UserViews()[0].CurrentMatches == 2 : f.Auto.Checked,
                            "dispatch failure permanently wedged a refresh slot");
                    }
        }

        private static void UiAsyncFailedReadsKeepKnownState()
        {
            using (var f = new UiAsyncFixture())
            {
                f.Auto.SetSilently(true);
                f.Form.StartupTaskQueryForTest = delegate { throw new InvalidOperationException("mock task read failed"); };
                f.RefreshSettings(); f.Drain();
                UiAsyncCheck(f.Auto.Checked && f.Shader.Value == CacheSweep.FmtBytes(f.ShaderBytes),
                    "unknown task state was displayed as disabled or blocked independent cache data");
                f.FastRows = UiAsyncRows(new[] { "alpha" }, -1);
                f.DeepRows = UiAsyncRows(new[] { "alpha" }, 9);
                f.OnDeepRead = delegate { throw new InvalidOperationException("mock whitelist scan failed"); };
                f.RefreshWhitelist(true); f.Drain();
                UiAsyncCheck(f.UserKeys() == "alpha" && f.UserViews()[0].CurrentMatches == -1,
                    "failed deep scan replaced valid fast whitelist data");
                f.RefreshWhitelist(true); f.Drain();
                UiAsyncCheck(f.UserViews()[0].CurrentMatches == 9, "failed deep scan blocked the next refresh");
            }
        }

        private static void UiAsyncUpdateHintStateAndRebuild()
        {
            using (var f = new UiAsyncFixture())
            {
                f.Call("RefreshUpdatePresentation");
                UiAsyncCheck(!f.UpdateButton.Visible, "unchecked version showed an update");
                f.Form.NotifyUpdate(new UpdateResult { Ok = true, Latest = App.Version }); f.RunPost();
                UiAsyncCheck(!f.UpdateButton.Visible, "current version showed an update");
                f.Set("uiActive", false);
                f.Form.NotifyUpdate(new UpdateResult { Ok = true, Latest = "99.0.0.0" });
                UiAsyncCheck(!f.UpdateButton.Visible, "worker wrote controls before UI dispatch");
                f.RunPost();
                UiAsyncCheck(f.UpdateButton.Visible && f.UpdateButton.Text == "v99.0.0.0 · 查看更新", "hidden completion lost the version hint");
                f.Form.NotifyUpdate(new UpdateResult { Ok = false }); f.RunPost();
                f.Form.NotifyUpdate(new UpdateResult { Ok = true, Latest = App.Version }); f.RunPost();
                UiAsyncCheck(f.UpdateButton.Visible, "failure or older late response erased the known update");
                LinkLabel old = f.UpdateButton; f.ReplaceUpdateButton(); old.Dispose();
                Lang.Cur = 1; f.Call("RefreshUpdatePresentation");
                UiAsyncCheck(f.UpdateButton.Visible && f.UpdateButton.Text == "v99.0.0.0 · What's new", "rebuild lost the update or language");
                Lang.Cur = 2; f.Call("RefreshUpdatePresentation");
                UiAsyncCheck(f.UpdateButton.Text == "v99.0.0.0 · 更新内容", "Japanese update hint missing");
            }
        }

        private sealed class UiAsyncFixture : IDisposable
        {
            internal readonly PanelForm Form;
            internal Toggle Auto;
            internal SettingCard Shader;
            internal ListBox White;
            internal EmptyStatePanel WhitePanel;
            internal LinkLabel UpdateButton;
            internal readonly Queue<Action> Work = new Queue<Action>();
            internal readonly Queue<Action> Posts = new Queue<Action>();
            internal bool TaskState;
            internal int TaskChanges, TaskReads, ShaderReads, DeepReads, WorkCount, Warnings;
            internal long ShaderBytes = 4096;
            internal Action OnMeasure, OnDeepRead;
            internal Func<bool, int> OnTaskChange;
            internal List<WhitelistRuleView> FastRows = new List<WhitelistRuleView>();
            internal List<WhitelistRuleView> DeepRows = new List<WhitelistRuleView>();
            private readonly List<Control> controls = new List<Control>();
            private readonly Dictionary<FieldInfo, object> peripheralState = new Dictionary<FieldInfo, object>();
            private readonly int oldLanguage = Lang.Cur;

            internal UiAsyncFixture()
            {
                Settings.UseTransientStoreForCurrentProcess();
                Lang.Cur = 0;
                // Auto-exempt rows use the cached vendor name
                // Fill the cache first so FillWhitelist does not enumerate hardware in this test
                foreach (string name in new[] { "tokens", "stamp", "scanned" })
                {
                    FieldInfo field = typeof(PeripheralVendorProbe).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                    peripheralState.Add(field, field.GetValue(null));
                    field.SetValue(null, name == "tokens" ? (object)new string[0]
                        : name == "stamp" ? (object)Environment.TickCount : true);
                }
                Form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                GC.SuppressFinalize(Form);
                Set("uiActive", true);
                ReplaceSettingsControls();
                ReplaceWhitelistControls();
                ReplaceUpdateButton();
                Form.StartupTaskQueryForTest = delegate { TaskReads++; return TaskState; };
                Form.StartupTaskChangeForTest = delegate(bool enabled)
                {
                    TaskChanges++;
                    if (OnTaskChange != null) return OnTaskChange(enabled);
                    TaskState = enabled; return 0;
                };
                Form.StartupTaskWarningForTest = delegate { Warnings++; };
                Form.ShaderMeasureForTest = delegate
                {
                    ShaderReads++;
                    Action action = OnMeasure; OnMeasure = null;
                    if (action != null) action();
                    return ShaderBytes;
                };
                Form.WhitelistQueryForTest = delegate(bool deep)
                {
                    if (!deep) return new List<WhitelistRuleView>(FastRows);
                    DeepReads++;
                    var result = new List<WhitelistRuleView>(DeepRows);
                    Action action = OnDeepRead; OnDeepRead = null;
                    if (action != null) action();
                    return result;
                };
                Form.UiStateWorkQueueForTest = delegate(Action action) { WorkCount++; Work.Enqueue(action); };
                Form.UiStatePostForTest = delegate(Action action) { Posts.Enqueue(action); };
            }

            internal void Set(string name, object value)
            {
                typeof(PanelForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Form, value);
            }

            internal void Call(string name, params object[] args)
            {
                typeof(PanelForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Form, args);
            }

            internal void ReplaceSettingsControls()
            {
                Auto = new Toggle(); Shader = new SettingCard { Value = "untouched" };
                controls.Add(Auto); controls.Add(Shader);
                Set("swAuto", Auto); Set("cardShader", Shader);
            }

            internal void ReplaceWhitelistControls()
            {
                White = new ListBox(); WhitePanel = new EmptyStatePanel();
                controls.Add(White); controls.Add(WhitePanel);
                Set("lstWhite", White); Set("whitePanel", WhitePanel);
            }

            internal void ReplaceUpdateButton()
            {
                UpdateButton = new LinkLabel();
                controls.Add(UpdateButton); Set("btnUpdateHint", UpdateButton);
            }

            internal void RefreshSettings() { Call("RefreshSlowStateAsync"); }
            internal void RefreshWhitelist(bool deep) { Form.RefreshWhitelist(deep); }
            internal void ToggleStartup(bool enabled)
            {
                Auto.SetSilently(enabled);
                Call("OnAutoToggle", Auto, EventArgs.Empty);
            }
            internal void RunWork()
            {
                UiAsyncCheck(Work.Count != 0, "no queued mock worker"); Work.Dequeue()();
            }
            internal void RunPost()
            {
                UiAsyncCheck(Posts.Count != 0, "no queued mock UI result"); Posts.Dequeue()();
            }
            internal void Drain()
            {
                for (int i = 0; i < 20 && (Work.Count != 0 || Posts.Count != 0); i++)
                {
                    if (Work.Count != 0) RunWork();
                    if (Posts.Count != 0) RunPost();
                }
                UiAsyncCheck(Work.Count == 0 && Posts.Count == 0, "refresh follow-up loop did not settle");
            }

            internal string UserKeys()
            {
                var names = new List<string>();
                foreach (WhitelistRuleView view in UserViews()) names.Add(view.Rule.Value);
                return string.Join(",", names.ToArray());
            }

            internal List<WhitelistRuleView> UserViews()
            {
                var views = new List<WhitelistRuleView>();
                foreach (object item in White.Items)
                {
                    var view = item.GetType().GetField("View").GetValue(item) as WhitelistRuleView;
                    if (view != null) views.Add(view);
                }
                return views;
            }

            public void Dispose()
            {
                Set("uiActive", false);
                Work.Clear(); Posts.Clear();
                foreach (Control control in controls)
                {
                    UiAsyncCheck(!control.IsHandleCreated, "mock UI control acquired a native handle");
                    control.Dispose();
                }
                foreach (KeyValuePair<FieldInfo, object> entry in peripheralState) entry.Key.SetValue(null, entry.Value);
                Lang.Cur = oldLanguage;
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
    }
}
#endif
