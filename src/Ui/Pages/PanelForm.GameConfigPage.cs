// @author bdth 2074055628@qq.com
// 文件用途 逐游戏配置页的进出 刷新与分节构建
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
        // 仅一轮 SyncCfgRows 内有效的厂商可用性快照 为 null 时实查
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
                    return true;
                }
            cfgProfile = null;
            return false;
        }

        private static bool CfgPresetForces(string key, PerformancePreset mode, out bool effective)
        {
            // 极限档对清单内键整体锁定 展示层与快照层同一判定来源
            if (mode == PerformancePreset.Extreme)
            {
                string forced = ExtremeMode.ForcedPolicyValue(key);
                if (forced != null) { effective = forced != "off"; return true; }
            }
            bool competitive = mode == PerformancePreset.Competitive
                || mode == PerformancePreset.Extreme
                || mode == PerformancePreset.Handheld;
            bool custom = mode == PerformancePreset.Custom;
            switch (key)
            {
                case PolicyCatalog.KeyAggressive:
                case PolicyCatalog.KeyPauseDl:
                    effective = competitive;
                    return !custom;
                // 电竞和极限档在台式机上锁定开启 其余档位用户自选 不像激进项那样在智能档锁关
                case PolicyCatalog.KeyGpuClockLock:
                    effective = true;
                    return GpuClockLock.ForcedByTier(mode, Native.HasSystemBattery());
                case PolicyCatalog.KeyLaptopPerf:
                    effective = true;
                    return LaptopPerfMode.ForcedByTier(mode, Native.HasSystemBattery());
                default:
                    effective = false;
                    return false;
            }
        }

        private void SyncCfgRows()
        {
            if (!RefreshCfgProfile()) return;
            string presetOverride;
            int presetParsed;
            // 走 From 不裸转 极限锁着时存量的 5 解析为电竞 展示跟核心同一口径
            cfgEffMode = cfgProfile.Overrides.TryGetValue(PolicyCatalog.KeyPreset, out presetOverride)
                && int.TryParse(presetOverride, out presetParsed)
                && PresetValue.IsValid(presetParsed)
                ? PresetValue.From(presetParsed) : gameMode.Preset;
            // 一轮同步内各行共享厂商可用性探测 驱动状态不会在一轮里变
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

        // 逐游戏禁用全屏优化 不是对局临时下发 而是立即持久写该 exe 的兼容层
        //   所以不进 PolicyCatalog 不走 Overrides 直接读写 HKCU 兼容层字符串 拨开即写 拨关即删
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
                // 兼容层写不进去只有日志里有 界面上开关自己弹回来 看着像开关坏了
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

        // 逐游戏 DPI 感知 和全屏优化同一个兼容层键 缩放 100% 时行文案提示无意义
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
            var cards = new List<SettingCard>();
            SettingCard presetCard;
            if (cfgProfile.Overrides.ContainsKey(PolicyCatalog.KeyPreset)
                && cfgCardByKey.TryGetValue(PolicyCatalog.KeyPreset, out presetCard))
                cards.Add(presetCard);
            foreach (string[] tabKeys in cfgTabKeys)
                foreach (string key in tabKeys)
                {
                    if (!cfgProfile.Overrides.ContainsKey(key)) continue;
                    SettingCard card;
                    if (!cfgCardByKey.TryGetValue(key, out card)) card = cfgCoreCard;
                    if (card != null && !card.IsDisposed && !cards.Contains(card)) cards.Add(card);
                }
            if (cards.Count == 0) return;
            SettingCard target = cards[cfgJumpCursor % cards.Count];
            cfgJumpCursor++;
            RevealTabFor(cfgTabs, cfgTabPanels, target);
            ScrollCardIntoView(target);
        }
    }
}
