using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class PerformanceFeatureCard : SettingCard
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

            internal bool CompactRow;

            internal string Title;

            internal string Detail;

            internal string Status;

            internal string Lock;

            internal Font TitleFont;

            internal Font DetailFont;

            internal Font LockFont;

            internal Rectangle IconBounds;

            internal Rectangle TitleBounds;

            internal Rectangle DetailBounds;

            internal Rectangle StatusBounds;

            internal Rectangle LockBounds;

            internal Rectangle HostBounds;

            internal Rectangle InfoBounds;
        }

        private bool arranging;

        private string observedTitle;

        private string observedSummary;

        private string observedStatus;

        private string observedLock;

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

        private bool compactRow;

        internal bool CompactRow
        {
            get { return compactRow; }
            set
            {
                if (compactRow == value) return;
                compactRow = value;
                cachedPresentation = null;
                ArrangePresentation();
                if (PresentationChanged != null) PresentationChanged();
                Invalidate();
            }
        }

        private bool UsesCompactRow
        {
            get { return compactRow; }
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

        protected virtual string ShownSummary
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

        internal virtual string EffectText
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
                if (CompactRow) return Theme.S(320);
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
            if (statusText != Lang.T("lock.na") && statusText != Lang.T("env.graphics.unknown"))
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
            bool presentationChanged = observedTitle != shownTitle || observedSummary != shownSummary || observedStatus != shownStatus || observedLock != base.LockText;
            bool statusColorChanged = observedStatusColor != base.StatusColor;
            observedTitle = shownTitle;
            observedSummary = shownSummary;
            observedStatus = shownStatus;
            observedLock = base.LockText;
            observedStatusColor = base.StatusColor;
            SyncAccessibility();
            if (presentationChanged)
            {
                cachedPresentation = null;
                if (PresentationChanged != null)
                {
                    PresentationChanged();
                }
            }
            else if (statusColorChanged && PresentationStateChanged != null)
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
            if (accessibleTitle != base.Title || accessibleDisplayTitle != ShownTitle || accessibleSummary != ShownSummary || accessibleEffect != EffectText || accessibleStatus != base.StatusText || accessibleLock != base.LockText || accessibleDetail != base.Desc || accessibleHost != accessibleDescriptionOffset)
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
            bool measurementsCurrent = measuredPicker == picker && measuredPickerFont == font && measuredPickerScale == Dpi.Scale && measuredPickerLabels != null && measuredPickerLabels.Length == labelsCount;
            if (measurementsCurrent)
            {
                for (int i = 0; i < labelsCount; i++)
                {
                    if (measuredPickerLabels[i] != labels[i])
                    {
                        measurementsCurrent = false;
                        break;
                    }
                }
            }
            if (measurementsCurrent)
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
            if (UsesCompactRow) return LayoutCompactPresentation(width, apply);
            int scaledDimension = Theme.S((width < Theme.S(440)) ? 16 : 24);
            int scaledDimension2 = Theme.S(34);
            int scaledDimension3 = Theme.S(16);
            int scaledDimension3Offset = scaledDimension + scaledDimension2 + scaledDimension3;
            int lowerBound = Math.Max(scaledDimension3Offset + 1, width - scaledDimension);
            int lowerBound2 = Math.Max(1, lowerBound - scaledDimension3Offset);
            bool hostedToggle = base.Hosted is Toggle;
            bool inlineControl = hostedToggle || base.Hosted is PerformanceProfilePicker || base.Hosted is TechSelect || (base.Hosted is PillButton && base.Hosted.Width <= Theme.S(140));
            int scaledDimension4 = ((base.Hosted != null) ? (hostedToggle ? Theme.S(94) : ((base.Hosted is TierPicker) ? Math.Min(PickerWidth((TierPicker)base.Hosted), lowerBound2) : ((base.Hosted is PerformanceProfilePicker) ? Math.Min(Theme.S(230), lowerBound2) : Math.Min((intrinsicHostWidth > 0) ? intrinsicHostWidth : base.Hosted.Width, lowerBound2)))) : 0);
            bool widePicker = FullWidthPicker && base.Hosted is TierPicker;
            if (widePicker) scaledDimension4 = Math.Max(1, width - 2 * scaledDimension);
            int scaledDimension5 = ((base.Hosted != null) ? (hostedToggle ? Theme.S(40) : Math.Max((intrinsicHostHeight > 0) ? intrinsicHostHeight : base.Hosted.Height, Theme.S(30))) : 0);
            if (inlineControl && lowerBound2 - scaledDimension4 - Theme.S(16) < Theme.S(96))
            {
                inlineControl = false;
            }
            int lowerBound3 = Math.Max(1, lowerBound2 - (inlineControl ? (scaledDimension4 + Theme.S(20)) : 0));
            int scaledDimension6 = Theme.S(28);
            int titleAvailableWidth = Math.Max(1, lowerBound3 - scaledDimension6 - Theme.S(5));
            string shownTitle = ShownTitle;
            string shownSummary = ShownSummary;
            string shownStatus = ShownStatus;
            Font font = Theme.UI(11f, true);
            Font font2 = Theme.UI(9f, false);
            Font font3 = Theme.UI(8.5f, false);
            Presentation presentation = cachedPresentation;
            if (presentation != null && !presentation.CompactRow && presentation.Width == width && presentation.Scale == Dpi.Scale && presentation.Host == base.Hosted && presentation.FullWidthPicker == FullWidthPicker && presentation.HostWidth == scaledDimension4 && presentation.HostHeight == scaledDimension5 && presentation.Title == shownTitle && presentation.Detail == shownSummary && presentation.Status == shownStatus && presentation.Lock == base.LockText && object.ReferenceEquals(presentation.TitleFont, font) && object.ReferenceEquals(presentation.DetailFont, font2) && object.ReferenceEquals(presentation.LockFont, font3))
            {
                if (apply)
                {
                    ApplyPresentation(presentation);
                }
                return presentation.Height;
            }
            bool compact = width < Theme.S(440);
            int titleHeight = Measure(shownTitle, font, titleAvailableWidth, Theme.S(24));
            int measureResult = ((shownSummary.Length != 0) ? (compact ? Measure(shownSummary, font2, lowerBound3, Theme.S(18)) : Theme.S(20)) : 0);
            int scaledDimension6Limit = Math.Max(titleHeight, scaledDimension6);
            int layoutPresentationValue = scaledDimension6Limit;
            Rectangle titleBounds = new Rectangle(scaledDimension3Offset, 0, titleAvailableWidth, titleHeight);
            int titleNaturalWidth = TextRenderer.MeasureText(shownTitle, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
            titleBounds.Width = Math.Max(1, Math.Min(titleAvailableWidth, titleNaturalWidth));
            int firstLineHeight = TextRenderer.MeasureText(FirstLine(shownTitle), font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;
            if (firstLineHeight <= 0) firstLineHeight = font.Height;
            Rectangle infoBounds = new Rectangle(scaledDimension3Offset + Math.Min(titleAvailableWidth, titleNaturalWidth) + Theme.S(5), (firstLineHeight - scaledDimension6) / 2, scaledDimension6, scaledDimension6);
            Rectangle detailBounds = new Rectangle(scaledDimension3Offset, layoutPresentationValue + ((measureResult != 0) ? Theme.S(3) : 0), lowerBound3, measureResult);
            layoutPresentationValue = detailBounds.Bottom;
            Rectangle hostBounds = Rectangle.Empty;
            if (base.Hosted != null && !inlineControl)
            {
                hostBounds = new Rectangle(widePicker ? scaledDimension : scaledDimension3Offset, layoutPresentationValue + Theme.S(10), scaledDimension4, scaledDimension5);
                layoutPresentationValue = hostBounds.Bottom;
            }
            Rectangle statusBounds = Rectangle.Empty;
            Rectangle lockBounds = Rectangle.Empty;
            int layoutPresentationValue2 = (inlineControl ? lowerBound3 : lowerBound2);
            bool statusFitsBesideControl = false;
            int remainingS = lowerBound2 - scaledDimension4 - Theme.S(12);
            if (base.Hosted != null && !inlineControl && shownStatus.Length > 0 && remainingS >= Theme.S(110))
            {
                int measureResult2 = Measure(shownStatus, font3, remainingS, Theme.S(18));
                if (measureResult2 <= scaledDimension5)
                {
                    statusBounds = new Rectangle(hostBounds.Right + Theme.S(12), hostBounds.Top + (scaledDimension5 - measureResult2) / 2, remainingS, measureResult2);
                    statusFitsBesideControl = true;
                }
            }
            if (shownStatus.Length > 0 && !statusFitsBesideControl)
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
                if (inlineControl)
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
                TitleFont = font,
                DetailFont = font2,
                LockFont = font3,
                IconBounds = new Rectangle(scaledDimension, widePicker ? sLimit2 : (sLimit - scaledDimension2) / 2, scaledDimension2, scaledDimension2),
                TitleBounds = titleBounds,
                StatusBounds = statusBounds,
                LockBounds = lockBounds,
                DetailBounds = detailBounds,
                HostBounds = hostBounds,
                InfoBounds = infoBounds,
                Height = sLimit
            };
            observedTitle = shownTitle;
            observedSummary = shownSummary;
            observedStatus = shownStatus;
            observedLock = base.LockText;
            observedStatusColor = base.StatusColor;
            SyncAccessibility();
            if (apply)
            {
                ApplyPresentation(cachedPresentation);
            }
            return cachedPresentation.Height;
        }

        private int LayoutCompactPresentation(int width, bool apply)
        {
            int pad = Theme.S(12), iconSize = Theme.S(22), infoSize = Theme.S(24);
            int availableWidth = Math.Max(1, width - pad * 2);
            Size hostSize = CompactHostSize(availableWidth);
            int hostWidth = hostSize.Width, hostHeight = hostSize.Height;
            int textLeft = pad + iconSize + Theme.S(10);
            bool inlineHost = base.Hosted is Toggle || (base.Hosted != null && !(base.Hosted is TierPicker)
                && width - pad - textLeft - hostWidth - Theme.S(16) >= Theme.S(220));
            int textRight = Math.Max(textLeft + 1, width - pad - (inlineHost ? hostWidth + Theme.S(16) : 0));
            int textWidth = Math.Max(1, textRight - textLeft);
            int titleWidth = Math.Max(1, textWidth - infoSize - Theme.S(4));
            string shownTitle = ShownTitle, shownSummary = ShownSummary;
            string shownStatus = ShownStatus;
            Font titleFont = Theme.UI(9f, true), detailFont = Theme.UI(8.5f, false);
            Font stateFont = Theme.UI(8f, false);
            Presentation value = cachedPresentation;
            if (value != null && value.CompactRow && value.Width == width && value.Scale == Dpi.Scale
                && value.Host == base.Hosted && value.FullWidthPicker == FullWidthPicker
                && value.HostWidth == hostWidth && value.HostHeight == hostHeight && value.Title == shownTitle && value.Detail == shownSummary
                && value.Status == shownStatus && value.Lock == base.LockText
                && object.ReferenceEquals(value.TitleFont, titleFont) && object.ReferenceEquals(value.DetailFont, detailFont)
                && object.ReferenceEquals(value.LockFont, stateFont))
            {
                if (apply) ApplyPresentation(value);
                return value.Height;
            }
            int titleHeight = Measure(shownTitle, titleFont, titleWidth, Theme.S(18));
            int titleNaturalWidth = TextRenderer.MeasureText(shownTitle, titleFont, Size.Empty,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
            int titleOffset = Math.Max(0, (infoSize - titleFont.Height) / 2);
            Rectangle titleBounds = new Rectangle(textLeft, pad + titleOffset,
                Math.Max(1, Math.Min(titleWidth, titleNaturalWidth)), titleHeight);
            Rectangle infoBounds = new Rectangle(titleBounds.Right + Theme.S(4), pad, infoSize, infoSize);
            int bottom = Math.Max(titleBounds.Bottom, infoBounds.Bottom);
            Rectangle detailBounds = Rectangle.Empty;
            if (shownSummary.Length > 0)
            {
                detailBounds = new Rectangle(textLeft, bottom + Theme.S(2), textWidth,
                    Measure(shownSummary, detailFont, textWidth, Theme.S(16)));
                bottom = detailBounds.Bottom;
            }
            Rectangle hostBounds = Rectangle.Empty;
            if (base.Hosted != null && !inlineHost)
            {
                hostBounds = new Rectangle(pad, bottom + Theme.S(8), hostWidth, hostHeight);
                bottom = hostBounds.Bottom;
            }
            Rectangle statusBounds = Rectangle.Empty, lockBounds = Rectangle.Empty;
            // 异常与锁定说明按实际内容增高，紧凑列表也不隐藏失败原因。
            if (shownStatus.Length > 0)
            {
                statusBounds = new Rectangle(textLeft, bottom + Theme.S(4), textWidth,
                    Measure(shownStatus, stateFont, textWidth, Theme.S(16)));
                bottom = statusBounds.Bottom;
            }
            if (base.LockText.Length > 0)
            {
                lockBounds = new Rectangle(textLeft, bottom + Theme.S(4), textWidth,
                    Measure(base.LockText, stateFont, textWidth, Theme.S(16)));
                bottom = lockBounds.Bottom;
            }
            int height = Math.Max(Theme.S(72), bottom + pad);
            cachedPresentation = new Presentation
            {
                Width = width, Scale = Dpi.Scale, Host = base.Hosted, CompactRow = true, FullWidthPicker = FullWidthPicker,
                HostWidth = hostWidth, HostHeight = hostHeight,
                Title = shownTitle, Detail = shownSummary, Status = shownStatus, Lock = base.LockText,
                TitleFont = titleFont, DetailFont = detailFont, LockFont = stateFont,
                IconBounds = new Rectangle(pad, pad + (infoSize - iconSize) / 2, iconSize, iconSize),
                TitleBounds = titleBounds, InfoBounds = infoBounds, DetailBounds = detailBounds,
                StatusBounds = statusBounds, LockBounds = lockBounds,
                HostBounds = inlineHost ? new Rectangle(Math.Max(0, width - pad - hostWidth), (height - hostHeight) / 2, hostWidth, hostHeight) : hostBounds,
                Height = height
            };
            observedTitle = shownTitle; observedSummary = shownSummary; observedStatus = shownStatus;
            observedLock = base.LockText; observedStatusColor = base.StatusColor;
            SyncAccessibility();
            if (apply) ApplyPresentation(cachedPresentation);
            return height;
        }

        private Size CompactHostSize(int availableWidth)
        {
            Control host = base.Hosted;
            if (host == null) return Size.Empty;
            if (host is Toggle) return new Size(Theme.S(44), Theme.S(24));
            TierPicker picker = host as TierPicker;
            if (picker != null) return new Size(availableWidth, picker.WrappedHeightFor(availableWidth));
            if (host is StandbyCleanerActionHost)
            {
                foreach (Control child in host.Controls)
                    if (child is PillButton)
                        return new Size(Math.Min(availableWidth, child.Width + Theme.S(14 + 44)), Math.Max(child.Height, Theme.S(24)));
            }
            int naturalWidth = intrinsicHostWidth > 0 ? intrinsicHostWidth : host.Width;
            int naturalHeight = intrinsicHostHeight > 0 ? intrinsicHostHeight : host.Height;
            PillButton button = host as PillButton;
            if (button != null)
            {
                int textWidth = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                naturalWidth = Math.Max(naturalWidth, textWidth + Theme.S(24));
            }
            return new Size(Math.Min(availableWidth, Math.Max(1, naturalWidth)), Math.Max(naturalHeight, Theme.S(30)));
        }

        private void ApplyPresentation(Presentation value)
        {
            IconBounds = value.IconBounds;
            TitleBounds = value.TitleBounds;
            StatusBounds = value.StatusBounds;
            LockBounds = value.LockBounds;
            DetailBounds = value.DetailBounds;
            InfoBounds = value.InfoBounds;
            if (information.Bounds != value.InfoBounds)
            {
                information.Bounds = value.InfoBounds;
            }
            information.Bg = Theme.Card;
            if (base.Hosted != null)
            {
                Toggle toggle = base.Hosted as Toggle;
                if (toggle != null) toggle.Compact = value.CompactRow;
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
                    tierPicker.WrapChoices = value.CompactRow;
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
                ArrangeComposite(value.CompactRow);
            }
        }

        private void ArrangeComposite(bool compact)
        {
            if (!(base.Hosted is StandbyCleanerActionHost)) return;
            Toggle toggle = null;
            PillButton button = null;
            foreach (Control child in base.Hosted.Controls)
            {
                if (child is Toggle) toggle = (Toggle)child;
                else if (child is PillButton) button = (PillButton)child;
            }
            if (toggle == null || button == null) return;
            // 子控件尺寸不反写固有宽度，切换紧凑布局时不会累计扩张复合操作区。
            toggle.Compact = compact;
            toggle.Size = new Size(Theme.S(compact ? 44 : 94), Theme.S(compact ? 24 : 40));
            toggle.Location = new Point(Math.Max(0, base.Hosted.Width - toggle.Width), Math.Max(0, (base.Hosted.Height - toggle.Height) / 2));
            button.Location = new Point(0, Math.Max(0, (base.Hosted.Height - button.Height) / 2));
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
                bool compact = UsesCompactRow;
                DrawFeatureIcon(graphics);
                TextRenderer.DrawText(graphics, ShownTitle, Theme.UI(compact ? 9f : 11f, true), TitleBounds, Theme.Fg, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                if (DetailBounds.Height > 0)
                {
                    TextRenderer.DrawText(graphics, ShownSummary, Theme.UI(compact ? 8.5f : 9f, false), DetailBounds, Theme.Dim, ((compact || base.Width < Theme.S(440)) ? TextFormatFlags.WordBreak : (TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine)) | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
                if (!StatusBounds.IsEmpty)
                {
                    TextRenderer.DrawText(graphics, base.StatusText, Theme.UI(compact ? 8f : 8.5f, false), StatusBounds, (base.StatusColor == Theme.Warning || base.StatusColor == Theme.Danger) ? base.StatusColor : Theme.Warning, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
                if (!LockBounds.IsEmpty)
                {
                    TextRenderer.DrawText(graphics, base.LockText, Theme.UI(compact ? 8f : 8.5f, false), LockBounds, base.IsLocked ? Theme.Warning : Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
            }
        }

        protected virtual void DrawFeatureIcon(Graphics graphics)
        {
            PerformanceFeatureIcons.Draw(graphics, IconBounds, IconKey, Theme.Accent);
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
