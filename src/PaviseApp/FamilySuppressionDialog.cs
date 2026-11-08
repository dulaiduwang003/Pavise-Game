using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class FamilySuppressionDialog : Form
	{
		private readonly LibraryDialogButton keepClosed;

		internal readonly LibraryDialogButton EnableAnyway;

		internal readonly TextBox ExecutableBox;

		internal FamilySuppressionDialog(string gameName, string executablePath)
		{
			base.AutoScaleMode = AutoScaleMode.None;
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.MinimizeBox = false;
			base.MaximizeBox = false;
			base.ShowInTaskbar = false;
			BackColor = Theme.Bg;
			ForeColor = Theme.Fg;
			Font = Theme.UI(9f, false);
			DoubleBuffered = true;
			Text = Lang.T("lib.family.warning.title");
			base.AccessibleName = Text;
			int scaledDimension = Theme.S(544);
			int left = Theme.S(26);
			int width = scaledDimension - left * 2;
			Label label = MakeLabel("FAMILY // CAUTION", Theme.Mono(7.3f), Theme.Accent);
			label.SetBounds(left, Theme.S(21), width, Theme.S(19));
			Label label2 = MakeLabel(Text, Theme.UI(15f, true), Theme.Fg);
			label2.SetBounds(left, Theme.S(48), width, Theme.S(34));
			Label label3 = MakeLabel(gameName ?? "", Theme.UI(9.2f, true), Theme.Dim);
			label3.AutoEllipsis = true;
			label3.SetBounds(left, Theme.S(92), width, Theme.S(24));
			Label label4 = MakeLabel(Lang.T("lib.family.warning.target"), Theme.UI(8f, false), Theme.Dim);
			label4.SetBounds(left, Theme.S(126), width, Theme.S(22));
			ExecutableBox = new TextBox();
			ExecutableBox.ReadOnly = true;
			ExecutableBox.Multiline = true;
			ExecutableBox.WordWrap = true;
			ExecutableBox.ScrollBars = ScrollBars.Vertical;
			ExecutableBox.BorderStyle = BorderStyle.FixedSingle;
			ExecutableBox.BackColor = Theme.Inset;
			ExecutableBox.ForeColor = Theme.Fg;
			ExecutableBox.Font = Theme.UI(8.5f, false);
			ExecutableBox.TabIndex = 2;
			ExecutableBox.AccessibleName = Lang.T("lib.family.warning.target");
			Native.Dark(ExecutableBox);
			ExecutableBox.Text = (string.IsNullOrEmpty(executablePath) ? Lang.T("lib.family.warning.noexe") : executablePath);
			ExecutableBox.SetBounds(left, Theme.S(150), width, Theme.S(70));
			string libFamilyWarningBodyText = Lang.T("lib.family.warning.body");
			int height = TextRenderer.MeasureText(libFamilyWarningBodyText, Theme.UI(9.2f, false), new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(12);
			Label label5 = MakeLabel(libFamilyWarningBodyText, Theme.UI(9.2f, false), Theme.Dim);
			int top = Theme.S(240);
			label5.SetBounds(left, top, width, height);
			int top2 = label5.Bottom + Theme.S(25);
			int height2 = Theme.S(40);
			keepClosed = new LibraryDialogButton(Lang.T("lib.family.warning.cancel"), true);
			keepClosed.DialogResult = DialogResult.Cancel;
			keepClosed.TabIndex = 0;
			EnableAnyway = new LibraryDialogButton(Lang.T("lib.family.warning.enable"), false);
			EnableAnyway.DialogResult = DialogResult.OK;
			EnableAnyway.TabIndex = 1;
			int width2 = Theme.S(152);
			int width3 = Theme.S(196);
			int scaledDimension2 = Theme.S(12);
			keepClosed.SetBounds(scaledDimension - left - width2 - scaledDimension2 - width3, top2, width3, height2);
			EnableAnyway.SetBounds(scaledDimension - left - width2, top2, width2, height2);
			base.ClientSize = new Size(scaledDimension, top2 + height2 + Theme.S(24));
			base.Controls.AddRange(new Control[8] { label, label2, label3, label4, ExecutableBox, label5, keepClosed, EnableAnyway });
			base.AcceptButton = keepClosed;
			base.CancelButton = keepClosed;
			base.ActiveControl = keepClosed;
		}

		private static Label MakeLabel(string text, Font font, Color color)
		{
			Label label = new Label();
			label.Text = text;
			label.Font = font;
			label.ForeColor = color;
			label.BackColor = Color.Transparent;
			label.UseCompatibleTextRendering = false;
			label.UseMnemonic = false;
			return label;
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			keepClosed.Focus();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle r = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(12)))
			{
				using (Pen pen = new Pen(Theme.StrokeHi))
				{
					graphics.DrawPath(pen, path);
				}
			}
			using (Pen pen2 = new Pen(Theme.Accent, Math.Max(1, Theme.S(2))))
			{
				graphics.DrawLine(pen2, Theme.S(1), Theme.S(1), Theme.S(116), Theme.S(1));
				graphics.DrawLine(pen2, base.Width - Theme.S(13), Theme.S(1), base.Width - Theme.S(1), Theme.S(13));
			}
			int top = keepClosed.Top - Theme.S(13);
			using (Pen pen3 = new Pen(Theme.Stroke))
			{
				graphics.DrawLine(pen3, Theme.S(26), top, base.Width - Theme.S(26), top);
			}
		}
	}
}
