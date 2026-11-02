#if PAVISE_SELFTEST && PAVISE_PRIVACY_UI
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Privacy UI fixtures cannot start the application runtime"); }
    }

    internal static class PrivacyUiChecks
    {
        private static int checks;
        private static void Check(bool ok, string detail)
        { if (!ok) throw new InvalidOperationException(detail); checks++; }
        private static T Find<T>(Control owner, string name) where T : Control
        { Control[] found = owner.Controls.Find(name, true); Check(found.Length == 1, "Expected one control " + name); return (T)found[0]; }
        private static string Identity(PrivacyAction action, string target) { return action.Key + ":" + (target ?? ""); }
        private sealed class Fixture : IDisposable
        {
            internal readonly Form Owner;
            internal readonly PrivacyOptionsPanel Panel;
            internal readonly Queue<Action> Queue = new Queue<Action>();
            internal readonly Dictionary<string, PrivacyResult> States = new Dictionary<string, PrivacyResult>();
            internal bool MayModify = true, SetAccepted = true, InspectionVisible = true;
            internal bool? LastDesired;
            internal int NativeQueries, Sets, Notifications;
            internal Action Completion;
            internal Fixture(bool elevated = true)
            {
                EnvironmentEnhancementEligibility.SampleForTest = delegate {
                    return new EnvironmentEnhancementEligibility.MemorySnapshot {
                        Installed = 32 * EnvironmentEnhancementEligibility.GiB,
                        Total = 31 * EnvironmentEnhancementEligibility.GiB,
                        Available = 8 * EnvironmentEnhancementEligibility.GiB };
                };
                Owner = new HiddenOwner { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                    Location = new Point(-32000, -32000), ClientSize = new Size(Theme.S(980), Theme.S(800)) };
                Panel = new PrivacyOptionsPanel(delegate { return MayModify; }, delegate { Notifications++; }, elevated);
                Panel.StateForTest = delegate(PrivacyAction action, string target) {
                    PrivacyResult result;
                    if (!States.TryGetValue(Identity(action, target), out result))
                        return new PrivacyResult { State = "unverified", SelectedTarget = target };
                    return result;
                };
                Panel.PostForTest = delegate(Action work) { work(); };
                Panel.InspectionVisibleForTest = delegate { return InspectionVisible; };
                Panel.RefreshForTest = delegate(PrivacyAction action, string target, Action done) {
                    NativeQueries++;
                    Queue.Enqueue(delegate {
                        if (!States.ContainsKey(Identity(action, target))) States[Identity(action, target)] = new PrivacyResult {
                            State = "ready", Supported = true, SelectedTarget = target };
                        done();
                    });
                    return true;
                };
                Panel.SetForTest = delegate(PrivacyAction action, bool enabled, string target, Action done) {
                    Sets++; LastDesired = enabled; if (!SetAccepted) return false;
                    Completion = delegate {
                        States[Identity(action, target)] = new PrivacyResult { State = enabled ? "configured" : "restored",
                            Configured = enabled, Supported = true, SelectedTarget = target };
                        done();
                    };
                    return true;
                };
                Panel.SetBounds(0, 0, Theme.S(930), Theme.S(600));
                Owner.Controls.Add(Panel);
                Panel.Visible = false;
                IntPtr ownerHandle = Owner.Handle;
                Panel.CreateControl();
                if (!Panel.IsHandleCreated) { IntPtr panelHandle = Panel.Handle; }
                Panel.SyncCached();
            }
            internal void Drain()
            { int left = 100; while (Queue.Count > 0) { if (--left == 0) throw new InvalidOperationException("Unbounded refresh loop"); Queue.Dequeue()(); } }
            public void Dispose() { Panel.Dispose(); Owner.Dispose(); }
        }

        private static void Interactions()
        {
            using (var fixture = new Fixture()) {
                CheckRetiredControlsAbsent(fixture.Panel);
                Check(fixture.NativeQueries == 0 && fixture.Sets == 0, "Cache-only construction performs no native queries or mutations");
                fixture.Panel.RefreshAllForTest(); fixture.Panel.RefreshAllForTest();
                Check(fixture.Queue.Count == 1 && fixture.Panel.BusyForTest, "Duplicate refresh queues once");
                fixture.Drain();
                Check(!fixture.Panel.BusyForTest && fixture.NativeQueries == PrivacyActionCatalog.All.Length, "Refresh serially verifies each action without package enumeration");
                Toggle ntfs = Find<Toggle>(fixture.Panel, "privacyToggle" + PrivacyActionCatalog.NtfsMemory);
                Check(ntfs.Enabled && !ntfs.Checked, "Verified NTFS option can be enabled");
                ntfs.PerformClick(); ntfs.PerformClick();
                Check(fixture.Sets == 1 && fixture.Panel.BusyForTest && ntfs.Checked && !ntfs.Enabled, "One explicit user change admitted; duplicate interaction blocked");
                fixture.Panel.SyncCached(); Check(ntfs.Checked, "Pending desired state survives unrelated cache refresh");
                fixture.Completion(); fixture.Completion = null;
                Check(!fixture.Panel.BusyForTest && ntfs.Checked && ntfs.Enabled, "Verified completion restores interactivity");

                fixture.MayModify = false; fixture.Panel.SyncCached(); ntfs.PerformClick();
                Check(fixture.Sets == 1 && !ntfs.Enabled, "Active match guard blocks native changes");
                fixture.MayModify = true; fixture.Panel.SyncCached();
                fixture.SetAccepted = false; ntfs.PerformClick();
                Check(fixture.Sets == 2 && ntfs.Checked && !fixture.Panel.BusyForTest && fixture.Notifications > 0, "Refused request restores checked state without sticking busy");
                fixture.SetAccepted = true;
            }
            using (var fixture = new Fixture()) {
                fixture.Panel.RefreshAllForTest(); fixture.Panel.Dispose(); fixture.Drain();
                Check(fixture.Sets == 0 && fixture.NativeQueries == 1, "Disposed inspection cannot queue further queries");
            }
            using (var fixture = new Fixture()) {
                fixture.Panel.RefreshAllForTest(); fixture.Drain();
                Toggle ntfs = Find<Toggle>(fixture.Panel, "privacyToggle" + PrivacyActionCatalog.NtfsMemory);
                ntfs.PerformClick(); fixture.Panel.Dispose(); fixture.Completion();
                Check(fixture.Sets == 1, "Disposed mutation completion cannot trigger another request");
            }
            using (var fixture = new Fixture()) {
                fixture.Panel.RefreshAllForTest(); fixture.Drain();
                fixture.Panel.PostForTest = null;
                Toggle ntfs = Find<Toggle>(fixture.Panel, "privacyToggle" + PrivacyActionCatalog.NtfsMemory);
                ntfs.PerformClick();
                Exception workerError = null;
                var worker = new Thread(delegate() { try { fixture.Completion(); } catch (Exception error) { workerError = error; } });
                worker.Start(); worker.Join();
                for (int i = 0; i < 20 && fixture.Panel.BusyForTest; i++) Application.DoEvents();
                Check(workerError == null && !fixture.Panel.BusyForTest && ntfs.Checked, "Worker completion marshalled via handle with cross-thread checks enabled");
            }
            using (var fixture = new Fixture(false)) {
                fixture.Panel.RefreshAllForTest(); fixture.Drain();
                Find<Toggle>(fixture.Panel, "privacyToggle" + PrivacyActionCatalog.NtfsMemory).PerformClick();
                Check(fixture.Sets == 0 && fixture.Notifications == 1 && !fixture.Panel.BusyForTest, "Administrator guard prevents native admission");
            }
            using (var fixture = new Fixture()) {
                fixture.Panel.RefreshAllForTest(); fixture.Drain();
                var action = PrivacyActionCatalog.Find(PrivacyActionCatalog.NtfsMemory);
                var stopped = new PrivacyResult { State = "stopped", Supported = true };
                fixture.States[Identity(action, "")] = stopped; fixture.Panel.SyncCached();
                var ntfs = Find<Toggle>(fixture.Panel, "privacyToggle" + action.Key);
                Check(!ntfs.Enabled && !ntfs.Checked, "First fault stops further explicit application without a permit");
                stopped.RetryAllowed = true; fixture.Panel.SyncCached();
                Check(ntfs.Enabled && PrivacyOptionsPanel.Status(stopped).Contains("重试"), "Manual permit unblocks exactly the explicit retry path");
                ntfs.PerformClick(); Check(fixture.Sets == 1, "Manual permit can start one explicit transaction");
                fixture.Completion(); fixture.Completion = null;
                fixture.States[Identity(action, "")] = new PrivacyResult { State = "restore-failed", HasRecovery = true };
                fixture.Panel.SyncCached();
                Check(ntfs.Checked && ntfs.Enabled, "Pending original receipt remains available for restore even when preference is off");
                ntfs.PerformClick(); Check(fixture.Sets == 2, "Pending receipt restoration admitted independently of apply isolation");
                fixture.Completion(); fixture.Completion = null;
                Check(!ntfs.Checked, "Verified recovery releases owned switch state");
            }
        }

        private static void CheckRetiredControlsAbsent(PrivacyOptionsPanel panel)
        {
            Check(PrivacyActionCatalog.All.Length == 2 && PrivacyActionCatalog.All[0].Key == PrivacyActionCatalog.NtfsMemory && PrivacyActionCatalog.All[1].Key == PrivacyActionCatalog.BackgroundGlobal,
                "System environment enhancements must expose only the NTFS option");
            Check(panel.Controls.Count == 2, "Expected NTFS and UWP controls");
            foreach (string key in new[] { "EnvPrivacyDiagnosticsV1", "EnvPrivacyServiceV1", "EnvPrivacyCeipV1",
                "EnvPrivacyAppraiserV1", "EnvPrivacyErrorReportsV1", "EnvPrivacySecurityReportsV1", "EnvPrivacyInputV1",
                "EnvPrivacyPersonalizedV1", "EnvPrivacyCompatibilityV1", "EnvPrivacyArchivesV1" })
            {
                Check(panel.Controls.Find("privacyToggle" + key, true).Length == 0, "Retired option leaves a switch: " + key);
                Check(panel.Controls.Find("privacyCard" + key, true).Length == 0, "Retired option leaves a card: " + key);
            }
            foreach (string name in new[] { "privacyMatchPausePca", "privacyMatchPcaCard", "privacyBackgroundApp" })
                Check(panel.Controls.Find(name, true).Length == 0, "Retired match/background UI remains: " + name);
        }
        private static void RestoreThenEnable()
        {
            using (var fixture = new Fixture()) {
                fixture.Panel.RefreshAllForTest(); fixture.Drain();
                PrivacyAction action = PrivacyActionCatalog.Find(PrivacyActionCatalog.NtfsMemory);
                fixture.States[Identity(action, "")] = new PrivacyResult { State = "restored" };
                fixture.Panel.SyncCached();
                Toggle toggle = Find<Toggle>(fixture.Panel, "privacyToggle" + action.Key);
                Check(toggle.Enabled && !toggle.Checked, "Successful restoration must allow a new checked application request");
                toggle.PerformClick(); Check(fixture.Sets == 1, "Re-enabling after restoration is admitted once");
                fixture.Completion(); Check(toggle.Checked && toggle.Enabled, "Re-enable completed without a disabled toggle");
            }
        }

        private static void UnavailableReasons()
        {
            string[] reasons = { "system", "edition", "component", "external", "interface", "unimplemented", "unknown", null };
            string[] chinese = { "需要较新的系统", "此版本类型不提供该策略", "系统未安装此组件", "现有配置无法安全还原",
                "系统未提供可用接口", "此功能尚未开放", "此项目暂不可用", "此项目暂不可用" };
            string[] english = { "Requires a newer Windows version", "This Windows edition does not provide this policy",
                "This component is not installed", "The existing configuration cannot be safely restored",
                "No supported system interface is available", "This feature is not yet available", "This option is currently unavailable", "This option is currently unavailable" };
            for (int language = 0; language < 2; language++) {
                Lang.Cur = language;
                for (int i = 0; i < reasons.Length; i++) {
                    var state = new PrivacyResult { State = "unsupported", UnavailableReason = reasons[i] };
                    Check(PrivacyOptionsPanel.Status(state) == (language == 0 ? chinese[i] : english[i]),
                        "Unavailable reason must be precise and localized: " + reasons[i]);
                }
                Check(PrivacyOptionsPanel.Status(new PrivacyResult { State = "unverified" })
                    == (language == 0 ? "暂时无法读取状态" : "Unable to read status"), "Read failure must not imply unsupported Windows");
                Check(PrivacyOptionsPanel.Status(new PrivacyResult { State = "managed" })
                    == (language == 0 ? "由系统或组织管理" : "Managed by Windows or your organization"), "Organization policy must remain distinct");
            }
            Lang.Cur = 0;
            PrivacyAction action = PrivacyActionCatalog.Find(PrivacyActionCatalog.NtfsMemory);
            foreach (string reason in reasons) using (var fixture = new Fixture()) {
                fixture.States[Identity(action, "")] = new PrivacyResult { State = "unsupported", UnavailableReason = reason };
                fixture.Panel.SyncCached();
                Toggle toggle = Find<Toggle>(fixture.Panel, "privacyToggle" + action.Key);
                toggle.PerformClick();
                Check(!toggle.Enabled && !toggle.Checked && fixture.Sets == 0,
                    "Unsupported setting without owned data must not start a mutation: " + reason);
            }
            foreach (string state in new[] { "unsupported", "unverified", "managed" })
            foreach (bool configured in new[] { false, true }) using (var fixture = new Fixture()) {
                fixture.States[Identity(action, "")] = new PrivacyResult { State = state, UnavailableReason = "component",
                    Configured = configured, HasRecovery = !configured, Managed = state == "managed" };
                fixture.Panel.SyncCached();
                Toggle toggle = Find<Toggle>(fixture.Panel, "privacyToggle" + action.Key);
                Check(toggle.Enabled && toggle.Checked, "Configured setting or recovery receipt must retain a close path: " + state);
                toggle.PerformClick();
                Check(fixture.Sets == 1 && fixture.LastDesired == false, "Closing must request restoration rather than reapply: " + state);
                fixture.Completion();
                Check(!toggle.Checked && !fixture.Panel.BusyForTest, "Restoration completion must release the owned setting: " + state);
            }
        }

        private static void AutomaticRefresh()
        {
            using (var fixture = new Fixture()) {
                Check(fixture.NativeQueries == 0, "Hidden page must not start inspecting");
                foreach (string name in new[] { "privacyScope", "privacyRefresh", "privacyNetworkProbe", "privacyBackgroundApp" })
                    Check(fixture.Panel.Controls.Find(name, true).Length == 0, "Removed page control remains: " + name);
                foreach (Control control in fixture.Panel.Controls)
                    Check(control is SettingCard, "Privacy page must contain setting cards only");
                fixture.Panel.Visible = true; fixture.Owner.Show(); fixture.Drain(); Application.DoEvents(); fixture.Drain();
                Check(fixture.NativeQueries == PrivacyActionCatalog.All.Length && !fixture.Panel.BusyForTest,
                    "Showing the page automatically checks all switches once");
                fixture.Panel.Visible = false;
                PrivacyAction action = PrivacyActionCatalog.Find(PrivacyActionCatalog.NtfsMemory);
                fixture.States[Identity(action, "")] = new PrivacyResult { State = "configured", Supported = true, Configured = true };
                fixture.Panel.Visible = true; fixture.Drain();
                Check(fixture.NativeQueries == PrivacyActionCatalog.All.Length * 2 && !fixture.Panel.BusyForTest,
                    "Returning to the page rechecks settings without a refresh button");
                Check(Find<Toggle>(fixture.Panel, "privacyToggle" + action.Key).Checked, "Returning to the page reflects updated state");
            }
        }

        private static void RefreshStopsOutsidePage()
        {
            using (var fixture = new Fixture()) {
                fixture.Panel.InspectionVisibleForTest = null;
                fixture.Panel.Visible = true; fixture.Owner.Show();
                Check(fixture.NativeQueries == 1 && fixture.Panel.BusyForTest, "Visible page begins one asynchronous check");
                fixture.Panel.Visible = false; fixture.Drain();
                Check(fixture.NativeQueries == 1 && !fixture.Panel.BusyForTest, "Hidden page stops after the in-flight check");
                fixture.Panel.RefreshAllForTest(); fixture.Panel.RefreshIfNeeded();
                Check(fixture.NativeQueries == 1, "Hidden page cannot start inspection through refresh callbacks");
                fixture.Panel.Visible = true; fixture.Drain(); Application.DoEvents(); fixture.Drain();
                Check(fixture.NativeQueries == 1 + PrivacyActionCatalog.All.Length && !fixture.Panel.BusyForTest,
                    "Returning after cancellation verifies the entire page");
            }
            using (var fixture = new Fixture()) {
                fixture.Panel.RefreshAllForTest(); fixture.MayModify = false; fixture.Drain();
                Check(fixture.NativeQueries == 1 && !fixture.Panel.BusyForTest, "Match entry stops the remaining checks");
                fixture.Panel.RefreshAllForTest(); fixture.Panel.RefreshIfNeeded();
                Check(fixture.NativeQueries == 1, "An active match does not queue inspection");
                fixture.MayModify = true; fixture.Panel.RefreshIfNeeded(); fixture.Drain();
                Check(fixture.NativeQueries == 1 + PrivacyActionCatalog.All.Length && !fixture.Panel.BusyForTest,
                    "Leaving the match permits one complete recheck");
                fixture.Panel.RefreshIfNeeded();
                Check(fixture.Queue.Count == 0, "A verified page does not poll its backends on UI ticks");
            }
            using (var fixture = new Fixture()) {
                fixture.Panel.InspectionVisibleForTest = null;
                fixture.Panel.Visible = true; fixture.Owner.Show();
                fixture.Panel.Visible = false; fixture.Panel.Visible = true; fixture.Drain();
                Check(fixture.NativeQueries == 1 + PrivacyActionCatalog.All.Length && !fixture.Panel.BusyForTest,
                    "Returning before the old query ends restarts from the first setting once");
                Toggle toggle = Find<Toggle>(fixture.Panel, "privacyToggle" + PrivacyActionCatalog.NtfsMemory);
                toggle.Checked = true;
                Check(fixture.Sets == 1 && fixture.Panel.BusyForTest, "Explicit write begins normally");
                fixture.Panel.Visible = false; fixture.Completion();
                Check(!fixture.Panel.BusyForTest && toggle.Checked, "Hiding the page does not discard a write completion");
            }
        }
        private static void Layout(string output, float scale, bool light, int language)
        {
            using (var fixture = new Fixture()) {
                CheckRetiredControlsAbsent(fixture.Panel);
                fixture.States[PrivacyActionCatalog.NtfsMemory + ":"] = new PrivacyResult { State = "partial", Supported = true };
                fixture.Panel.RefreshAllForTest(); fixture.Drain();
                fixture.Panel.Visible = true; fixture.Owner.Show();
                fixture.Drain();
                var cards = new List<PerformanceFeatureCard>();
                foreach (PrivacyAction action in PrivacyActionCatalog.All) {
                    var card = Find<PerformanceFeatureCard>(fixture.Panel, "privacyCard" + action.Key);
                    cards.Add(card);
                    Check(card.Top >= 0 && card.Right <= fixture.Panel.Width, "Cards remain within viewport");
                    Check(card.Hosted.Right <= card.Width && card.Hosted.Bottom <= card.Height, "Hosted switches remain within cards");
                    using (var bitmap = new Bitmap(card.Width, card.Height)) card.DrawToBitmap(bitmap, card.ClientRectangle);
                    Check(card.ClientRectangle.Contains(card.StatusBounds), "Real status stays visible at DPI " + scale);
                    int measured = TextRenderer.MeasureText(card.Desc, Theme.UI(9f, false), new Size(card.DetailBounds.Width, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
                    Check(card.DetailBounds.Height >= measured && card.ClientRectangle.Contains(card.DetailBounds), "Full text is not clipped");
                }
                Check(cards.Count < 2 || cards[0].Top == cards[1].Top, "Wide panel places cards side by side");
                for (int i = 0; i < cards.Count; i++) for (int j = i + 1; j < cards.Count; j++)
                    Check(!cards[i].Bounds.IntersectsWith(cards[j].Bounds), "Privacy cards overlap");
                if (scale == 1f && !light && language == 0) {
                    using (var bitmap = new Bitmap(fixture.Panel.Width, Math.Min(fixture.Panel.Height, Theme.S(1000)))) {
                        fixture.Panel.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.Combine(output, "system-environment-panel-100-dark-zh.png"));
                    }
                }
                foreach (string state in new[] { "configured", "partial", "pending-reboot", "restore-pending-reboot", "restored", "restore-failed", "managed", "unsupported", "stopped", "failed", "ready", "off", "unverified" }) {
                    string label = PrivacyOptionsPanel.Status(new PrivacyResult { State = state });
                    Check(label.Length > 0 && !label.Contains("全部遥测关闭"), "Truthful localized status " + state);
                }
                foreach (int width in new[] { 250, 480, 930 }) {
                    fixture.Panel.Width = Theme.S(width);
                    var first = Find<PerformanceFeatureCard>(fixture.Panel, "privacyCard" + PrivacyActionCatalog.NtfsMemory);
                    Check(first.Top == 0, "Switch cards start without an empty header");
                    if (width < 760) {
                        Check(first.Width == fixture.Panel.Width, "Narrow panel uses the full single column");
                        foreach (var card in cards) Check(card.Left == 0 && card.Right <= fixture.Panel.Width, "Narrow cards stay in one column");
                    } else Check(first.Width < fixture.Panel.Width && (cards.Count < 2 || cards[1].Top == first.Top), "Wide panel uses two columns");
                }
            }
        }

        private sealed class HiddenOwner : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            IntPtr desktop = CreateDesktop("PavisePrivacyUi-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
            if (desktop == IntPtr.Zero) { Console.Error.WriteLine("Fixture desktop unavailable"); return 1; }
            int result = 1;
            try {
                var thread = new Thread(delegate() {
                    if (!SetThreadDesktop(desktop)) { Console.Error.WriteLine("Cannot assign private fixture desktop"); return; }
                    try {
                        Settings.UseTransientStoreForCurrentProcess(); PrivacyControl.ResetForTest();
                        Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                        Dpi.Init(); Lang.Init(); Dpi.Scale = 1f; Lang.Cur = 0;
                        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                        Control.CheckForIllegalCrossThreadCalls = true;
                        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                        Interactions(); RestoreThenEnable(); UnavailableReasons(); AutomaticRefresh(); RefreshStopsOutsidePage(); int runs = 0;
                        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
                        foreach (bool light in new[] { false, true })
                        foreach (int language in new[] { 0, 1 }) {
                            Dpi.Scale = scale; Theme.DropFontCache(); Theme.SetLight(light); Lang.Cur = language;
                            Layout(args[0], scale, light, language); runs++;
                        }
                        Console.WriteLine("PASS privacy UI assertions=" + checks + " combinations=" + runs
                            + " cache_only_build=true native_mutations=mocked settings=transient fixture_desktop=private input_desktop_unchanged=true application_started=false");
                        result = 0;
                    } catch (Exception error) { Console.Error.WriteLine(error); }
                    Console.Out.Flush(); Console.Error.Flush(); Environment.Exit(result);
                });
                thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
            } finally { CloseDesktop(desktop); }
        }
    }
}
#endif
