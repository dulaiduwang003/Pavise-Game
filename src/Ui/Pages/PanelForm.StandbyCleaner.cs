// @author bdth 2074055628@qq.com
// 待机清理的显式授权与全局参数编辑；UI 不直接查询或清理内存。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swPolicyStandbyCleaner;
        private SettingCard cardPolicyStandbyCleaner;
#if PAVISE_SELFTEST
        internal Func<bool> StandbyCleanerConfirmationForTest;
        internal Action StandbyCleanerInvalidOptionsForTest;
        internal Func<StandbyCleanerOptions, StandbyCleanerOptions> StandbyCleanerOptionsEditorForTest;
#endif

        private void BuildStandbyCleanerPolicyCard(Control parent, ref int y)
        {
            var actions = new StandbyCleanerActionHost();
            actions.Size = new Size(Theme.S(188), Theme.S(30));
            var options = new PillButton(Lang.T("standbycleaner.options"), BtnKind.Normal);
            options.SetBounds(0, 0, Theme.S(128), Theme.S(30));
            options.AccessibleName = Lang.T("standbycleaner.options.title");
            options.Click += delegate { EditStandbyCleanerOptions(); };
            swPolicyStandbyCleaner = MakeSwitch(gameMode.StandbyCleanerEnabled, null);
            swPolicyStandbyCleaner.Location = new Point(Theme.S(142), Theme.S(3));
            swPolicyStandbyCleaner.AccessibleName = Lang.T("gm.standbycleaner");
            swPolicyStandbyCleaner.CheckedChanged += delegate
            {
                OnStandbyCleanerToggle(swPolicyStandbyCleaner.Checked);
            };
            actions.Controls.Add(options);
            actions.Controls.Add(swPolicyStandbyCleaner);
            int cardH;
            cardPolicyStandbyCleaner = MakeAutoCard(parent, 6, y, ScrollContentW,
                StandbyCleanerCardHeight(ScrollContentW, actions, 78, false),
                Lang.T("gm.standbycleaner"), Lang.T("gm.standbycleaner.sub"), actions, out cardH);
            cardPolicyStandbyCleaner.TrackChildHover(options);
            cardPolicyStandbyCleaner.TrackChildHover(swPolicyStandbyCleaner);
            y += cardH + 8;
            policySync.Add(delegate { swPolicyStandbyCleaner.SetSilently(gameMode.StandbyCleanerEnabled); });
        }

        private int StandbyCleanerCardHeight(int width, Control host, int minimum, bool perGame)
        {
            int height = AutoCardHeight(Lang.T(perGame ? "gm.standbycleaner.cfgsub" : "gm.standbycleaner.sub"),
                width, host, minimum);
            height = Math.Max(height, AutoCardHeight(Lang.T("gm.standbycleaner.needadmin"), width, host, minimum));
            return Math.Max(height, AutoCardHeight(Lang.T("standbycleaner.config.invalid"), width, host, minimum));
        }

        private void RefreshStandbyCleanerPresentation()
        {
            // No preset forces this experimental option on. Lack of elevation
            // blocks a new opt-in but must not prevent turning an old one off.
            if (swPolicyStandbyCleaner != null)
            {
                swPolicyStandbyCleaner.SetSilently(gameMode.StandbyCleanerEnabled);
                swPolicyStandbyCleaner.Enabled = elevated || gameMode.StandbyCleanerEnabled;
            }
            if (cardPolicyStandbyCleaner != null)
                cardPolicyStandbyCleaner.Desc = Lang.T(!gameMode.StandbyCleaningOptionsValid
                    ? "standbycleaner.config.invalid" : elevated
                        ? "gm.standbycleaner.sub" : "gm.standbycleaner.needadmin");
        }

        private bool ConfirmStandbyCleanerEnable()
        {
            if (!elevated) return false;
            if (!gameMode.StandbyCleaningOptionsValid)
            {
#if PAVISE_SELFTEST
                if (StandbyCleanerInvalidOptionsForTest != null) StandbyCleanerInvalidOptionsForTest();
                else
#endif
                    PaviseDialog.Warn(this, Lang.T("gm.standbycleaner"), Lang.T("standbycleaner.config.invalid"));
                return false;
            }
#if PAVISE_SELFTEST
            if (StandbyCleanerConfirmationForTest != null) return StandbyCleanerConfirmationForTest();
#endif
            return PaviseDialog.Confirm(this, Lang.T("gm.standbycleaner"),
                Lang.T("standbycleaner.warn"), DlgKind.Warn);
        }

        private void OnStandbyCleanerToggle(bool on)
        {
            if (on && !ConfirmStandbyCleanerEnable())
            {
                if (swPolicyStandbyCleaner != null)
                    swPolicyStandbyCleaner.SetSilently(gameMode.StandbyCleanerEnabled);
                return;
            }
            gameMode.StandbyCleanerEnabled = on;
            RefreshStandbyCleanerPresentation();
        }

        private bool EditStandbyCleanerOptions()
        {
            StandbyCleanerOptions current = gameMode.StandbyCleaningOptions;
#if PAVISE_SELFTEST
            if (StandbyCleanerOptionsEditorForTest != null)
                return ApplyStandbyCleanerOptions(StandbyCleanerOptionsEditorForTest(current));
#endif
            using (var dialog = new StandbyCleanerOptionsDialog(current, ApplyStandbyCleanerOptions,
                !gameMode.StandbyCleaningOptionsValid))
                return ShowDim(dialog) == DialogResult.OK;
        }

        private bool ApplyStandbyCleanerOptions(StandbyCleanerOptions value)
        {
            // A canceled editor returns null. Neither editing nor saving options
            // is an opt-in and this path must never invoke a memory operation.
            if (value == null || !value.IsValid || !gameMode.TrySetStandbyCleanerOptions(value)) return false;
            RefreshStandbyCleanerPresentation();
            return true;
        }
    }

    // RoundPanel ownership lets both child controls use the same card backdrop.
    // Do not draw a second frame around this compact button/toggle group.
    internal sealed class StandbyCleanerActionHost : RoundPanel
    {
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var card = Parent as RoundPanel;
            Fill = card == null ? Theme.Card : card.Fill;
            if (Backdrop.AppliesTo(this)) Backdrop.PaintOnCard(e.Graphics, this, e.ClipRectangle);
            else using (var brush = new SolidBrush(Fill)) e.Graphics.FillRectangle(brush, e.ClipRectangle);
        }
        protected override void OnPaint(PaintEventArgs e) { }
    }

    internal sealed class StandbyCleanerOptionsDialog : Form
    {
        private readonly TextBox listBox, freeBox, pollingBox;
        private readonly Label errorLabel;
        private readonly LibraryDialogButton cancelButton;
        private readonly Func<StandbyCleanerOptions, bool> save;
        private readonly int dividerY;

        internal StandbyCleanerOptionsDialog(StandbyCleanerOptions initial,
            Func<StandbyCleanerOptions, bool> saveOptions, bool repairing = false)
        {
            if (saveOptions == null) throw new ArgumentNullException("saveOptions");
            save = saveOptions;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9f, false);
            DoubleBuffered = true;
            Text = Lang.T("standbycleaner.options.title"); AccessibleName = Text;

            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int width = Math.Min(Theme.S(620), Math.Max(Theme.S(360), work.Width - Theme.S(40)));
            int pad = Theme.S(26), inner = width - pad * 2;
            var kicker = MakeLabel("MEMORY // OPTIONS", Theme.Mono(7.3f), Theme.Accent);
            kicker.SetBounds(pad, Theme.S(21), inner, Theme.S(19));
            var title = MakeLabel(Text, Theme.UI(15f, true), Theme.Fg);
            int titleHeight = Measure(Text, title.Font, inner);
            title.SetBounds(pad, Theme.S(48), inner, Math.Max(Theme.S(34), titleHeight));
            var body = new DBPanel { BackColor = Theme.Bg, AutoScroll = true, TabStop = false };
            int contentW = inner - SystemInformation.VerticalScrollBarWidth - Theme.S(4);
            int y = 0;
            if (repairing) AddBodyText(body, Lang.T("standbycleaner.options.repair"), contentW, ref y);
            AddBodyText(body, Lang.T("standbycleaner.options.hint"), contentW, ref y);
            listBox = AddField(body, "standbycleaner.options.list", "standbycleaner.options.list.sub", contentW, 0, ref y);
            freeBox = AddField(body, "standbycleaner.options.free", "standbycleaner.options.free.sub", contentW, 1, ref y);
            pollingBox = AddField(body, "standbycleaner.options.poll", "standbycleaner.options.poll.sub", contentW, 2, ref y);
            AddBodyText(body, Lang.T("standbycleaner.options.units"), contentW, ref y);
            body.AutoScrollMinSize = new Size(0, y);
            Native.Dark(body);

            errorLabel = MakeLabel("", Theme.UI(9f, false), Color.FromArgb(255, 176, 32));
            errorLabel.AccessibleName = Lang.T("standbycleaner.options.invalid");
            int errorH = Math.Max(Measure(Lang.T("standbycleaner.options.invalid"), errorLabel.Font, inner),
                Measure(Lang.T("standbycleaner.options.savefailed"), errorLabel.Font, inner)) + Theme.S(4);
            int bodyTop = title.Bottom + Theme.S(16);
            int extraFooter = inner < Theme.S(384) ? Theme.S(48) : 0;
            int fixedH = bodyTop + errorH + Theme.S(92) + extraFooter;
            int bodyH = Math.Min(y, Math.Max(Theme.S(40), work.Height - Theme.S(40) - fixedH));
            body.SetBounds(pad, bodyTop, inner, bodyH);
            errorLabel.SetBounds(pad, body.Bottom + Theme.S(8), inner, errorH);
            int buttonY = errorLabel.Bottom + Theme.S(24), buttonH = Theme.S(36);
            dividerY = buttonY - Theme.S(13);
            var defaults = new LibraryDialogButton(Lang.T("standbycleaner.options.defaults"), false);
            defaults.SetBounds(pad, buttonY, Theme.S(136), buttonH);
            defaults.TabIndex = 3;
            defaults.Click += delegate { SetInputs(StandbyCleanerOptions.Default); errorLabel.Text = ""; };
            buttonY += extraFooter;
            cancelButton = new LibraryDialogButton(Lang.T("dlg.cancel"), false);
            cancelButton.SetBounds(width - pad - Theme.S(232), buttonY, Theme.S(108), buttonH);
            cancelButton.TabIndex = 4; cancelButton.DialogResult = DialogResult.Cancel;
            var accept = new LibraryDialogButton(Lang.T("standbycleaner.options.save"), true);
            accept.SetBounds(width - pad - Theme.S(108), buttonY, Theme.S(108), buttonH);
            accept.TabIndex = 5; accept.Click += delegate { SaveInputs(); };
            ClientSize = new Size(width, buttonY + buttonH + Theme.S(24));
            Controls.AddRange(new Control[] { kicker, title, body, errorLabel, defaults, cancelButton, accept });
            AcceptButton = accept; CancelButton = cancelButton;
            SetInputs(initial != null && initial.IsValid ? initial : StandbyCleanerOptions.Default);
            ActiveControl = listBox;
        }

        internal static bool TryParseInput(string list, string free, string polling, out StandbyCleanerOptions options)
        {
            options = null;
            int listValue, freeValue, pollValue;
            if (!TryInteger(list, 0, StandbyCleanerOptions.MaximumMegabytes, out listValue)
                || !TryInteger(free, 0, StandbyCleanerOptions.MaximumMegabytes, out freeValue)
                || !TryInteger(polling, StandbyCleanerOptions.MinimumPollingMilliseconds,
                    StandbyCleanerOptions.MaximumPollingMilliseconds, out pollValue)) return false;
            options = new StandbyCleanerOptions(listValue, freeValue, pollValue);
            return true;
        }

        private static bool TryInteger(string text, int minimum, int maximum, out int value)
        {
            return int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value >= minimum && value <= maximum;
        }

        private void SetInputs(StandbyCleanerOptions value)
        {
            listBox.Text = value.ListMegabytes.ToString(CultureInfo.InvariantCulture);
            freeBox.Text = value.FreeMegabytes.ToString(CultureInfo.InvariantCulture);
            pollingBox.Text = value.PollingMilliseconds.ToString(CultureInfo.InvariantCulture);
        }

        private void SaveInputs()
        {
            StandbyCleanerOptions value;
            if (!TryParseInput(listBox.Text, freeBox.Text, pollingBox.Text, out value))
            {
                errorLabel.Text = Lang.T("standbycleaner.options.invalid");
                return;
            }
            bool saved;
            try { saved = save(value); }
            catch { saved = false; }
            if (!saved)
            {
                errorLabel.Text = Lang.T("standbycleaner.options.savefailed");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private static int Measure(string text, Font font, int width)
        {
            return TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height;
        }

        private static Label MakeLabel(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, BackColor = Color.Transparent,
                UseCompatibleTextRendering = false, UseMnemonic = false };
        }

        private static void AddBodyText(Control parent, string text, int width, ref int y)
        {
            var label = MakeLabel(text, Theme.UI(9f, false), Theme.Dim);
            label.SetBounds(0, y, width, Measure(text, label.Font, width) + Theme.S(4));
            parent.Controls.Add(label);
            y = label.Bottom + Theme.S(14);
        }

        private static TextBox AddField(Control parent, string titleKey, string detailKey,
            int width, int tabIndex, ref int y)
        {
            int boxW = Theme.S(136), labelW = width - boxW - Theme.S(16);
            var title = MakeLabel(Lang.T(titleKey), Theme.UI(9f, true), Theme.Fg);
            int titleH = Math.Max(Theme.S(24), Measure(title.Text, title.Font, labelW));
            title.SetBounds(0, y, labelW, titleH);
            var box = Theme.MakeTextBox(width - boxW, y, boxW);
            box.TextAlign = HorizontalAlignment.Right; box.MaxLength = 10;
            box.TabIndex = tabIndex; box.AccessibleName = Lang.T(titleKey);
            var detail = MakeLabel(Lang.T(detailKey), Theme.UI(9f, false), Theme.Dim);
            detail.SetBounds(0, y + Math.Max(titleH, box.Height) + Theme.S(3), width,
                Measure(detail.Text, detail.Font, width) + Theme.S(4));
            parent.Controls.AddRange(new Control[] { title, box, detail });
            y = detail.Bottom + Theme.S(14);
            return box;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.TechPath(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(12)))
            using (var edge = new Pen(Theme.StrokeHi)) g.DrawPath(edge, path);
            using (var rail = new Pen(Theme.Accent, Math.Max(1, Theme.S(2))))
                g.DrawLine(rail, Theme.S(1), Theme.S(1), Theme.S(116), Theme.S(1));
            using (var line = new Pen(Theme.Stroke))
                g.DrawLine(line, Theme.S(26), dividerY, Width - Theme.S(26), dividerY);
        }
    }
}
