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
			int matchIndex = text.IndexOf('\n');
			int matchIndex2 = text.IndexOf('\r');
			if (matchIndex < 0 || (matchIndex2 >= 0 && matchIndex2 < matchIndex))
			{
				matchIndex = matchIndex2;
			}
			if (matchIndex >= 0)
			{
				return text.Substring(0, matchIndex);
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
			string[] exceptionalWordsSnapshot = exceptionalWords;
			foreach (string value in exceptionalWordsSnapshot)
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
			bool anyConditionMet = observedTitle != shownTitle || observedSummary != shownSummary || observedStatus != shownStatus || observedLock != base.LockText || observedEffect != shownEffect;
			bool matchesObservedStatusColor = observedStatusColor != base.StatusColor;
			observedTitle = shownTitle;
			observedSummary = shownSummary;
			observedStatus = shownStatus;
			observedLock = base.LockText;
			observedEffect = shownEffect;
			observedStatusColor = base.StatusColor;
			SyncAccessibility();
			if (anyConditionMet)
			{
				cachedPresentation = null;
				if (PresentationChanged != null)
				{
					PresentationChanged();
				}
			}
			else if (matchesObservedStatusColor && PresentationStateChanged != null)
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
			string accessibleDescriptionOffset = ((base.Hosted == null) ? "" : (base.Hosted.AccessibleDescription ?? ""));
			if (!(accessibleTitle == base.Title) || !(accessibleDisplayTitle == ShownTitle) || !(accessibleSummary == ShownSummary) || !(accessibleEffect == EffectText) || !(accessibleStatus == base.StatusText) || !(accessibleLock == base.LockText) || !(accessibleDetail == base.Desc) || !(accessibleHost == accessibleDescriptionOffset))
			{
				accessibleTitle = base.Title;
				accessibleDisplayTitle = ShownTitle;
				accessibleSummary = ShownSummary;
				accessibleEffect = EffectText;
				accessibleStatus = base.StatusText;
				accessibleLock = base.LockText;
				accessibleDetail = base.Desc;
				accessibleHost = accessibleDescriptionOffset;
				detailsText = base.Title + "\r\n\r\n" + base.Desc + ((base.StatusText.Length == 0) ? "" : ("\r\n\r\n" + base.StatusText)) + ((base.LockText.Length == 0 || string.Equals(base.LockText, base.StatusText, StringComparison.Ordinal)) ? "" : ("\r\n" + base.LockText)) + "\r\n" + EffectText;
				if (accessibleDescriptionOffset.Length > 0 && accessibleDescriptionOffset != base.Desc && !base.Desc.Contains(accessibleDescriptionOffset))
				{
					detailsText = detailsText + "\r\n\r\n" + accessibleDescriptionOffset;
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
			int labelsCount = ((labels != null) ? labels.Length : 0);
			bool allConditionsMet = measuredPicker == picker && measuredPickerFont == font && measuredPickerScale == Dpi.Scale && measuredPickerLabels != null && measuredPickerLabels.Length == labelsCount;
			if (allConditionsMet)
			{
				for (int i = 0; i < labelsCount; i++)
				{
					if (measuredPickerLabels[i] != labels[i])
					{
						allConditionsMet = false;
						break;
					}
				}
			}
			if (allConditionsMet)
			{
				return measuredPickerWidth;
			}
			int pickerWidthValue = 0;
			for (int j = 0; j < labelsCount; j++)
			{
				pickerWidthValue = Math.Max(pickerWidthValue, TextRenderer.MeasureText(labels[j] ?? "", font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
			}
			measuredPicker = picker;
			measuredPickerFont = font;
			measuredPickerScale = Dpi.Scale;
			measuredPickerLabels = ((labels == null) ? new string[0] : ((string[])labels.Clone()));
			measuredPickerWidth = Math.Max(Theme.S(310), labelsCount * (pickerWidthValue + Theme.S(18)) + Math.Max(0, labelsCount - 1) * Theme.S(4));
			return measuredPickerWidth;
		}

		private int LayoutPresentation(int width, bool apply)
		{
			int scaledDimension = Theme.S((width < Theme.S(440)) ? 16 : 24);
			int scaledDimension2 = Theme.S(34);
			int scaledDimension3 = Theme.S(16);
			int scaledDimension3Offset = scaledDimension + scaledDimension2 + scaledDimension3;
			int lowerBound = Math.Max(scaledDimension3Offset + 1, width - scaledDimension);
			int lowerBound2 = Math.Max(1, lowerBound - scaledDimension3Offset);
			bool matchesHosted = base.Hosted is Toggle;
			bool anyConditionMet = matchesHosted || base.Hosted is QuantumProfilePicker || base.Hosted is TechSelect || (base.Hosted is PillButton && base.Hosted.Width <= Theme.S(140));
			int scaledDimension4 = ((base.Hosted != null) ? (matchesHosted ? Theme.S(94) : ((base.Hosted is TierPicker) ? Math.Min(PickerWidth((TierPicker)base.Hosted), lowerBound2) : ((base.Hosted is QuantumProfilePicker) ? Math.Min(Theme.S(230), lowerBound2) : Math.Min((intrinsicHostWidth > 0) ? intrinsicHostWidth : base.Hosted.Width, lowerBound2)))) : 0);
            bool widePicker = FullWidthPicker && base.Hosted is TierPicker;
            if (widePicker) scaledDimension4 = Math.Max(1, width - 2 * scaledDimension);
			int scaledDimension5 = ((base.Hosted != null) ? (matchesHosted ? Theme.S(40) : Math.Max((intrinsicHostHeight > 0) ? intrinsicHostHeight : base.Hosted.Height, Theme.S(30))) : 0);
			if (anyConditionMet && lowerBound2 - scaledDimension4 - Theme.S(16) < Theme.S(96))
			{
				anyConditionMet = false;
			}
			int lowerBound3 = Math.Max(1, lowerBound2 - (anyConditionMet ? (scaledDimension4 + Theme.S(20)) : 0));
			int scaledDimension6 = Theme.S(28);
			int val = Math.Max(1, lowerBound3 - scaledDimension6 - Theme.S(5));
			string shownTitle = ShownTitle;
			string shownSummary = ShownSummary;
			string shownStatus = ShownStatus;
			string shownEffect = ShownEffect;
			Font font = Theme.UI(11f, true);
			Font font2 = Theme.UI(9f, false);
			Font font3 = Theme.UI(8.5f, false);
			Font font4 = Theme.UI(7.7f, false);
			Presentation presentation = cachedPresentation;
			if (presentation != null && presentation.Width == width && presentation.Scale == Dpi.Scale && presentation.Host == base.Hosted && presentation.FullWidthPicker == FullWidthPicker && presentation.HostWidth == scaledDimension4 && presentation.HostHeight == scaledDimension5 && presentation.Title == shownTitle && presentation.Detail == shownSummary && presentation.Status == shownStatus && presentation.Lock == base.LockText && presentation.Effect == shownEffect && object.ReferenceEquals(presentation.TitleFont, font) && object.ReferenceEquals(presentation.DetailFont, font2) && object.ReferenceEquals(presentation.LockFont, font3) && object.ReferenceEquals(presentation.TagFont, font4))
			{
				if (apply)
				{
					ApplyPresentation(presentation);
				}
				return presentation.Height;
			}
			bool matchesWidth = width < Theme.S(440);
			int val2 = Measure(shownTitle, font, val, Theme.S(24));
			int measureResult = ((shownSummary.Length != 0) ? (matchesWidth ? Measure(shownSummary, font2, lowerBound3, Theme.S(18)) : Theme.S(20)) : 0);
			int scaledDimension6Limit = Math.Max(val2, scaledDimension6);
			int layoutPresentationValue = scaledDimension6Limit;
			Rectangle titleBounds = new Rectangle(scaledDimension3Offset, 0, val, val2);
			int val3 = TextRenderer.MeasureText(shownTitle, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			titleBounds.Width = Math.Max(1, Math.Min(val, val3));
			int firstLineHeight = TextRenderer.MeasureText(FirstLine(shownTitle), font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;
			if (firstLineHeight <= 0) firstLineHeight = font.Height;
			Rectangle infoBounds = new Rectangle(scaledDimension3Offset + Math.Min(val, val3) + Theme.S(5), (firstLineHeight - scaledDimension6) / 2, scaledDimension6, scaledDimension6);
			Rectangle detailBounds = new Rectangle(scaledDimension3Offset, layoutPresentationValue + ((measureResult != 0) ? Theme.S(3) : 0), lowerBound3, measureResult);
			layoutPresentationValue = detailBounds.Bottom;
			Rectangle hostBounds = Rectangle.Empty;
			if (base.Hosted != null && !anyConditionMet)
			{
				hostBounds = new Rectangle(widePicker ? scaledDimension : scaledDimension3Offset, layoutPresentationValue + Theme.S(10), scaledDimension4, scaledDimension5);
				layoutPresentationValue = hostBounds.Bottom;
			}
			Rectangle restartBounds = Rectangle.Empty;
			Rectangle statusBounds = Rectangle.Empty;
			Rectangle lockBounds = Rectangle.Empty;
			int layoutPresentationValue2 = (anyConditionMet ? lowerBound3 : lowerBound2);
			bool layoutPresentationReady = false;
			int remainingS = lowerBound2 - scaledDimension4 - Theme.S(12);
			if (base.Hosted != null && !anyConditionMet && shownStatus.Length > 0 && remainingS >= Theme.S(110))
			{
				int measureResult2 = Measure(shownStatus, font3, remainingS, Theme.S(18));
				if (measureResult2 <= scaledDimension5)
				{
					statusBounds = new Rectangle(hostBounds.Right + Theme.S(12), hostBounds.Top + (scaledDimension5 - measureResult2) / 2, remainingS, measureResult2);
					layoutPresentationReady = true;
				}
			}
			if (shownEffect.Length > 0)
			{
				int lowerBound3Limit = Math.Min(lowerBound3, TextRenderer.MeasureText(shownEffect, font4, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + Theme.S(16));
				restartBounds = new Rectangle(scaledDimension3Offset, layoutPresentationValue + Theme.S(7), lowerBound3Limit, Theme.S(20));
				layoutPresentationValue = restartBounds.Bottom;
			}
			if (shownStatus.Length > 0 && !layoutPresentationReady)
			{
				statusBounds = new Rectangle(scaledDimension3Offset, layoutPresentationValue + Theme.S(6), layoutPresentationValue2, Measure(shownStatus, font3, layoutPresentationValue2, Theme.S(18)));
				layoutPresentationValue = statusBounds.Bottom;
			}
			if (base.LockText.Length > 0)
			{
				lockBounds = new Rectangle(scaledDimension3Offset, layoutPresentationValue + Theme.S(5), layoutPresentationValue2, Measure(base.LockText, font3, layoutPresentationValue2, Theme.S(18)));
				layoutPresentationValue = lockBounds.Bottom;
			}
			int sLimit = Math.Max(Theme.S(124), layoutPresentationValue + Theme.S(32));
			int sLimit2 = Math.Max(Theme.S(16), (sLimit - layoutPresentationValue) / 2);
			titleBounds.Offset(0, sLimit2);
			infoBounds.Offset(0, sLimit2);
			detailBounds.Offset(0, sLimit2);
			if (!restartBounds.IsEmpty)
			{
				restartBounds.Offset(0, sLimit2);
			}
			if (!statusBounds.IsEmpty)
			{
				statusBounds.Offset(0, sLimit2);
			}
			if (!lockBounds.IsEmpty)
			{
				lockBounds.Offset(0, sLimit2);
			}
			if (base.Hosted != null)
			{
				if (anyConditionMet)
				{
					hostBounds = new Rectangle(lowerBound - scaledDimension4, (sLimit - scaledDimension5) / 2, scaledDimension4, scaledDimension5);
				}
				else
				{
					hostBounds.Offset(0, sLimit2);
				}
			}
			cachedPresentation = new Presentation
			{
				Width = width,
				Scale = Dpi.Scale,
				Host = base.Hosted,
                FullWidthPicker = FullWidthPicker,
				HostWidth = scaledDimension4,
				HostHeight = scaledDimension5,
				Title = shownTitle,
				Detail = shownSummary,
				Status = shownStatus,
				Lock = base.LockText,
				Effect = shownEffect,
				TitleFont = font,
				DetailFont = font2,
				LockFont = font3,
				TagFont = font4,
				IconBounds = new Rectangle(scaledDimension, widePicker ? sLimit2 : (sLimit - scaledDimension2) / 2, scaledDimension2, scaledDimension2),
				TitleBounds = titleBounds,
				StatusBounds = statusBounds,
				LockBounds = lockBounds,
				DetailBounds = detailBounds,
				RestartBounds = restartBounds,
				HostBounds = hostBounds,
				InfoBounds = infoBounds,
				Height = sLimit
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
