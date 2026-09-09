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
                foreach (string stored in new[] { "", "garbage", "-1", "0", "12", "999999", "4", "11" })
                {
                    Settings.SaveStr("DeepTuningLastPage", stored);
                    int page = (int)typeof(PanelForm).GetMethod("LoadLastAdvancedPage", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                    Check(page == (stored == "4" ? 4 : stored == "11" ? 11 : stored == "12" ? 12 : 2), "Remembered category validation failed: " + stored);
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
            Settings.SaveStr(CoreScheduling.Key, new CoreSchedulingPlan { GameMask = 12, IsolationMask = 12,
                Topology = CoreScheduling.CurrentStamp }.Encode());
            Lang.Cur = language; Theme.SetLight(light);
            string data = Path.Combine(output, "fixture-" + language + "-" + light);
            Directory.CreateDirectory(data);
            var core = new SuppressionCore();
            var tamer = new Tamer(core);
            var mode = new GameMode(data, core); // Fresh temporary library; never Start/Stop or tune processes.
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
                Check(Field<SearchFlyout>(form, "searchFlyout").Visible
                    && searchKey.Handled && searchKey.SuppressKeyPress, "Ctrl+F did not open global search");
                Call(form, "OnEscHide", null, new KeyEventArgs(Keys.Escape));
                Check(!Field<SearchFlyout>(form, "searchFlyout").Visible, "Escape did not close global search");
                var banner = Field<ModuleBanner>(form, "policyBanner");
                int redundantBannerPaints = 0;
                banner.Invalidated += delegate { redundantBannerPaints++; };
                Call(form, "UpdateModePresentation", true);
                Call(form, "UpdateModePresentation", true);
                Check(redundantBannerPaints == 0, "Stable mode polling invalidated the hidden policy banner");
                Check(Field<int>(form, "lastAdvancedPage") == (int)PageId.Policy, "Invalid remembered page was not rejected");
                CheckExtremeUnlockOrder(form);
                int prompts = 0;
                form.DeepTuningConfirmationForTest = delegate { prompts++; return false; };
                form.SelectPageForTest((int)PageId.Log);
                var main = Field<NavRail>(form, "nav");
                main.InvokeItem((int)PageId.Policy);
                Check(main.Selected == (int)PageId.Log && main.Visible && prompts == 1, "Canceled entry changed the current page");
                form.DeepTuningConfirmationForTest = delegate { prompts++; return true; };
                main.InvokeItem((int)PageId.Policy);
                var tuning = Field<NavRail>(form, "tuningNav");
                Check(main.Selected == (int)PageId.Policy && tuning.Visible && !main.Visible, "Entry did not directly open a section");
                CheckTuningHeader(form);
                Check(prompts == 2, "Entry warning missing");
                Check(Field<int[]>(tuning, "order").Length == 6, "Advanced navigation must expose six sections");
                foreach (PageId page in new[] { PageId.Interrupt, PageId.Environment, PageId.Graphics, PageId.AntiCheat, PageId.CoreScheduling, PageId.Policy })
                {
                    tuning.InvokeItem((int)page);
                    Check(main.Selected == (int)page && tuning.Selected == (int)page && tuning.Visible, "Category handoff failed");
                    Check(Field<int>(form, "pageBaseLeft") == main.Width, "Page overlaps the persistent navigation");
                }
                Check(prompts == 2, "In-area switching repeated the warning");
                var policyTabs = Field<TechTabs>(form, "policyTabs");
                Check(Field<DBPanel[]>(form, "policyTabPanels").Length == 3, "Policy must retain three sub-tabs");
                policyTabs.Index = 2;
                tuning.InvokeItem((int)PageId.CoreScheduling);
                var coreEditor = Field<CoreSchedulingPanel>(form, "coreSchedulingPanel");
                // 分配与独占合成一页 没有子页可切 独占范围跟着选核走
                Check(Field<DBPanel>(form, "coreScrollPanel") != null, "Core scheduling lost its content panel");
                coreEditor.SelectMask(28);
                Check(coreEditor.Matrix.Visible && coreEditor.Draft.IsolationMask == 48,
                    "Exclusive range did not follow the game selection");
                tuning.InvokeItem((int)PageId.Graphics);
                var gpuPanels = Field<DBPanel[]>(form, "gfxTabPanels");
                var gpuTabs = Field<TechTabs>(form, "gfxTabs");
                // 可见的显卡功能会跟着测试机硬件变 滚动这块由测试自己建一个确定的溢出区
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
                // 没有真实溢出时 WinForms 会把合成位置钳回 0 跨 DPI 的逻辑位置另有纯合成测试
                Check(!naturalGpuOverflow || rebuiltGpuPanel.AutoScrollPosition.Y == scrollY,
                    "Rebuild lost an inactive tab's scroll");
                Check(Field<int>(form, "mainReturnPage") == (int)PageId.Log, "Rebuild overwrote the return destination");
                tuning.InvokeItem((int)PageId.Policy);
                Check(Field<TechTabs>(form, "policyTabs").Index == 2, "Rebuild lost an inactive page's sub-tab");
                tuning.InvokeItem((int)PageId.CoreScheduling);
                coreEditor = Field<CoreSchedulingPanel>(form, "coreSchedulingPanel");
                Check(coreEditor.Draft.GameMask == 28 && coreEditor.Draft.IsolationMask == 48
                    && coreEditor.SaveButton.Enabled,
                    "Rebuild lost the core scheduling draft");
                Check(CoreScheduling.LoadGlobal().GameMask == 12 && CoreScheduling.LoadGlobal().IsolationMask == 12,
                    "Switching or rebuilding unexpectedly saved the draft");
                tuning.InvokeItem((int)PageId.Graphics);
                Field<AdvancedBackBar>(form, "advBackBar").BackRequested();
                Check(main.Selected == (int)PageId.Log && main.Visible, "Return did not restore the entry page");
                main.InvokeItem((int)PageId.Policy);
                Check(main.Selected == (int)PageId.Graphics && prompts == 3, "Re-entry did not restore the last section");
                Check(Settings.LoadStr("DeepTuningLastPage", "") == ((int)PageId.Graphics).ToString(), "Last section was not persisted");

                var hits = (List<SearchHit>)Call(form, "QuerySettingCards", "");
                SearchHit isolationHit = hits.Find(delegate(SearchHit hit) { return hit.PageId == (int)PageId.CoreScheduling
                    && hit.Card.Title == Lang.T("schedule.isolation.enable"); });
                Check(isolationHit != null && isolationHit.PageName == Lang.T("nav.corescheduling"), "Isolation search is missing its standalone page");
                Call(form, "OnSearchHitChosen", isolationHit);
                Check(main.Selected == (int)PageId.CoreScheduling && Field<TechTabs>(form, "coreTabs").Index == 1
                    && isolationHit.Card.Visible && prompts == 3, "Isolation search did not reveal the correct sub-tab");
                SearchHit target = hits.Find(delegate(SearchHit hit) { return hit.PageId == (int)PageId.Environment; });
                Check(target != null, "No search fixture target");
                Call(form, "OnSearchHitChosen", target);
                Check(main.Selected == (int)PageId.Environment && prompts == 3, "Search within the area added a prompt");
                Call(form, "OnEscHide", null, new KeyEventArgs(Keys.Escape));
                Check(main.Selected == (int)PageId.Log && form.Visible, "Escape should leave the advanced area, not hide the app");
                form.DeepTuningConfirmationForTest = delegate { prompts++; return false; };
                Call(form, "OnSearchHitChosen", target);
                Check(main.Selected == (int)PageId.Log && prompts == 4, "Search bypassed the entry warning");
                Settings.Save("DeepTuningWarningSuppressed", true);
                Call(form, "OnSearchHitChosen", target);
                Check(main.Selected == (int)PageId.Environment && prompts == 4, "Don't-show-again was ignored");

                CheckProfileCoreNavigation(form, mode, data);

                Theme.SetLight(light);
                Call(form, "RebuildUi");
                CheckRebuildCaches(form);
                form.SelectPageForTest((int)PageId.Policy);
                Field<TechTabs>(form, "policyTabs").Index = 0;
                Settle(form);
                Save(form, Path.Combine(output, "tuning-" + language + "-" + (light ? "light" : "dark") + ".png"));
                form.SelectPageForTest((int)PageId.CoreScheduling);
                foreach (int tab in new[] { 0, 1 })
                {
                    Field<TechTabs>(form, "coreTabs").Index = tab;
                    Settle(form);
                    Save(form, Path.Combine(output, "core-" + (tab == 0 ? "game" : "isolation") + "-" + language + "-" + (light ? "light" : "dark") + ".png"));
                }
                Field<AdvancedBackBar>(form, "advBackBar").BackRequested();
                Settle(form);
                Save(form, Path.Combine(output, "main-" + language + "-" + (light ? "light" : "dark") + ".png"));
                form.SelectPageForTest((int)PageId.Overview);
                CheckOverviewFooter(Field<DBPanel>(form, "pageOverview"));
                Settle(form);
                Save(form, Path.Combine(output, "overview-" + language + "-" + (light ? "light" : "dark") + ".png"));
                Check(!form.UiActive && Field<object>(tamer, "worker") == null, "Navigation started runtime workers");
                Check(Field<System.Windows.Forms.Timer>(form, "uiTimer") == null
                    || !Field<System.Windows.Forms.Timer>(form, "uiTimer").Enabled, "UI polling unexpectedly resumed");
                TransitionUiChecks.CheckWindow(form);
                form.Hide();
            }
            Console.WriteLine("PASS full-window language=" + language + " light=" + light);
        }

        private static void CheckProfileCoreNavigation(PanelForm form, GameMode mode, string data)
        {
            var profile = new GameProfile { Id = "navigation-core-profile", Name = "NAVIGATION CORE FIXTURE",
                Root = data, ExecutablePath = Path.Combine(data, "fixture.exe") };
            Field<List<GameProfile>>(mode, "profiles").Add(profile);
            form.SelectPageForTest((int)PageId.Library);
            Call(form, "ShowGameConfigPage", profile.Id);
            Field<TechTabs>(form, "cfgTabs").Index = 1;
            var editor = Field<CoreSchedulingPanel>(form, "cfgCoreSchedulingPanel");
            editor.FollowToggle.Checked = false; editor.SelectMask(48);
            // 独占只有全局一份 逐游戏页给只读摘要 不给开关
            Check(!editor.IsolationToggle.Visible && !editor.IsolationToggle.Enabled,
                "Profile must show the exclusive setting read-only");
            Check(Field<DBPanel>(form, "pageGameConfig").Visible
                && Field<NavRail>(form, "nav").Selected == (int)PageId.Library,
                "Profile core page navigated away from the profile");
            string globalStored = Settings.LoadStr(CoreScheduling.Key, "");
            string profileStored = CoreScheduling.ProfileToken(mode.GetProfiles().Find(delegate(GameProfile p) { return p.Id == profile.Id; }));
            Call(form, "RebuildUi");
            editor = Field<CoreSchedulingPanel>(form, "cfgCoreSchedulingPanel");
            Check(Field<DBPanel>(form, "pageGameConfig").Visible, "Rebuild hid the profile page");
            Check(editor.Draft.GameMask == 48 && !editor.FollowToggle.Checked,
                "Rebuild lost the profile draft: game=" + editor.Draft.GameMask + " follow=" + editor.FollowToggle.Checked);
            Check(Field<TechTabs>(form, "cfgTabs").Index == 1,
                "Rebuild lost the profile tab: " + Field<TechTabs>(form, "cfgTabs").Index);
            Check(!editor.IsolationToggle.Visible, "Rebuild restored the profile exclusive switch");
            Check(Settings.LoadStr(CoreScheduling.Key, "") == globalStored
                && CoreScheduling.ProfileToken(mode.GetProfiles().Find(delegate(GameProfile p) { return p.Id == profile.Id; })) == profileStored,
                "Profile rebuilding saved an uncommitted draft");
            form.SelectPageForTest((int)PageId.Library);
            Field<List<GameProfile>>(mode, "profiles").RemoveAll(delegate(GameProfile p) { return p.Id == profile.Id; });
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
            Check(Field<List<GuardVeil>>(form, "guardVeils").Count == 2,
                "Rebuild retained disposed guard veils");
            Check(Field<List<Action>>(form, "themeRefreshers").Count == 1,
                "Rebuild duplicated theme refresh callbacks");
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

        private static void CheckExtremeUnlockOrder(PanelForm form)
        {
            var card = Field<RoundPanel>(form, "cardExtreme");
            Check(card != null && card.Top == Theme.S(2), "Extreme unlock card must be the first setting");
        }

        private static void CheckOverviewFooter(Control overview)
        {
            var links = new List<Control>();
            FindOverviewLinks(overview, links);
            Check(links.Count == 2, "Overview must retain its notice and help/feedback entries");
            links.Sort(delegate(Control a, Control b) { return a.Left.CompareTo(b.Left); });
            Check(links[0].Left >= Theme.S(276), "Overview links overlap readiness status");
            for (int i = 0; i < links.Count; i++)
            {
                Check(links[i].Parent.ClientRectangle.Contains(links[i].Bounds), "Overview footer link clipped");
                if (i > 0) Check(links[i - 1].Right < links[i].Left, "Overview footer links overlap");
            }
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
                string[] names = { Lang.T("v20.advanced.nav.policy"), Lang.T("nav.corescheduling"), Lang.T("v20.advanced.nav.anticheat"), Lang.T("nav.graphics"), Lang.T("v20.advanced.nav.system"), Lang.T("v20.advanced.nav.irq") };
                using (var rail = new NavRail(names, new[] { "settings", "chip", "acshield", "gpu", "chip", "chip" }))
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
                    Check(selected == 5, "Keyboard category activation failed");
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

    // 这个夹具不能出现在桌面上 也不能在用户干活的时候抢焦点
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
