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
				bool compactMetricsSucceeded = CompactMetrics(width, out titleWidth, out hostWidth, out titleHeight);
				int titleHeightOffset = titleHeight + ((SecondaryText.Length > 0) ? (SecondaryHeight + Theme.S(4)) : 0);
				return Math.Max(Theme.S(72), (compactMetricsSucceeded ? (titleHeightOffset + Theme.S(44)) : Math.Max(titleHeightOffset, Theme.S(34))) + Theme.S(24));
			}
			int scaledDimension = Theme.S(22);
			int scaledDimensionOffset = scaledDimension + Theme.S(86);
			int hostWidthResult = HostWidth(width);
			int scaledDimension2 = Theme.S(28);
			bool matchesStackHost = StackHost || (base.Hosted != null && !(base.Hosted is Toggle) && (width < Theme.S(500) || width - scaledDimensionOffset - scaledDimension - scaledDimension2 - Theme.S(28) - hostWidthResult < Theme.S(96)));
			int sLimit = Math.Max(Theme.S(60), width - scaledDimensionOffset - scaledDimension - scaledDimension2 - Theme.S(8) - ((!matchesStackHost && base.Hosted != null) ? (hostWidthResult + Theme.S(20)) : 0));
			int height = TextRenderer.MeasureText(base.Title, Theme.UI(12f, true), new Size(sLimit, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
			int sLimit2 = Math.Max(Theme.S(64), height + ((Summary.Length > 0) ? Theme.S(27) : 0) + ((matchesStackHost && base.Hosted != null) ? Theme.S(58) : 0));
			return Math.Max(Theme.S(112), sLimit2 + Theme.S(36));
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
			int lowerBound = Math.Max(1, width - Theme.S(66));
			hostWidth = 0;
			if (base.Hosted is Toggle)
			{
				hostWidth = Theme.S(76);
			}
			else if (base.Hosted is TierPicker)
			{
				TierPicker tierPicker = (TierPicker)base.Hosted;
				int scaledDimension = Theme.S((tierPicker.Labels.Length > 3) ? 46 : 78);
				string[] labels = tierPicker.Labels;
				foreach (string entryText in labels)
				{
					scaledDimension = Math.Max(scaledDimension, TextRenderer.MeasureText(entryText, DsType.Font(DsText.Label, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(20));
				}
				hostWidth = scaledDimension * tierPicker.Labels.Length + Theme.S(4) * (tierPicker.Labels.Length - 1);
			}
			else if (base.Hosted is StandbyCleanerActionHost)
			{
				hostWidth = Theme.S(228);
			}
			else if (base.Hosted != null)
			{
				hostWidth = Math.Max(Theme.S(112), base.Hosted.Width);
			}
			hostWidth = Math.Min(lowerBound, hostWidth);
			int width2 = TextRenderer.MeasureText(base.Title, TitleFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			int compactMetricsValue = lowerBound - Theme.S(28) - ((base.Hosted != null) ? (hostWidth + Theme.S(14)) : 0);
			bool allConditionsMet = base.Hosted != null && !(base.Hosted is Toggle) && width2 > compactMetricsValue;
			titleWidth = Math.Max(Theme.S(48), allConditionsMet ? (lowerBound - Theme.S(28)) : compactMetricsValue);
			titleHeight = TextRenderer.MeasureText(base.Title, TitleFont, new Size(titleWidth, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
			return allConditionsMet;
		}

		private void ArrangeCompactContent()
		{
			int titleWidth;
			int hostWidth;
			int titleHeight;
			bool compactMetricsSucceeded = CompactMetrics(base.Width, out titleWidth, out hostWidth, out titleHeight);
			int titleHeightOffset = titleHeight + ((SecondaryText.Length > 0) ? (SecondaryHeight + Theme.S(4)) : 0);
			int titleHeightOffsetOffset = (compactMetricsSucceeded ? (titleHeightOffset + Theme.S(44)) : Math.Max(titleHeightOffset, Theme.S(34)));
			int sLimit = Math.Max(Theme.S(12), (base.Height - titleHeightOffsetOffset) / 2);
			int ratio = (compactMetricsSucceeded ? sLimit : ((base.Height - titleHeightOffset) / 2));
			int val = TextRenderer.MeasureText(base.Title, TitleFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			TitleBounds = new Rectangle(Theme.S(50), ratio, Math.Min(val, titleWidth), titleHeight);
			IconBounds = new Rectangle(Theme.S(16), compactMetricsSucceeded ? (ratio - Theme.S(3)) : ((base.Height - Theme.S(22)) / 2), Theme.S(22), Theme.S(22));
			InfoBounds = new Rectangle(TitleBounds.Right + Theme.S(6), ratio + (titleHeight - Theme.S(22)) / 2, Theme.S(22), Theme.S(22));
			SummaryBounds = new Rectangle(TitleBounds.Left, TitleBounds.Bottom + Theme.S(4), titleWidth, Math.Max(Theme.S(18), SecondaryHeight));
			information.Bounds = InfoBounds;
			if (base.Hosted != null)
			{
				HostBounds = new Rectangle(compactMetricsSucceeded ? TitleBounds.Left : (base.Width - Theme.S(16) - hostWidth), compactMetricsSucceeded ? (sLimit + titleHeightOffset + Theme.S(10)) : ((base.Height - Theme.S(34)) / 2), hostWidth, Theme.S(34));
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
				int scaledDimension = Theme.S(22);
				int scaledDimensionOffset = scaledDimension + Theme.S(86);
				int scaledDimension2 = Theme.S(28);
				int hostWidthResult = HostWidth(base.Width);
				bool matchesStackHost = StackHost || (base.Hosted != null && !(base.Hosted is Toggle) && (base.Width < Theme.S(500) || base.Width - scaledDimensionOffset - scaledDimension - scaledDimension2 - Theme.S(28) - hostWidthResult < Theme.S(96)));
				int val = Math.Max(Theme.S(60), base.Width - scaledDimensionOffset - scaledDimension - scaledDimension2 - Theme.S(8) - ((!matchesStackHost && base.Hosted != null) ? (hostWidthResult + Theme.S(20)) : 0));
				int height = TextRenderer.MeasureText(base.Title, Theme.UI(12f, true), new Size(val, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
				int heightOffset = height + ((Summary.Length > 0) ? Theme.S(27) : 0);
				int sLimit = Math.Max(Theme.S(64), heightOffset + ((matchesStackHost && base.Hosted != null) ? Theme.S(58) : 0));
				int sLimit2 = Math.Max(Theme.S(16), (base.Height - sLimit) / 2);
				IconBounds = new Rectangle(scaledDimension, sLimit2 + Math.Max(0, (Math.Min(sLimit, Theme.S(80)) - Theme.S(64)) / 2), Theme.S(64), Theme.S(64));
				int sLimit2Offset = (matchesStackHost ? sLimit2 : (sLimit2 + Math.Max(0, (sLimit - heightOffset) / 2)));
				int val2 = TextRenderer.MeasureText(base.Title, Theme.UI(12f, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
				TitleBounds = new Rectangle(scaledDimensionOffset, sLimit2Offset, Math.Min(val, val2), height);
				InfoBounds = new Rectangle(TitleBounds.Right + Theme.S(6), sLimit2Offset - Theme.S(2), scaledDimension2, scaledDimension2);
				SummaryBounds = new Rectangle(scaledDimensionOffset, sLimit2Offset + height + Theme.S(6), Math.Max(1, base.Width - scaledDimensionOffset - scaledDimension), Theme.S(22));
				information.Bounds = InfoBounds;
				if (base.Hosted != null)
				{
					HostBounds = new Rectangle(matchesStackHost ? scaledDimensionOffset : (base.Width - scaledDimension - hostWidthResult), matchesStackHost ? (sLimit2Offset + heightOffset + Theme.S(18)) : ((base.Height - Theme.S(40)) / 2), hostWidthResult, Theme.S(40));
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
