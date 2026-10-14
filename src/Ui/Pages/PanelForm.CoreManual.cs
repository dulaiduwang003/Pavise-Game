using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private RoundPanel coreEditor;
        private CoreMatrix coreEditMatrix;
        private Toggle coreIsolationToggle, coreEnableToggle;
        private Label coreEditorTitle, coreEditorHelp, coreEditorIsolation, coreEditorResult, coreEditorHardAffinity, coreEditorHardHelp;
        private string coreDraftProfile, coreDraftExpected, coreDraftTopology;
        private string coreSaveError;
        private CoreEditorDraftSnapshot coreRebuildDraft;

        private sealed class CoreEditorDraftSnapshot
        {
            internal string Profile, Expected, Topology, Error;
            internal ulong Mask;
            internal bool Isolation;
        }

        private void BuildCoreEditor(int x, int y, int w)
        {
            if (coreDraftProfile != null && coreEditMatrix != null
                && coreIsolationToggle != null)
                coreRebuildDraft = new CoreEditorDraftSnapshot {
                    Profile = coreDraftProfile, Expected = coreDraftExpected,
                    Topology = coreDraftTopology, Error = coreSaveError,
                    Mask = coreEditMatrix.Selected, Isolation = coreIsolationToggle.Checked
                };
            coreDraftProfile = null;
            coreEditor = MakeConsolePanel(coreScrollPanel, x, y, w, 180, false);
            coreEditor.Name = "coreManualEditor";
            coreEditorTitle = CardLabel(coreEditor, "", 18, 12, w - 120, 26, 11f, true, Theme.Fg);
            coreEnableToggle = new Toggle { Name = "coreSchedulingEnabled", AccessibleName = Lang.T("nav.corescheduling") };
            coreEnableToggle.SetBounds(Theme.S(w - 66), Theme.S(10), Theme.S(46), Theme.S(30));
            coreEnableToggle.CheckedChanged += delegate
            {
                if (coreDraftProfile == null) return;
                coreSaveError = gameMode.SetProfileOverride(coreDraftProfile, PolicyCatalog.KeyAutoCores,
                    coreEnableToggle.Checked ? "1" : "0") ? null : "schedule.manual.savefailed";
                SyncCoreEditor(CorePageProfile());
            };
            coreEditor.Controls.Add(coreEnableToggle);
            coreEditorHelp = CardLabel(coreEditor, "", 18, 46, w - 36, 40, 9f, false, Theme.Dim);
            coreEditorHelp.AutoEllipsis = false;
            BuildCoreShortcuts(w - 36);
            coreEditMatrix = new CoreMatrix { Name = "coreManualMatrix", Editable = true, MarkExclusive = false,
                BackColor = Theme.Card, PrimaryTag = Lang.T("schedule.manual.selected"), AccessibleName = Lang.T("schedule.manual.select") };
            coreEditMatrix.SetBounds(Theme.S(18), 0, Theme.S(w - 36), Theme.S(40));
            coreEditMatrix.Height = coreEditMatrix.LayoutFor(coreEditMatrix.Width);
            coreEditMatrix.SelectionChanged += delegate { coreDraftTopology = ManualCorePlan.CurrentStamp(); SaveCoreDraft(); };
            coreEditor.Controls.Add(coreEditMatrix);
            coreIsolationToggle = new Toggle { Name = "coreManualIsolation", AccessibleName = Lang.T("schedule.manual.isolation") };
            coreIsolationToggle.SetBounds(Theme.S(18), 0, Theme.S(46), Theme.S(30));
            coreIsolationToggle.CheckedChanged += delegate
            {
                SaveCoreDraft();
            };
            coreEditor.Controls.Add(coreIsolationToggle);
            coreEditorIsolation = CardLabel(coreEditor, Lang.T("schedule.manual.isolation"), 76, 0, w - 94, 30, 9f, true, Theme.Fg);
            coreEditorIsolation.AutoEllipsis = false;
            coreEditorHardAffinity = CardLabel(coreEditor, Lang.T("schedule.manual.hard"), 76, 0, w - 94, 30, 9f, true, Theme.Fg);
            coreEditorHardAffinity.AutoEllipsis = false;
            coreEditorHardHelp = CardLabel(coreEditor, "", 76, 0, w - 94, 40, 8.5f, false, Theme.Dim);
            coreEditorHardHelp.AutoEllipsis = false;
            coreEditorResult = CardLabel(coreEditor, "", 18, 0, w - 36, 36, 8.5f, false, Theme.Dim);
            coreEditorResult.Name = "coreManualResult"; coreEditorResult.AutoEllipsis = false;
        }

        private void SyncCoreEditor(GameProfile profile)
        {
            coreEditor.Visible = profile != null;
            if (profile == null) { coreDraftProfile = null; coreRebuildDraft = null; return; }
            if (coreRebuildDraft != null && curPage == pageCoreScheduling)
            {
                CoreEditorDraftSnapshot draft = coreRebuildDraft;
                coreRebuildDraft = null;
                if (string.Equals(draft.Profile, profile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    coreDraftProfile = profile.Id;
                    coreDraftExpected = draft.Expected;
                    coreDraftTopology = draft.Topology;
                    coreSaveError = draft.Error;
                    coreEditMatrix.ShowSelectionImmediately(draft.Mask);
                    coreIsolationToggle.SetSilently(draft.Isolation);
                }
            }
            coreEnableToggle.SetSilently(PolicyResolver.For(profile).AutoCoresAllowed);
            coreEnableToggle.Enabled = PolicyResolver.For(profile).ManualCores != null;
            coreEditorTitle.Text = Lang.F("schedule.manual.game", profile.Name);
            coreEditMatrix.DisabledMask = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
            string raw = ManualCorePlan.Raw(profile);
            if (coreDraftProfile != profile.Id || coreDraftExpected != raw)
            {
                bool externalChange = coreDraftProfile == profile.Id;
                coreDraftProfile = profile.Id;
                coreDraftExpected = raw;
                RestoreCoreSelection();
                coreSaveError = externalChange ? "schedule.manual.changed" : null;
            }
            CoreDraftChanged();
        }

        private void RestoreCoreSelection()
        {
            ManualCorePlan plan = ManualCorePlan.Parse(coreDraftExpected);
            coreDraftTopology = plan != null ? plan.Topology : ManualCorePlan.CurrentStamp();
            coreEditMatrix.ShowSelectionImmediately(plan != null ? plan.GameMask : 0);
            coreIsolationToggle.SetSilently(plan != null && plan.Isolation);
        }

        private ManualCorePlan CoreDraftPlan()
        {
            ManualCorePlan saved = ManualCorePlan.Parse(coreDraftExpected);
            return new ManualCorePlan(coreEditMatrix.Selected, coreIsolationToggle.Checked,
                coreDraftTopology, saved != null && saved.HardAffinity);
        }

        private void CoreDraftChanged()
        {
            ManualCorePlan plan = CoreDraftPlan();
            string error = plan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask);
            bool unsaved = plan.Encode() != coreDraftExpected;
            coreEditorResult.Text = coreSaveError == "schedule.manual.changed" ? Lang.T(coreSaveError)
                : coreSaveError != null ? Lang.F("schedule.manual.autofailed", Lang.T(coreSaveError))
                : error != null ? (coreDraftExpected == null && coreEditMatrix.Selected == 0
                    ? Lang.T("schedule.manual.required") : Lang.F(unsaved ? "schedule.manual.unsaved" : "schedule.manual.invalid", Lang.T(error)))
                : Lang.T(coreEnableToggle.Checked
                    ? "schedule.manual.autosaved" : "schedule.manual.savedoff");
            coreEditorResult.ForeColor = coreSaveError != null || error != null && (coreEditMatrix.Selected != 0 || coreDraftExpected != null)
                ? Theme.Warning : Theme.Dim;
            coreEditorHelp.Text = Lang.T("schedule.manual.help");
            coreEditorHardHelp.Text = Lang.T("schedule.manual.hard.on");
            coreIsolationToggle.Enabled = coreIsolationToggle.Checked || new ManualCorePlan(coreEditMatrix.Selected, true, coreDraftTopology)
                .ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask) == null;
            SyncCoreShortcuts();
            LayoutCoreEditor();
            if (coreStatusPanel != null) LayoutCorePage();
        }

        private void SaveCoreDraft()
        {
            if (coreDraftProfile == null) return;
            coreSaveError = null;
            ManualCorePlan plan = CoreDraftPlan();
            if (plan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask) != null)
            { CoreDraftChanged(); return; }
            if (plan.Encode() == coreDraftExpected) { CoreDraftChanged(); return; }
            string error = gameMode.SaveManualCorePlan(coreDraftProfile, coreDraftExpected, plan);
            if (error == null) coreDraftExpected = plan.Encode();
            else RestoreCoreSelection();
            SyncCoreEditor(CorePageProfile());
            coreSaveError = error;
            CoreDraftChanged();
        }

        private void LayoutCoreEditor()
        {
            if (coreEditor == null || coreDraftProfile == null) return;
            coreEditorHelp.Height = WrappedHeight(coreEditorHelp);
            coreEditMatrix.Visible = true;
            coreIsolationToggle.Visible = coreEditorIsolation.Visible = true;
            bool showHard = coreIsolationToggle.Checked;
            coreEditorHardAffinity.Visible = coreEditorHardHelp.Visible = showHard;
            LayoutCoreShortcuts();
            coreEditMatrix.Top = coreQuickPanel.Bottom + Theme.S(10);
            coreIsolationToggle.Top = coreEditMatrix.Bottom + Theme.S(10);
            coreEditorIsolation.Top = coreIsolationToggle.Top;
            coreEditorIsolation.Height = Math.Max(Theme.S(30), WrappedHeight(coreEditorIsolation));
            coreEditorHardAffinity.Top = coreEditorIsolation.Bottom + Theme.S(6);
            coreEditorHardAffinity.Height = Math.Max(Theme.S(30), WrappedHeight(coreEditorHardAffinity));
            coreEditorHardHelp.Top = coreEditorHardAffinity.Bottom;
            coreEditorHardHelp.Height = WrappedHeight(coreEditorHardHelp);
            coreEditorResult.Top = (showHard ? coreEditorHardHelp.Bottom : coreEditorIsolation.Bottom) + Theme.S(10);
            coreEditorResult.Height = WrappedHeight(coreEditorResult);
            coreEditor.Height = coreEditorResult.Bottom + Theme.S(14);
        }
    }
}
