// @author bdth 2074055628@qq.com
// 文件用途 单个游戏配置二级页的核心 tab 逐核分配选择与手动掩码编辑
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private TierPicker cfgCorePicker;
        private SettingCard cfgCoreCard;
        private DBPanel cfgCoreManualGroup;
        private CoreMatrix cfgCoreMatrix;
        private Label cfgCoreState;
        private PillButton cfgCoreApply;
        private ulong cfgCorePending;
        private bool cfgCoreManualPicked;
        private bool cfgCorePartitionAvailable;
        private bool cfgCoreThreeWay;
        private int cfgCoreAllIndex, cfgCorePartIndex, cfgCoreAltIndex, cfgCoreManualIndex;

        private void BuildCfgCoreTab(Control panel)
        {
            cfgCorePartitionAvailable = CpuTopology.HasSafeBackgroundPartition();
            cfgCoreThreeWay = cfgCorePartitionAvailable && CpuTopology.HasAltPartition();

            var labels = new List<string> { Lang.T("cfg.follow"), Lang.T("cpu.place.all") };
            cfgCoreAllIndex = 1;
            cfgCorePartIndex = -1;
            cfgCoreAltIndex = -1;
            if (cfgCorePartitionAvailable)
            {
                cfgCorePartIndex = labels.Count;
                labels.Add(cfgCoreThreeWay ? PrimaryDomainLabel() : CorePartitionLabel());
                if (cfgCoreThreeWay)
                {
                    cfgCoreAltIndex = labels.Count;
                    labels.Add(AltDomainLabel());
                }
            }
            cfgCoreManualIndex = labels.Count;
            labels.Add(Lang.T("cpu.place.manual"));

            cfgCorePicker = new TierPicker();
            cfgCorePicker.Labels = labels.ToArray();
            cfgCorePicker.Index = CfgCoreIndex();
            cfgCorePicker.SetBounds(0, 0, Theme.S(labels.Count * 88 + 12), Theme.S(34));
            cfgCorePicker.IndexChanged = delegate(int index) { ApplyCfgCorePlacement(index); };

            int cardH;
            cfgCoreCard = MakeAutoCard(panel, 6, 2, ScrollContentW, 88,
                Lang.T("cpu.place.title"), Lang.T("cfg.core.range.sub"), cfgCorePicker,
                Theme.S(labels.Count * 88 + 24), out cardH);
            int groupTop = 2 + cardH + 10;

            cfgCorePending = CfgCoreInitialMask();
            BuildCfgCoreManualGroup(panel, groupTop);

            cfgRowSync.Add(SyncCfgCoreTab);
        }

        private ulong CfgCoreInitialMask()
        {
            string raw;
            ulong mask;
            if (cfgProfile != null && cfgProfile.Overrides.TryGetValue(PolicyCatalog.KeyCoreMask, out raw)
                && raw.Length > 0
                && ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out mask)
                && mask != 0)
                return mask;
            ulong global = CpuTopology.CustomMask;
            return global != 0 ? global : CpuTopology.AllMask;
        }

        private ulong CfgCoreOverrideMask(out bool hasMask, out bool maskEmpty)
        {
            hasMask = false;
            maskEmpty = false;
            string raw;
            if (cfgProfile == null
                || !cfgProfile.Overrides.TryGetValue(PolicyCatalog.KeyCoreMask, out raw)) return 0;
            hasMask = true;
            if (raw.Length == 0) { maskEmpty = true; return 0; }
            ulong mask;
            return ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out mask) ? mask : 0;
        }

        private int CfgCoreIndex()
        {
            if (cfgCoreManualPicked) return cfgCoreManualIndex;
            bool hasMask, maskEmpty;
            ulong mask = CfgCoreOverrideMask(out hasMask, out maskEmpty);
            if (hasMask && mask != 0) return cfgCoreManualIndex;
            string strict = null;
            bool hasStrict = cfgProfile != null
                && cfgProfile.Overrides.TryGetValue(PolicyCatalog.KeyStrictCores, out strict);
            if (hasStrict && strict == "1" && cfgCorePartIndex >= 0)
            {
                string domain;
                bool alt = cfgCoreThreeWay && cfgProfile.Overrides.TryGetValue(
                    PolicyCatalog.KeyCoreDomainAlt, out domain) && domain == "1";
                return alt ? cfgCoreAltIndex : cfgCorePartIndex;
            }
            if (hasStrict || hasMask) return cfgCoreAllIndex;
            return 0;
        }

        private void ApplyCfgCorePlacement(int index)
        {
            cfgCoreManualPicked = index == cfgCoreManualIndex;
            if (index == 0)
            {
                gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyStrictCores);
                gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyCoreDomainAlt);
                gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyCoreMask);
            }
            else if (index == cfgCoreAllIndex)
            {
                gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyStrictCores, "0");
                gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyCoreMask, "");
                gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyCoreDomainAlt);
            }
            else if (index == cfgCorePartIndex || index == cfgCoreAltIndex)
            {
                gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyStrictCores, "1");
                gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyCoreMask, "");
                if (cfgCoreThreeWay)
                    gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyCoreDomainAlt,
                        index == cfgCoreAltIndex ? "1" : "0");
                else gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyCoreDomainAlt);
            }
            SyncCfgRows();
        }

        private void BuildCfgCoreManualGroup(Control panel, int groupTop)
        {
            cfgCoreManualGroup = new DBPanel();
            cfgCoreManualGroup.BackColor = Theme.Bg;

            var howTo = new Label();
            howTo.SetBounds(Theme.S(12), 0, Theme.S(ScrollContentW - 130), Theme.S(17));
            howTo.BackColor = Theme.Bg;
            howTo.ForeColor = Theme.Dim;
            howTo.Font = Theme.UI(7.9f, false);
            howTo.UseCompatibleTextRendering = false;
            howTo.Text = Lang.T("core.howto.game");
            cfgCoreManualGroup.Controls.Add(howTo);

            var presetRow = new DBPanel();
            presetRow.BackColor = Theme.Bg;
            int px = 6;
            foreach (KeyValuePair<string, ulong> preset in CorePresets(CpuTopology.AllMask))
            {
                ulong value = preset.Value;
                var b = new PillButton(preset.Key, BtnKind.Normal);
                b.Bg = Theme.Card;
                b.SetBounds(Theme.S(px), 0, Theme.S(96), Theme.S(30));
                b.Click += delegate
                {
                    cfgCorePending = value;
                    if (cfgCoreMatrix != null) cfgCoreMatrix.Selected = cfgCorePending;
                    SyncCfgCoreTab();
                };
                presetRow.Controls.Add(b);
                px += 102;
            }
            var invert = new PillButton(Lang.T("core.preset.invert"), BtnKind.Normal);
            invert.Bg = Theme.Card;
            invert.SetBounds(Theme.S(px), 0, Theme.S(80), Theme.S(30));
            invert.Click += delegate
            {
                cfgCorePending = ~cfgCorePending & CpuTopology.AllMask;
                if (cfgCoreMatrix != null) cfgCoreMatrix.Selected = cfgCorePending;
                SyncCfgCoreTab();
            };
            presetRow.Controls.Add(invert);
            presetRow.SetBounds(0, Theme.S(21), Theme.S(ScrollContentW + 6), Theme.S(30));
            cfgCoreManualGroup.Controls.Add(presetRow);

            cfgCoreMatrix = new CoreMatrix();
            cfgCoreMatrix.PrimaryTag = Lang.T("core.tag.game");
            cfgCoreMatrix.Selected = cfgCorePending;
            cfgCoreMatrix.SelectionChanged = delegate(ulong mask)
            {
                cfgCorePending = mask;
                SyncCfgCoreTab();
            };
            int widthPx = Theme.S(ScrollContentW - 6);
            int heightPx = cfgCoreMatrix.LayoutFor(widthPx);
            cfgCoreMatrix.SetBounds(Theme.S(12), presetRow.Bottom + Theme.S(8), widthPx, heightPx);
            cfgCoreManualGroup.Controls.Add(cfgCoreMatrix);

            cfgCoreState = new Label();
            cfgCoreState.SetBounds(Theme.S(12), cfgCoreMatrix.Bottom + Theme.S(12),
                Theme.S(ScrollContentW - 140), Theme.S(34));
            cfgCoreState.BackColor = Theme.Bg;
            cfgCoreState.ForeColor = Theme.Dim;
            cfgCoreState.Font = Theme.UI(8.4f, false);
            cfgCoreState.UseCompatibleTextRendering = false;
            cfgCoreState.TextAlign = ContentAlignment.MiddleLeft;
            cfgCoreManualGroup.Controls.Add(cfgCoreState);

            cfgCoreApply = new PillButton(Lang.T("core.apply"), BtnKind.Primary);
            cfgCoreApply.SetBounds(Theme.S(ScrollContentW - 110), cfgCoreMatrix.Bottom + Theme.S(10),
                Theme.S(104), Theme.S(36));
            cfgCoreApply.Click += delegate { ApplyCfgCoreMask(); };
            cfgCoreManualGroup.Controls.Add(cfgCoreApply);

            cfgCoreManualGroup.SetBounds(0, Theme.S(groupTop),
                Theme.S(ScrollContentW + 12), cfgCoreApply.Bottom + Theme.S(8));
            cfgCoreManualGroup.Visible = false;
            panel.Controls.Add(cfgCoreManualGroup);
        }

        private void ApplyCfgCoreMask()
        {
            ulong clean = CpuTopology.SanitizeCustomMask(cfgCorePending, CpuTopology.AllMask);
            if (clean == 0) return;
            gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyCoreMask,
                clean == CpuTopology.AllMask ? "" : clean.ToString("X"));
            // 自定义掩码为空表示没有自定义亲和性 不是覆盖全局的分区选择
            // 要全选就得显式声明退出
            if (clean == CpuTopology.AllMask)
                gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyStrictCores, "0");
            else gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyStrictCores);
            gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyCoreDomainAlt);
            SyncCfgRows();
        }

        private string CfgCoreSummary()
        {
            bool hasMask, maskEmpty;
            ulong mask = CfgCoreOverrideMask(out hasMask, out maskEmpty);
            string strict = null;
            bool hasStrict = cfgProfile != null
                && cfgProfile.Overrides.TryGetValue(PolicyCatalog.KeyStrictCores, out strict);
            bool overridden = hasMask || hasStrict
                || (cfgProfile != null && cfgProfile.Overrides.ContainsKey(PolicyCatalog.KeyCoreDomainAlt));
            if (!overridden)
            {
                ulong global = CpuTopology.CustomMask;
                string globalText = global != 0 ? CpuTopology.DescribeMask(global)
                    : gameMode.CorePartitionEnabled && cfgCorePartitionAvailable
                        ? (cfgCoreThreeWay && gameMode.CoreDomainAlt ? AltDomainLabel()
                            : cfgCoreThreeWay ? PrimaryDomainLabel() : CorePartitionLabel())
                        : Lang.T("cpu.place.all");
                return Lang.F("cfg.state.follow", globalText);
            }
            string text;
            if (hasMask && mask != 0) text = CpuTopology.DescribeMask(mask);
            else if (hasStrict && strict == "1" && cfgCorePartIndex >= 0)
            {
                string domain;
                bool alt = cfgCoreThreeWay && cfgProfile.Overrides.TryGetValue(
                    PolicyCatalog.KeyCoreDomainAlt, out domain) && domain == "1";
                text = alt ? AltDomainLabel()
                    : cfgCoreThreeWay ? PrimaryDomainLabel() : CorePartitionLabel();
            }
            else text = Lang.T("cpu.place.all");
            return Lang.F("cfg.core.over", text);
        }

        private void SyncCfgCoreTab()
        {
            if (cfgCorePicker == null) return;
            int idx = CfgCoreIndex();
            cfgCorePicker.Index = idx;

            bool overridden = cfgProfile != null
                && (cfgProfile.Overrides.ContainsKey(PolicyCatalog.KeyCoreMask)
                    || cfgProfile.Overrides.ContainsKey(PolicyCatalog.KeyStrictCores)
                    || cfgProfile.Overrides.ContainsKey(PolicyCatalog.KeyCoreDomainAlt));
            if (cfgCoreCard != null)
                cfgCoreCard.SetValue(CfgCoreSummary(), overridden ? Theme.Accent : Theme.Faint);

            bool manual = idx == cfgCoreManualIndex;
            if (cfgCoreManualGroup != null)
            {
                cfgCoreManualGroup.Visible = manual;
            }
            if (!manual) return;

            int logical = CpuTopology.CountSetBits(cfgCorePending);
            int physical = 0;
            foreach (ulong group in CpuTopology.PhysicalCoreMasks())
                if ((group & cfgCorePending) != 0) physical++;
            bool valid = CpuTopology.SanitizeCustomMask(cfgCorePending, CpuTopology.AllMask) != 0;

            bool hasMask, maskEmpty;
            ulong applied = CfgCoreOverrideMask(out hasMask, out maskEmpty);
            ulong appliedEffective = hasMask
                ? (applied != 0 ? applied : CpuTopology.AllMask) : 0;
            bool dirty = appliedEffective == 0 || cfgCorePending != appliedEffective
                || (cfgCorePending == CpuTopology.AllMask
                    && PolicyResolver.Read(cfgProfile, PolicyCatalog.KeyStrictCores) == "1");

            if (cfgCoreState != null)
            {
                cfgCoreState.Text = valid
                    ? Lang.F(dirty ? "core.state.dirty" : "core.state.applied", logical, physical)
                    : Lang.T("core.state.few");
                cfgCoreState.ForeColor = valid ? (dirty ? Theme.Accent : Theme.Dim) : Theme.Danger;
            }
            if (cfgCoreApply != null) cfgCoreApply.Enabled = valid && dirty;
        }
    }
}
