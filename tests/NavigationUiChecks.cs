#if PAVISE_NAV_BENCH && PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class NavigationUiChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks;
        private static void Check(bool value, string message)
        { if (!value) throw new Exception(message); checks++; }
        private static T Field<T>(object host, string name)
        {
            for (Type type = host.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, Hidden | BindingFlags.DeclaredOnly);
                if (field != null) return (T)field.GetValue(host);
            }
            throw new MissingFieldException(host.GetType().Name, name);
        }

        private static object Call(object host, string name, params object[] args)
        {
            for (Type type = host.GetType(); type != null; type = type.BaseType)
            {
                MethodInfo method = type.GetMethod(name, Hidden | BindingFlags.DeclaredOnly);
                if (method == null) continue;
                try { return method.Invoke(host, args); }
                catch (TargetInvocationException error) { throw new Exception(name, error.InnerException); }
            }
            throw new MissingMethodException(host.GetType().Name, name);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length != 1 || Path.GetFullPath(args[0]).TrimEnd('\\') != AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')) return 2;
            Settings.UseTransientStoreForCurrentProcess();
            Logger.LogPath = Path.Combine(args[0], "navigation.log");
            PanelForm.SuppressUiWorkersForTest = true;
            Dpi.Init(); Lang.Init();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Check(typeof(PanelForm).GetField("advancedPanel", Hidden) == null, "Old category popup remains");
                Check(typeof(PanelForm).Assembly.GetType("PaviseApp.AdvancedEntryButton") == null, "Removed overview entry still ships");
                foreach (string stored in new[] { "", "garbage", "-1", "0", "3", "12", "999999", "4", "11" })
                {
                    Settings.SaveStr("DeepTuningLastPage", stored);
                    int page = (int)typeof(PanelForm).GetMethod("LoadLastAdvancedPage", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                    Check(page == (stored == "4" ? 4 : stored == "11" ? 11 : 2), "Remembered category validation failed: " + stored);
                }
                foreach (int lang in new[] { 0, 1, 2 })
                foreach (bool light in new[] { false, true }) RunWindow(args[0], lang, light);
                CheckScaledPosition();
                CheckScaledRail(args[0]);
                TransitionUiChecks.Run(args[0]);
                Console.WriteLine("PASS navigation assertions=" + checks + " application_started=false workers_started=false settings=transient");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void RunWindow(string output, int language, bool light)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.SaveStr("DeepTuningLastPage", "999999");
            CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(new[] { new KeyValuePair<uint, ulong>(32u << 20, 0xFF) });
            AutoCores.SupportedForTest = true;
            AutoCores.AllocatedForTest = delegate { return 0; };
            Lang.Cur = language; Theme.SetLight(light);
            string data = Path.Combine(output, "fixture-" + language + "-" + light);
            Directory.CreateDirectory(data);
            var core = new SuppressionCore();
            var tamer = new Tamer(core);
            var mode = new GameMode(data, core);
            using (var icon = IconArt.MakeIcon(24))
            using (var form = new OffscreenPanelForm(tamer, mode, icon))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-20000, -20000); form.Show();
                Check(!form.UiActive, "UI refresh workers must stay paused");
                Check(Field<object>(tamer, "worker") == null, "Tamer worker unexpectedly started");
                Check(Field<ModeButton>(form, "modeButton").SourceText == Lang.T("mode.source.global"),
                    "Top mode control does not show the global policy source");
                Check(PanelForm.ModeSourceText("NEBULA", true) == Lang.F("mode.source.game.short", "NEBULA")
                    && PanelForm.ModeSourceText("NEBULA", false) == Lang.F("mode.source.game", "NEBULA"),
                    "Per-game policy source labels diverged");
                var searchKey = new KeyEventArgs(Keys.Control | Keys.F);
                Call(form, "OnEscHide", null, searchKey);
                Check(!searchKey.Handled && !searchKey.SuppressKeyPress, "Retired Ctrl+F shortcut is still intercepted");
                var banner = Field<ModuleBanner>(form, "policyBanner");
                int redundantBannerPaints = 0;
                banner.Invalidated += delegate { redundantBannerPaints++; };
                Call(form, "UpdateModePresentation", true);
                Call(form, "UpdateModePresentation", true);
                Check(redundantBannerPaints == 0, "Stable mode polling invalidated the hidden policy banner");
                Check(Field<int>(form, "lastAdvancedPage") == (int)PageId.Policy, "Invalid remembered page was not rejected");
                int prompts = 0;
                form.DeepTuningConfirmationForTest = delegate { prompts++; return false; };
                form.SelectPageForTest((int)PageId.Log);
                var main = Field<NavRail>(form, "nav");
                main.InvokeItem((int)PageId.AntiCheat);
                Check(main.ItemAtSlot(2) == (int)PageId.AntiCheat && main.Visible
                    && main.Selected == (int)PageId.AntiCheat && prompts == 0,
                    "Anti-cheat must open from main navigation without an advanced-entry warning");
                main.InvokeItem((int)PageId.Log);
                main.InvokeItem((int)PageId.Policy);
                Check(main.Selected == (int)PageId.Log && main.Visible && prompts == 1, "Canceled entry changed the current page");
                form.DeepTuningConfirmationForTest = delegate { prompts++; return true; };
                main.InvokeItem((int)PageId.Policy);
                var tuning = Field<NavRail>(form, "tuningNav");
                Check(main.Selected == (int)PageId.Policy && tuning.Visible && !main.Visible, "Entry did not directly open a section");
                CheckTuningHeader(form);
                Check(prompts == 2, "Entry warning missing");
                Check(Field<int[]>(tuning, "order").Length == 4, "Advanced navigation must expose four sections");
                foreach (PageId page in new[] { PageId.Interrupt, PageId.Environment, PageId.Graphics, PageId.Policy })
                {
                    tuning.InvokeItem((int)page);
                    Check(main.Selected == (int)page && tuning.Selected == (int)page && tuning.Visible, "Category handoff failed");
                    Check(Field<int>(form, "pageBaseLeft") == main.Width, "Page overlaps the persistent navigation");
                }
                Check(prompts == 2, "In-area switching repeated the warning");
                var policyTabs = Field<TechTabs>(form, "policyTabs");
                Check(Field<DBPanel[]>(form, "policyTabPanels").Length == 3, "Policy must retain three sub-tabs");
                policyTabs.Index = 2;
                tuning.InvokeItem((int)PageId.Environment);
                Check(Field<DBPanel>(form, "coreScrollPanel") != null, "Core scheduling lost its content panel");
                CheckAutoCorePage(form, Lang.T("schedule.auto.state.off"), Lang.T("schedule.auto.uniform"));
                tuning.InvokeItem((int)PageId.Graphics);
                var gpuPanels = Field<DBPanel[]>(form, "gfxTabPanels");
                var gpuTabs = Field<TechTabs>(form, "gfxTabs");
                bool naturalGpuOverflow = gpuPanels[0].DisplayRectangle.Height > gpuPanels[0].ClientSize.Height;
                int overflow = gpuPanels[0].ClientSize.Height + Theme.S(240);
                if (gpuPanels[0].AutoScrollMinSize.Height < overflow)
                    gpuPanels[0].AutoScrollMinSize = new Size(0, overflow);
                gpuPanels[0].AutoScrollPosition = new Point(0, Theme.S(200));
                int scrollY = gpuPanels[0].AutoScrollPosition.Y;
                Check(scrollY < 0, "Fixture must exercise real overflow");
                gpuTabs.Index = 1; gpuTabs.Index = 0;
                Check(gpuPanels[0].AutoScrollPosition.Y == scrollY, "Switching an inner tab lost scroll");
                tuning.InvokeItem((int)PageId.Policy);
                Check(policyTabs.Index == 2, "Switching category reset its sub-tab");
                tuning.InvokeItem((int)PageId.Graphics);
                Check(gpuPanels[0].AutoScrollPosition.Y == scrollY, "Switching category lost scroll");

                gpuTabs.Index = 1;
                Theme.SetLight(!light);
                Call(form, "RebuildUi");
                CheckRebuildCaches(form);
                main = Field<NavRail>(form, "nav"); tuning = Field<NavRail>(form, "tuningNav");
                Check(main.Selected == (int)PageId.Graphics && tuning.Visible, "Rebuild left the advanced section");
                CheckTuningHeader(form);
                Check(Field<TechTabs>(form, "gfxTabs").Index == 1, "Rebuild lost the active sub-tab");
                DBPanel rebuiltGpuPanel = Field<DBPanel[]>(form, "gfxTabPanels")[0];
                Field<TechTabs>(form, "gfxTabs").Index = 0;
                Check(!naturalGpuOverflow || rebuiltGpuPanel.AutoScrollPosition.Y == scrollY,
                    "Rebuild lost an inactive tab's scroll");
                Check(Field<int>(form, "mainReturnPage") == (int)PageId.Log, "Rebuild overwrote the return destination");
                tuning.InvokeItem((int)PageId.Policy);
                Check(Field<TechTabs>(form, "policyTabs").Index == 2, "Rebuild lost an inactive page's sub-tab");
                Check(Array.IndexOf(Field<int[]>(tuning, "order"), (int)PageId.CoreScheduling) < 0,
                    "Core scheduling must no longer be an advanced destination");
                tuning.InvokeItem((int)PageId.Graphics);
                Field<AdvancedBackBar>(form, "advBackBar").BackRequested();
                Check(main.Selected == (int)PageId.Log && main.Visible, "Return did not restore the entry page");
                main.InvokeItem((int)PageId.Policy);
                Check(main.Selected == (int)PageId.Graphics && prompts == 3, "Re-entry did not restore the last section");
                Check(Settings.LoadStr("DeepTuningLastPage", "") == ((int)PageId.Graphics).ToString(), "Last section was not persisted");

                CheckProfileCoreNavigation(form, mode, data);
                CheckAdaptiveCardFollowsPreset(form, mode);

                Theme.SetLight(light);
                Call(form, "RebuildUi");
                CheckRebuildCaches(form);
                form.SelectPageForTest((int)PageId.Environment);
                Check(Field<DBPanel[]>(form, "envTabPanels").Length == 4,
                    "Environment must include Performance, Low latency, Graphics and Privacy tabs");
                Settle(form);
                Save(form, Path.Combine(output, "environment-" + language + "-" + (light ? "light" : "dark") + ".png"));
                form.SelectPageForTest((int)PageId.Policy);
                Field<TechTabs>(form, "policyTabs").Index = 0;
                Settle(form);
                Save(form, Path.Combine(output, "tuning-" + language + "-" + (light ? "light" : "dark") + ".png"));
                form.SelectPageForTest((int)PageId.CoreScheduling);
                Settle(form);
                Save(form, Path.Combine(output, "core-" + language + "-" + (light ? "light" : "dark") + ".png"));
                form.SelectPageForTest((int)PageId.AntiCheat);
                Settle(form);
                Save(form, Path.Combine(output, "anticheat-" + language + "-" + (light ? "light" : "dark") + ".png"));
                Field<AdvancedBackBar>(form, "advBackBar").BackRequested();
                Settle(form);
                Save(form, Path.Combine(output, "main-" + language + "-" + (light ? "light" : "dark") + ".png"));
                form.SelectPageForTest((int)PageId.Overview);
                CheckOverviewFooter(Field<DBPanel>(form, "pageOverview"));
                Settle(form);
                Save(form, Path.Combine(output, "overview-" + language + "-" + (light ? "light" : "dark") + ".png"));
                Call(form, "ApplyUpdateResult", new UpdateResult { Ok = true, Latest = App.Version });
                Check(!Field<DBPanel>(form, "updateBadge").Visible,
                    "Current version must not show a NEW badge");
                form.SelectPageForTest((int)PageId.About);
                Settle(form);
                Save(form, Path.Combine(output, "about-" + language + "-" + (light ? "light" : "dark") + ".png"));
                form.SelectPageForTest((int)PageId.Overview);
                Settle(form);
                Call(form, "ApplyUpdateResult", new UpdateResult { Ok = true, Latest = "99.0.0.0" });
                var updateLink = Field<LinkLabel>(form, "btnUpdateHint");
                Check(updateLink.Visible, "New version hint is not visible");
                var updateBadge = Field<DBPanel>(form, "updateBadge");
                Check(updateBadge.Visible && updateLink.Text.Contains("99.0.0.0"),
                    "Update hint must show both NEW badge and latest version");
                Check(updateBadge.Right < updateLink.Left
                    && updateLink.Right < Field<RogLinkButton>(form, "btnDonate").Left,
                    "Update hint overlaps adjacent footer controls");
                Check(updateLink.Left > Field<Label>(form, "lblEvidenceLive").Right,
                    "Update link overlaps the readiness label's hit area");
                Save(form, Path.Combine(output, "overview-update-" + language + "-" + (light ? "light" : "dark") + ".png"));
                form.SelectPageForTest((int)PageId.About);
                Settle(form);
                Save(form, Path.Combine(output, "about-update-" + language + "-" + (light ? "light" : "dark") + ".png"));
                Check(!form.UiActive && Field<object>(tamer, "worker") == null, "Navigation started runtime workers");
                Check(Field<System.Windows.Forms.Timer>(form, "uiTimer") == null
                    || !Field<System.Windows.Forms.Timer>(form, "uiTimer").Enabled, "UI polling unexpectedly resumed");
                TransitionUiChecks.CheckWindow(form);
                CaptureHybridCoreCards(form, output, language, light);
                form.Hide();
            }
            Console.WriteLine("PASS full-window language=" + language + " light=" + light);
        }

        private static void CaptureHybridCoreCards(PanelForm form, string output, int language, bool light)
        {
            CpuTopology.TopologySnapshot saved = CpuTopology.CaptureTopologyForTest();
            try
            {
                CpuTopology.InjectTopologyForTest(0xFFF,
                    new ulong[] { 3, 12, 48, 192, 256, 512, 1024, 2048 },
                    new ulong[0], 0xFF, 0xF00, 0, 0, true, false);
                AutoCores.AllocatedForTest = delegate { return 0x0CUL; };
                form.SelectPageForTest((int)PageId.CoreScheduling);
                Call(form, "SyncCorePage");
                CheckAutoCorePage(form, Lang.F("schedule.auto.state.ready", CpuTopology.DescribeMask(0xF0)),
                    Lang.F("schedule.auto.reason.ready", 2));
                var matrix = Field<CoreMatrix>(form, "coreMatrix");
                Check(matrix.Selected == 0xF0 && matrix.DisabledMask == 0x0C && matrix.SchedulingIsolationMask == 0,
                    "Core map does not show the exclusive and reserved cores");
                Check(Field<Label>(form, "lblCoreReserved").Text == Lang.F("schedule.auto.reserved.other", CpuTopology.DescribeMask(0x0C)),
                    "Reserved cores are not explained");
                Settle(form);
                Save(form, Path.Combine(output,
                    "core-hybrid-" + language + "-" + (light ? "light" : "dark") + ".png"));
                byte[] reserved = { 0x0C, 0, 0, 0, 0, 0, 0, 0 };
                LegacyReservedCores.ReadForTest = delegate { return reserved; };
                LegacyReservedCores.WriteForTest = delegate(byte[] value) { reserved = value; return true; };
                Settings.SaveStr(LegacyReservedCores.SuspectKey, "C");
                Call(form, "SyncCorePage");
                var clear = Field<PillButton>(form, "btnCoreClearReserved");
                Check(clear.Visible && Field<Label>(form, "lblCoreReserved").Text
                    == Lang.F("schedule.auto.reserved.suspect", CpuTopology.DescribeMask(0x0C)), "Unowned reservation is not offered for clearing");
                Check(clear.Bottom <= Field<RoundPanel>(form, "coreMapPanel").Height, "Clear button falls outside the core map");
                Settle(form);
                Save(form, Path.Combine(output,
                    "core-suspect-" + language + "-" + (light ? "light" : "dark") + ".png"));
                form.ClearReservedConfirmationForTest = delegate { return false; };
                Call(form, "ClearSuspectReservedCores");
                Check(reserved != null && clear.Visible, "Cancel still cleared the reservation");
                form.ClearReservedConfirmationForTest = delegate { return true; };
                Call(form, "ClearSuspectReservedCores");
                Check(reserved == null && !clear.Visible && Field<Label>(form, "lblCoreReserved").Text
                    == Lang.F("schedule.auto.reserved.legacy", CpuTopology.DescribeMask(0x0C)), "Clearing did not switch to the restart notice");
                LegacyReservedCores.ReadForTest = null;
                LegacyReservedCores.WriteForTest = null;
                Settings.Remove(LegacyReservedCores.ClearedKey);
                Settings.Remove(LegacyReservedCores.SuspectKey);
                AutoCores.Publish(new AutoCoreStatus { Plan = AutoCores.Current(false), Watching = true,
                    ObservationOnly = true, Reason = "schedule.auto.observe" });
                Call(form, "SyncCorePage");
                CheckAutoCorePage(form, Lang.T("schedule.auto.state.shared"), Lang.T("schedule.auto.observe"));
                AutoCores.Publish(new AutoCoreStatus { Plan = AutoCores.Current(false), Watching = true,
                    Reason = "schedule.auto.recheck", CooldownSeconds = 42 });
                Call(form, "SyncCorePage");
                CheckAutoCorePage(form, Lang.T("schedule.auto.state.shared"),
                    Lang.F("schedule.auto.cooldown", Lang.T("schedule.auto.recheck"), 42));
                foreach (string cause in new[] { "schedule.auto.affinityaccess", "schedule.auto.identity", "schedule.auto.capture", "schedule.auto.placementunavailable", "schedule.auto.unstable" })
                {
                    AutoCores.Publish(new AutoCoreStatus { Plan = AutoCores.Current(false), Watching = true,
                        Blocked = cause, Reason = "schedule.auto.foreground", CooldownSeconds = 59 });
                    Call(form, "SyncCorePage");
                    CheckAutoCorePage(form, Lang.T("schedule.auto.state.blocked"), Lang.T(cause));
                }
                AutoCores.Publish(new AutoCoreStatus { Pending = true, ObservationOnly = true, Blocked = "schedule.auto.failed" });
                Call(form, "SyncCorePage");
                CheckAutoCorePage(form, Lang.T("schedule.auto.state.pending"), Lang.T("schedule.isolation.pending"));
                AutoCores.AllocatedForTest = delegate { return 0; };
                AutoCores.Publish(new AutoCoreStatus
                {
                    Plan = AutoCores.Current(false), Game = "NEBULA STRIKE", Watching = true, Exclusive = true, Engaged = true,
                    Active = 3, Load = 2.4, Capacity = 3, Windows = 6
                });
                Call(form, "SyncCorePage");
                CheckAutoCorePage(form, Lang.F("schedule.auto.state.exclusive", CpuTopology.DescribeMask(0xFC)),
                    Lang.F("schedule.auto.reason.exclusive", 3, "2.4", 3));
                Check(matrix.SchedulingIsolationMask == 0xFC && Field<Label[]>(form, "coreLiveValues")[0].Text == "NEBULA STRIKE",
                    "Engaged state is not shown on the core map");
                Settle(form);
                Save(form, Path.Combine(output,
                    "core-engaged-" + language + "-" + (light ? "light" : "dark") + ".png"));
                AutoCores.Publish(new AutoCoreStatus
                {
                    Plan = AutoCores.Current(false), Game = "NEBULA STRIKE", Watching = true,
                    Active = 9, Load = 6.1, Capacity = 3, Windows = 8, Releases = 1
                });
                Call(form, "SyncCorePage");
                CheckAutoCorePage(form, Lang.T("schedule.auto.state.shared"), Lang.F("schedule.auto.reason.shared", 9, "6.1", 3));
                Settle(form);
                Save(form, Path.Combine(output,
                    "core-shared-" + language + "-" + (light ? "light" : "dark") + ".png"));
            }
            finally
            {
                LegacyReservedCores.ReadForTest = null;
                LegacyReservedCores.WriteForTest = null;
                AutoCores.Publish(null);
                AutoCores.AllocatedForTest = delegate { return 0; };
                CpuTopology.RestoreTopologyForTest(saved);
                Call(form, "SyncCorePage");
            }
        }

        private static void CheckAutoCorePage(PanelForm form, string state, string reason)
        {
            Label stateLabel = Field<Label>(form, "lblCoreState"), reasonLabel = Field<Label>(form, "lblCoreReason");
            Check(stateLabel.Text == state, "Core page state: " + stateLabel.Text);
            Check(reasonLabel.Text == reason, "Core page reason: " + reasonLabel.Text);
            CoreMatrix matrix = Field<CoreMatrix>(form, "coreMatrix");
            Check(matrix.Height > Theme.S(40) && matrix.Height == matrix.LayoutFor(matrix.Width),
                "Core map is not laid out: height=" + matrix.Height);
            Check(Field<Label>(form, "lblCorePlan").Text != reason, "Core map caption repeats the reason");
            RoundPanel map = Field<RoundPanel>(form, "coreMapPanel");
            Check(Field<Label>(form, "lblCoreReserved").Bottom <= map.Height, "Core map clips its notes");
        }

        private static void CheckAdaptiveCardFollowsPreset(PanelForm form, GameMode mode)
        {
            form.SelectPageForTest((int)PageId.Policy);
            Field<TechTabs>(form, "policyTabs").Index = 0;
            var adaptive = Field<SettingCard>(form, "cardPolicyAdaptive");
            var gpu = Field<SettingCard>(form, "cardPolicyGpuDemote");
            var boost = Field<SettingCard>(form, "cardPolicyBoost");
            int gap = Theme.S(8);
            PerformancePreset saved = mode.Preset;
            try
            {
                mode.Preset = PerformancePreset.Competitive;
                Call(form, "RefreshPolicyPresentation");
                Check(!adaptive.Visible && adaptive.Suppressed, "Adaptive card still shown outside Smart");
                Check(boost.Top == gpu.Bottom + gap,
                    "Cards below the hidden adaptive card did not move up: " + boost.Top + " vs " + (gpu.Bottom + gap));

                mode.Preset = PerformancePreset.Standard;
                Call(form, "RefreshPolicyPresentation");
                Check(adaptive.Visible && !adaptive.Suppressed, "Adaptive card did not return in Smart");
                Check(adaptive.Top == gpu.Bottom + gap && boost.Top == adaptive.Bottom + gap,
                    "Cards did not settle back after the adaptive card returned");

                form.SelectPageForTest((int)PageId.Overview);
                mode.Preset = PerformancePreset.Handheld;
                Call(form, "RefreshPolicyPresentation");
                form.SelectPageForTest((int)PageId.Policy);
                Check(!adaptive.Visible && adaptive.Suppressed, "Adaptive card shown on Handheld after a hidden refresh");
                Check(boost.Top == gpu.Bottom + gap,
                    "Hidden-page refresh left a gap where the adaptive card was: " + boost.Top + " vs " + (gpu.Bottom + gap));
                bool wasExpanded = gpu.Expanded;
                gpu.Expanded = !wasExpanded;
                Check(boost.Top == gpu.Bottom + gap,
                    "Collapsing a card brought the hidden adaptive card's gap back: " + boost.Top + " vs " + (gpu.Bottom + gap));
                gpu.Expanded = wasExpanded;
                Check(boost.Top == gpu.Bottom + gap,
                    "Expanding a card brought the hidden adaptive card's gap back: " + boost.Top + " vs " + (gpu.Bottom + gap));
                form.SelectPageForTest((int)PageId.Overview);
                mode.Preset = PerformancePreset.Standard;
                Call(form, "RefreshPolicyPresentation");
                form.SelectPageForTest((int)PageId.Policy);
                Check(adaptive.Visible && adaptive.Top == gpu.Bottom + gap && boost.Top == adaptive.Bottom + gap,
                    "Hidden-page refresh back to Smart did not restack the cards");
            }
            finally { mode.Preset = saved; Call(form, "RefreshPolicyPresentation"); }
        }

        private static void CheckProfileCoreNavigation(PanelForm form, GameMode mode, string data)
        {
            bool savedEnabled = mode.Enabled;
            mode.Enabled = true;
            var profile = new GameProfile { Id = "navigation-core-profile", Name = "NAVIGATION CORE FIXTURE",
                Root = data, ExecutablePath = Path.Combine(data, "fixture.exe") };
            Field<List<GameProfile>>(mode, "profiles").Add(profile);
            form.SelectPageForTest((int)PageId.Library);
            Call(form, "RefreshGames");
            var library = Field<GameLibraryList>(form, "lstGames");
            GameLibraryRow selectedRow = null;
            foreach (Control control in library.Controls)
            {
                var row = control as GameLibraryRow;
                if (row != null && row.Item.Profile.Id == profile.Id) selectedRow = row;
            }
            Check(selectedRow != null, "Missing per-game core entry");
            Settle(form);
            Save(form, Path.Combine(Directory.GetParent(data).FullName, "library-core-entry-" + Lang.Cur + "-" + Theme.LightMode + ".png"));
            selectedRow.CoreButton.PerformClick();
            Check(Field<DBPanel>(form, "curPage") == Field<DBPanel>(form, "pageCoreScheduling")
                && !Field<NavRail>(form, "nav").Visible && !Field<NavRail>(form, "tuningNav").Visible
                && Field<NavRail>(form, "nav").Selected == (int)PageId.Library,
                "Core detail must hide both rails and retain the library return target");
            Check(Field<int>(form, "pageBaseLeft") == 0
                && Field<DBPanel>(form, "pageCoreScheduling").Width == Field<DBPanel>(form, "root").Width,
                "Core detail must use the full content width");
            var coreBack = Field<AdvancedBackBar>(form, "coreBackBar");
            Check(coreBack.Visible && coreBack.Parent == Field<DBPanel>(form, "root")
                && coreBack.Bounds == new Rectangle(0, 0, Theme.S(224), Theme.S(64))
                && coreBack.AccessibleName == Lang.T("cfg.back"),
                "Core return must reuse the ROG module at the main window upper left");
            Check(Field<string>(form, "coreProfileId") == profile.Id, "Core entry lost game context");
            AutoCores.Publish(new AutoCoreStatus { ProfileId = "another-game", Game = "OTHER GAME", Watching = true,
                Windows = 9, Active = 8, Engaged = true });
            Call(form, "SyncCorePage");
            Check(Field<Label[]>(form, "coreLiveValues")[0].Text == profile.Name
                && Field<Label[]>(form, "coreLiveValues")[3].Text == "—", "Another game's live results leaked into this profile");
            AutoCores.Publish(null);
            Call(form, "SyncCorePage");
            Settle(form);
            Save(form, Path.Combine(Directory.GetParent(data).FullName, "library-core-profile-" + Lang.Cur + "-" + Theme.LightMode + ".png"));
            Call(form, "RebuildUi");
            Check(Field<DBPanel>(form, "curPage") == Field<DBPanel>(form, "pageCoreScheduling")
                && Field<string>(form, "coreProfileId") == profile.Id
                && !Field<NavRail>(form, "nav").Visible && Field<int>(form, "pageBaseLeft") == 0,
                "Rebuild lost standalone core page context");
            Call(form, "OnEscHide", null, new KeyEventArgs(Keys.Escape));
            Check(Field<DBPanel>(form, "curPage") == Field<DBPanel>(form, "pageLibrary"), "Escape must return to library");
            Check(Field<NavRail>(form, "nav").Visible && Field<int>(form, "pageBaseLeft") == Theme.S(224),
                "Escape must restore the library rail and inset");
            Call(form, "OpenGameCoreScheduling", profile.Id);
            mode.Enabled = false;
            Call(form, "UpdateModePresentation", false);
            var returnButton = Field<AdvancedBackBar>(form, "coreBackBar");
            Check(Field<DBPanel>(form, "root").GetChildAtPoint(new Point(returnButton.Width / 2,
                returnButton.Height / 2), GetChildAtPointSkip.Invisible) == returnButton,
                "Guard-off veil must not intercept the main-window return module");
            Call(returnButton, "OnKeyDown", new KeyEventArgs(Keys.Enter));
            mode.Enabled = true;
            Check(Field<DBPanel>(form, "curPage") == Field<DBPanel>(form, "pageLibrary")
                && Field<NavRail>(form, "nav").Visible && !Field<AdvancedBackBar>(form, "coreBackBar").Visible,
                "Back module must restore library navigation and hide itself");
            Call(form, "OpenGameCoreScheduling", profile.Id);
            form.SelectPageForTest((int)PageId.Library);
            Field<List<GameProfile>>(mode, "profiles").RemoveAll(delegate(GameProfile p) { return p.Id == profile.Id; });
            typeof(PanelForm).GetField("coreProfileId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, null);
            mode.Enabled = savedEnabled;
            Call(form, "UpdateModePresentation", false);
        }

        private static void Settle(PanelForm form)
        {
            Field<NavRail>(form, "nav").SnapToSelection();
            Field<NavRail>(form, "tuningNav").SnapToSelection();
            Control page = Field<DBPanel>(form, "curPage");
            page.Left = Field<int>(form, "pageBaseLeft");
            form.Refresh();
        }

        private static void CheckRebuildCaches(PanelForm form)
        {
            Check(typeof(PanelForm).GetField("guardVeils", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                "Removed guard overlay state remains on the form");
            Check(typeof(PanelForm).GetField("themeRefreshers", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                "Retired theme callback registry must remain absent");
            foreach (Label label in Field<List<Label>>(form, "accentLabels"))
                Check(label != null && !label.IsDisposed, "Rebuild retained a disposed accent label");
            foreach (Control panel in Field<Dictionary<Control, Dictionary<Control, int>>>(form, "stackBase").Keys)
                Check(panel != null && !panel.IsDisposed, "Rebuild retained a disposed collapsible panel");
        }

        private static void CheckTuningHeader(PanelForm form)
        {
            var main = Field<NavRail>(form, "nav");
            var tuning = Field<NavRail>(form, "tuningNav");
            var back = Field<AdvancedBackBar>(form, "advBackBar");
            Check(main.ShowBranding && !tuning.ShowBranding, "Branding must remain only on main navigation");
            tuning.RefreshLogo();
            Check(Field<Image>(tuning, "logo") == null, "Hidden branding must not regenerate a logo bitmap");
            Check(back.Parent == tuning && back.Left == 0 && back.Top == 0 && back.Width == tuning.Width
                && back.Height == Theme.S(64), "Return action must replace the top brand area");
            Check(back.Bottom < (int)Call(tuning, "SlotY", 0), "Top return overlaps the first category");
            Check(back.AccessibleName == Lang.T("v20.advanced.back"), "Return action is not named for accessibility");
        }

        private static void CheckOverviewFooter(Control overview)
        {
            var links = new List<Control>();
            FindOverviewLinks(overview, links);
            Check(links.Count == 2, "Overview must show donate and website entries: " + links.Count);
            links.Sort(delegate(Control a, Control b) { return a.Left.CompareTo(b.Left); });
            Check(links[0].Left >= Theme.S(276), "Overview links overlap readiness status");
            Check(links[0].Text == Lang.T("donate.entry") && ((RogLinkButton)links[0]).Tint.HasValue
                && !((RogLinkButton)links[1]).Tint.HasValue,
                "Donate entry must sit leftmost with its own tint");
            for (int i = 0; i < links.Count; i++)
            {
                Check(links[i].Parent.ClientRectangle.Contains(links[i].Bounds), "Overview footer link clipped");
                if (i > 0) Check(links[i - 1].Right < links[i].Left, "Overview footer links overlap");
            }
            Check(links[1].Text == Lang.T("site.entry") && ((RogLinkButton)links[1]).External,
                "Website entry should open the browser");
            Check(Math.Abs(links[1].Right - (links[1].Parent.Width - Theme.S(30))) <= 2,
                "Overview still reserves an empty slot for the removed entry");
        }

        private static void FindOverviewLinks(Control control, List<Control> links)
        {
            Check(control.Text != Lang.T("v20.advanced.entry"), "Duplicate Deep Tuning entry remains in Overview");
            if (control is RogLinkButton) links.Add(control);
            foreach (Control child in control.Controls) FindOverviewLinks(child, links);
        }

        private static void Save(Control control, string path)
        {
            using (var bmp = new Bitmap(control.Width, control.Height))
            { control.DrawToBitmap(bmp, control.ClientRectangle); bmp.Save(path, ImageFormat.Png); }
        }

        private static void CheckScaledRail(string output)
        {
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
            foreach (int language in new[] { 0, 1, 2 })
            {
                Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language;
                string[] names = { Lang.T("v20.advanced.nav.policy"), Lang.T("nav.corescheduling"), Lang.T("nav.graphics"), Lang.T("v20.advanced.nav.system"), Lang.T("v20.advanced.nav.irq") };
                using (var rail = new NavRail(names, new[] { "settings", "chip", "gpu", "chip", "chip" }))
                {
                    rail.Size = new Size(Theme.S(224), Theme.S(760));
                    rail.ShowBranding = false;
                    var back = new AdvancedBackBar { Bounds = new Rectangle(0, 0, rail.Width, Theme.S(64)) };
                    rail.Controls.Add(back);
                    Check(Field<Image>(rail, "logo") == null && back.Bottom < (int)Call(rail, "SlotY", 0),
                        "Scaled top return must replace branding without overlapping categories");
                    int selected = -1;
                    rail.ItemInvoked = delegate(int item) { selected = item; };
                    Call(rail, "OnKeyDown", new KeyEventArgs(Keys.End));
                    Call(rail, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                    Check(selected == 4, "Keyboard category activation failed");
                    for (int i = 0; i < names.Length; i++)
                    {
                        int y = (int)Call(rail, "SlotY", i);
                        Check(y >= Theme.S(127) && y + Theme.S(47) <= Theme.S(510), "Category overlaps title or footer");
                        int textX = Theme.S(14) + Theme.S(20) + Theme.S(20) + Theme.S(16);
                        int textWidth = rail.Width - Theme.S(14) - Theme.S(10) - textX;
                        Check(TextRenderer.MeasureText(names[i], Theme.UI(8.5f, true)).Width <= textWidth,
                            "Category title does not fit even at the minimum font: " + names[i] + " scale=" + scale);
                    }
                    if (scale == 3f) Save(rail, Path.Combine(output, "rail-300-" + language + ".png"));
                }
            }
        }

        private static void CheckScaledPosition()
        {
            PageViewPosition saved;
            using (var root = new OffscreenTestForm())
            using (var scroll = new Panel { AutoScroll = true, Bounds = new Rectangle(0, 0, 180, 100),
                AutoScrollMinSize = new Size(0, 900) })
            {
                root.ShowInTaskbar = false; root.StartPosition = FormStartPosition.Manual;
                root.Location = new Point(-20000, -20000); root.Controls.Add(scroll); root.Show();
                scroll.AutoScrollPosition = new Point(0, 200);
                Check(scroll.AutoScrollPosition.Y == -200, "DPI scroll fixture not initialized");
                saved = PageViewPosition.Capture(root, 1f); root.Hide();
            }
            using (var root = new OffscreenTestForm { ClientSize = new Size(420, 260) })
            using (var scroll = new Panel { AutoScroll = true, Bounds = new Rectangle(0, 0, 360, 200),
                AutoScrollMinSize = new Size(0, 1800) })
            {
                root.ShowInTaskbar = false; root.StartPosition = FormStartPosition.Manual;
                root.Location = new Point(-20000, -20000); root.Controls.Add(scroll); root.Show();
                saved.Restore(root, 2f);
                Check(scroll.AutoScrollPosition.Y == -400, "DPI rebuild must restore logical, not old pixel positions");
                scroll.AutoScrollMinSize = new Size(0, 50);
                saved.Restore(root, 2f);
                Check(scroll.AutoScrollPosition.Y == 0, "Shorter content must clamp the saved position");
                root.Hide();
            }
        }
    }

    internal sealed class OffscreenPanelForm : PanelForm
    {
        public OffscreenPanelForm(Tamer tamer, GameMode mode, Icon icon) : base(tamer, mode, icon, true) { }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; }
        }
    }

    internal sealed class OffscreenTestForm : Form
    {
        public OffscreenTestForm()
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; }
        }
    }

    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Navigation bench cannot start the application runtime"); }
    }
}
#endif
