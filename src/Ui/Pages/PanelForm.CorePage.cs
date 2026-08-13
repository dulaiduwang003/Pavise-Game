// @author bdth 2074055628@qq.com
// 文件用途 构建核心分配标签页 自动按档位 手动逐核 两种方式任何时刻只显示一套

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private CoreMatrix coreMatrix;
        private Label coreMaskLabel;
        private DBPanel coreManualGroup;
        private CoreSplitBar coreSplitBar;
        private Label coreLandingLine;
        private PillButton coreApplyBtn;
        private ulong corePending;
        private int coreGroupTop;

        private CoreRolePicker rolePicker;
        private DBPanel gamePresetRow;
        private Label coreHowTo;
        private Toggle swCoreSqueeze;
        private SettingCard cardCoreSqueeze;

#if PAVISE_SELFTEST
        internal static string CpuNameOverride;
#endif

        private static string CpuDisplayName()
        {
#if PAVISE_SELFTEST
            if (!string.IsNullOrEmpty(CpuNameOverride)) return CpuNameOverride;
#endif
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string name = key == null ? null : key.GetValue("ProcessorNameString") as string;
                    if (!string.IsNullOrEmpty(name))
                        return System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
                }
            }
            catch { }
            return Lang.T("core.topo.unknown");
        }

        private static string CoreListText(ulong mask)
        {
            var parts = new List<string>();
            for (int i = 0; i < 64 && parts.Count < 12; i++)
                if ((mask & (1UL << i)) != 0) parts.Add(i.ToString());
            string joined = string.Join(",", parts.ToArray());
            return CpuTopology.CountSetBits(mask) > parts.Count ? joined + "…" : joined;
        }

        private static string TopologyLine()
        {
            ulong[] cores = CpuTopology.PhysicalCoreMasks();
            int smt = 0;
            foreach (ulong c in cores)
                if (CpuTopology.CountSetBits(c & CpuTopology.AllMask) > 1) smt++;
            return Lang.F("core.topo.line", cores.Length,
                CpuTopology.CountSetBits(CpuTopology.AllMask), smt);
        }

        private void BuildCorePage(Control panel)
        {
            int y = 2;

            RoundPanel topo = MakeConsolePanel(panel, 6, y, ScrollContentW, 62, true);
            CardLabel(topo, CpuDisplayName(), 16, 10, ScrollContentW - 210, 20, 9.2f, true, Theme.Fg);
            CardLabel(topo, TopologyLine(), 16, 32, ScrollContentW - 210, 18, 7.9f, false, Theme.Dim);
            coreMaskLabel = CardLabel(topo, "", ScrollContentW - 200, 21, 184, 20, 8.6f, true, Theme.Accent);
            coreMaskLabel.TextAlign = ContentAlignment.MiddleRight;
            y += 70;

            ulong all = CpuTopology.AllMask;
            corePending = gameMode.CustomCoreMask;
            if (corePending == 0) corePending = all;

            pickPolicyCores = AddCorePlacementPicker(panel, ref y);

            coreLandingLine = new Label();
            coreLandingLine.SetBounds(Theme.S(18), Theme.S(y - 2),
                Theme.S(ScrollContentW - 24), Theme.S(18));
            coreLandingLine.BackColor = Theme.Bg;
            coreLandingLine.ForeColor = Theme.Faint;
            coreLandingLine.Font = Theme.UI(7.8f, false);
            coreLandingLine.UseCompatibleTextRendering = false;
            panel.Controls.Add(coreLandingLine);
            y += 22;

            int sy = y;
            swCoreSqueeze = AddPolicyToggle(panel, ref sy, Lang.T("gm.squeezebg"),
                Lang.T("gm.squeezebg.sub"),
                delegate { return gameMode.SqueezeBackgroundOn; },
                delegate(bool v) { gameMode.SqueezeBackgroundOn = v; SyncCorePage(); });
            cardCoreSqueeze = swCoreSqueeze.Parent as SettingCard;
            y = sy;

            coreGroupTop = y;

            BuildCoreManualGroup(panel, all);

            policySync.Add(SyncCorePage);
            SyncCorePage();
        }

        internal static List<KeyValuePair<string, ulong>> CorePresets(ulong all)
        {
            ulong physical = CpuTopology.PhysicalOnlyMask();
            ulong cacheHeavy = CpuTopology.CacheHeavyMask();
            ulong[] dies = CpuTopology.DieMasks();

            var raw = new List<KeyValuePair<string, ulong>>
            {
                new KeyValuePair<string, ulong>(Lang.T("core.preset.all"), all),
                new KeyValuePair<string, ulong>(Lang.T("core.preset.physical"), physical),
            };
            if (CpuTopology.Hybrid)
                raw.Add(new KeyValuePair<string, ulong>(
                    Lang.T("core.preset.perf"), CpuTopology.PerfMask & physical));
            if (cacheHeavy != 0)
                raw.Add(new KeyValuePair<string, ulong>(
                    Lang.T("core.preset.cache"), cacheHeavy & physical));
            if (dies.Length >= 2 && dies.Length <= 4)
                for (int d = 0; d < dies.Length; d++)
                    raw.Add(new KeyValuePair<string, ulong>(
                        Lang.F("core.preset.ccd", d), dies[d] & physical));

            var kept = new List<KeyValuePair<string, ulong>>();
            var seen = new List<ulong>();
            foreach (KeyValuePair<string, ulong> preset in raw)
            {
                if (preset.Value == 0
                    || CpuTopology.CountSetBits(preset.Value) < CpuTopology.MinCustomCores) continue;
                if (seen.Contains(preset.Value)) continue;
                seen.Add(preset.Value);
                kept.Add(preset);
            }
            return kept;
        }

        internal static string[][] CoreGuideSteps()
        {
            var rows = new List<string[]>
            {
                new[] { "01", Lang.T("core.guide.s1.t"), Lang.T("core.guide.s1.d") },
            };

            string platform = null;
            if (CpuTopology.AsymCache) platform = "x3d";
            else if (CpuTopology.Hybrid) platform = "hy";
            else if (CpuTopology.DieMasks().Length >= 2) platform = "ccd";
            if (platform != null)
                rows.Add(new[] { "02", Lang.T("core.guide." + platform + ".t"),
                    Lang.T("core.guide." + platform + ".d") });

            bool smt = false;
            foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                if (CpuTopology.CountSetBits(core & CpuTopology.AllMask) > 1) { smt = true; break; }
            if (smt)
                rows.Add(new[] { "", Lang.T("core.guide.smt.t"), Lang.T("core.guide.smt.d") });

            rows.Add(new[] { "", Lang.T("core.guide.s2.t"), Lang.T("core.guide.s2.d") });
            rows.Add(new[] { "", Lang.T("core.guide.judge.t"), Lang.T("core.guide.judge.d") });
            rows.Add(new[] { "", Lang.T("core.guide.s3.t"), Lang.T("core.guide.s3.d") });
            rows.Add(new[] { "", Lang.T("core.guide.s4.t"), Lang.T("core.guide.s4.d") });

            for (int i = 0; i < rows.Count; i++) rows[i][0] = (i + 1).ToString("00");
            return rows.ToArray();
        }

        private void BuildCoreManualGroup(Control panel, ulong all)
        {
            coreManualGroup = new DBPanel();
            coreManualGroup.BackColor = Theme.Bg;

            List<KeyValuePair<string, ulong>> presets = CorePresets(all);

            rolePicker = new CoreRolePicker();
            rolePicker.GameLabel = Lang.T("core.role.game");
            rolePicker.SetBounds(Theme.S(6), 0,
                Theme.S(ScrollContentW - 116), CoreRolePicker.PreferredHeight);
            coreManualGroup.Controls.Add(rolePicker);

            coreHowTo = new Label();
            coreHowTo.SetBounds(Theme.S(12), rolePicker.Bottom + Theme.S(7),
                Theme.S(ScrollContentW - 12), Theme.S(17));
            coreHowTo.BackColor = Theme.Bg;
            coreHowTo.ForeColor = Theme.Dim;
            coreHowTo.Font = Theme.UI(7.9f, false);
            coreHowTo.UseCompatibleTextRendering = false;
            coreManualGroup.Controls.Add(coreHowTo);

            gamePresetRow = new DBPanel();
            gamePresetRow.BackColor = Theme.Bg;
            int px = 6;
            foreach (KeyValuePair<string, ulong> preset in presets)
            {
                ulong value = preset.Value;
                var b = new PillButton(preset.Key, BtnKind.Normal);
                b.Bg = Theme.Card;
                b.SetBounds(Theme.S(px), 0, Theme.S(96), Theme.S(30));
                b.Click += delegate
                {
                    corePending = value;
                    if (coreMatrix != null) coreMatrix.Selected = corePending;
                    SyncCorePage();
                };
                gamePresetRow.Controls.Add(b);
                px += 102;
            }
            var invert = new PillButton(Lang.T("core.preset.invert"), BtnKind.Normal);
            invert.Bg = Theme.Card;
            invert.SetBounds(Theme.S(px), 0, Theme.S(80), Theme.S(30));
            invert.Click += delegate
            {
                corePending = ~corePending & CpuTopology.AllMask;
                if (coreMatrix != null) coreMatrix.Selected = corePending;
                SyncCorePage();
            };
            gamePresetRow.Controls.Add(invert);

            var guideBtn = new PillButton(Lang.T("core.guide.open"), BtnKind.Normal);
            guideBtn.Bg = Theme.Card;
            guideBtn.SetBounds(Theme.S(ScrollContentW + 6) - Theme.S(96), 0, Theme.S(96), Theme.S(30));
            guideBtn.Click += delegate { ShowCoreGuide(); };
            gamePresetRow.Controls.Add(guideBtn);
            coreManualGroup.Controls.Add(gamePresetRow);

            coreMatrix = new CoreMatrix();
            coreMatrix.PrimaryTag = Lang.T("core.tag.game");
            coreMatrix.Selected = corePending;
            coreMatrix.SelectionChanged = delegate(ulong mask) { corePending = mask; SyncCorePage(); };
            int widthPx = Theme.S(ScrollContentW - 6);
            int heightPx = coreMatrix.LayoutFor(widthPx);
            int presetY = coreHowTo.Bottom + Theme.S(6);
            gamePresetRow.SetBounds(0, presetY, Theme.S(ScrollContentW + 6), Theme.S(30));

            coreMatrix.SetBounds(Theme.S(12), presetY + Theme.S(38), widthPx, heightPx);
            coreManualGroup.Controls.Add(coreMatrix);

            coreSplitBar = new CoreSplitBar();
            coreSplitBar.SetBounds(Theme.S(12), coreMatrix.Bottom + Theme.S(12),
                Theme.S(ScrollContentW - 12), CoreSplitBar.PreferredHeight);
            coreManualGroup.Controls.Add(coreSplitBar);

            coreApplyBtn = new PillButton(Lang.T("core.apply"), BtnKind.Primary);
            coreApplyBtn.SetBounds(Theme.S(ScrollContentW - 98),
                (CoreRolePicker.PreferredHeight - Theme.S(38)) / 2, Theme.S(104), Theme.S(38));
            coreApplyBtn.Click += delegate
            {
                ulong clean = CpuTopology.SanitizeCustomMask(corePending, CpuTopology.AllMask);
                if (clean == 0) return;
                gameMode.CustomCoreMask = clean == CpuTopology.AllMask ? 0 : clean;
                SyncCorePage();
            };
            coreManualGroup.Controls.Add(coreApplyBtn);

            coreManualGroup.SetBounds(0, Theme.S(coreGroupTop),
                Theme.S(ScrollContentW + 12), coreSplitBar.Bottom + Theme.S(6));
            panel.Controls.Add(coreManualGroup);
        }

        private void ShowCoreGuide()
        {
            const int dlgW = 720;
            var scroll = new DBPanel();
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);

            var guide = new CoreGuide();
            guide.SetContent("", Lang.T("core.guide.tag"), Lang.T("core.guide.note"),
                CoreGuideSteps());
            int innerW = Theme.S(dlgW - 52) - Theme.S(18);
            int innerH = guide.LayoutFor(innerW);
            guide.SetBounds(0, 0, innerW, innerH);
            scroll.Controls.Add(guide);
            scroll.Size = new Size(Theme.S(dlgW - 52), Math.Min(innerH + Theme.S(4), Theme.S(410)));

            PaviseDialog.Show(this, Lang.T("core.guide.title"), TopologyLine(), scroll, dlgW);
        }

        private void SyncSqueezeCard()
        {
            if (swCoreSqueeze == null || cardCoreSqueeze == null) return;
            int status = CpuTopology.SqueezeStatusFor(PendingGameMask());
            bool permanent = status == CpuTopology.SqueezeTooFewCores
                || status == CpuTopology.SqueezeMultiGroup;

            string desc;
            if (status == CpuTopology.SqueezeTooFewCores)
                desc = Lang.F("gm.squeezebg.none", CpuTopology.PhysicalCoreMasks().Length,
                    CpuPartitionPolicy.SqueezeMinPhysical);
            else if (status == CpuTopology.SqueezeMultiGroup)
                desc = Lang.T("gm.squeezebg.multigroup");
            else if (status == CpuTopology.SqueezeAlreadyNarrow)
                desc = Lang.F("gm.squeezebg.narrow",
                    CpuTopology.BackgroundWholeCoresFor(PendingGameMask()));
            else desc = Lang.T("gm.squeezebg.sub");

            swCoreSqueeze.Enabled = !permanent;
            cardCoreSqueeze.Desc = desc;
        }

        private ulong PendingGameMask()
        {
            ulong clean = CpuTopology.SanitizeCustomMask(corePending, CpuTopology.AllMask);
            return clean == CpuTopology.AllMask ? 0 : clean;
        }

        private void SyncCorePage()
        {
            if (coreMatrix == null) return;
            ulong live = gameMode.CustomCoreMask;
            bool manual = pickPolicyCores != null && pickPolicyCores.Index == coreManualIndex;

            if (coreManualGroup != null)
            {
                bool wasShown = coreManualGroup.Visible;
                if (!manual) Fx.Settle(coreManualGroup);
                coreManualGroup.Visible = manual;
                if (manual && !wasShown) Fx.SlideIn(coreManualGroup);
            }
            if (coreHowTo != null) coreHowTo.Text = Lang.T("core.howto.game");

            int logical = CpuTopology.CountSetBits(corePending);
            int physical = 0;
            foreach (ulong group in CpuTopology.PhysicalCoreMasks())
                if ((group & corePending) != 0) physical++;

            bool valid = CpuTopology.SanitizeCustomMask(corePending, CpuTopology.AllMask) != 0;
            ulong effective = live == 0 ? CpuTopology.AllMask : live;
            bool dirty = corePending != effective;

            if (coreMaskLabel != null)
            {
                coreMaskLabel.Text = live == 0
                    ? Lang.T("core.topo.default")
                    : "0x" + live.ToString("X") + "  ·  " + CpuTopology.CountSetBits(live);
                coreMaskLabel.ForeColor = live == 0 ? Theme.Dim : Theme.Accent;
            }

            SyncSqueezeCard();

            if (rolePicker != null)
            {
                rolePicker.GameCountText = Lang.F("core.role.count", logical, physical);
                rolePicker.SetCounts(logical, physical);
                rolePicker.Invalidate();
            }

            if (coreSplitBar != null)
            {
                if (!valid)
                    coreSplitBar.SetState(0, 0, Lang.T("core.state.few"), "", true, dirty, false);
                else
                {
                    ulong rest = corePending == CpuTopology.AllMask
                        ? 0
                        : CpuTopology.BackgroundRemainderFor(corePending, CpuTopology.AllMask);
                    int bgCores = CpuTopology.CountSetBits(rest);
                    string tail;
                    if (corePending == CpuTopology.AllMask) tail = Lang.T("core.bg.allcores");
                    else if (rest == 0)
                        tail = Lang.F("core.bg.toofull", CpuTopology.MinCustomBackgroundCores);
                    else tail = Lang.F("core.bg.ok", bgCores, CoreListText(rest));
                    coreSplitBar.SetState(logical,
                        CpuTopology.CountSetBits(CpuTopology.AllMask),
                        Lang.F(dirty ? "core.state.dirty" : "core.state.applied", logical, physical),
                        tail, rest == 0 && corePending != CpuTopology.AllMask, dirty, true);
                }
            }
            if (coreApplyBtn != null) coreApplyBtn.Enabled = valid && dirty;
            if (coreLandingLine != null)
            {
                bool wasShown = coreLandingLine.Visible;
                coreLandingLine.Visible = !manual;
                if (!manual)
                {
                    coreLandingLine.Text = BackgroundLandingText(0);
                    if (!wasShown) Fx.SlideIn(coreLandingLine);
                }
            }
        }

        private string BackgroundLandingText(ulong pendingGameMask)
        {
            ulong allowed = CpuTopology.BackgroundAllowedMaskFor(pendingGameMask);
            if (allowed == 0) return Lang.T("core.land.none");
            if (!gameMode.SqueezeBackgroundOn || CpuTopology.MultiGroup)
                return Lang.F("core.land.plain", CoreListText(allowed));
            ulong squeeze = CpuTopology.BackgroundSqueezeMaskFor(pendingGameMask);
            if (squeeze != 0)
                return Lang.F("core.land.squeezed", CoreListText(squeeze),
                    CpuTopology.CountSetBits(allowed), CpuTopology.CountSetBits(squeeze));
            int whole = 0;
            foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                if ((core & allowed) == core) whole++;
            return Lang.F(whole == 0 ? "core.land.nowhole" : "core.land.narrow", CoreListText(allowed));
        }
    }
}
