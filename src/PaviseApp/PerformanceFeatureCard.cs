using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PerformanceFeatureCard : SettingCard
	{
		private sealed class Presentation
		{
			internal int Width;

			internal int HostWidth;

			internal int HostHeight;

			internal int Height;

			internal float Scale;

			internal Control Host;

            internal bool FullWidthPicker;

			internal string Title;

			internal string Detail;

			internal string Status;

			internal string Lock;

			internal string Effect;

			internal Font TitleFont;

			internal Font DetailFont;

			internal Font LockFont;

			internal Font TagFont;

			internal Rectangle IconBounds;

			internal Rectangle TitleBounds;

			internal Rectangle DetailBounds;

			internal Rectangle StatusBounds;

			internal Rectangle LockBounds;

			internal Rectangle RestartBounds;

			internal Rectangle HostBounds;

			internal Rectangle InfoBounds;
		}

		private bool arranging;

		private string observedTitle;

		private string observedSummary;

		private string observedStatus;

		private string observedLock;

		private string observedEffect;

		private Color observedStatusColor;

		private Presentation cachedPresentation;

		private readonly PerformanceInfoButton information;

		private int intrinsicHostWidth;

		private int intrinsicHostHeight;

		private bool resetHostCursor;

		private bool informationOwnerReleased;

		private TierPicker measuredPicker;

        private bool fullWidthPicker;
        internal bool FullWidthPicker
        {
            get { return fullWidthPicker; }
            set
            {
                if (fullWidthPicker == value) return;
                fullWidthPicker = value;
                cachedPresentation = null;
                ArrangePresentation();
                if (PresentationChanged != null) PresentationChanged();
            }
        }

		private string[] measuredPickerLabels;

		private Font measuredPickerFont;

		private float measuredPickerScale;

		private int measuredPickerWidth;

		internal Action PresentationChanged;

		internal Action PresentationStateChanged;

		internal string IconKey = "";

		internal string Summary = "";

		internal string DisplayTitle = "";

		internal PerformanceRestartScope RestartScope;

		internal PerformanceLifetime Lifetime = PerformanceLifetime.Persistent;

		internal string RestartText = "";

		private bool statusRequiresAttention;

		private string fallbackDetail;

		private string fallbackSummary;

		private static readonly string[] exceptionalWords = new string[27]
		{
			"不支持", "不适用", "不可用", "无法", "未核验", "未验证", "故障", "失败", "受限", "恢复未",
			"外部", "其他程序", "系统或", "unsupported", "unavailable", "not supported", "not applicable", "unverified", "failed", "failure",
			"fault", "stopped", "blocked", "external", "outside", "incomplete", "unable"
		};

		private string severityStatus;

		private Color severityInk;

		private Color severityWarning;

		private Color severityDanger;

		private bool severity;

		private string accessibleTitle;

		private string accessibleDisplayTitle;

		private string accessibleSummary;

		private string accessibleEffect;

		private string accessibleStatus;

		private string accessibleLock;

		private string accessibleDetail;

		private string accessibleHost;

		private string detailsText = "";

		internal Rectangle IconBounds { get; private set; }

		internal Rectangle TitleBounds { get; private set; }

		internal Rectangle StatusBounds { get; private set; }

		internal Rectangle DetailBounds { get; private set; }

		internal Rectangle LockBounds { get; private set; }

		internal Rectangle RestartBounds { get; private set; }

		internal Rectangle InfoBounds { get; private set; }

		internal bool StatusRequiresAttention
		{
			get
			{
				return statusRequiresAttention;
			}
			set
			{
				if (statusRequiresAttention != value)
				{
					statusRequiresAttention = value;
					severityStatus = null;
					Invalidate();
				}
			}
		}

		internal PillButton InformationButton
		{
			get
			{
				return information;
			}
		}

		internal bool ShowsRestart
		{
			get
			{
				return RestartScope != PerformanceRestartScope.None;
			}
		}

		internal bool ShowsLock
		{
			get
			{
				return !string.IsNullOrEmpty(base.LockText);
			}
		}

		internal bool ShowsStatus
		{
			get
			{
				return !string.IsNullOrEmpty(base.StatusText)
					&& !string.Equals(base.StatusText, base.LockText, StringComparison.Ordinal)
					&& ExceptionalStatus();
			}
		}

		private string ShownTitle
		{
			get
			{
				if (!string.IsNullOrEmpty(DisplayTitle))
				{
					return DisplayTitle;
				}
				return base.Title;
			}
		}

		private string ShownSummary
		{
			get
			{
				if (!string.IsNullOrEmpty(Summary))
				{
					return Summary;
				}
				if (fallbackDetail != base.Desc)
				{
					fallbackDetail = base.Desc;
					fallbackSummary = FirstLine(base.Desc);
				}
				return fallbackSummary ?? "";
			}
		}

		private string ShownStatus
		{
			get
			{
				if (!ShowsStatus)
				{
					return "";
				}
				return base.StatusText;
			}
		}

		private string ShownEffect
		{
			get
			{
				if (!ShowsRestart)
				{
					return "";
				}
				return EffectText;
			}
		}

		internal string EffectText
		{
			get
			{
				if (!string.IsNullOrEmpty(RestartText))
				{
					return RestartText;
				}
				return RestartLabel(RestartScope);
			}
		}

		internal string DetailsText
		{
			get
			{
				SyncAccessibility();
				return detailsText;
			}
		}

		internal int MinimumPresentationWidth
		{
			get
			{
				TierPicker tierPicker = base.Hosted as TierPicker;
				if (tierPicker == null || Lang.Cur == 0)
				{
					return Theme.S(320);
				}
				return Math.Max(Theme.S(320), PickerWidth(tierPicker) + Theme.S(98));
			}
		}

		internal static string RestartLabel(PerformanceRestartScope scope)
		{
			switch (scope)
			{
			default:
				return PerformancePolicy.Text("无需重启", "No restart");
			case PerformanceRestartScope.ProcessRestart:
				return PerformancePolicy.Text("重启游戏", "Restart game");
			case PerformanceRestartScope.SystemRestart:
				return PerformancePolicy.Text("重启电脑", "Restart PC");
			}
		}

		internal PerformanceFeatureCard()
		{
			PerformanceInformationTip.AddOwner();
			UseBackdrop = true;
			base.TabStop = false;
			Cursor = Cursors.Default;
			base.AccessibleRole = AccessibleRole.Grouping;
			information = new PerformanceInfoButton
			{
				Name = "performanceInfo",
				TabIndex = 1,
				TabStop = true
			};
			PerformanceInfoButton performanceInfoButton = information;
			EventHandler value = delegate
			{
				ShowInformation();
			};
			performanceInfoButton.Click += value;
			information.MouseEnter += delegate
			{
				SyncAccessibility();
				PerformanceInformationTip.Schedule(information, detailsText);
			};
			information.MouseLeave += delegate
			{
				PerformanceInformationTip.Cancel(information);
			};
			information.GotFocus += delegate
			{
				SyncAccessibility();
                PerformanceInformationTip.Show(information, detailsText);
			};
            information.LostFocus += delegate { PerformanceInformationTip.Cancel(information); };
			base.Controls.Add(information);
		}

		private static string FirstLine(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}
			int num = text.IndexOf('\n');
			int num2 = text.IndexOf('\r');
			if (num < 0 || (num2 >= 0 && num2 < num))
			{
				num = num2;
			}
			if (num >= 0)
			{
				return text.Substring(0, num);
			}
			return text;
		}

		private bool ExceptionalStatus()
		{
			if (severityStatus == base.StatusText && severityInk == base.StatusColor && severityWarning == Theme.Warning && severityDanger == Theme.Danger)
			{
				return severity;
			}
			severityStatus = base.StatusText;
			severityInk = base.StatusColor;
			severityWarning = Theme.Warning;
			severityDanger = Theme.Danger;
			severity = ComputeExceptionalStatus();
			return severity;
		}

		private bool ComputeExceptionalStatus()
		{
			if (StatusRequiresAttention)
			{
				return true;
			}
			string statusText = base.StatusText;
			if (string.IsNullOrEmpty(statusText))
			{
				return false;
			}
			if (statusText == Lang.T("env.performance.reboot") || statusText == Lang.T("env.performance.restore.reboot"))
			{
				return false;
			}
			if (base.StatusColor == Theme.Warning || base.StatusColor == Theme.Danger)
			{
				return true;
			}
			string[] array = exceptionalWords;
			foreach (string value in array)
			{
				if (statusText.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			if (!(statusText == Lang.T("lock.na")) && !(statusText == Lang.T("env.graphics.unknown")))
			{
				return statusText == Lang.T("lock.external");
			}
			return true;
		}

		protected override void OnInvalidated(InvalidateEventArgs e)
		{
			base.OnInvalidated(e);
			string shownTitle = ShownTitle;
			string shownSummary = ShownSummary;
			string shownStatus = ShownStatus;
			string shownEffect = ShownEffect;
			bool flag = observedTitle != shownTitle || observedSummary != shownSummary || observedStatus != shownStatus || observedLock != base.LockText || observedEffect != shownEffect;
			bool flag2 = observedStatusColor != base.StatusColor;
			observedTitle = shownTitle;
			observedSummary = shownSummary;
			observedStatus = shownStatus;
			observedLock = base.LockText;
			observedEffect = shownEffect;
			observedStatusColor = base.StatusColor;
			SyncAccessibility();
			if (flag)
			{
				cachedPresentation = null;
				if (PresentationChanged != null)
				{
					PresentationChanged();
				}
			}
			else if (flag2 && PresentationStateChanged != null)
			{
				PresentationStateChanged();
			}
		}

		private void SyncAccessibility()
		{
			if (information == null)
			{
				return;
			}
			string text = ((base.Hosted == null) ? "" : (base.Hosted.AccessibleDescription ?? ""));
			if (!(accessibleTitle == base.Title) || !(accessibleDisplayTitle == ShownTitle) || !(accessibleSummary == ShownSummary) || !(accessibleEffect == EffectText) || !(accessibleStatus == base.StatusText) || !(accessibleLock == base.LockText) || !(accessibleDetail == base.Desc) || !(accessibleHost == text))
			{
				accessibleTitle = base.Title;
				accessibleDisplayTitle = ShownTitle;
				accessibleSummary = ShownSummary;
				accessibleEffect = EffectText;
				accessibleStatus = base.StatusText;
				accessibleLock = base.LockText;
				accessibleDetail = base.Desc;
				accessibleHost = text;
				detailsText = base.Title + "\r\n\r\n" + base.Desc + ((base.StatusText.Length == 0) ? "" : ("\r\n\r\n" + base.StatusText)) + ((base.LockText.Length == 0 || string.Equals(base.LockText, base.StatusText, StringComparison.Ordinal)) ? "" : ("\r\n" + base.LockText)) + "\r\n" + EffectText;
				if (text.Length > 0 && text != base.Desc && !base.Desc.Contains(text))
				{
					detailsText = detailsText + "\r\n\r\n" + text;
				}
				base.AccessibleName = ShownTitle;
				base.AccessibleDescription = detailsText;
				information.AccessibleName = ShownTitle + PerformancePolicy.Text(" · 功能详情", " · Feature details");
				information.AccessibleDescription = detailsText;
				PerformanceInformationTip.Refresh(information, detailsText);
			}
		}

		private void ShowInformation()
		{
            PerformanceInformationTip.Show(information, DetailsText);
		}

		private static int Measure(string text, Font font, int width, int minimum)
		{
			if (string.IsNullOrEmpty(text))
			{
				return 0;
			}
			return Math.Max(minimum, TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
		}

		internal int PresentationHeight(int width)
		{
			return LayoutPresentation(width, false);
		}

		private int PickerWidth(TierPicker picker)
		{
			Font font = DsType.Font(DsText.Label, true);
			string[] labels = picker.Labels;
			int num = ((labels != null) ? labels.Length : 0);
			bool flag = measuredPicker == picker && measuredPickerFont == font && measuredPickerScale == Dpi.Scale && measuredPickerLabels != null && measuredPickerLabels.Length == num;
			if (flag)
			{
				for (int i = 0; i < num; i++)
				{
					if (measuredPickerLabels[i] != labels[i])
					{
						flag = false;
						break;
					}
				}
			}
			if (flag)
			{
				return measuredPickerWidth;
			}
			int num2 = 0;
			for (int j = 0; j < num; j++)
			{
				num2 = Math.Max(num2, TextRenderer.MeasureText(labels[j] ?? "", font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
			}
			measuredPicker = picker;
			measuredPickerFont = font;
			measuredPickerScale = Dpi.Scale;
			measuredPickerLabels = ((labels == null) ? new string[0] : ((string[])labels.Clone()));
			measuredPickerWidth = Math.Max(Theme.S(310), num * (num2 + Theme.S(18)) + Math.Max(0, num - 1) * Theme.S(4));
			return measuredPickerWidth;
		}

		private int LayoutPresentation(int width, bool apply)
		{
			int num = Theme.S((width < Theme.S(440)) ? 16 : 24);
			int num2 = Theme.S(34);
			int num3 = Theme.S(16);
			int num4 = num + num2 + num3;
			int num5 = Math.Max(num4 + 1, width - num);
			int num6 = Math.Max(1, num5 - num4);
			bool flag = base.Hosted is Toggle;
			bool flag2 = flag || base.Hosted is QuantumProfilePicker || base.Hosted is TechSelect || (base.Hosted is PillButton && base.Hosted.Width <= Theme.S(140));
			int num7 = ((base.Hosted != null) ? (flag ? Theme.S(94) : ((base.Hosted is TierPicker) ? Math.Min(PickerWidth((TierPicker)base.Hosted), num6) : ((base.Hosted is QuantumProfilePicker) ? Math.Min(Theme.S(230), num6) : Math.Min((intrinsicHostWidth > 0) ? intrinsicHostWidth : base.Hosted.Width, num6)))) : 0);
            bool widePicker = FullWidthPicker && base.Hosted is TierPicker;
            if (widePicker) num7 = Math.Max(1, width - 2 * num);
			int num8 = ((base.Hosted != null) ? (flag ? Theme.S(40) : Math.Max((intrinsicHostHeight > 0) ? intrinsicHostHeight : base.Hosted.Height, Theme.S(30))) : 0);
			if (flag2 && num6 - num7 - Theme.S(16) < Theme.S(96))
			{
				flag2 = false;
			}
			int num9 = Math.Max(1, num6 - (flag2 ? (num7 + Theme.S(20)) : 0));
			int num10 = Theme.S(28);
			int val = Math.Max(1, num9 - num10 - Theme.S(5));
			string shownTitle = ShownTitle;
			string shownSummary = ShownSummary;
			string shownStatus = ShownStatus;
			string shownEffect = ShownEffect;
			Font font = Theme.UI(11f, true);
			Font font2 = Theme.UI(9f, false);
			Font font3 = Theme.UI(8.5f, false);
			Font font4 = Theme.UI(7.7f, false);
			Presentation presentation = cachedPresentation;
			if (presentation != null && presentation.Width == width && presentation.Scale == Dpi.Scale && presentation.Host == base.Hosted && presentation.FullWidthPicker == FullWidthPicker && presentation.HostWidth == num7 && presentation.HostHeight == num8 && presentation.Title == shownTitle && presentation.Detail == shownSummary && presentation.Status == shownStatus && presentation.Lock == base.LockText && presentation.Effect == shownEffect && object.ReferenceEquals(presentation.TitleFont, font) && object.ReferenceEquals(presentation.DetailFont, font2) && object.ReferenceEquals(presentation.LockFont, font3) && object.ReferenceEquals(presentation.TagFont, font4))
			{
				if (apply)
				{
					ApplyPresentation(presentation);
				}
				return presentation.Height;
			}
			bool flag3 = width < Theme.S(440);
			int val2 = Measure(shownTitle, font, val, Theme.S(24));
			int num11 = ((shownSummary.Length != 0) ? (flag3 ? Measure(shownSummary, font2, num9, Theme.S(18)) : Theme.S(20)) : 0);
			int num12 = Math.Max(val2, num10);
			int num13 = num12;
			Rectangle titleBounds = new Rectangle(num4, 0, val, val2);
			int val3 = TextRenderer.MeasureText(shownTitle, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			titleBounds.Width = Math.Max(1, Math.Min(val, val3));
			int firstLineHeight = TextRenderer.MeasureText(FirstLine(shownTitle), font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;
			if (firstLineHeight <= 0) firstLineHeight = font.Height;
			Rectangle infoBounds = new Rectangle(num4 + Math.Min(val, val3) + Theme.S(5), (firstLineHeight - num10) / 2, num10, num10);
			Rectangle detailBounds = new Rectangle(num4, num13 + ((num11 != 0) ? Theme.S(3) : 0), num9, num11);
			num13 = detailBounds.Bottom;
			Rectangle hostBounds = Rectangle.Empty;
			if (base.Hosted != null && !flag2)
			{
				hostBounds = new Rectangle(widePicker ? num : num4, num13 + Theme.S(10), num7, num8);
				num13 = hostBounds.Bottom;
			}
			Rectangle restartBounds = Rectangle.Empty;
			Rectangle statusBounds = Rectangle.Empty;
			Rectangle lockBounds = Rectangle.Empty;
			int num14 = (flag2 ? num9 : num6);
			bool flag4 = false;
			int num15 = num6 - num7 - Theme.S(12);
			if (base.Hosted != null && !flag2 && shownStatus.Length > 0 && num15 >= Theme.S(110))
			{
				int num16 = Measure(shownStatus, font3, num15, Theme.S(18));
				if (num16 <= num8)
				{
					statusBounds = new Rectangle(hostBounds.Right + Theme.S(12), hostBounds.Top + (num8 - num16) / 2, num15, num16);
					flag4 = true;
				}
			}
			if (shownEffect.Length > 0)
			{
				int num17 = Math.Min(num9, TextRenderer.MeasureText(shownEffect, font4, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + Theme.S(16));
				restartBounds = new Rectangle(num4, num13 + Theme.S(7), num17, Theme.S(20));
				num13 = restartBounds.Bottom;
			}
			if (shownStatus.Length > 0 && !flag4)
			{
				statusBounds = new Rectangle(num4, num13 + Theme.S(6), num14, Measure(shownStatus, font3, num14, Theme.S(18)));
				num13 = statusBounds.Bottom;
			}
			if (base.LockText.Length > 0)
			{
				lockBounds = new Rectangle(num4, num13 + Theme.S(5), num14, Measure(base.LockText, font3, num14, Theme.S(18)));
				num13 = lockBounds.Bottom;
			}
			int num18 = Math.Max(Theme.S(124), num13 + Theme.S(32));
			int num19 = Math.Max(Theme.S(16), (num18 - num13) / 2);
			titleBounds.Offset(0, num19);
			infoBounds.Offset(0, num19);
			detailBounds.Offset(0, num19);
			if (!restartBounds.IsEmpty)
			{
				restartBounds.Offset(0, num19);
			}
			if (!statusBounds.IsEmpty)
			{
				statusBounds.Offset(0, num19);
			}
			if (!lockBounds.IsEmpty)
			{
				lockBounds.Offset(0, num19);
			}
			if (base.Hosted != null)
			{
				if (flag2)
				{
					hostBounds = new Rectangle(num5 - num7, (num18 - num8) / 2, num7, num8);
				}
				else
				{
					hostBounds.Offset(0, num19);
				}
			}
			cachedPresentation = new Presentation
			{
				Width = width,
				Scale = Dpi.Scale,
				Host = base.Hosted,
                FullWidthPicker = FullWidthPicker,
				HostWidth = num7,
				HostHeight = num8,
				Title = shownTitle,
				Detail = shownSummary,
				Status = shownStatus,
				Lock = base.LockText,
				Effect = shownEffect,
				TitleFont = font,
				DetailFont = font2,
				LockFont = font3,
				TagFont = font4,
				IconBounds = new Rectangle(num, widePicker ? num19 : (num18 - num2) / 2, num2, num2),
				TitleBounds = titleBounds,
				StatusBounds = statusBounds,
				LockBounds = lockBounds,
				DetailBounds = detailBounds,
				RestartBounds = restartBounds,
				HostBounds = hostBounds,
				InfoBounds = infoBounds,
				Height = num18
			};
			observedTitle = shownTitle;
			observedSummary = shownSummary;
			observedStatus = shownStatus;
			observedLock = base.LockText;
			observedEffect = shownEffect;
			observedStatusColor = base.StatusColor;
			SyncAccessibility();
			if (apply)
			{
				ApplyPresentation(cachedPresentation);
			}
			return cachedPresentation.Height;
		}

		private void ApplyPresentation(Presentation value)
		{
			IconBounds = value.IconBounds;
			TitleBounds = value.TitleBounds;
			StatusBounds = value.StatusBounds;
			LockBounds = value.LockBounds;
			DetailBounds = value.DetailBounds;
			RestartBounds = value.RestartBounds;
			InfoBounds = value.InfoBounds;
			if (information.Bounds != value.InfoBounds)
			{
				information.Bounds = value.InfoBounds;
			}
			information.Bg = Theme.Card;
			if (base.Hosted != null)
			{
				if (base.Hosted.TabIndex != 0)
				{
					base.Hosted.TabIndex = 0;
				}
				if (resetHostCursor)
				{
					Cursor = Cursors.Default;
					resetHostCursor = false;
				}
				TierPicker tierPicker = base.Hosted as TierPicker;
				if (tierPicker != null)
				{
					tierPicker.PlainSegments = false;
				}
				FxControl fxControl = base.Hosted as FxControl;
				if (fxControl != null)
				{
					fxControl.Bg = Theme.Card;
				}
				if (base.Hosted.Bounds != value.HostBounds)
				{
					base.Hosted.Bounds = value.HostBounds;
				}
			}
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			ArrangePresentation();
		}

		protected override void OnControlAdded(ControlEventArgs e)
		{
			base.OnControlAdded(e);
			if (e.Control is PerformanceInfoButton)
			{
				return;
			}
			PrepareComposite(e.Control);
			resetHostCursor = true;
			intrinsicHostWidth = e.Control.Width;
			intrinsicHostHeight = e.Control.Height;
			e.Control.SizeChanged += delegate
			{
				if (!arranging)
				{
					intrinsicHostWidth = e.Control.Width;
					intrinsicHostHeight = e.Control.Height;
					ArrangePresentation();
					if (PresentationChanged != null)
					{
						PresentationChanged();
					}
				}
			};
			Toggle toggle = e.Control as Toggle;
			if (toggle == null)
			{
				return;
			}
			toggle.PerformanceConsole = true;
			bool selected = toggle.Checked;
			toggle.Invalidated += delegate
			{
				if (selected != toggle.Checked)
				{
					selected = toggle.Checked;
					if (PresentationStateChanged != null)
					{
						PresentationStateChanged();
					}
				}
			};
		}

		private static void PrepareComposite(Control host)
		{
			if (!(host is StandbyCleanerActionHost))
			{
				return;
			}
			Toggle toggle = null;
			PillButton pillButton = null;
			foreach (Control control in host.Controls)
			{
				if (control is Toggle)
				{
					toggle = (Toggle)control;
				}
				else if (control is PillButton)
				{
					pillButton = (PillButton)control;
				}
			}
			if (toggle != null && pillButton != null)
			{
				toggle.PerformanceConsole = true;
				toggle.Size = new Size(Theme.S(94), Theme.S(40));
				host.Size = new Size(pillButton.Width + Theme.S(14) + toggle.Width, Math.Max(toggle.Height, pillButton.Height));
				pillButton.Location = new Point(0, (host.Height - pillButton.Height) / 2);
			}
		}

		internal void ArrangePresentation()
		{
			if (arranging || information == null)
			{
				return;
			}
			arranging = true;
			try
			{
				LayoutPresentation(base.Width, true);
			}
			finally
			{
				arranging = false;
			}
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
			if (base.Width >= 2 && base.Height >= 2)
			{
				ArrangePresentation();
				Graphics graphics = e.Graphics;
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				PerformanceFeatureIcons.Draw(graphics, IconBounds, IconKey, Theme.Accent);
				TextRenderer.DrawText(graphics, ShownTitle, Theme.UI(11f, true), TitleBounds, Theme.Fg, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
				if (DetailBounds.Height > 0)
				{
					TextRenderer.DrawText(graphics, ShownSummary, Theme.UI(9f, false), DetailBounds, Theme.Dim, ((base.Width < Theme.S(440)) ? TextFormatFlags.WordBreak : (TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine)) | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
				}
				if (!StatusBounds.IsEmpty)
				{
					TextRenderer.DrawText(graphics, base.StatusText, Theme.UI(8.5f, false), StatusBounds, (base.StatusColor == Theme.Warning || base.StatusColor == Theme.Danger) ? base.StatusColor : Theme.Warning, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
				}
				if (!LockBounds.IsEmpty)
				{
					TextRenderer.DrawText(graphics, base.LockText, Theme.UI(8.5f, false), LockBounds, base.IsLocked ? Theme.Warning : Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
				}
				if (!RestartBounds.IsEmpty)
				{
					DsPaint.TechFrame(graphics, RestartBounds, Theme.S(3), Col.Lerp(Theme.Card, Theme.Warning, Theme.LightMode ? 0.06f : 0.08f), Col.Lerp(Theme.Card, Theme.Warning, 0.26f));
					TextRenderer.DrawText(graphics, EffectText, Theme.UI(7.7f, false), RestartBounds, Theme.Warning, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				}
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && !informationOwnerReleased)
			{
				informationOwnerReleased = true;
				PerformanceInformationTip.Cancel(information);
				PerformanceInformationTip.ReleaseOwner();
			}
			base.Dispose(disposing);
		}
	}
}
