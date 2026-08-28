// 隔离 UI 回归入口：仅构造控件，不构造 GameMode、不启动应用、不读写用户配置。
#if PAVISE_UI_TEST
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class LibraryFamilyUiChecks
    {
        private static int checks;
        internal static int Run(string outputDirectory)
        {
            checks = 0;
            Exception failure = null;
            var thread = new Thread(delegate()
            {
                try { RunSta(outputDirectory); }
                catch (Exception ex) { failure = ex; }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(30000)) throw new Exception("UI checks exceeded 30 seconds");
            if (failure != null) throw new Exception("UI checks failed", failure);
            return checks;
        }
        private static void Check(bool good, string message)
        {
            if (!good) throw new Exception(message); checks++;
        }
        private static void CreateHiddenTree(Control control)
        {
            typeof(Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(bool) }, null).Invoke(control, new object[] { true });
            foreach (Control child in control.Controls) CreateHiddenTree(child);
        }
        private static GameLibraryItem MakeItem(string id, bool observed, bool suppress)
        {
            var profile = new GameProfile();
            profile.Id = id; profile.Name = "星际远征 · Aurora & Beyond " + id;
            profile.ExecutablePath = @"D:\Games\Aurora\Project With A Very Long Folder Name\Binaries\Win64\Aurora-Client-Win64-Shipping.exe";
            if (suppress) profile.Overrides[PolicyCatalog.KeySuppressFamily] = "1";
            if (id == "01")
            {
                profile.ForceTrigger = true;
                profile.Overrides[PolicyCatalog.KeyBoost] = "1";
                profile.Overrides[PolicyCatalog.KeyPauseUpdate] = "1";
            }
            return new GameLibraryItem(profile, id == "01", observed);
        }
        private static void CheckLongResetDialog()
        {
            var lines = new string[40];
            for (int i = 0; i < lines.Length; i++)
                lines[i] = @"D:\Pavise\" + new string('x', 180) + i + ".dat (IOException)";
            string body = Lang.F("store.savefatal.deletefail", @"D:\Pavise", string.Join("\r\n", lines));
            ConstructorInfo create = typeof(PaviseDialog).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string), typeof(DlgKind), typeof(string), typeof(string), typeof(Control), typeof(int) }, null);
            using (var dialog = (Form)create.Invoke(new object[]
                { Lang.T("store.savefatal.title"), body, DlgKind.Danger, null, null, null, 468 }))
            {
                CreateHiddenTree(dialog);
                Control[] matches = dialog.Controls.Find("ScrollableDialogDetails", true);
                Check(matches.Length == 1, "Long reset diagnostics must scroll");
                var details = matches[0] as TextBox;
                Check(details != null && details.Multiline && details.ReadOnly
                    && details.ScrollBars == ScrollBars.Vertical && details.Text == body,
                    "Reset diagnostics must remain complete and copyable");
                Check(dialog.ClientSize.Height < Screen.FromPoint(Cursor.Position).WorkingArea.Height,
                    "Long reset diagnostics put the confirmation button off-screen");
                foreach (Control control in dialog.Controls)
                    Check(dialog.ClientRectangle.Contains(control.Bounds), "Reset dialog control is clipped");
            }
        }

        private static void CheckAddGameDialog()
        {
            // Do not create a handle or show this dialog: Load starts real install/process scanning.
            using (var dialog = new AddGameDialog(new string[0], false))
            {
                Type type = typeof(AddGameDialog);
                BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                Check(type.GetField("btnDeep", fields) == null && type.GetMethod("DeepScan", fields) == null,
                    "Removed deep scan still has a UI entry");
                Check(!dialog.IsHandleCreated
                    && !(bool)type.GetField("scanning", fields).GetValue(dialog)
                    && !(bool)type.GetField("collectingRunning", fields).GetValue(dialog)
                    && type.GetField("infoTimer", fields).GetValue(dialog) == null
                    && type.GetField("runningTimer", fields).GetValue(dialog) == null,
                    "Isolated add-game UI check unexpectedly started scanning");
                var buttons = new List<Control>();
                Label hint = null;
                foreach (Control control in dialog.Controls)
                {
                    if (control is PillButton && control.Top == Theme.S(506)) buttons.Add(control);
                    if (control is Label && control.Text == Lang.T("scan.hint")) hint = (Label)control;
                }
                Check(buttons.Count == 4, "Add-game footer must contain only Running, Browse, Add, and Cancel");
                foreach (Control button in buttons)
                {
                    Check(dialog.ClientRectangle.Contains(button.Bounds), "Add-game footer button is clipped");
                    int textWidth = TextRenderer.MeasureText(button.Text, button.Font,
                        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine).Width;
                    Check(textWidth <= button.Width - Theme.S(16), "Add-game footer button text is clipped");
                }
                for (int i = 0; i < buttons.Count; i++)
                    for (int j = i + 1; j < buttons.Count; j++)
                        Check(!buttons[i].Bounds.IntersectsWith(buttons[j].Bounds), "Add-game footer buttons overlap");
                buttons.Sort(delegate(Control a, Control b) { return a.Left.CompareTo(b.Left); });
                Check(buttons[0].Text == Lang.T("scan.running.btn") && buttons[1].Text == Lang.T("scan.browse")
                    && buttons[2].Text == Lang.T("btn.add") && buttons[3].Text == Lang.T("btn.cancel"),
                    "Add-game footer action order changed");
                Check(hint != null, "Install-record scan limitation hint is missing");
                int hintHeight = TextRenderer.MeasureText(hint.Text, hint.Font,
                    new Size(hint.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
                Check(hintHeight <= hint.Height, "Install-record scan limitation hint is clipped");
                type.GetMethod("UpdateInfoLabel", fields).Invoke(dialog, null);
                Label info = (Label)type.GetField("lblInfo", fields).GetValue(dialog);
                Check(info.Text == Lang.T("scan.none") && info.Text.IndexOf("深度扫描", StringComparison.Ordinal) < 0
                    && info.Text.IndexOf("Deep Scan", StringComparison.OrdinalIgnoreCase) < 0,
                    "Empty scan results still recommend the removed deep scan");
            }
        }

        private static void RunSta(string output)
        {
            Directory.CreateDirectory(output);
            float originalScale = Dpi.Scale; int originalLang = Lang.Cur; bool originalLight = Theme.LightMode;
            try
            {
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
                foreach (int language in new[] { 0, 1 })
                foreach (bool light in new[] { false, true })
                {
                    Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
                    CheckLongResetDialog();
                    CheckAddGameDialog();
                    var emptyConfig = new GameProfile { Name = "Profile" };
                    Check(PanelForm.CfgClearConfirmation(emptyConfig) == null, "Empty profile should not prompt to clear");
                    var familyConfig = MakeItem("02", false, true).Profile;
                    Check(PanelForm.CfgOverrideSummary(familyConfig) == Lang.T("cfg.count.none.family"),
                        "Family-only config must not advertise a nonexistent override target");
                    Check(PanelForm.CfgClearConfirmation(familyConfig) == Lang.F("cfg.clear.family.only", familyConfig.Name),
                        "Family-only profile must remain clearable with explicit protection wording");
                    var mixedConfig = MakeItem("01", true, true).Profile;
                    Check(PanelForm.CfgOverrideSummary(mixedConfig) == Lang.F("cfg.count", 2), "Config summary counted family switch");
                    Check(PanelForm.CfgClearConfirmation(mixedConfig) == Lang.F("cfg.clear.family.confirm", mixedConfig.Name, 2),
                        "Mixed clear must state ordinary count and closing family suppression");
                    mixedConfig.Overrides.Remove(PolicyCatalog.KeySuppressFamily);
                    Check(PanelForm.CfgClearConfirmation(mixedConfig) == Lang.F("cfg.clear.confirm", mixedConfig.Name, 2),
                        "Ordinary-only clear text changed meaning");
                    foreach (int logicalWidth in new[] { 340, 480, 560, 680, 920 })
                    {
                        int width = Theme.S(logicalWidth), height = GameLibraryRowLayout.HeightForWidth(width);
                        GameLibraryRowLayout layout = GameLibraryRowLayout.ForSize(new Size(width, height));
                        var bounds = new Rectangle(0, 0, width, height);
                        Check(bounds.Contains(layout.Policy), "Policy outside row at " + scale + "/" + logicalWidth);
                        Check(bounds.Contains(layout.Name) && bounds.Contains(layout.Path) && bounds.Contains(layout.Metadata), "Text outside row");
                        Check(!layout.Name.IntersectsWith(layout.Policy) && !layout.Path.IntersectsWith(layout.Policy)
                            && !layout.Metadata.IntersectsWith(layout.Policy), "Policy overlaps text");
                        Check(!layout.Icon.IntersectsWith(layout.Name) && !layout.Name.IntersectsWith(layout.Path)
                            && !layout.Path.IntersectsWith(layout.Metadata), "Row text/icon overlaps");
                    }
                    using (var form = new Form { AutoScaleMode = AutoScaleMode.None, BackColor = Theme.Bg,
                        ClientSize = new Size(Theme.S(728), Theme.S(466)) })
                    using (var list = new GameLibraryList())
                    {
                        list.SetBounds(Theme.S(14), Theme.S(14), Theme.S(700), Theme.S(438));
                        form.Controls.Add(list);
                        var first = MakeItem("01", true, false);
                        var second = MakeItem("02", false, false);
                        var third = MakeItem("03", true, true);
                        list.SetItems(new List<GameLibraryItem> { first, second, third });
                        CreateHiddenTree(form); list.PerformLayout();
                        list.SelectedIndex = 1;
                        Check(list.SelectedItem == second, "Selection model mismatch");
                        Check(list.Controls.Count == 3, "Wrong row count");
                        var row = (GameLibraryRow)list.Controls[1];
                        Check(row.FamilySwitch.AccessibleDescription.Contains("Steam")
                            && row.FamilySwitch.AccessibleDescription.Contains("CS:GO"),
                            "Family switch help must include the launcher/game example");
                        Check(row.FamilySwitch is CheckBox && row.FamilySwitch.TabStop
                            && !row.FamilySwitch.AutoCheck, "Switch must remain a native keyboard-accessible checkbox");
                        Check(row.FamilySwitch.AccessibilityObject.Role == AccessibleRole.CheckButton, "Wrong accessibility role");
                        int requests = 0;
                        list.FamilyToggleRequested += delegate(object sender, GameLibraryEventArgs e)
                        {
                            Check(e.Item == second, "Toggle routed to wrong game"); requests++;
                        };
                        typeof(FamilySuppressionSwitch).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row.FamilySwitch, new object[] { new KeyEventArgs(Keys.Enter) });
                        Check(requests == 1 && !row.FamilySwitch.Checked, "Enter must request, not optimistically save");
                        typeof(FamilySuppressionSwitch).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row.FamilySwitch, new object[] { new KeyEventArgs(Keys.Space) });
                        typeof(CheckBox).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row.FamilySwitch, new object[] { new KeyEventArgs(Keys.Space) });
                        Check(requests == 2 && !row.FamilySwitch.Checked, "Space must request without optimistic state");
                        row.FamilySwitch.AccessibilityObject.DoDefaultAction();
                        Check(requests == 3 && !row.FamilySwitch.Checked, "Accessible default action must request without optimistic state");
                        var refreshed = new List<GameLibraryItem> { MakeItem("01", true, false), MakeItem("02", true, true), MakeItem("03", false, false) };
                        list.SetItems(refreshed);
                        Check(ReferenceEquals(row, list.Controls[1]), "Metadata refresh rebuilt native row");
                        Check(row.FamilySwitch.Checked && list.SelectedIndex == 1, "Saved state/selection not reflected");
                        Check(GameLibraryRow.OrdinaryOverrideCount(row.Item.Profile) == 0, "Family toggle leaked into ordinary override badge");
                        Check(GameLibraryRow.OrdinaryOverrideCount(refreshed[0].Profile) == 2, "Ordinary override count lost");
                        Check((row.FamilySwitch.AccessibilityObject.State & AccessibleStates.Checked) != 0, "Checked accessibility state missing");
                        int deleteRequests = 0, activations = 0;
                        list.KeyDown += delegate(object sender, KeyEventArgs e)
                        {
                            if (e.KeyCode == Keys.Delete) deleteRequests++;
                        };
                        list.ItemActivated += delegate { activations++; };
                        typeof(FamilySuppressionSwitch).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row.FamilySwitch, new object[] { new KeyEventArgs(Keys.Delete) });
                        Check(deleteRequests == 1 && list.SelectedIndex == 1, "Delete no longer reaches the selected library entry");
                        typeof(GameLibraryRow).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row, new object[] { new KeyEventArgs(Keys.Enter) });
                        Check(activations == 1, "Enter no longer opens per-game configuration");
                        typeof(GameLibraryRow).GetMethod("OnDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row, new object[] { EventArgs.Empty });
                        Check(activations == 2 && list.SelectedIndex == 1, "Double-click no longer opens the correct entry");
                        var down = new KeyEventArgs(Keys.Down);
                        typeof(GameLibraryRow).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(row, new object[] { down });
                        Check(down.Handled && list.SelectedIndex == 2, "Arrow navigation broken");
                        if (scale == 1f)
                            using (var bitmap = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                                bitmap.Save(Path.Combine(output, "library-" + (light ? "light" : "dark") + "-" + language + ".png"));
                            }
                        var many = new List<GameLibraryItem>();
                        for (int i = 0; i < 20; i++) many.Add(MakeItem(i.ToString("00"), i % 2 == 0, i % 3 == 0));
                        list.SetItems(many); CreateHiddenTree(list); list.PerformLayout();
                        Check(list.VerticalScroll.Visible, "Long library must scroll");
                        foreach (Control card in list.Controls)
                            Check(card.Right <= list.ClientSize.Width, "Scroll bar clips policy control");
                        list.AutoScrollPosition = new Point(0, list.AutoScrollMinSize.Height);
                        Check(list.Controls[0].Top < 0 && list.Controls[list.Controls.Count - 1].Bottom <= list.ClientSize.Height,
                            "Last row unreachable through vertical scroll");
                        list.AutoScrollPosition = Point.Empty;
                        Check(list.Controls[0].Top == 0, "Scroll return corrupted row location");
                        list.Width = Theme.S(480); list.PerformLayout();
                        Check(list.Controls[0].Height == Theme.S(184), "Narrow library did not stack policy below metadata");
                        if (scale == 1f)
                            using (var bitmap = new Bitmap(list.Width, list.Height))
                            {
                                list.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                                bitmap.Save(Path.Combine(output, "library-narrow-" + (light ? "light" : "dark") + "-" + language + ".png"));
                            }
                    }
                    using (var warning = new FamilySuppressionDialog("Aurora & Beyond", @"D:\Games\Launcher\Entry.exe"))
                    {
                        string warningBody = Lang.T("lib.family.warning.body");
                        Check(warningBody.Contains("League of Legends.exe")
                            && warningBody.Contains(language == 0 ? "每次开启" : "every time")
                            && warning.Text.Contains(language == 0 ? "不建议开启" : "not recommended"),
                            "Every-enable warning must discourage suppression and explain the LoL exception");
                        Check(!warningBody.Contains(language == 0 ? "暂未确认" : "has not confirmed"),
                            "Warning must not claim an observed renderer is unconfirmed");
                        Check(warningBody.Contains("Steam.exe") && warningBody.Contains("CS:GO"),
                            "Family warning must explain choosing the game EXE, not the launcher");
                        Label warningMessage = null;
                        foreach (Control control in warning.Controls)
                            if (control is Label && control.Text == warningBody) warningMessage = (Label)control;
                        Check(warningMessage != null && warningMessage.Bottom < warning.EnableAnyway.Top,
                            "Family warning example overlaps its action buttons");
                        int requiredBodyHeight = TextRenderer.MeasureText(warningBody, warningMessage.Font,
                            new Size(warningMessage.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
                        Check(requiredBodyHeight <= warningMessage.Height,
                            "Family warning example is clipped");
                        Check(warning.AcceptButton == warning.CancelButton && warning.AcceptButton.DialogResult == DialogResult.Cancel,
                            "Default dialog action must keep suppression off");
                        Check(warning.EnableAnyway.DialogResult == DialogResult.OK, "Explicit opt-in missing");
                        Check(warning.ExecutableBox.ReadOnly && warning.ExecutableBox.Multiline
                            && warning.ExecutableBox.Text.EndsWith("Entry.exe"), "Full EXE unavailable to copy");
                        Check(warning.ClientRectangle.Contains(warning.EnableAnyway.Bounds), "Dialog buttons clipped");
                        if (scale == 1f)
                            using (var bitmap = new Bitmap(warning.Width, warning.Height))
                            {
                                CreateHiddenTree(warning);
                                warning.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                                bitmap.Save(Path.Combine(output, "warning-" + (light ? "light" : "dark") + "-" + language + ".png"));
                            }
                    }
                }
            }
            finally { Dpi.Scale = originalScale; Lang.Cur = originalLang; Theme.SetLight(originalLight); Theme.DropFontCache(); }
        }
    }
}
#endif
