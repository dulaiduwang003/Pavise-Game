// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private string coreProfileId;
        private AdvancedBackBar coreBackBar;
        private DBPanel coreScrollPanel;
        private SettingCard coreBackgroundCard;
        private Toggle automaticCoreLayoutSwitch;

        private GameProfile CorePageProfile()
        {
            if (string.IsNullOrEmpty(coreProfileId)) return null;
            foreach (GameProfile profile in gameMode.GetProfiles())
                if (string.Equals(profile.Id, coreProfileId, StringComparison.OrdinalIgnoreCase)) return profile;
            return null;
        }

        private void OpenGameCoreScheduling(string profileId)
        {
            coreProfileId = profileId;
            if (CorePageProfile() == null) { coreProfileId = null; return; }
            nav.Select((int)PageId.CoreScheduling);
            coreScrollPanel.AutoScrollPosition = Point.Empty;
            SyncCorePage();
        }
        private CoreMatrix coreMatrix;
        private RoundPanel coreStatusPanel, coreLivePanel, coreMapPanel;
        private Label lblCoreState, lblCoreReason, lblCoreRule, lblCorePlan, lblCoreReserved, lblCoreTelemetry;
        private PillButton btnCoreClearReserved;
        private readonly Label[] coreLiveValues = new Label[5];
        private ulong coreMapAll = ulong.MaxValue;
        private int coreMapCores = -1;

