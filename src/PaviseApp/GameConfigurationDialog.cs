using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class GameConfigurationDialog : Form
	{
		private readonly DBPanel viewport;

		private readonly Label title;

		private readonly Label gameName;

		private readonly Label platform;

		private readonly LibraryDialogButton close;

		private readonly LibraryDialogButton dismiss;

		private Bitmap icon;

		private bool arranging;

		internal string ProfileId { get; private set; }

		internal Control ConfigurationContent { get; private set; }

		internal GameConfigurationDialog(GameLibraryItem item, Control content, Bitmap originalIcon)
		{
			base.Name = "LibraryConfigurationDialog";
			base.AutoScaleMode = AutoScaleMode.None;
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.ShowInTaskbar = false;
			base.MinimizeBox = false;
			base.MaximizeBox = false;
			DoubleBuffered = true;
			BackColor = Theme.Bg;
			ForeColor = Theme.Fg;
			Font = Theme.UI(9f, false);
			Text = PerformancePolicy.Text("深度调优", "Advanced tuning");
			base.AccessibleName = Text;
			title = MakeLabel(Text, "LibraryConfigurationTitle", Theme.UI(15f, true), Theme.Fg);
			gameName = MakeLabel("", "LibraryConfigurationGame", Theme.UI(11f, true), Theme.Fg);
			gameName.AutoEllipsis = true;
			platform = MakeLabel("", "LibraryConfigurationPlatform", Theme.UI(8.5f, false), Theme.Dim);
			dismiss = new LibraryDialogButton("×", false)
			{
				Name = "LibraryConfigurationDismiss",
				TabIndex = 1,
				AccessibleName = PerformancePolicy.Text("关闭深度调优", "Close advanced tuning"),
				DialogResult = DialogResult.Cancel
			};
			LibraryDialogButton libraryDialogButton = dismiss;
			EventHandler value = delegate
			{
				Close();
			};
			libraryDialogButton.Click += value;
			close = new LibraryDialogButton(PerformancePolicy.Text("关闭", "Close"), false)
			{
				Name = "LibraryConfigurationClose",
				TabIndex = 2,
				DialogResult = DialogResult.Cancel
			};
			close.Click += delegate
			{
				Close();
			};
			base.CancelButton = close;
			viewport = new DBPanel
			{
				Name = "LibraryConfigurationViewport",
				BackColor = Theme.Card,
				AutoScroll = true,
				TabIndex = 0
			};
			Native.Dark(viewport);
			ConfigurationContent = content;
			content.TabIndex = 0;
			content.Dock = DockStyle.None;
			viewport.Controls.Add(content);
			content.SizeChanged += ContentSizeChanged;
			base.Controls.AddRange(new Control[6] { title, gameName, platform, dismiss, viewport, close });
			ProfileId = item.Profile.Id;
			RefreshGame(item, originalIcon);
			base.ClientSize = new Size(Theme.S(672), Theme.S(420));
			ArrangeDialog();
		}

		private static Label MakeLabel(string text, string name, Font font, Color color)
		{
			Label label = new Label();
			label.Name = name;
			label.Text = text;
			label.Font = font;
			label.ForeColor = color;
			label.BackColor = Theme.Bg;
			label.UseCompatibleTextRendering = false;
			label.UseMnemonic = false;
			return label;
		}

		internal void RefreshGame(GameLibraryItem fresh, Bitmap originalIcon)
		{
			if (fresh != null && fresh.Profile != null && !(fresh.Profile.Id != ProfileId))
			{
				gameName.Text = fresh.Profile.Name;
				platform.Text = GameLibraryGallery.PlatformName(fresh.Profile);
				base.AccessibleDescription = gameName.Text + " · " + platform.Text;
				icon = originalIcon;
				Invalidate();
			}
		}

		private void ContentSizeChanged(object sender, EventArgs e)
		{
			ArrangeDialog();
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			ArrangeDialog();
			Rectangle workingArea = Screen.FromControl(base.Owner ?? this).WorkingArea;
			Rectangle rectangle = ((base.Owner == null) ? workingArea : base.Owner.Bounds);
			base.Location = new Point(Math.Max(workingArea.Left, Math.Min(workingArea.Right - base.Width, rectangle.Left + (rectangle.Width - base.Width) / 2)), Math.Max(workingArea.Top, Math.Min(workingArea.Bottom - base.Height, rectangle.Top + (rectangle.Height - base.Height) / 2)));
			close.Focus();
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			ArrangeDialog();
		}

		private void ArrangeDialog()
		{
			if (arranging || ConfigurationContent == null || viewport == null || close == null)
			{
				return;
			}
			arranging = true;
			try
			{
				Rectangle workingArea = Screen.FromControl(base.Owner ?? this).WorkingArea;
				int scaledDimension = Theme.S(24);
				int left = Theme.S(24);
				int top = Theme.S(126);
				int scaledDimension2 = Theme.S(74);
				int minLimit = Math.Max(1, Math.Min(Theme.S(672), workingArea.Width - scaledDimension * 2));
				int width = Math.Max(1, minLimit - left * 2);
				Point point = new Point(0, Math.Max(0, -viewport.AutoScrollPosition.Y));
				viewport.AutoScrollPosition = Point.Empty;
				ConfigurationContent.SetBounds(0, 0, width - Theme.S(24), Math.Max(1, ConfigurationContent.Height));
				int heightOffset = ConfigurationContent.Height + Theme.S(20);
				int val = Math.Max(1, workingArea.Height - scaledDimension * 2);
				int valLimit = Math.Min(val, top + heightOffset + scaledDimension2);
				int lowerBound = Math.Max(1, valLimit - top - scaledDimension2);
				bool matchesHeightOffset = heightOffset > lowerBound;
				int val2 = width - Theme.S(24) - (matchesHeightOffset ? SystemInformation.VerticalScrollBarWidth : 0);
				ConfigurationContent.SetBounds(Theme.S(12), Theme.S(10), Math.Max(1, val2), Math.Max(1, ConfigurationContent.Height));
				heightOffset = ConfigurationContent.Height + Theme.S(20);
				valLimit = Math.Min(val, top + heightOffset + scaledDimension2);
				int topOffset = base.Top + base.Height / 2;
				base.ClientSize = new Size(minLimit, valLimit);
				title.SetBounds(left, Theme.S(20), Math.Max(1, minLimit - left * 2 - Theme.S(50)), Theme.S(34));
				dismiss.SetBounds(minLimit - left - Theme.S(40), Theme.S(16), Theme.S(40), Theme.S(36));
				gameName.SetBounds(left + Theme.S(52), Theme.S(65), Math.Max(1, width - Theme.S(52)), Theme.S(26));
				platform.SetBounds(gameName.Left, Theme.S(94), gameName.Width, Theme.S(20));
				viewport.SetBounds(left, top, width, Math.Max(1, valLimit - top - scaledDimension2));
				viewport.AutoScrollMinSize = new Size(0, heightOffset);
				viewport.AutoScrollPosition = new Point(0, Math.Min(point.Y, Math.Max(0, heightOffset - viewport.ClientSize.Height)));
				close.SetBounds(minLimit - left - Theme.S(112), valLimit - Theme.S(56), Theme.S(112), Theme.S(36));
				if (base.Visible)
				{
					base.Top = Math.Max(workingArea.Top, Math.Min(workingArea.Bottom - base.Height, topOffset - base.Height / 2));
				}
				Invalidate();
			}
			finally
			{
				arranging = false;
			}
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			e.Graphics.Clear(Theme.Bg);
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
				graphics.DrawLine(pen2, Theme.S(13), 1, Theme.S(126), 1);
				graphics.DrawLine(pen2, base.Width - Theme.S(13), 1, base.Width - 1, Theme.S(13));
				graphics.DrawLine(pen2, base.Width - Theme.S(94), base.Height - 1, base.Width - Theme.S(13), base.Height - 1);
			}
			Rectangle rectangle = new Rectangle(Theme.S(24), Theme.S(66), Theme.S(38), Theme.S(38));
			DsPaint.TechFrame(graphics, rectangle, Theme.S(6), Theme.Inset, Theme.Stroke);
			if (icon == null)
			{
				Glyphs.Draw(graphics, "gamepad", Rectangle.Inflate(rectangle, -Theme.S(7), -Theme.S(7)), Theme.Accent);
			}
			else
			{
				graphics.DrawImage(icon, Rectangle.Inflate(rectangle, -Theme.S(4), -Theme.S(4)));
			}
			if (viewport != null)
			{
				using (Pen pen3 = new Pen(Theme.Stroke))
				{
					graphics.DrawLine(pen3, viewport.Left, viewport.Top - Theme.S(9), viewport.Right, viewport.Top - Theme.S(9));
					graphics.DrawLine(pen3, viewport.Left, close.Top - Theme.S(14), viewport.Right, close.Top - Theme.S(14));
				}
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && ConfigurationContent != null)
			{
				ConfigurationContent.SizeChanged -= ContentSizeChanged;
			}
			base.Dispose(disposing);
		}
	}
}
