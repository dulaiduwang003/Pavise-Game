// @author bdth 2074055628@qq.com
// 文件用途 设置搜索浮层 收集各页设置卡 命中后跳页定位并高亮
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private SearchFlyout searchFlyout;

        private void ToggleSearchFlyout()
        {
            SetSearchFlyout(searchFlyout == null || !searchFlyout.Visible);
        }

        private void SetSearchFlyout(bool visible)
        {
            if (visible) StopPageReveal();
            if (searchFlyout == null) return;
            if (visible) { SetModeFlyout(false); SetPowerFlyout(false); }
            searchFlyout.Visible = visible;
            if (visible)
            {
                searchFlyout.BringToFront();
                searchFlyout.Open();
            }
        }

        private List<SearchHit> QuerySettingCards(string text)
        {
            var titleHits = new List<SearchHit>();
            var descHits = new List<SearchHit>();
            string needle = text != null ? text.Trim() : "";
            if (pageGameConfig != null && pageGameConfig.Visible && cfgProfile != null)
                CollectCards(pageGameConfig, -1, cfgProfile.Name, needle, titleHits, descHits);
            if (pages != null)
                for (int i = 0; i < pages.Length; i++)
                    if (pages[i] != null) CollectCards(pages[i], i, PageTitleOf(i), needle, titleHits, descHits);
            titleHits.AddRange(descHits);
            return titleHits;
        }

        private static void CollectCards(Control root, int pageId, string pageName, string needle,
            List<SearchHit> titleHits, List<SearchHit> descHits)
        {
            foreach (Control c in root.Controls)
            {
                var card = c as SettingCard;
                if (card != null && !card.Suppressed && card.Title.Length > 0)
                {
                    bool inTitle = needle.Length == 0
                        || card.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool inDesc = !inTitle && card.Desc.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (inTitle || inDesc)
                        (inTitle ? titleHits : descHits).Add(new SearchHit
                        {
                            Card = card,
                            PageId = pageId,
                            PageName = pageName
                        });
                }
                if (c.Controls.Count > 0) CollectCards(c, pageId, pageName, needle, titleHits, descHits);
            }
        }

        private static string PageTitleOf(int pageId)
        {
            switch ((PageId)pageId)
            {
                case PageId.Overview: return Lang.T("nav.overview");
                case PageId.Library: return Lang.T("nav.library");
                case PageId.Policy: return Lang.T("nav.policy");
                case PageId.CoreScheduling: return Lang.T("nav.corescheduling");
                case PageId.AntiCheat: return Lang.T("v14.anticheat");
                case PageId.Graphics: return Lang.T("nav.graphics");
                case PageId.Environment: return Lang.T("nav.env");
                case PageId.Interrupt: return Lang.T("nav.irq");
                case PageId.Audit: return Lang.T("nav.audit");
                case PageId.Log: return Lang.T("nav.log");
                case PageId.Settings: return Lang.T("nav.set");
                case PageId.About: return Lang.T("nav.about");
                default: return Lang.T("nav.white");
            }
        }

        private void OnSearchHitChosen(SearchHit hit)
        {
            if (hit == null || hit.Card == null || hit.Card.IsDisposed) return;
            // 搜索能直接命中高级区页面 不能绕过概览入口的风险警告
            if (hit.PageId >= 0 && IsAdvancedPage(hit.PageId)
                && !CanEnterDeepTuning()) return;
            SetSearchFlyout(false);
            if (hit.PageId < 0)
            {
                if (pageGameConfig == null || !pageGameConfig.Visible) return;
                RevealTabFor(cfgTabs, cfgTabPanels, hit.Card);
                ScrollCardIntoView(hit.Card);
                return;
            }
            nav.Select(hit.PageId);
            EnsureTabFor(hit.Card);
            ScrollCardIntoView(hit.Card);
        }

        private static void ScrollCardIntoView(SettingCard card)
        {
            Control ancestor = card.Parent;
            while (ancestor != null)
            {
                var scrollable = ancestor as ScrollableControl;
                if (scrollable != null && scrollable.AutoScroll)
                {
                    scrollable.ScrollControlIntoView(card);
                    break;
                }
                ancestor = ancestor.Parent;
            }
            card.Flash();
        }
    }
}
