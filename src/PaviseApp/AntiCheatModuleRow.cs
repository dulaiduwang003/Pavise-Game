using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class AntiCheatModuleRow : SettingCard
	{
		private sealed class Measurement
		{
			internal int Width;

			internal int Height;

			internal int HostWidth;

			internal int HostHeight;

			internal float Scale;

			internal Control Host;

			internal Font TitleFont;

			internal Font ProviderFont;

			internal Font BodyFont;

			internal Font StatusFont;

			internal string Title;

			internal string Provider;

			internal string Description;

			internal string Status;

			internal string Lock;

			internal Rectangle Icon;

			internal Rectangle Heading;

			internal Rectangle ProviderLine;

			internal Rectangle DescriptionLine;

			internal Rectangle StatusLine;

			internal Rectangle Locked;

			internal Rectangle HostBounds;

			internal Rectangle Info;
		}

		internal string Provider = "";

		internal string IconKey = "";

		internal string AdditionalDetails = "";

		internal Action DetailsRequested;

		internal Action ContentSizeChanged;

		private readonly PerformanceInfoButton information;

		private bool arranging;

		private bool released;

		private bool resetCursor;

		private Measurement measured;

		private string detailsTitle;

		private string detailsProvider;

		private string detailsDescription;

		private string detailsStatus;

		private string detailsLock;

		private string detailsExtra;

		private string detailsHost;

		private string detailsText = "";

		internal PillButton InformationButton
		{
			get
			{
				return information;
			}
		}

		internal string DetailsText
		{
			get
			{
				SyncDetails();
				return detailsText;
			}
		}

		internal Rectangle IconBounds { get; private set; }

		internal Rectangle TitleBounds { get; private set; }

		internal Rectangle ProviderBounds { get; private set; }

		internal Rectangle DescriptionBounds { get; private set; }

		internal Rectangle StatusBounds { get; private set; }

		internal Rectangle LockBounds { get; private set; }

		internal Rectangle HostBounds { get; private set; }

		internal Rectangle InfoBounds { get; private set; }

		internal bool Active
		{
			get
			{
				if (!(base.StatusColor == Theme.Accent))
				{
					return base.StatusColor == Theme.Green;
				}
				return true;
			}
		}

		internal AntiCheatModuleRow()
		{
			PerformanceInformationTip.AddOwner();
			UseBackdrop = true;
			base.TabStop = false;
			Cursor = Cursors.Default;
			base.AccessibleRole = AccessibleRole.Grouping;
			information = new PerformanceInfoButton
			{
				Name = "AntiCheatModuleInfo",
				TabIndex = 1,
				Bg = Theme.Card
			};
			PerformanceInfoButton performanceInfoButton = information;
			EventHandler value = delegate
			{
				ShowDetails();
			};
			performanceInfoButton.Click += value;
			information.MouseEnter += delegate
			{
				SyncDetails();
				PerformanceInformationTip.Schedule(information, detailsText);
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

		private void ShowDetails()
		{
			PerformanceInformationTip.Cancel(information);
			string body = DetailsText;
			if (DetailsRequested != null)
			{
				DetailsRequested();
			}
			else
			{
				PaviseDialog.Info(FindForm(), base.Title, body);
			}
		}

		private void SyncDetails()
		{
			if (information == null)
			{
				return;
			}
			string accessibleDescriptionOffset = ((base.Hosted == null) ? "" : (base.Hosted.AccessibleDescription ?? ""));
			if (!(detailsTitle == base.Title) || !(detailsProvider == Provider) || !(detailsDescription == base.Desc) || !(detailsStatus == base.StatusText) || !(detailsLock == base.LockText) || !(detailsExtra == AdditionalDetails) || !(detailsHost == accessibleDescriptionOffset))
			{
				detailsTitle = base.Title;
				detailsProvider = Provider;
				detailsDescription = base.Desc;
				detailsStatus = base.StatusText;
				detailsLock = base.LockText;
				detailsExtra = AdditionalDetails;
				detailsHost = accessibleDescriptionOffset;
				detailsText = base.Title + (string.IsNullOrEmpty(Provider) ? "" : ("\r\n" + Provider)) + "\r\n\r\n" + base.Desc + ((base.StatusText.Length == 0) ? "" : ("\r\n\r\n" + base.StatusText)) + ((base.LockText.Length == 0) ? "" : ("\r\n" + base.LockText)) + (string.IsNullOrEmpty(AdditionalDetails) ? "" : ("\r\n\r\n" + AdditionalDetails));
				if (accessibleDescriptionOffset.Length > 0 && accessibleDescriptionOffset != base.Desc && !detailsText.Contains(accessibleDescriptionOffset))
				{
					detailsText = detailsText + "\r\n\r\n" + accessibleDescriptionOffset;
				}
				base.AccessibleName = base.Title;
				base.AccessibleDescription = detailsText;
				information.AccessibleName = base.Title + PerformancePolicy.Text(" · 详情", " · Details");
				information.AccessibleDescription = detailsText;
				PerformanceInformationTip.Refresh(information, detailsText);
			}
		}

		protected override void OnInvalidated(InvalidateEventArgs e)
		{
			base.OnInvalidated(e);
			SyncDetails();
			if (measured != null && (!(measured.Title == base.Title) || !(measured.Provider == Provider) || !(measured.Description == base.Desc) || !(measured.Status == base.StatusText) || !(measured.Lock == base.LockText)))
			{
				measured = null;
				if (ContentSizeChanged != null)
				{
					ContentSizeChanged();
				}
			}
		}

		protected override void OnControlAdded(ControlEventArgs e)
		{
			base.OnControlAdded(e);
			if (e.Control is PerformanceInfoButton)
			{
				return;
			}
			resetCursor = true;
			Toggle toggle = e.Control as Toggle;
			if (toggle != null)
			{
				toggle.PerformanceConsole = true;
			}
			e.Control.SizeChanged += delegate
			{
				if (!arranging)
				{
					measured = null;
					ArrangeContent();
					if (ContentSizeChanged != null)
					{
						ContentSizeChanged();
					}
				}
			};
		}

		private static int TextHeight(string text, Font font, int width, int minimum)
		{
			if (!string.IsNullOrEmpty(text))
			{
				return Math.Max(minimum, TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
			}
			return 0;
		}

		internal int MinimumContentHeight(int width)
		{
			return MeasureHeight(width);
		}

		internal int MeasureHeight(int width)
		{
			return Measure(width).Height;
		}

		private Measurement Measure(int width)
		{
			int scaledDimension = Theme.S((width < Theme.S(500)) ? 18 : 24);
			int scaledDimension2 = Theme.S(44);
			int sOffset = scaledDimension + scaledDimension2 + Theme.S(18);
			int lowerBound = Math.Max(1, width - scaledDimension - sOffset);
			int scaledDimension3 = Theme.S(28);
			int scaledDimension4 = ((base.Hosted != null) ? ((base.Hosted is Toggle) ? Theme.S(94) : Math.Min(base.Hosted.Width, lowerBound)) : 0);
			int scaledDimension5 = ((base.Hosted != null) ? ((base.Hosted is Toggle) ? Theme.S(40) : Math.Max(Theme.S(28), base.Hosted.Height)) : 0);
			Font font = Theme.UI(12f, true);
			Font font2 = Theme.UI(8.5f, false);
			Font font3 = Theme.UI(9f, false);
			Font font4 = Theme.UI(8.5f, false);
			if (measured != null && measured.Width == width && measured.Scale == Dpi.Scale && measured.Host == base.Hosted && measured.HostWidth == scaledDimension4 && measured.HostHeight == scaledDimension5 && measured.Title == base.Title && measured.Provider == Provider && measured.Description == base.Desc && measured.Status == base.StatusText && measured.Lock == base.LockText && object.ReferenceEquals(measured.TitleFont, font) && object.ReferenceEquals(measured.ProviderFont, font2) && object.ReferenceEquals(measured.BodyFont, font3) && object.ReferenceEquals(measured.StatusFont, font4))
			{
				return measured;
			}
			int val = TextRenderer.MeasureText(base.StatusText, font4, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			int minLimit = Math.Max(Theme.S(110), Math.Min(Theme.S(160), val));
			int scaledDimension4Offset = minLimit + Theme.S(18) + scaledDimension4;
			bool allConditionsMet = base.Hosted != null && width >= Theme.S(500) && lowerBound - scaledDimension4Offset - Theme.S(24) >= Theme.S(200);
			int lowerBound2 = Math.Max(1, lowerBound - (allConditionsMet ? (scaledDimension4Offset + Theme.S(24)) : 0));
			int val2 = Math.Max(1, lowerBound2 - scaledDimension3 - Theme.S(6));
			int val3 = TextHeight(base.Title, font, val2, Theme.S(24));
			int val4 = TextRenderer.MeasureText(base.Title, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			Rectangle heading = new Rectangle(sOffset, 0, Math.Max(1, Math.Min(val2, val4)), val3);
			Rectangle info = new Rectangle(sOffset + Math.Min(val2, val4) + Theme.S(6), 0, scaledDimension3, scaledDimension3);
			int scaledDimension3Limit = Math.Max(val3, scaledDimension3);
			Rectangle providerLine = Rectangle.Empty;
			if (!string.IsNullOrEmpty(Provider))
			{
				providerLine = new Rectangle(sOffset, scaledDimension3Limit + Theme.S(3), lowerBound2, TextHeight(Provider, font2, lowerBound2, Theme.S(16)));
				scaledDimension3Limit = providerLine.Bottom;
			}
			Rectangle descriptionLine = new Rectangle(sOffset, scaledDimension3Limit + ((base.Desc.Length != 0) ? Theme.S(7) : 0), lowerBound2, TextHeight(base.Desc, font3, lowerBound2, Theme.S(18)));
			scaledDimension3Limit = descriptionLine.Bottom;
			Rectangle hostBounds = Rectangle.Empty;
			Rectangle statusLine = Rectangle.Empty;
			Rectangle locked = Rectangle.Empty;
			int val5 = scaledDimension3Limit;
			if (allConditionsMet)
			{
				int remainingScaledDimension4 = width - scaledDimension - scaledDimension4;
				int remainingMinLimit = remainingScaledDimension4 - Theme.S(18) - minLimit;
				int measureValue = 0;
				if (base.StatusText.Length > 0)
				{
					statusLine = new Rectangle(remainingMinLimit, 0, minLimit, TextHeight(base.StatusText, font4, minLimit, Theme.S(18)));
					measureValue = statusLine.Bottom;
				}
				if (base.LockText.Length > 0)
				{
					locked = new Rectangle(remainingMinLimit, measureValue + Theme.S(5), minLimit, TextHeight(base.LockText, font4, minLimit, Theme.S(18)));
					measureValue = locked.Bottom;
				}
				int measureValueLimit = Math.Max(scaledDimension5, measureValue);
				hostBounds = new Rectangle(remainingScaledDimension4, (measureValueLimit - scaledDimension5) / 2, scaledDimension4, scaledDimension5);
				int lowerBound3 = Math.Max(0, (measureValueLimit - measureValue) / 2);
				if (!statusLine.IsEmpty)
				{
					statusLine.Offset(0, lowerBound3);
				}
				if (!locked.IsEmpty)
				{
					locked.Offset(0, lowerBound3);
				}
				val5 = Math.Max(val5, measureValueLimit);
				int lowerBound4 = Math.Max(0, (val5 - measureValueLimit) / 2);
				hostBounds.Offset(0, lowerBound4);
				if (!statusLine.IsEmpty)
				{
					statusLine.Offset(0, lowerBound4);
				}
				if (!locked.IsEmpty)
				{
					locked.Offset(0, lowerBound4);
				}
			}
			else
			{
				if (base.Hosted != null)
				{
					hostBounds = new Rectangle(sOffset, scaledDimension3Limit + Theme.S(12), scaledDimension4, scaledDimension5);
					scaledDimension3Limit = hostBounds.Bottom;
				}
				int remainingS = lowerBound - scaledDimension4 - Theme.S(14);
				int textHeightResult = TextHeight(base.StatusText, font4, remainingS, Theme.S(18));
				if (base.StatusText.Length > 0)
				{
					if (base.Hosted != null && remainingS >= Theme.S(90) && textHeightResult <= scaledDimension5)
					{
						statusLine = new Rectangle(hostBounds.Right + Theme.S(14), hostBounds.Top + (scaledDimension5 - textHeightResult) / 2, remainingS, textHeightResult);
					}
					else
					{
						statusLine = new Rectangle(sOffset, scaledDimension3Limit + Theme.S(7), lowerBound, TextHeight(base.StatusText, font4, lowerBound, Theme.S(18)));
						scaledDimension3Limit = statusLine.Bottom;
					}
				}
				if (base.LockText.Length > 0)
				{
					locked = new Rectangle(sOffset, scaledDimension3Limit + Theme.S(5), lowerBound, TextHeight(base.LockText, font4, lowerBound, Theme.S(18)));
					scaledDimension3Limit = locked.Bottom;
				}
				val5 = scaledDimension3Limit;
			}
			int sLimit = Math.Max(Theme.S(132), val5 + Theme.S(36));
			int ratio = (sLimit - val5) / 2;
			heading.Offset(0, ratio);
			info.Offset(0, ratio);
			descriptionLine.Offset(0, ratio);
			if (!providerLine.IsEmpty)
			{
				providerLine.Offset(0, ratio);
			}
			if (!hostBounds.IsEmpty)
			{
				hostBounds.Offset(0, ratio);
			}
			if (!statusLine.IsEmpty)
			{
				statusLine.Offset(0, ratio);
			}
			if (!locked.IsEmpty)
			{
				locked.Offset(0, ratio);
			}
			measured = new Measurement
			{
				Width = width,
				Height = sLimit,
				Scale = Dpi.Scale,
				Host = base.Hosted,
				HostWidth = scaledDimension4,
				HostHeight = scaledDimension5,
				TitleFont = font,
				ProviderFont = font2,
				BodyFont = font3,
				StatusFont = font4,
				Title = base.Title,
				Provider = Provider,
				Description = base.Desc,
				Status = base.StatusText,
				Lock = base.LockText,
				Icon = new Rectangle(scaledDimension, (sLimit - scaledDimension2) / 2, scaledDimension2, scaledDimension2),
				Heading = heading,
				ProviderLine = providerLine,
				DescriptionLine = descriptionLine,
				StatusLine = statusLine,
				Locked = locked,
				HostBounds = hostBounds,
				Info = info
			};
			SyncDetails();
			return measured;
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
				Measurement measurement = Measure(base.Width);
				int distance = Math.Max(0, (base.Height - measurement.Height) / 2);
				IconBounds = Offset(measurement.Icon, distance);
				TitleBounds = Offset(measurement.Heading, distance);
				ProviderBounds = Offset(measurement.ProviderLine, distance);
				DescriptionBounds = Offset(measurement.DescriptionLine, distance);
				StatusBounds = Offset(measurement.StatusLine, distance);
				HostBounds = Offset(measurement.HostBounds, distance);
				InfoBounds = Offset(measurement.Info, distance);
				LockBounds = Offset(measurement.Locked, distance);
				if (information.Bounds != InfoBounds)
				{
					information.Bounds = InfoBounds;
				}
				information.Bg = Theme.Card;
				if (base.Hosted != null)
				{
					if (base.Hosted.TabIndex != 0)
					{
						base.Hosted.TabIndex = 0;
					}
					if (resetCursor)
					{
						Cursor = Cursors.Default;
						resetCursor = false;
					}
					FxControl fxControl = base.Hosted as FxControl;
					if (fxControl != null)
					{
						fxControl.Bg = Theme.Card;
					}
					if (base.Hosted.Bounds != HostBounds)
					{
						base.Hosted.Bounds = HostBounds;
					}
				}
			}
			finally
			{
				arranging = false;
			}
		}

		private static Rectangle Offset(Rectangle bounds, int distance)
		{
			if (!bounds.IsEmpty)
			{
				bounds.Offset(0, distance);
			}
			return bounds;
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
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			ArrangeContent();
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle r = Rectangle.Inflate(IconBounds, Theme.S(8), Theme.S(8));
			DsPaint.TechFrame(graphics, r, Theme.S(8), Theme.Inset, Active ? Col.Lerp(Theme.Stroke, Theme.Accent, 0.65f) : Theme.Stroke);
			if (Active)
			{
				using (Pen pen = new Pen(Theme.Accent, Math.Max(1f, Dpi.Scale)))
				{
					graphics.DrawLine(pen, r.Left, r.Top + Theme.S(17), r.Left, r.Bottom - Theme.S(17));
				}
			}
			AntiCheatFeatureIcons.Draw(graphics, IconBounds, IconKey, Active ? Theme.Accent : Theme.Dim);
			TextRenderer.DrawText(graphics, base.Title, Theme.UI(12f, true), TitleBounds, Theme.Fg, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			if (!ProviderBounds.IsEmpty)
			{
				TextRenderer.DrawText(graphics, Provider, Theme.UI(8.5f, false), ProviderBounds, Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			}
			if (DescriptionBounds.Height > 0)
			{
				TextRenderer.DrawText(graphics, base.Desc, Theme.UI(9f, false), DescriptionBounds, Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			}
			if (!StatusBounds.IsEmpty)
			{
				TextRenderer.DrawText(graphics, base.StatusText, Theme.UI(8.5f, false), StatusBounds, base.StatusColor, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			}
			if (!LockBounds.IsEmpty)
			{
				TextRenderer.DrawText(graphics, base.LockText, Theme.UI(8.5f, false), LockBounds, base.IsLocked ? Theme.Warning : Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
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
