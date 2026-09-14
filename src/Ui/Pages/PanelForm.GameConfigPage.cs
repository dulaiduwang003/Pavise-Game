// @author bdth 2074055628@qq.com
// File purpose Per-game config page: enter/leave, refresh and section building
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private DBPanel pageGameConfig;
        private string cfgProfileId;
        private GameProfile cfgProfile;
        private Label lblCfgCount;
        private Label lblCfgSub;
        private ModuleBanner cfgBanner;
        private TechTabs cfgTabs;
        private DBPanel[] cfgTabPanels;
        private string[][] cfgTabKeys;
        private readonly List<Action> cfgRowSync = new List<Action>();
        private readonly Dictionary<string, SettingCard> cfgCardByKey
            = new Dictionary<string, SettingCard>(StringComparer.Ordinal);
        private int cfgJumpCursor;
#if PAVISE_SELFTEST
        internal Func<string, bool> VendorGraphicsSupportForTest;
#endif

        private PerformancePreset cfgEffMode;
        // Vendor availability snapshot valid only within one SyncCfgRows round; null means probe for real
        private bool? cfgSyncNvOk, cfgSyncAmdOk;

        private void ShowGameConfigPage(string profileId)
        {
            cfgProfileId = profileId;
            if (!RefreshCfgProfile()) return;
            BuildGameConfigContent();
            SetModeFlyout(false);
            SetSearchFlyout(false);
            SetPowerFlyout(false);
            pageBaseLeft = Theme.S(RailW);
            pageGameConfig.Left = pageBaseLeft;
            bool revealing = PreparePageReveal(pageGameConfig);
            bool ready = false;
            root.SuspendLayout();
            try
            {
                foreach (var p in pages) p.Visible = false;
                pageGameConfig.Visible = true;
                curPage = pageGameConfig;
                if (UiActive) UiClock.Wake();
                NotifyPageActivation();
                ready = true;
            }
            finally
            {
                try
                {
                    root.ResumeLayout(true);
                    if (revealing)
                    {
                        if (ready) StartPageReveal();
                        else StopPageReveal();
                    }
                }
                catch { StopPageReveal(); throw; }
            }
        }

        private void CloseGameConfigPage()
        {
            nav.Select((int)PageId.Library);
        }

        private bool RefreshCfgProfile()
        {
            if (string.IsNullOrEmpty(cfgProfileId)) return false;
            foreach (GameProfile p in gameMode.GetProfiles())
                if (string.Equals(p.Id, cfgProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    cfgProfile = p;
                    string presetOverride;
                    int presetParsed;
                    // Resolve this game's tier before building rows; sync and reopening the page share this entry
                    cfgEffMode = p.Overrides.TryGetValue(PolicyCatalog.KeyPreset, out presetOverride)
                        && int.TryParse(presetOverride, out presetParsed)
                        && PresetValue.IsValid(presetParsed)
                        ? PresetValue.From(presetParsed) : gameMode.Preset;
                    return true;
                }
            cfgProfile = null;
            return false;
        }

#if PAVISE_SELFTEST
        internal static bool CfgPresetForcesForTest(string key, PerformancePreset mode, out bool effective)
        {
            return CfgPresetForces(key, mode, out effective);
        }
#endif

        private static bool CfgPresetForces(string key, PerformancePreset mode, out bool effective)
        {
            bool competitive = mode == PerformancePreset.Competitive
                || mode == PerformancePreset.Handheld;
            bool custom = mode == PerformancePreset.Custom;
            if (mode == PerformancePreset.Handheld && PolicyCatalog.IsHandheldBlocked(key))
            {
                effective = false;
                return true;
            }
            switch (key)
            {
                case PolicyCatalog.KeyAggressive:
                case PolicyCatalog.KeyPauseDl:
                    effective = competitive;
                    return !custom;
                default:
                    effective = false;
                    return false;
            }
        }

        private void SyncCfgRows()
        {
            if (!RefreshCfgProfile()) return;
            // Rows in one sync round share the vendor availability probe; driver state does not change within a round
            cfgSyncNvOk = NvApi.Available;
            cfgSyncAmdOk = AdlxTweaks.Available;
            try { foreach (Action sync in cfgRowSync) sync(); }
            finally { cfgSyncNvOk = cfgSyncAmdOk = null; }
            if (cfgTabs != null && cfgTabKeys != null)
            {
                var hotFlags = new bool[cfgTabKeys.Length];
                for (int i = 0; i < cfgTabKeys.Length; i++)
                    foreach (string key in cfgTabKeys[i])
                        if (cfgProfile.Overrides.ContainsKey(key)) { hotFlags[i] = true; break; }
                cfgTabs.SetHot(hotFlags);
            }
            if (lblCfgCount != null)
            {
                int n = GameLibraryRow.OrdinaryOverrideCount(cfgProfile);
                lblCfgCount.Text = CfgOverrideSummary(cfgProfile);
                lblCfgCount.ForeColor = n > 0 ? Theme.Accent : Theme.Faint;
                lblCfgCount.Cursor = n > 0 ? Cursors.Hand : Cursors.Default;
            }
            if (lblCfgSub != null)
            {
                bool frozen = cfgProfileId != null
                    && string.Equals(gameMode.SessionPolicyProfileId, cfgProfileId, StringComparison.OrdinalIgnoreCase);
                lblCfgSub.Text = frozen ? Lang.T("cfg.frozen") : Lang.T("cfg.sub");
                lblCfgSub.ForeColor = frozen ? Theme.Accent : Theme.Dim;
            }
            if (cfgBanner != null)
            {
                int count = GameLibraryRow.OrdinaryOverrideCount(cfgProfile);
                bool frozen = cfgProfileId != null
                    && string.Equals(gameMode.SessionPolicyProfileId, cfgProfileId, StringComparison.OrdinalIgnoreCase);
                cfgBanner.State = CfgOverrideSummary(cfgProfile);
                cfgBanner.StateColor = count > 0 ? Theme.Accent : Theme.Green;
                cfgBanner.Cursor = count > 0 ? Cursors.Hand : Cursors.Default;
                cfgBanner.Detail = frozen ? Lang.T("cfg.frozen") : Lang.T("cfg.sub");
            }
        }

        private void AddCfgSection(Control panel, string title, ref int y, string[] keys)
        {
            Section(panel, title, 6, y);
            y += 24;
            foreach (string key in keys)
                AddCfgPickerRow(panel, ref y, PolicyCatalog.ItemOf(key));
            y += 6;
        }

        // Per-game disable of fullscreen optimizations is not a temporary match-time push; it writes the exe's compatibility layer persistently and immediately
        //   So it is not in PolicyCatalog and does not go through Overrides; it reads and writes the HKCU compatibility-layer string directly, on writes and off deletes
        private string CfgExePath()
        {
            if (cfgProfile == null) return null;
            return cfgProfile.PreferredExecutablePath;
        }

        private void AddCfgFsoRow(Control parent, ref int y)
        {
            Section(parent, Lang.T("cfg.fso.group"), 6, y);
            y += 24;
            string exe = CfgExePath();
            bool hasExe = !string.IsNullOrEmpty(exe);
            Toggle sw = MakeSwitch(hasExe && FsoTweak.IsDisabledForExe(exe), null);
            sw.Enabled = hasExe;
            int cardH;
            MakeAutoCard(parent, 6, y, ScrollContentW, 56, Lang.T("cfg.fso"),
                hasExe ? Lang.T("cfg.fso.sub") : Lang.T("cfg.fso.noexe"), sw, out cardH);
            y += cardH + 8;
            sw.CheckedChanged += delegate
            {
                string p = CfgExePath();
                if (string.IsNullOrEmpty(p)) { sw.SetSilently(false); return; }
                bool want = sw.Checked;
                bool ok = IrqMutationBoundary.Run<bool>(delegate
                {
                    return FsoTweak.SetForExe(p, want);
                });
                sw.SetSilently(FsoTweak.IsDisabledForExe(p));
                // A failed compatibility-layer write only shows in the log; the switch snaps back on its own in the UI and looks broken
                if (!ok) PaviseDialog.Warn(this, App.DisplayName, Lang.T("cfg.fso.fail"));
            };
            cfgRowSync.Add(delegate
            {
                string p = CfgExePath();
                bool ok = !string.IsNullOrEmpty(p);
                sw.Enabled = ok;
                sw.SetSilently(ok && FsoTweak.IsDisabledForExe(p));
            });
        }

        // Per-game DPI awareness shares the compatibility-layer key with fullscreen optimizations; at 100% scaling the row copy notes it is pointless
        private void AddCfgDpiRow(Control parent, ref int y)
        {
            string exe = CfgExePath();
            bool hasExe = !string.IsNullOrEmpty(exe);
            bool scaling = DpiTweak.ScalingActive();
            Toggle sw = MakeSwitch(hasExe && DpiTweak.IsAwareForExe(exe), null);
            sw.Enabled = hasExe;
            int cardH;
            MakeAutoCard(parent, 6, y, ScrollContentW, 56, Lang.T("cfg.dpi"),
                !hasExe ? Lang.T("cfg.fso.noexe") : scaling ? Lang.T("cfg.dpi.sub") : Lang.T("cfg.dpi.noscale"),
                sw, out cardH);
            y += cardH + 8;
            sw.CheckedChanged += delegate
            {
                string p = CfgExePath();
                if (string.IsNullOrEmpty(p)) { sw.SetSilently(false); return; }
                bool want = sw.Checked;
                bool ok = IrqMutationBoundary.Run<bool>(delegate
                {
                    return DpiTweak.SetForExe(p, want);
                });
                sw.SetSilently(DpiTweak.IsAwareForExe(p));
                if (!ok) PaviseDialog.Warn(this, App.DisplayName, Lang.T("cfg.dpi.fail"));
            };
            cfgRowSync.Add(delegate
            {
                string p = CfgExePath();
                bool ok = !string.IsNullOrEmpty(p);
                sw.Enabled = ok;
                sw.SetSilently(ok && DpiTweak.IsAwareForExe(p));
            });
        }

        private void JumpToNextCfgOverride()
        {
            if (cfgProfile == null || cfgTabKeys == null || GameLibraryRow.OrdinaryOverrideCount(cfgProfile) == 0) return;
            var cards = new List<Control>();
            SettingCard presetCard;
            if (cfgProfile.Overrides.ContainsKey(PolicyCatalog.KeyPreset)
                && cfgCardByKey.TryGetValue(PolicyCatalog.KeyPreset, out presetCard))
                cards.Add(presetCard);
            foreach (string[] tabKeys in cfgTabKeys)
                foreach (string key in tabKeys)
                {
                    if (!cfgProfile.Overrides.ContainsKey(key)) continue;
                    SettingCard setting;
                    Control card = cfgCardByKey.TryGetValue(key, out setting) ? (Control)setting : cfgCoreSchedulingPanel;
                    if (card != null && !card.IsDisposed && !cards.Contains(card)) cards.Add(card);
                }
            if (cards.Count == 0) return;
            Control target = cards[cfgJumpCursor % cards.Count];
            cfgJumpCursor++;
            RevealTabFor(cfgTabs, cfgTabPanels, target);
            SettingCard targetCard = target as SettingCard;
            if (targetCard != null) ScrollCardIntoView(targetCard);
            else if (target.Parent is ScrollableControl) ((ScrollableControl)target.Parent).ScrollControlIntoView(target);
        }
    }
}
