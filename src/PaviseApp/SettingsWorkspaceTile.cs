using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class SettingsWorkspaceTile : SettingCard
	{
		internal string IconKey = "";

		internal string Summary = "";

		internal bool BottomSeparator = true;

		internal bool StackHost;

		internal bool CompactRow;

		private readonly PerformanceInfoButton information;

		private bool arranging;

		private bool released;

		internal Rectangle IconBounds { get; private set; }

		internal Rectangle TitleBounds { get; private set; }

		internal Rectangle SummaryBounds { get; private set; }

		internal Rectangle HostBounds { get; private set; }

		internal Rectangle InfoBounds { get; private set; }

		internal PillButton InformationButton
		{
			get
			{
				return information;
			}
		}

		internal Font TitleFont
		{
			get
			{
				return Theme.UI(CompactRow ? 10.5f : 12f, true);
			}
		}

		private string SecondaryText
		{
			get
			{
				if (!CompactRow || Summary.Length != 0)
				{
					return Summary;
				}
				return base.Value.Trim();
			}
		}

		private int SecondaryHeight
		{
			get
			{
				if (SecondaryText.Length != 0)
				{
					return TextRenderer.MeasureText(SecondaryText, Theme.UI(9f, false), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;
				}
				return 0;
			}
		}

		internal string DetailsText
		{
			get
			{
				return base.Title + "\r\n\r\n" + base.Desc + ((base.StatusText.Length == 0) ? "" : ("\r\n\r\n" + base.StatusText)) + ((base.Value.Trim().Length == 0) ? "" : ("\r\n" + base.Value));
			}
		}

		internal SettingsWorkspaceTile()
		{
			UseBackdrop = true;
			base.TabStop = false;
			Cursor = Cursors.Default;
			base.AccessibleRole = AccessibleRole.Grouping;
			PerformanceInformationTip.AddOwner();
			information = new PerformanceInfoButton
			{
				Name = "settingsInformation",
				TabIndex = 1,
				Bg = Theme.Card
			};
			PerformanceInfoButton performanceInfoButton = information;
			EventHandler value = delegate
			{
				PerformanceInformationTip.Cancel(information);
				PaviseDialog.Info(FindForm(), base.Title, DetailsText);
			};
			performanceInfoButton.Click += value;
			information.MouseEnter += delegate
			{
				SyncDetails();
				PerformanceInformationTip.Schedule(information, DetailsText);
			};
			information.MouseLeave += delegate
			{
				PerformanceInformationTip.Cancel(information);
			};
			information.GotFocus += delegate
			{
				SyncDetails();
			};
			base.Controls.Add(information);
		}

		private void SyncDetails()
		{
			base.AccessibleName = base.Title;
			base.AccessibleDescription = DetailsText;
			information.AccessibleName = base.Title + PerformancePolicy.Text(" · 详情", " · Details");
			information.AccessibleDescription = DetailsText;
			PerformanceInformationTip.Refresh(information, DetailsText);
		}

		internal int MeasureHeight(int width)
		{
			if (CompactRow)
			{
				int titleWidth;
				int hostWidth;
				int titleHeight;
				bool flag = CompactMetrics(width, out titleWidth, out hostWidth, out titleHeight);
				int num = titleHeight + ((SecondaryText.Length > 0) ? (SecondaryHeight + Theme.S(4)) : 0);
				return Math.Max(Theme.S(72), (flag ? (num + Theme.S(44)) : Math.Max(num, Theme.S(34))) + Theme.S(24));
			}
			int num2 = Theme.S(22);
			int num3 = num2 + Theme.S(86);
			int num4 = HostWidth(width);
			int num5 = Theme.S(28);
			bool flag2 = StackHost || (base.Hosted != null && !(base.Hosted is Toggle) && (width < Theme.S(500) || width - num3 - num2 - num5 - Theme.S(28) - num4 < Theme.S(96)));
			int num6 = Math.Max(Theme.S(60), width - num3 - num2 - num5 - Theme.S(8) - ((!flag2 && base.Hosted != null) ? (num4 + Theme.S(20)) : 0));
			int num7 = TextRenderer.MeasureText(base.Title, Theme.UI(12f, true), new Size(num6, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
			int num8 = Math.Max(Theme.S(64), num7 + ((Summary.Length > 0) ? Theme.S(27) : 0) + ((flag2 && base.Hosted != null) ? Theme.S(58) : 0));
			return Math.Max(Theme.S(112), num8 + Theme.S(36));
		}

		private int HostWidth(int width)
		{
			if (base.Hosted == null)
			{
				return 0;
			}
			if (base.Hosted is Toggle)
			{
				return Theme.S(88);
			}
			if (base.Hosted is TierPicker)
			{
				return Math.Min(Theme.S((((TierPicker)base.Hosted).Labels.Length > 3) ? 404 : 232), Math.Max(Theme.S(140), width - Theme.S(130)));
			}
			if (base.Hosted is StandbyCleanerActionHost)
			{
				return Math.Min(Theme.S(302), Math.Max(Theme.S(200), width - Theme.S(154)));
			}
			return Math.Min(Math.Max(Theme.S(112), base.Hosted.Width), Math.Max(Theme.S(100), width - Theme.S(154)));
		}

		private bool CompactMetrics(int width, out int titleWidth, out int hostWidth, out int titleHeight)
		{
			int num = Math.Max(1, width - Theme.S(66));
			hostWidth = 0;
			if (base.Hosted is Toggle)
			{
				hostWidth = Theme.S(76);
			}
			else if (base.Hosted is TierPicker)
			{
				TierPicker tierPicker = (TierPicker)base.Hosted;
				int num2 = Theme.S((tierPicker.Labels.Length > 3) ? 46 : 78);
				string[] labels = tierPicker.Labels;
				foreach (string text in labels)
				{
					num2 = Math.Max(num2, TextRenderer.MeasureText(text, DsType.Font(DsText.Label, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(20));
				}
				hostWidth = num2 * tierPicker.Labels.Length + Theme.S(4) * (tierPicker.Labels.Length - 1);
			}
			else if (base.Hosted is StandbyCleanerActionHost)
			{
				hostWidth = Theme.S(228);
			}
			else if (base.Hosted != null)
			{
				hostWidth = Math.Max(Theme.S(112), base.Hosted.Width);
			}
			hostWidth = Math.Min(num, hostWidth);
			int num3 = TextRenderer.MeasureText(base.Title, TitleFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			int num4 = num - Theme.S(28) - ((base.Hosted != null) ? (hostWidth + Theme.S(14)) : 0);
			bool flag = base.Hosted != null && !(base.Hosted is Toggle) && num3 > num4;
			titleWidth = Math.Max(Theme.S(48), flag ? (num - Theme.S(28)) : num4);
			titleHeight = TextRenderer.MeasureText(base.Title, TitleFont, new Size(titleWidth, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
			return flag;
		}

		private void ArrangeCompactContent()
		{
			int titleWidth;
			int hostWidth;
			int titleHeight;
			bool flag = CompactMetrics(base.Width, out titleWidth, out hostWidth, out titleHeight);
			int num = titleHeight + ((SecondaryText.Length > 0) ? (SecondaryHeight + Theme.S(4)) : 0);
			int num2 = (flag ? (num + Theme.S(44)) : Math.Max(num, Theme.S(34)));
			int num3 = Math.Max(Theme.S(12), (base.Height - num2) / 2);
			int num4 = (flag ? num3 : ((base.Height - num) / 2));
			int val = TextRenderer.MeasureText(base.Title, TitleFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			TitleBounds = new Rectangle(Theme.S(50), num4, Math.Min(val, titleWidth), titleHeight);
			IconBounds = new Rectangle(Theme.S(16), flag ? (num4 - Theme.S(3)) : ((base.Height - Theme.S(22)) / 2), Theme.S(22), Theme.S(22));
			InfoBounds = new Rectangle(TitleBounds.Right + Theme.S(6), num4 + (titleHeight - Theme.S(22)) / 2, Theme.S(22), Theme.S(22));
			SummaryBounds = new Rectangle(TitleBounds.Left, TitleBounds.Bottom + Theme.S(4), titleWidth, Math.Max(Theme.S(18), SecondaryHeight));
			information.Bounds = InfoBounds;
			if (base.Hosted != null)
			{
				HostBounds = new Rectangle(flag ? TitleBounds.Left : (base.Width - Theme.S(16) - hostWidth), flag ? (num3 + num + Theme.S(10)) : ((base.Height - Theme.S(34)) / 2), hostWidth, Theme.S(34));
				base.Hosted.Bounds = HostBounds;
				Toggle toggle = base.Hosted as Toggle;
				if (toggle != null)
				{
					toggle.PerformanceConsole = true;
				}
				StandbyCleanerActionHost standbyCleanerActionHost = base.Hosted as StandbyCleanerActionHost;
				if (standbyCleanerActionHost != null)
				{
					foreach (Control control in standbyCleanerActionHost.Controls)
					{
						Toggle toggle2 = control as Toggle;
						if (toggle2 != null)
						{
							toggle2.PerformanceConsole = true;
							toggle2.Size = new Size(Theme.S(76), Theme.S(34));
						}
						else
						{
							control.SetBounds(0, 0, Math.Max(1, standbyCleanerActionHost.Width - Theme.S(88)), Theme.S(34));
						}
					}
				}
			}
			SyncDetails();
		}

		internal void ArrangeContent()
		{
			if (arranging || information == null || base.IsDisposed)
			{
				return;
			}
			arranging = true;
			try
			{
				if (CompactRow)
				{
					ArrangeCompactContent();
					return;
				}
				int num = Theme.S(22);
				int num2 = num + Theme.S(86);
				int num3 = Theme.S(28);
				int num4 = HostWidth(base.Width);
				bool flag = StackHost || (base.Hosted != null && !(base.Hosted is Toggle) && (base.Width < Theme.S(500) || base.Width - num2 - num - num3 - Theme.S(28) - num4 < Theme.S(96)));
				int val = Math.Max(Theme.S(60), base.Width - num2 - num - num3 - Theme.S(8) - ((!flag && base.Hosted != null) ? (num4 + Theme.S(20)) : 0));
				int num5 = TextRenderer.MeasureText(base.Title, Theme.UI(12f, true), new Size(val, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
				int num6 = num5 + ((Summary.Length > 0) ? Theme.S(27) : 0);
				int num7 = Math.Max(Theme.S(64), num6 + ((flag && base.Hosted != null) ? Theme.S(58) : 0));
				int num8 = Math.Max(Theme.S(16), (base.Height - num7) / 2);
				IconBounds = new Rectangle(num, num8 + Math.Max(0, (Math.Min(num7, Theme.S(80)) - Theme.S(64)) / 2), Theme.S(64), Theme.S(64));
				int num9 = (flag ? num8 : (num8 + Math.Max(0, (num7 - num6) / 2)));
				int val2 = TextRenderer.MeasureText(base.Title, Theme.UI(12f, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
				TitleBounds = new Rectangle(num2, num9, Math.Min(val, val2), num5);
				InfoBounds = new Rectangle(TitleBounds.Right + Theme.S(6), num9 - Theme.S(2), num3, num3);
				SummaryBounds = new Rectangle(num2, num9 + num5 + Theme.S(6), Math.Max(1, base.Width - num2 - num), Theme.S(22));
				information.Bounds = InfoBounds;
				if (base.Hosted != null)
				{
					HostBounds = new Rectangle(flag ? num2 : (base.Width - num - num4), flag ? (num9 + num6 + Theme.S(18)) : ((base.Height - Theme.S(40)) / 2), num4, Theme.S(40));
					base.Hosted.Bounds = HostBounds;
					Toggle toggle = base.Hosted as Toggle;
					if (toggle != null)
					{
						toggle.PerformanceConsole = true;
					}
					StandbyCleanerActionHost standbyCleanerActionHost = base.Hosted as StandbyCleanerActionHost;
					if (standbyCleanerActionHost != null)
					{
						foreach (Control control in standbyCleanerActionHost.Controls)
						{
							Toggle toggle2 = control as Toggle;
							if (toggle2 != null)
							{
								toggle2.PerformanceConsole = true;
								toggle2.Size = new Size(Theme.S(88), Theme.S(40));
							}
							else
							{
								control.SetBounds(0, 0, Math.Max(Theme.S(112), standbyCleanerActionHost.Width - Theme.S(110)), Theme.S(40));
							}
						}
					}
				}
				SyncDetails();
			}
			finally
			{
				arranging = false;
			}
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			ArrangeContent();
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			DsPaint.CardBackground(e.Graphics, this, Theme.Card);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			ArrangeContent();
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			if (BottomSeparator)
			{
				using (Pen pen = new Pen(Theme.Stroke))
				{
					graphics.DrawLine(pen, Theme.S(12), base.Height - 1, base.Width - Theme.S(12), base.Height - 1);
				}
			}
			if (CompactRow)
			{
				SettingsWorkspaceIcons.Draw(graphics, IconBounds, IconKey, base.Enabled ? Theme.Accent : Theme.Dim);
			}
			else
			{
				using (GraphicsPath path = Theme.TechPath(IconBounds, Theme.S(10)))
				{
					using (SolidBrush brush = new SolidBrush(Theme.Inset))
					{
						using (Pen pen2 = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.32f)))
						{
							graphics.FillPath(brush, path);
							graphics.DrawPath(pen2, path);
						}
					}
				}
				using (Pen pen3 = new Pen(Theme.Accent, Math.Max(1f, Dpi.Scale)))
				{
					graphics.DrawLine(pen3, IconBounds.Left + Theme.S(10), IconBounds.Top, IconBounds.Left + Theme.S(34), IconBounds.Top);
				}
				SettingsWorkspaceIcons.Draw(graphics, Rectangle.Inflate(IconBounds, -Theme.S(17), -Theme.S(17)), IconKey, Theme.Dim);
			}
			TextRenderer.DrawText(graphics, base.Title, TitleFont, TitleBounds, base.Enabled ? Theme.Fg : Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			if (SecondaryText.Length > 0)
			{
				TextRenderer.DrawText(graphics, SecondaryText, Theme.UI(9f, false), SummaryBounds, (base.StatusColor == Theme.Warning || base.StatusColor == Theme.Danger) ? base.StatusColor : Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
			}
			if (!CompactRow && base.Value.Trim().Length > 0 && base.Hosted != null && !StackHost)
			{
				TextRenderer.DrawText(graphics, base.Value, Theme.UI(8.5f, false), new Rectangle(TitleBounds.Left, TitleBounds.Bottom + Theme.S(8), Math.Max(1, HostBounds.Left - TitleBounds.Left - Theme.S(12)), Theme.S(20)), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && !released)
			{
				released = true;
				PerformanceInformationTip.Cancel(information);
				PerformanceInformationTip.ReleaseOwner();
			}
			base.Dispose(disposing);
		}
	}
}
