using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class ContactDialog : Form
	{
		private sealed class ChannelRow : FxControl
		{
			private readonly string label;

			private readonly string value;

			private string hint;

			private bool hintAccent;

			private readonly Action<ChannelRow> action;

			public ChannelRow(string labelText, string valueText, string hintText, Action<ChannelRow> onClick)
			{
				label = labelText;
				value = valueText;
				hint = hintText;
				action = onClick;
			}

			public void SetHint(string text, bool accent)
			{
				hint = text;
				hintAccent = accent;
				Invalidate();
			}

			protected override void OnClick(EventArgs e)
			{
				base.OnClick(e);
				if (action != null)
				{
					action(this);
				}
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				Graphics graphics = e.Graphics;
				FillBg(graphics);
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				Rectangle r = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
				using (GraphicsPath path = Theme.TechPath(r, Theme.S(8)))
				{
					Color color = Col.Lerp(Theme.Card, Theme.CardHover, hover.Value);
					using (SolidBrush brush = new SolidBrush(color))
					{
						graphics.FillPath(brush, path);
					}
					using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, hover.Value * 0.7f)))
					{
						graphics.DrawPath(pen, path);
					}
				}
				using (Pen pen2 = new Pen(Theme.Accent, Math.Max(2, Theme.S(2))))
				{
					graphics.DrawLine(pen2, 0, Theme.S(12), 0, base.Height - Theme.S(12));
				}
				TextRenderer.DrawText(graphics, label, Theme.UI(7.3f, true), new Rectangle(Theme.S(14), Theme.S(7), base.Width - Theme.S(28), Theme.S(15)), Theme.Faint, TextFormatFlags.EndEllipsis);
				TextRenderer.DrawText(graphics, value, Theme.UI(11.5f, true), new Rectangle(Theme.S(14), Theme.S(22), base.Width - Theme.S(74), Theme.S(26)), Theme.Fg, TextFormatFlags.EndEllipsis);
				Color foreColor = (hintAccent ? Theme.Green : Col.Lerp(Theme.Faint, Theme.Accent, hover.Value));
				TextRenderer.DrawText(graphics, hint, Theme.UI(7.4f, hintAccent), new Rectangle(base.Width - Theme.S(66), Theme.S(24), Theme.S(54), Theme.S(20)), foreColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
			}
		}

		private const int DlgW = 640;

		private const int DlgH = 578;

		private const int RailW = 190;

		private const int RailSlant = 32;

		private const int BodyX = 216;

		private const int BodyW = 394;

		private Bitmap logo;

		private bool clockWasSuspended;

		public ContactDialog()
		{
			Text = Lang.T("contact.title");
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.MaximizeBox = false;
			base.MinimizeBox = false;
			base.ShowInTaskbar = false;
			Icon taskbarIcon = IconArt.MakeIcon(Theme.S(24));
			base.Icon = taskbarIcon;
			base.ClientSize = new Size(Theme.S(640), Theme.S(578));
			BackColor = Theme.Bg;
			ForeColor = Theme.Fg;
			Font = Theme.UI(9.5f, false);
			DoubleBuffered = true;
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			logo = IconArt.Render(Theme.S(58), Theme.CurrentMode, true);
			base.Disposed += delegate
			{
				try
				{
					taskbarIcon.Dispose();
				}
				catch
				{
				}
				try
				{
					if (logo != null)
					{
						logo.Dispose();
					}
				}
				catch
				{
				}
			};
			base.Paint += PaintChrome;
			base.MouseDown += DragMove;
			Label close = new Label();
			close.Text = "✕";
			close.ForeColor = Theme.Faint;
			close.BackColor = Color.Transparent;
			close.Font = Theme.UI(10f, false);
			close.TextAlign = ContentAlignment.MiddleCenter;
			close.Cursor = Cursors.Hand;
			close.SetBounds(Theme.S(598), Theme.S(12), Theme.S(26), Theme.S(26));
			close.MouseEnter += delegate
			{
				close.ForeColor = Theme.Accent;
			};
			close.MouseLeave += delegate
			{
				close.ForeColor = Theme.Faint;
			};
			Label label = close;
			EventHandler value = delegate
			{
				Finish();
			};
			label.Click += value;
			base.Controls.Add(close);
			int ctorValue = 192;
			int ctorValue2 = 216 + ctorValue + 10;
			int ctorValue3 = 102;
			AddRow(216, ctorValue3, 394, Lang.T("contact.douyin"), "44601770838", CopyAction("44601770838"), Lang.T("contact.copy"));
			ctorValue3 += 58;
			AddRow(216, ctorValue3, ctorValue, Lang.T("contact.qq"), "1051472054", CopyAction("1051472054"), Lang.T("contact.copy"));
			AddRow(ctorValue2, ctorValue3, ctorValue, Lang.T("contact.qq2"), "1101249532", CopyAction("1101249532"), Lang.T("contact.copy"));
			ctorValue3 += 58;
			AddRow(216, ctorValue3, ctorValue, Lang.T("contact.qq3"), "383761286", CopyAction("383761286"), Lang.T("contact.copy"));
			AddRow(ctorValue2, ctorValue3, ctorValue, Lang.T("contact.qq4"), "166255062", CopyAction("166255062"), Lang.T("contact.copy"));
			ctorValue3 += 58;
			AddRow(216, ctorValue3, ctorValue, Lang.T("contact.qq5"), "1109874913", CopyAction("1109874913"), Lang.T("contact.copy"));
			ctorValue3 += 58;
			AddRow(216, ctorValue3, 394, Lang.T("site.entry"), "pavise.club", OpenAction("https://pavise.club/"), Lang.T("contact.open"));
			ctorValue3 += 70;
			BuildUpdateArea(216, ctorValue3, 394);
			Label label2 = new Label();
			label2.Text = Lang.T("contact.free.n");
			label2.ForeColor = Theme.Dim;
			label2.BackColor = Theme.Bg;
			label2.Font = Theme.UI(7.5f, false);
			label2.UseCompatibleTextRendering = false;
			label2.SetBounds(Theme.S(218), Theme.S(468), Theme.S(390), Theme.S(44));
			base.Controls.Add(label2);
			PillButton pillButton = new PillButton(Lang.T("contact.close"), BtnKind.Primary);
			pillButton.SetBounds(Theme.S(464), Theme.S(520), Theme.S(146), Theme.S(38));
			pillButton.Click += delegate
			{
				Finish();
			};
			base.Controls.Add(pillButton);
		}

		private Action<ChannelRow> CopyAction(string value)
		{
			return delegate(ChannelRow row)
			{
				try
				{
					Clipboard.SetText(value);
					row.SetHint(Lang.T("contact.copied"), true);
				}
				catch
				{
					row.SetHint(Lang.T("contact.copyfail"), true);
				}
			};
		}

		private Action<ChannelRow> OpenAction(string url)
		{
			return delegate
			{
				try
				{
					using (Process.Start(url))
					{
					}
				}
				catch
				{
				}
			};
		}

		private void AddRow(int x, int y, int w, string label, string value, Action<ChannelRow> action, string hint)
		{
			ChannelRow channelRow = new ChannelRow(label, value, hint, action);
			channelRow.Bg = Theme.Bg;
			channelRow.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(52));
			base.Controls.Add(channelRow);
		}

		private void BuildUpdateArea(int x, int y, int w)
		{
			Label label = new Label();
			label.Text = Lang.T("contact.update") + " " + App.VersionTag;
			label.ForeColor = Theme.Fg;
			label.BackColor = Theme.Bg;
			label.Font = Theme.UI(9.5f, true);
			label.UseCompatibleTextRendering = false;
			label.SetBounds(Theme.S(x + 2), Theme.S(y + 8), Theme.S(180), Theme.S(22));
			base.Controls.Add(label);
			PillButton pillButton = new PillButton(Lang.T("site.download"));
			pillButton.Name = "contactWebsiteDownload";
			pillButton.Bg = Theme.Bg;
			pillButton.SetBounds(Theme.S(x + w - 154), Theme.S(y + 2), Theme.S(154), Theme.S(32));
			pillButton.Click += delegate
			{
				try
				{
					using (Process.Start(App.ChangelogUrl))
					{
					}
				}
				catch
				{
				}
			};
			base.Controls.Add(pillButton);
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			clockWasSuspended = UiClock.Borrow();
			Fx.EnterForm(this);
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			UiClock.Return(clockWasSuspended);
			base.OnFormClosed(e);
		}

		private void Finish()
		{
			base.DialogResult = DialogResult.OK;
			Close();
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			ApplyWindowRegion();
		}

		private void ApplyWindowRegion()
		{
			int width = base.ClientSize.Width;
			int height = base.ClientSize.Height;
			if (width <= 0 || height <= 0)
			{
				return;
			}
			int scaledDimension = Theme.S(26);
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddLine(scaledDimension, 0, width, 0);
				graphicsPath.AddLine(width, 0, width, height - scaledDimension);
				graphicsPath.AddLine(width, height - scaledDimension, width - scaledDimension, height);
				graphicsPath.AddLine(width - scaledDimension, height, 0, height);
				graphicsPath.AddLine(0, height, 0, scaledDimension);
				graphicsPath.CloseFigure();
				base.Region = new Region(graphicsPath);
			}
		}

		private void PaintChrome(object sender, PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
			int width = base.ClientSize.Width;
			int height = base.ClientSize.Height;
			int scaledDimension = Theme.S(26);
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddLine(scaledDimension, 0, width, 0);
				graphicsPath.AddLine(width, 0, width, height - scaledDimension);
				graphicsPath.AddLine(width, height - scaledDimension, width - scaledDimension, height);
				graphicsPath.AddLine(width - scaledDimension, height, 0, height);
				graphicsPath.AddLine(0, height, 0, scaledDimension);
				graphicsPath.CloseFigure();
				using (SolidBrush brush = new SolidBrush(Theme.Bg))
				{
					graphics.FillPath(brush, graphicsPath);
				}
				using (Pen pen = new Pen(Theme.Stroke, Theme.S(1)))
				{
					graphics.DrawPath(pen, graphicsPath);
				}
			}
			int left = Theme.S(190);
			int scaledDimension2 = Theme.S(32);
			using (GraphicsPath graphicsPath2 = new GraphicsPath())
			{
				graphicsPath2.AddLine(scaledDimension, 0, left, 0);
				graphicsPath2.AddLine(left, 0, left - scaledDimension2, height);
				graphicsPath2.AddLine(left - scaledDimension2, height, 0, height);
				graphicsPath2.AddLine(0, height, 0, scaledDimension);
				graphicsPath2.CloseFigure();
				using (SolidBrush brush2 = new SolidBrush(Theme.Nav))
				{
					graphics.FillPath(brush2, graphicsPath2);
				}
			}
			using (Pen pen2 = new Pen(Theme.Accent, Theme.S(2)))
			{
				graphics.DrawLine(pen2, left, 0, left - scaledDimension2, height);
			}
			using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, 60), Theme.S(1)))
			{
				graphics.DrawLine(pen3, left + Theme.S(5), 0, left - scaledDimension2 + Theme.S(5), height);
			}
			using (Pen pen4 = new Pen(Col.Alpha(Theme.Accent, 30), Theme.S(1)))
			{
				for (int i = 0; i < 5; i++)
				{
					int left2 = Theme.S(24 + i * 10);
					graphics.DrawLine(pen4, left2, height - Theme.S(60), left2 - Theme.S(12), height - Theme.S(16));
				}
			}
			int scaledDimension3 = Theme.S(24);
			if (logo != null)
			{
				graphics.DrawImageUnscaled(logo, Theme.S(50), Theme.S(44));
			}
			using (SolidBrush brush3 = new SolidBrush(Theme.Fg))
			{
				graphics.DrawString("PAVISE", Theme.UI(15f, true), brush3, Theme.S(42), Theme.S(112));
			}
			using (SolidBrush brush4 = new SolidBrush(Theme.Faint))
			{
				graphics.DrawString("C O R E   C O N T R O L", Theme.Mono(6.4f), brush4, Theme.S(38), Theme.S(142));
			}
			using (Pen pen5 = new Pen(Theme.Accent, Theme.S(2)))
			{
				graphics.DrawLine(pen5, Theme.S(44), Theme.S(164), Theme.S(122), Theme.S(164));
			}
			using (SolidBrush brush5 = new SolidBrush(Theme.Accent))
			{
				graphics.DrawString("//  " + App.VersionTag, Theme.Mono(7.5f), brush5, Theme.S(56), Theme.S(176));
			}
			using (SolidBrush brush6 = new SolidBrush(Theme.Accent))
			{
				graphics.DrawString(Lang.T("t.contactdialog.1"), Theme.UI(8.4f, true), brush6, scaledDimension3 - Theme.S(6), height - Theme.S(132));
				graphics.DrawString(Lang.T("t.contactdialog.2"), Theme.UI(8.4f, true), brush6, scaledDimension3 - Theme.S(6), height - Theme.S(112));
			}
			using (SolidBrush brush7 = new SolidBrush(Theme.Faint))
			{
				graphics.DrawString("PAVISE  //  CONTACT", Theme.Mono(6.75f), brush7, Theme.S(218), Theme.S(22));
			}
			using (SolidBrush brush8 = new SolidBrush(Theme.Fg))
			{
				graphics.DrawString(Lang.T("contact.title"), Theme.UI(15f, true), brush8, Theme.S(214), Theme.S(38));
			}
			using (SolidBrush brush9 = new SolidBrush(Theme.Dim))
			{
				graphics.DrawString(Lang.T("contact.sub"), Theme.UI(8.2f, false), brush9, Theme.S(216), Theme.S(74));
			}
			int top = Theme.S(336);
			using (LinearGradientBrush brush10 = new LinearGradientBrush(new Rectangle(Theme.S(216), top, Theme.S(394), Theme.S(2)), Theme.Accent, Col.Alpha(Theme.Accent, 0), LinearGradientMode.Horizontal))
			{
				graphics.FillRectangle(brush10, Theme.S(216), top, Theme.S(394), Theme.S(2));
			}
		}

		private void DragMove(object sender, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				Native.ReleaseCapture();
				Native.SendMessage(base.Handle, 161, (IntPtr)2, IntPtr.Zero);
			}
		}
	}
}
