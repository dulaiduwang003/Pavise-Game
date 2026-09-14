// @author bdth 2074055628@qq.com
// File purpose Row controls for per-game config: override writes and clear confirmation
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private void AddCfgModeRow(Control parent, ref int y)
        {
            PolicyItem item = PolicyCatalog.ItemOf(PolicyCatalog.KeyPreset);
            // The mode strip and the value array must share a source; a tier this machine does not support is absent on both sides
            string[] values = PresetValue.VisibleChoices();
            var strip = new ModeStrip();
            strip.Index = CfgRowIndexOf(item, values);
            strip.Size = new Size(Theme.S(360), Theme.S(38));
            int cardH;
            SettingCard card = MakeAutoCard(parent, ContentX, y, ContentW, 64,
                Lang.T(item.LangKey), Lang.T("cfg.mode.sub"), strip, out cardH);
            y += cardH + 8;
            cfgCardByKey[item.Key] = card;
            card.TrackChildHover(strip);
            cfgRowSync.Add(delegate
            {
                strip.Index = CfgRowIndexOf(item, values);
                strip.SetGlobal(gameMode.Preset);
            });
            strip.IndexChanged = delegate(int index)
            {
                if (cfgProfile == null) return;
                // The game currently in a match has its tier locked; changing other games is fine, the snapshot is frozen and takes effect next match
                if (gameMode.IsActive && gameMode.Enabled
                    && string.Equals(cfgProfileId, gameMode.SessionPolicyProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    PaviseDialog.Info(this, App.DisplayName, Lang.T("mode.locked.ingame"));
                    SyncCfgRows();
                    return;
                }
                bool wasSmart = cfgEffMode == PerformancePreset.Standard;
                string changedId = cfgProfileId;
                if (index <= 0) gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyPreset);
                else gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyPreset, values[index - 1]);
                SyncCfgRows();
                // The adaptive escalation row exists only on the Smart tier; crossing that line rebuilds the whole page, and the picker is still inside its own callback, so defer to the next round
                if (wasSmart != (cfgEffMode == PerformancePreset.Standard))
                    BeginInvoke((Action)delegate
                    {
                        if (pageGameConfig != null && pageGameConfig.Visible
                            && string.Equals(cfgProfileId, changedId, StringComparison.OrdinalIgnoreCase))
                            BuildGameConfigContent();
                    });
            };
        }

        private void AddCfgPickerRow(Control parent, ref int y, PolicyItem item)
        {
            AddCfgPickerRow(parent, ref y, item, 6, ScrollContentW, true);
        }

        private void AddCfgPickerRow(Control parent, ref int y, PolicyItem item,
            int x, int w, bool withDesc)
        {
            string[] values = CfgOptionValues(item);
            string[] optionLabels = CfgOptionLabels(item);
            var labels = new string[optionLabels.Length + 1];
            labels[0] = Lang.T("cfg.follow");
            for (int i = 0; i < optionLabels.Length; i++) labels[i + 1] = optionLabels[i];

            string reasonKey;
            bool supported = CfgItemSupported(item, out reasonKey);
            // Losing a capability must not trap an option that is already on
            bool canTurnOff = CfgCanTurnOff(item);
            bool vendorGraphics = CfgVendorGraphicsItem(item.Key);
            string descKey = CfgDescKey(item);
            string adminNoticeKey = CfgEnableAdminNoticeKey(item.Key);
            bool enableNeedsAdmin = adminNoticeKey != null && !elevated;
            string desc = enableNeedsAdmin ? Lang.T(adminNoticeKey)
                : !supported ? Lang.T(reasonKey)
                : (withDesc && descKey != null ? Lang.T(descKey) : "");
            if (item.Key == PolicyCatalog.KeyStandbyCleaner && !gameMode.StandbyCleaningOptionsValid)
                desc = Lang.T("standbycleaner.config.invalid");
            if (item.Key == PolicyCatalog.KeyPowerYield && supported && PowerBudgetYield.Fused)
                desc = Lang.T("gm.poweryield.fused");

            var picker = new TierPicker();
            picker.Labels = labels;
            picker.Index = supported || canTurnOff || vendorGraphics ? CfgRowIndexOf(item, values) : 1;
            int segW = labels.Length >= 6 ? 68 : 78;
            picker.SetBounds(0, 0, Theme.S(labels.Length * segW + 12), Theme.S(30));
            bool showDisabled = adminNoticeKey != null || item.Key == PolicyCatalog.KeyIntelLowLatency
                || item.Key == PolicyCatalog.KeyPowerYield || vendorGraphics;
            picker.Enabled = (supported || canTurnOff) && (!enableNeedsAdmin
                || PolicyResolver.Read(cfgProfile, item.Key) == "1");
            picker.Visible = supported || canTurnOff || showDisabled;

            int cardH;
            int minimumH = item.Key == PolicyCatalog.KeyStandbyCleaner
                ? StandbyCleanerCardHeight(w, picker, 54, true) : 54;
            if (item.Key == PolicyCatalog.KeyEnglishInput || item.Key == PolicyCatalog.KeyIntelLowLatency)
                minimumH = FullTextCardHeight(desc, w, picker, minimumH);
            SettingCard card = MakeAutoCard(parent, x, y, w, minimumH,
                Lang.T(item.LangKey), desc, picker, out cardH);
            y += cardH + 8;
            cfgCardByKey[item.Key] = card;

            string key = item.Key;
            Action sync = delegate
            {
                if (key == PolicyCatalog.KeyPowerYield)
                {
                    supported = CfgItemSupported(item, out reasonKey);
                    card.Desc = Lang.T(!supported ? reasonKey
                        : PowerBudgetYield.Fused ? "gm.poweryield.fused" : "gm.poweryield.sub");
                }
                else if (vendorGraphics || key == PolicyCatalog.KeyRenderLane)
                {
                    supported = CfgItemSupported(item, out reasonKey);
                    card.Desc = !supported ? Lang.T(reasonKey)
                        : withDesc && descKey != null ? Lang.T(descKey) : "";
                }
                bool has = cfgProfile.Overrides.ContainsKey(key);
                string globalLabel = CfgValueLabel(item, PolicyResolver.GlobalValue(key));
                bool allowOff = CfgCanTurnOff(item);
                picker.Index = supported || allowOff || vendorGraphics ? CfgRowIndexOf(item, values) : 1;
                bool forcedEffective;
                bool forced = CfgPresetForces(key, cfgEffMode, out forcedEffective);
                bool needsAdmin = adminNoticeKey != null && !elevated;
                bool usable = (supported || allowOff) && !forced && (!needsAdmin
                    || PolicyResolver.Read(cfgProfile, key) == "1");
                picker.Enabled = usable;
                picker.Visible = usable || showDisabled;
                if (adminNoticeKey != null)
                    card.Desc = Lang.T(key == PolicyCatalog.KeyStandbyCleaner && !gameMode.StandbyCleaningOptionsValid
                        ? "standbycleaner.config.invalid" : needsAdmin ? adminNoticeKey : descKey);
                // Three lock kinds share one label set site-wide; unsupported items no longer borrow the Forced off by preset label
                card.SetLock(!supported ? Lang.T("lock.na")
                    : forced ? Lang.T(forcedEffective ? "v14.preset.forced.on" : "v14.preset.forced.off")
                    : "", supported && forcedEffective);
                card.SetValue(has ? Lang.F("cfg.state.over", globalLabel)
                    : Lang.F("cfg.state.follow", globalLabel), has ? Theme.Accent : Theme.Faint);
            };
            cfgRowSync.Add(sync);
            picker.IndexChanged = delegate(int index)
            {
                string chosen = index <= 0 ? null : values[index - 1];
                if (!ApplyCfgPolicyChoice(key, chosen))
                {
                    picker.Index = CfgRowIndexOf(item, values);
                    // When the vendor GPU threshold blocks it the reason must be stated; the card description cannot explain why follow-global is not possible
                    //   Visible gate: the isolated regression drives this path on an unshown form and must not pop a dialog
                    string canonical = chosen == null ? null : PolicyCatalog.Canonical(key, chosen);
                    if (Visible && (chosen == null || canonical != null)
                        && !CfgVendorGraphicsChoiceAllowed(key, canonical))
                        PaviseDialog.Info(this, Lang.T(item.LangKey), Lang.T("cfg.inherit.blocked"));
                    return;
                }
                SyncCfgRows();
            };
        }

        private bool ApplyCfgPolicyChoice(string key, string value)
        {
            if (cfgProfile == null) return false;
            string canonical = value == null ? null : PolicyCatalog.Canonical(key, value);
            if (value != null && canonical == null) return false;
            if (!CfgVendorGraphicsChoiceAllowed(key, canonical)) return false;
            bool turningOn = canonical == "1";
            bool inheritingPowerYield = value == null && key == PolicyCatalog.KeyPowerYield
                && CfgPowerYieldInheritanceNeedsConfirmation(cfgProfile);
            // For guarded boolean items follow-global ranks equal to an explicit switch-on; when global is on and this game was previously in effect as off,
            //   clearing the override is enabling: the confirmation must still pop, and the circuit breaker must be cleared too
            bool inheritingGuarded = value == null && IsConfirmGuardedKey(key)
                && CfgGuardedInheritanceNeedsConfirmation(cfgProfile, key);
            bool inheritingGuardedOn = value == null
                && ((key == PolicyCatalog.KeyDisableCpuIdle && CfgCpuIdleInheritanceNeedsConfirmation(cfgProfile))
                    || (key == PolicyCatalog.KeyStandbyCleaner && CfgStandbyCleanerInheritanceNeedsConfirmation(cfgProfile))
                    || (key == PolicyCatalog.KeyIntelLowLatency && CfgIntelInheritanceNeedsConfirmation(cfgProfile))
                    || inheritingPowerYield || inheritingGuarded);
            // Switching on here is the same as turning the global switch on once; the enable confirmation must still pop
            //   Otherwise this page can bypass the gate the Policy page deliberately added and the user never sees the warning
            if ((turningOn || inheritingGuardedOn) && !ConfirmCfgEnable(key)) return false;
            bool saved = value == null ? gameMode.ClearProfileOverride(cfgProfileId, key)
                : gameMode.SetProfileOverride(cfgProfileId, key, canonical);
            // Switching VRAM residency on here also counts as re-enabling the global switch, so the circuit breaker must be cleared too
            //   Otherwise a breaker tripped by last time's failed verification makes this match skip outright, and the user has no way to clear it from this page
            if (saved && (turningOn || inheritingGuarded) && key == PolicyCatalog.KeyVramShield)
                VramShield.ClearFuse();
            if (saved && key == PolicyCatalog.KeyPowerYield && (turningOn || inheritingPowerYield))
                PowerBudgetYield.ClearFuse();
            return saved;
        }

        // Items whose global switch needs confirmation go through the same prompt when a per-game override switches on; the copy is shared
        private bool ConfirmCfgEnable(string key)
        {
            switch (key)
            {
                case PolicyCatalog.KeyVramShield:
                    return ConfirmVramShieldEnable();
                case PolicyCatalog.KeyPowerYield:
                    return ConfirmPowerYieldEnable();
                case PolicyCatalog.KeyDisableCpuIdle:
                    return ConfirmDisableCpuIdleEnable();
                case PolicyCatalog.KeyStandbyCleaner:
                    return ConfirmStandbyCleanerEnable();
                case PolicyCatalog.KeyIntelLowLatency:
                    return ConfirmIntelLowLatency();
                default:
                    return true;
            }
        }

        private void ClearAllCfgOverrides()
        {
            if (!RefreshCfgProfile()) return;
            string confirmation = CfgClearConfirmation(cfgProfile);
            if (confirmation == null) return;
            if (!PaviseDialog.Confirm(this, Lang.T("cfg.clear"),
                    confirmation, DlgKind.Warn))
                return;
            if (!ApplyCfgClearAllOverrides())
            {
                // Must not go silent after the confirmation dialog; when the gate blocks, say which item blocked the whole clear
                //   When the user cancels the confirmation the blocked key cannot be found here; keep returning without a prompt
                string blocked = Visible ? CfgBlockedVendorInheritKey() : null;
                if (blocked != null)
                    PaviseDialog.Info(this, Lang.T("cfg.clear"), Lang.F("cfg.clear.blocked",
                        Lang.T(PolicyCatalog.ItemOf(blocked).LangKey)));
                return;
            }
            if (cfgCoreSchedulingPanel != null) cfgCoreSchedulingPanel.Reload();
            SyncCfgRows();
        }

        // A vendor GPU key cannot go back to follow-global while global is still on and the current device does not support it
        //   Otherwise it amounts to confirming on the user's behalf a global enable that cannot take effect on the current device
        private string CfgBlockedVendorInheritKey()
        {
            if (cfgProfile == null) return null;
            foreach (string key in cfgProfile.Overrides.Keys)
                if (!CfgVendorGraphicsChoiceAllowed(key, null)) return key;
            return null;
        }

        private bool ApplyCfgClearAllOverrides()
        {
            if (cfgProfile == null) return false;
            if (CfgBlockedVendorInheritKey() != null) return false;
            // Before removing any override, finish confirming every enabling item; when the second warning is cancelled
            // the first must not have already wiped half the profile
            if (CfgCpuIdleInheritanceNeedsConfirmation(cfgProfile)
                && !ConfirmCfgEnable(PolicyCatalog.KeyDisableCpuIdle)) return false;
            if (CfgStandbyCleanerInheritanceNeedsConfirmation(cfgProfile)
                && !ConfirmCfgEnable(PolicyCatalog.KeyStandbyCleaner)) return false;
            if (CfgIntelInheritanceNeedsConfirmation(cfgProfile)
                && !ConfirmCfgEnable(PolicyCatalog.KeyIntelLowLatency)) return false;
            bool powerYieldWillEnable = CfgPowerYieldInheritanceNeedsConfirmation(cfgProfile);
            if (powerYieldWillEnable && !ConfirmCfgEnable(PolicyCatalog.KeyPowerYield)) return false;
            // Guarded boolean items rank equal; clear-all is the same as switching those this game had off over to the global on
            bool vramWillEnable = false;
            foreach (string guarded in ConfirmGuardedKeys)
            {
                if (!CfgGuardedInheritanceNeedsConfirmation(cfgProfile, guarded)) continue;
                if (!ConfirmCfgEnable(guarded)) return false;
                if (guarded == PolicyCatalog.KeyVramShield) vramWillEnable = true;
            }
            // Other warnings may have pumped UI messages, and driver availability can change in the meantime
            if (CfgBlockedVendorInheritKey() != null) return false;
            bool cleared = gameMode.ClearProfileOverrides(cfgProfileId) > 0;
            if (cleared && powerYieldWillEnable) PowerBudgetYield.ClearFuse();
            if (cleared && vramWillEnable) VramShield.ClearFuse();
            return cleared;
        }

        private static string CfgEnableAdminNoticeKey(string key)
        {
            if (key == PolicyCatalog.KeyDisableCpuIdle) return "gm.disablecpuidle.needadmin";
            if (key == PolicyCatalog.KeyStandbyCleaner) return "gm.standbycleaner.needadmin";
            return null;
        }

        // Guarded boolean items with an enable confirmation; follow-global / clear-all must go through the same gate as an explicit switch-on
        private static readonly string[] ConfirmGuardedKeys =
        {
            PolicyCatalog.KeyVramShield,
        };

        private static bool IsConfirmGuardedKey(string key)
        {
            foreach (string guarded in ConfirmGuardedKeys)
                if (string.Equals(guarded, key, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static bool CfgGuardedInheritanceNeedsConfirmation(GameProfile profile, string key)
        {
            // Same criterion as CpuIdle: clearing an explicit off is the same as choosing the global on
            return profile != null
                && PolicyResolver.Read(profile, key) != "1"
                && PolicyResolver.GlobalValue(key) == "1";
        }

        internal static bool CfgCpuIdleInheritanceNeedsConfirmation(GameProfile profile)
        {
            // Removing an explicit off has the same effect as actively choosing on
            return profile != null
                && PolicyResolver.Read(profile, PolicyCatalog.KeyDisableCpuIdle) != "1"
                && PolicyResolver.GlobalValue(PolicyCatalog.KeyDisableCpuIdle) == "1";
        }

        internal static bool CfgStandbyCleanerInheritanceNeedsConfirmation(GameProfile profile)
        {
            return profile != null
                && PolicyResolver.Read(profile, PolicyCatalog.KeyStandbyCleaner) != "1"
                && PolicyResolver.GlobalValue(PolicyCatalog.KeyStandbyCleaner) == "1";
        }

        internal static bool CfgIntelInheritanceNeedsConfirmation(GameProfile profile)
        {
            return profile != null
                && PolicyResolver.Read(profile, PolicyCatalog.KeyIntelLowLatency) != "1"
                && PolicyResolver.GlobalValue(PolicyCatalog.KeyIntelLowLatency) == "1";
        }

        internal static bool CfgPowerYieldInheritanceNeedsConfirmation(GameProfile profile)
        {
            return profile != null
                && PolicyResolver.Read(profile, PolicyCatalog.KeyPowerYield) != "1"
                && PolicyResolver.GlobalValue(PolicyCatalog.KeyPowerYield) == "1";
        }

        internal static string CfgOverrideSummary(GameProfile profile)
        {
            int count = GameLibraryRow.OrdinaryOverrideCount(profile);
            if (count > 0) return Lang.F("cfg.count", count);
            return Lang.T(profile != null && profile.Overrides.ContainsKey(PolicyCatalog.KeySuppressFamily)
                ? "cfg.count.none.family" : "cfg.count.none");
        }

        internal static string CfgClearConfirmation(GameProfile profile)
        {
            if (profile == null) return null;
            int count = GameLibraryRow.OrdinaryOverrideCount(profile);
            bool family = profile.Overrides.ContainsKey(PolicyCatalog.KeySuppressFamily);
            if (!family && count == 0) return null;
            if (!family) return Lang.F("cfg.clear.confirm", profile.Name, count);
            // The family switch is not on this page, but clear-all turns it off just the same; it cannot be treated as follow-global
            return count > 0 ? Lang.F("cfg.clear.family.confirm", profile.Name, count)
                : Lang.F("cfg.clear.family.only", profile.Name);
        }
    }
}