#if PAVISE_SELFTEST
        internal static string CpuNameOverride;
        internal Func<bool> ClearReservedConfirmationForTest;
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

        private static string TopologyLine()
        {
            ulong[] cores = CpuTopology.PhysicalCoreMasks();
            int smt = 0;
            foreach (ulong c in cores)
                if (CpuTopology.CountSetBits(c & CpuTopology.AllMask) > 1) smt++;
            string line = Lang.F("core.topo.line", cores.Length,
                CpuTopology.CountSetBits(CpuTopology.AllMask), smt);
            return CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled
                ? line : line + Lang.T("core.topo.split");
        }

        private void BuildCoreSchedulingPage()
        {
            coreMapAll = ulong.MaxValue;
            coreMapCores = -1;
            pageCoreScheduling.SetBounds(0, Theme.S(TopH), Theme.S(WinW), Theme.S(PageH));
            int contentWidth = ContentW + RailW;
            int y = PageHeader(pageCoreScheduling, Lang.T("nav.corescheduling"),
                Lang.T("schedule.page.sub"), 2, false, 0, contentWidth);
            var banner = new ModuleBanner();
            banner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(contentWidth), Theme.S(72));
            banner.Code = "CORE SCHEDULING // 10";
            banner.TitleText = CpuDisplayName();
            banner.Detail = TopologyLine();
            banner.Glyph = "chip";
            pageCoreScheduling.Controls.Add(banner);
            y += 84;

            coreScrollPanel = new WorkspacePanel();
            coreScrollPanel.SetBounds(Theme.S(20), Theme.S(y), Theme.S(WinW - 40), Theme.S(PageH - y - 8));
            coreScrollPanel.BackColor = Theme.Bg; coreScrollPanel.AutoScroll = true;
            Native.Dark(coreScrollPanel);
            pageCoreScheduling.Controls.Add(coreScrollPanel);

            int x = 10, w = ScrollContentW + RailW - 16, py = 2;
            automaticCoreLayoutSwitch = MakeSwitch(gameMode.AutomaticCoreLayoutEnabled, delegate(object sender, EventArgs args)
            {
                var toggle = (Toggle)sender;
                if (!gameMode.TrySetAutomaticCoreLayoutEnabled(toggle.Checked))
                {
                    toggle.SetSilently(gameMode.AutomaticCoreLayoutEnabled);
                    PaviseDialog.Warn(this, App.DisplayName, Lang.T("set.autolayout.fail"));
                }
            });
            automaticCoreLayoutSwitch.Name = "AutomaticCoreLayoutSwitch";
            int autoCardHeight;
            coreBackgroundCard = MakeAutoCard(coreScrollPanel, x, py, w,
                FullTextCardHeight(Lang.T("set.autolayout.n"), w, automaticCoreLayoutSwitch, 88),
                Lang.T("set.autolayout"), Lang.T("set.autolayout.n"), automaticCoreLayoutSwitch, out autoCardHeight);
            coreBackgroundCard.Expanded = true;
            py += autoCardHeight + 12;
            BuildCoreEditor(x, py, w);
            coreStatusPanel = MakeConsolePanel(coreScrollPanel, x, py, w, 116, true);
            coreStatusPanel.Name = "coreStatusPanel";
            MonoLabel(coreStatusPanel, "CORE STATUS // 00", 18, 12, 300);
            lblCoreState = CardLabel(coreStatusPanel, "", 18, 28, w - 36, 28, 12.5f, true, Theme.Fg);
            lblCoreState.Name = "coreState";
            lblCoreReason = CardLabel(coreStatusPanel, "", 18, 58, w - 36, 22, 9f, false, Theme.Dim);
            lblCoreReason.Name = "coreReason";
            lblCoreReason.AutoEllipsis = false;
            lblCoreRule = CardLabel(coreStatusPanel, Lang.T("schedule.auto.rule"),
                18, 84, w - 36, 22, 8.2f, false, Theme.Faint);
            lblCoreRule.Name = "coreRule";
            lblCoreRule.AutoEllipsis = false;
            lblCoreTelemetry = CardLabel(coreStatusPanel, "", 18, 108, w - 36, 22, 8.2f, false, Theme.Dim);
            lblCoreTelemetry.Name = "coreTelemetry";
            lblCoreTelemetry.AutoEllipsis = false;
            py += 128;

            coreLivePanel = MakeConsolePanel(coreScrollPanel, x, py, w, 78, false);
            coreLivePanel.Name = "coreLivePanel";
            MonoLabel(coreLivePanel, "LIVE // 01", 18, 10, 200);
            string[] keys = { "schedule.auto.live.game", "schedule.auto.live.active", "schedule.auto.live.load",
                "schedule.auto.live.windows", "schedule.auto.live.releases" };
            int cellW = (w - 36) / keys.Length;
            for (int i = 0; i < keys.Length; i++)
            {
                CardLabel(coreLivePanel, Lang.T(keys[i]), 18 + i * cellW, 28, cellW - 12, 18, 8f, false, Theme.Dim);
                coreLiveValues[i] = CardLabel(coreLivePanel, "", 18 + i * cellW, 46, cellW - 12, 24, 10.5f, true, Theme.Fg);
                coreLiveValues[i].Name = "coreLive" + i;
            }
            py += 90;

            coreMapPanel = MakeConsolePanel(coreScrollPanel, x, py, w, 120, false);
            coreMapPanel.Name = "coreMapPanel";
            MonoLabel(coreMapPanel, "CORE MAP // 02", 18, 12, 200);
            lblCorePlan = CardLabel(coreMapPanel, "", 18, 28, w - 36, 24, 9.5f, true, Theme.Fg);
            lblCorePlan.Name = "corePlan";
            coreMatrix = new CoreMatrix
            {
                MarkExclusive = false, SchedulingOverlay = true,
                SelectionColor = CoreMatrix.GameColor, PrimaryTag = Lang.T("core.tag.exclusive"),
                BackColor = Theme.Card
            };
            coreMatrix.SetBounds(Theme.S(18), Theme.S(58), Theme.S(w - 36), Theme.S(40));
            coreMapPanel.Controls.Add(coreMatrix);
            lblCoreReserved = CardLabel(coreMapPanel, "", 18, 0, w - 36, 36, 8.5f, false, Theme.Warning);
            lblCoreReserved.Name = "coreReserved";
            lblCoreReserved.AutoEllipsis = false;
            btnCoreClearReserved = new PillButton(Lang.T("schedule.auto.reserved.clear"), BtnKind.Normal);
            btnCoreClearReserved.Name = "coreClearReserved";
            btnCoreClearReserved.SetBounds(Theme.S(18), 0, Theme.S(200), Theme.S(30));
            btnCoreClearReserved.Font = Theme.UI(8.5f, false);
            btnCoreClearReserved.Visible = false;
            btnCoreClearReserved.Click += delegate { ClearSuspectReservedCores(); };
            coreMapPanel.Controls.Add(btnCoreClearReserved);
            SyncCorePage();
        }

        private void ClearSuspectReservedCores()
        {
            ulong suspect = LegacyReservedCores.Suspect();
            if (suspect == 0) return;
            bool confirmed;
#if PAVISE_SELFTEST
            if (ClearReservedConfirmationForTest != null) confirmed = ClearReservedConfirmationForTest();
            else
#endif
                confirmed = PaviseDialog.Confirm(this, Lang.T("schedule.auto.reserved.clear"),
                    Lang.F("schedule.auto.reserved.confirm", CpuTopology.DescribeMask(suspect)), DlgKind.Warn);
            if (!confirmed) return;
            if (!LegacyReservedCores.ClearSuspect())
                PaviseDialog.Warn(this, Lang.T("schedule.auto.reserved.clear"), Lang.T("schedule.auto.reserved.failed"));
            SyncCorePage();
        }

        private void LayoutCorePage()
        {
            coreEditor.Top = coreBackgroundCard.Bottom + Theme.S(12);
            LayoutCoreEditor();
            AutoCoreStatus current = AutoCores.Status;
            bool issue = CoreIsolationClient.State == "schedule.isolation.pending" || current.Pending
                || current.ProfileId == coreDraftProfile && current.Blocked != null;
            coreStatusPanel.Visible = coreDraftProfile == null || issue;
            coreLivePanel.Visible = false;
            coreMapPanel.Visible = lblCoreReserved.Text.Length > 0;
            coreStatusPanel.Top = coreDraftProfile != null ? coreEditor.Bottom + Theme.S(12) : coreEditor.Top;
            ulong all = CpuTopology.AllMask;
            int cores = CpuTopology.PhysicalCoreCount;
            if (all != coreMapAll || cores != coreMapCores)
            {
                coreMapAll = all;
                coreMapCores = cores;
                coreMatrix.Height = coreMatrix.LayoutFor(coreMatrix.Width);
            }
            lblCoreReason.Height = WrappedHeight(lblCoreReason);
            lblCoreRule.Top = lblCoreReason.Bottom + Theme.S(6);
            lblCoreRule.Height = WrappedHeight(lblCoreRule);
            lblCoreTelemetry.Top = lblCoreRule.Bottom + Theme.S(6);
            lblCoreTelemetry.Height = WrappedHeight(lblCoreTelemetry);
            coreStatusPanel.Height = lblCoreTelemetry.Bottom + Theme.S(12);
            coreLivePanel.Top = coreStatusPanel.Bottom + Theme.S(12);
            coreMapPanel.Top = (coreStatusPanel.Visible ? coreStatusPanel.Bottom : coreDraftProfile != null ? coreEditor.Bottom : coreEditor.Top) + Theme.S(12);
            lblCoreReserved.Top = coreMatrix.Bottom + Theme.S(8);
            lblCoreReserved.Height = lblCoreReserved.Text.Length == 0 ? 0 : WrappedHeight(lblCoreReserved);
            btnCoreClearReserved.Top = lblCoreReserved.Bottom + Theme.S(6);
            coreMapPanel.Height = (btnCoreClearReserved.Visible ? btnCoreClearReserved.Bottom : lblCoreReserved.Bottom) + Theme.S(12);
        }

        private static int WrappedHeight(Label label)
        {
            return TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, label.Width), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + Theme.S(2);
        }

        private void SyncCorePage()
        {
            if (lblCoreState == null || lblCoreState.IsDisposed) return;
            automaticCoreLayoutSwitch.SetSilently(gameMode.AutomaticCoreLayoutEnabled);
            AutoCoreStatus st = AutoCores.Status;
            GameProfile profile = CorePageProfile();
            SyncCoreEditor(profile);
            bool pending = st.Pending || CoreIsolationClient.State == "schedule.isolation.pending";
            bool own = profile != null && st.ProfileId == profile.Id;
            lblCoreState.Text = Lang.T(pending ? "schedule.auto.state.pending" : own && st.Blocked != null
                ? "schedule.auto.state.blocked" : "schedule.manual.mode");
            lblCoreReason.Text = Lang.T(pending ? "schedule.isolation.pending" : own && st.Blocked != null
                ? st.Blocked : profile == null ? "schedule.manual.pickgame" : "schedule.manual.required");
            lblCoreRule.Text = Lang.T("schedule.manual.rule");
            lblCoreTelemetry.Text = "";
            ulong allocated = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
            ulong legacy = allocated != 0 ? LegacyReservedCores.PendingReboot(allocated) : 0;
            ulong suspect = legacy == 0 ? LegacyReservedCores.Suspect() : 0;
            bool showClear = suspect != 0 && (suspect & ~allocated) == 0;
            lblCoreReserved.Text = legacy != 0
                ? Lang.F("schedule.auto.reserved.legacy", CpuTopology.DescribeMask(legacy))
                : showClear ? Lang.F("schedule.auto.reserved.suspect", CpuTopology.DescribeMask(suspect))
                : allocated != 0 ? Lang.F("schedule.auto.reserved.other", CpuTopology.DescribeMask(allocated)) : "";
            btnCoreClearReserved.Visible = showClear;
            ManualCorePlan saved = profile != null ? PolicyResolver.For(profile).ManualCores : null;
            coreMatrix.PrimaryTag = Lang.T("schedule.manual.selected");
            coreMatrix.DisabledMask = allocated;
            coreMatrix.Selected = saved != null ? saved.GameMask : 0;
            coreMatrix.SchedulingGameMask = saved != null ? saved.GameMask : 0;
            coreMatrix.SchedulingIsolationMask = own && st.Manual && st.ManualIsolation && st.Engaged && st.Plan != null ? st.Plan.Exclusive : 0;
            lblCorePlan.Text = saved != null ? Lang.F("schedule.manual.plan", CpuTopology.DescribeMask(saved.GameMask)) : Lang.T("schedule.manual.required");
            LayoutCorePage();
            coreMatrix.Invalidate();
        }

    }
}
