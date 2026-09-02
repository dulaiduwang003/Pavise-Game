// @author bdth 2074055628@qq.com
// 文件用途 逐游戏配置的行控件 覆盖写入与清除确认
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
            // 模式条与取值数组必须同源 极限锁着时两边都没有它
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
                // 正在对局的这个游戏锁档 改其它游戏无妨 快照冻结且下局才生效
                if (gameMode.IsActive && gameMode.Enabled
                    && string.Equals(cfgProfileId, gameMode.SessionPolicyProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    PaviseDialog.Info(this, App.DisplayName, Lang.T("mode.locked.ingame"));
                    SyncCfgRows();
                    return;
                }
                if (index <= 0) gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyPreset);
                else gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyPreset, values[index - 1]);
                SyncCfgRows();
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
            // 能力丢失不能把一个已经开着的选项困死在里面
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
                else if (vendorGraphics)
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
                // 三种锁定全站同一套标签 不支持的项不再借"预设强制关"的名义
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
                    // 被厂商显卡门槛挡下时要说明原因 卡片描述解释不了"为什么不能跟随全局"
                    //   Visible 门槛 隔离回归在未显示的窗体上驱动该路径 不能弹窗
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
            // 守门布尔项的"跟随全局"与显式拨开同权 全局开着而本游戏此前生效为关时
            //   清掉覆盖就是启用 确认必须照弹 熔断也要跟着清
            bool inheritingGuarded = value == null && IsConfirmGuardedKey(key)
                && CfgGuardedInheritanceNeedsConfirmation(cfgProfile, key);
            bool inheritingGuardedOn = value == null
                && ((key == PolicyCatalog.KeyDisableCpuIdle && CfgCpuIdleInheritanceNeedsConfirmation(cfgProfile))
                    || (key == PolicyCatalog.KeyStandbyCleaner && CfgStandbyCleanerInheritanceNeedsConfirmation(cfgProfile))
                    || (key == PolicyCatalog.KeyIntelLowLatency && CfgIntelInheritanceNeedsConfirmation(cfgProfile))
                    || inheritingPowerYield || inheritingGuarded);
            // 在这里拨到开 等同于把全局开关打开一次 开启确认必须照弹
            //   否则从这个页面能绕开优化策略页特意加的门槛 用户全程没见过警告
            if ((turningOn || inheritingGuardedOn) && !ConfirmCfgEnable(key)) return false;
            bool saved = value == null ? gameMode.ClearProfileOverride(cfgProfileId, key)
                : gameMode.SetProfileOverride(cfgProfileId, key, canonical);
            // 显存驻留在这里拨到开 也等同于全局开关重开一次 熔断要跟着清掉
            //   否则上次验不过留下的熔断会让这局直接跳过 用户在这个页面无从解除
            if (saved && (turningOn || inheritingGuarded) && key == PolicyCatalog.KeyVramShield)
                VramShield.ClearFuse();
            if (saved && key == PolicyCatalog.KeyPowerYield && (turningOn || inheritingPowerYield))
                PowerBudgetYield.ClearFuse();
            return saved;
        }

        // 全局开关带确认的项 逐游戏覆盖到开也要走同一段提示 文案共用一份
        private bool ConfirmCfgEnable(string key)
        {
            switch (key)
            {
                case PolicyCatalog.KeyVramShield:
                    return ConfirmVramShieldEnable();
                case PolicyCatalog.KeyCacheWarm:
                    return ConfirmCacheWarmEnable();
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
                // 确认弹窗之后不能沉默 门槛拦下时说明是哪一项挡住了整次清除
                //   确认被用户自己取消的情况这里查不到被挡的键 维持无提示返回
                string blocked = Visible ? CfgBlockedVendorInheritKey() : null;
                if (blocked != null)
                    PaviseDialog.Info(this, Lang.T("cfg.clear"), Lang.F("cfg.clear.blocked",
                        Lang.T(PolicyCatalog.ItemOf(blocked).LangKey)));
                return;
            }
            cfgCoreManualPicked = false;
            SyncCfgRows();
        }

        // 厂商显卡键在全局仍为开且当前设备不支持时不能改回跟随全局
        //   否则等于替用户确认一个当前设备生效不了的全局开启
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
            // 移除任何覆盖之前 先把所有开启项都确认完 第二个警告被取消时
            // 不能出现第一个已经把档案清掉一半的情况
            if (CfgCpuIdleInheritanceNeedsConfirmation(cfgProfile)
                && !ConfirmCfgEnable(PolicyCatalog.KeyDisableCpuIdle)) return false;
            if (CfgStandbyCleanerInheritanceNeedsConfirmation(cfgProfile)
                && !ConfirmCfgEnable(PolicyCatalog.KeyStandbyCleaner)) return false;
            if (CfgIntelInheritanceNeedsConfirmation(cfgProfile)
                && !ConfirmCfgEnable(PolicyCatalog.KeyIntelLowLatency)) return false;
            bool powerYieldWillEnable = CfgPowerYieldInheritanceNeedsConfirmation(cfgProfile);
            if (powerYieldWillEnable && !ConfirmCfgEnable(PolicyCatalog.KeyPowerYield)) return false;
            // 守门布尔项同权 全部清除等同把它们中被本游戏关着的那些拨到全局的开
            bool vramWillEnable = false;
            foreach (string guarded in ConfirmGuardedKeys)
            {
                if (!CfgGuardedInheritanceNeedsConfirmation(cfgProfile, guarded)) continue;
                if (!ConfirmCfgEnable(guarded)) return false;
                if (guarded == PolicyCatalog.KeyVramShield) vramWillEnable = true;
            }
            // 其它警告可能泵过界面消息 这期间驱动可用性会变
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

        // 带开启确认的守门布尔项 "跟随全局/全部清除"要与显式拨开走同一道门
        private static readonly string[] ConfirmGuardedKeys =
        {
            PolicyCatalog.KeyVramShield,
            PolicyCatalog.KeyCacheWarm,
        };

        private static bool IsConfirmGuardedKey(string key)
        {
            foreach (string guarded in ConfirmGuardedKeys)
                if (string.Equals(guarded, key, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static bool CfgGuardedInheritanceNeedsConfirmation(GameProfile profile, string key)
        {
            // 与 CpuIdle 同款判据 清掉显式的关等同选择了全局的开
            return profile != null
                && PolicyResolver.Read(profile, key) != "1"
                && PolicyResolver.GlobalValue(key) == "1";
        }

        internal static bool CfgCpuIdleInheritanceNeedsConfirmation(GameProfile profile)
        {
            // 把一条显式的关闭移掉 效果和主动选开是一样的
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
            // 家族开关并不在本页出现 但“全部清除”仍会关闭它 不能当作跟随全局
            return count > 0 ? Lang.F("cfg.clear.family.confirm", profile.Name, count)
                : Lang.F("cfg.clear.family.only", profile.Name);
        }
    }
}
