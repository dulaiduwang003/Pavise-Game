// 主窗口内的两级导航 主界面保持简洁 高级区可连续切换分类
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private const string LastAdvancedPageKey = "DeepTuningLastPage";
        private NavRail tuningNav;
        private int lastAdvancedPage = (int)PageId.Policy;
        private int mainReturnPage = (int)PageId.Overview;
        private float navigationScale = 1f;
        private readonly Dictionary<int, PageViewPosition> pagePositions = new Dictionary<int, PageViewPosition>();
        private readonly Dictionary<DBPanel, Point> tabScrollPositions = new Dictionary<DBPanel, Point>();
#if PAVISE_SELFTEST
        internal Func<bool> DeepTuningConfirmationForTest;
        internal static bool SuppressUiWorkersForTest;
#endif

        private static int LoadLastAdvancedPage()
        {
            int page;
            return int.TryParse(Settings.LoadStr(LastAdvancedPageKey, ""), out page)
                ? NormalizeAdvancedPage(page) : (int)PageId.Policy;
        }

        private static int NormalizeAdvancedPage(int page)
        {
            return IsAdvancedPage(page) ? page : (int)PageId.Policy;
        }

        private bool IsInDeepTuning
        {
            get { return pages != null && curPage != null && IsAdvancedPage(Array.IndexOf(pages, curPage)); }
        }

        private void BuildNavigation()
        {
            navigationScale = Dpi.Scale;
            var titles = new[] { Lang.T("nav.overview"), Lang.T("nav.library"), Lang.T("v20.advanced.nav.policy"),
                Lang.T("v20.advanced.nav.anticheat"), Lang.T("nav.graphics"), Lang.T("v20.advanced.nav.system"), Lang.T("v20.nav.report"),
                Lang.T("nav.log"), Lang.T("nav.set"), Lang.T("nav.about"), Lang.T("nav.white"), Lang.T("v20.advanced.nav.irq"),
                Lang.T("nav.corescheduling") };
            var glyphs = new[] { "game", "tiles", "settings", "acshield", "gpu", "chip", "chart", "log", "gear", "info", "white", "chip", "chip" };
            var mainTitles = (string[])titles.Clone();
            // Policy 在主侧栏是入口 在高级侧栏仍是具体的优化策略页
            mainTitles[(int)PageId.Policy] = Lang.T("v20.advanced.entry");
            nav = new NavRail(mainTitles, glyphs,
                new[] { (int)PageId.Overview, (int)PageId.Library, (int)PageId.Whitelist,
                    (int)PageId.Audit, (int)PageId.Log, (int)PageId.Policy, (int)PageId.Settings, (int)PageId.About },
                new[] { 5 }, new[] { "" }, 0);
            nav.Name = "MainNavigation";
            nav.SelectionChanged = ShowPage;
            nav.ItemInvoked = delegate(int page)
            {
                if (page == (int)PageId.Policy) EnterDeepTuning();
                else nav.Select(page);
            };
            tuningNav = new NavRail(titles, glyphs,
                new[] { (int)PageId.Policy, (int)PageId.CoreScheduling, (int)PageId.AntiCheat, (int)PageId.Graphics,
                    (int)PageId.Environment, (int)PageId.Interrupt }, null, null, 0);
            tuningNav.Name = "TuningNavigation";
            tuningNav.ShowBranding = false;
            tuningNav.SectionTitle = Lang.T("v20.advanced.title");
            tuningNav.SelectionChanged = delegate(int page)
            {
                if (IsAdvancedPage(page)) nav.Select(page);
            };
            foreach (NavRail rail in new[] { nav, tuningNav })
            {
                AssertNavMatchesPageIds(rail);
                rail.SetBounds(0, 0, Theme.S(RailW), Theme.S(WinH));
                rail.SetMode(visualMode, visualEnabled);
            }
            tuningNav.Visible = false;
            var hint = new Label { Name = "TuningNavigationHint", Text = Lang.T("v20.advanced.nav.hint"),
                BackColor = Color.Transparent, ForeColor = Theme.Faint, Font = Theme.UI(8.2f, false),
                UseCompatibleTextRendering = false };
            hint.SetBounds(Theme.S(28), Theme.S(521), Theme.S(RailW - 56), Theme.S(56));
            tuningNav.Controls.Add(hint);
            advBackBar = new AdvancedBackBar();
            advBackBar.SetBounds(0, 0, Theme.S(RailW), Theme.S(TopH));
            advBackBar.BackRequested = ReturnFromDeepTuning;
            tuningNav.Controls.Add(advBackBar);
        }

        private bool CanEnterDeepTuning()
        {
            return IsInDeepTuning || ConfirmDeepTuningEntry();
        }

        private void EnterDeepTuning()
        {
            SetModeFlyout(false);
            SetSearchFlyout(false);
            SetPowerFlyout(false);
            if (CanEnterDeepTuning())
            {
                nav.Select(NormalizeAdvancedPage(lastAdvancedPage));
                tuningNav.Focus();
            }
        }

        private void ReturnFromDeepTuning()
        {
            if (!IsInDeepTuning) return;
            int target = mainReturnPage;
            if (target < 0 || target >= (int)PageId.Count || IsAdvancedPage(target)) target = (int)PageId.Overview;
            nav.Select(target);
            nav.Focus();
        }

        private void SavePagePosition(Control page)
        {
            if (pages == null || page == null || page.IsDisposed) return;
            int index = Array.IndexOf(pages, page);
            if (index >= 0) pagePositions[index] = PageViewPosition.Capture(page, navigationScale);
        }

        private void RestorePagePosition(int index)
        {
            PageViewPosition position;
            if (!pagePositions.TryGetValue(index, out position)) return;
            position.Restore(pages[index], Dpi.Scale);
            DBPanel[] panels;
            if (pageTabPanels.TryGetValue(pages[index], out panels))
                foreach (DBPanel panel in panels)
                    tabScrollPositions[panel] = new Point(-panel.AutoScrollPosition.X, -panel.AutoScrollPosition.Y);
        }
    }
}
