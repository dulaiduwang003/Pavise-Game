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
			int num = Theme.S(544);
			int num2 = Theme.S(26);
			int num3 = num - num2 * 2;
			Label label = MakeLabel("FAMILY // CAUTION", Theme.Mono(7.3f), Theme.Accent);
			label.SetBounds(num2, Theme.S(21), num3, Theme.S(19));
			Label label2 = MakeLabel(Text, Theme.UI(15f, true), Theme.Fg);
			label2.SetBounds(num2, Theme.S(48), num3, Theme.S(34));
			Label label3 = MakeLabel(gameName ?? "", Theme.UI(9.2f, true), Theme.Dim);
			label3.AutoEllipsis = true;
			label3.SetBounds(num2, Theme.S(92), num3, Theme.S(24));
			Label label4 = MakeLabel(Lang.T("lib.family.warning.target"), Theme.UI(8f, false), Theme.Dim);
			label4.SetBounds(num2, Theme.S(126), num3, Theme.S(22));
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
			ExecutableBox.SetBounds(num2, Theme.S(150), num3, Theme.S(70));
			string text = Lang.T("lib.family.warning.body");
			int num4 = TextRenderer.MeasureText(text, Theme.UI(9.2f, false), new Size(num3, int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(12);
			Label label5 = MakeLabel(text, Theme.UI(9.2f, false), Theme.Dim);
			int num5 = Theme.S(240);
			label5.SetBounds(num2, num5, num3, num4);
			int num6 = label5.Bottom + Theme.S(25);
			int num7 = Theme.S(40);
			keepClosed = new LibraryDialogButton(Lang.T("lib.family.warning.cancel"), true);
			keepClosed.DialogResult = DialogResult.Cancel;
			keepClosed.TabIndex = 0;
			EnableAnyway = new LibraryDialogButton(Lang.T("lib.family.warning.enable"), false);
			EnableAnyway.DialogResult = DialogResult.OK;
			EnableAnyway.TabIndex = 1;
			int num8 = Theme.S(152);
			int num9 = Theme.S(196);
			int num10 = Theme.S(12);
			keepClosed.SetBounds(num - num2 - num8 - num10 - num9, num6, num9, num7);
			EnableAnyway.SetBounds(num - num2 - num8, num6, num8, num7);
			base.ClientSize = new Size(num, num6 + num7 + Theme.S(24));
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
			int num = keepClosed.Top - Theme.S(13);
			using (Pen pen3 = new Pen(Theme.Stroke))
			{
				graphics.DrawLine(pen3, Theme.S(26), num, base.Width - Theme.S(26), num);
			}
		}
	}
}
