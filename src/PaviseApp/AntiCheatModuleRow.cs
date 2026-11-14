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

            internal string Badge;

            internal string Status;

            internal string Lock;

            internal Rectangle Icon;

            internal Rectangle Heading;

            internal Rectangle ProviderLine;

            internal Rectangle DescriptionLine;

            internal Rectangle BadgeBounds;

            internal Rectangle StatusLine;

            internal Rectangle Locked;

            internal Rectangle HostBounds;

            internal Rectangle Info;
        }

        internal string Provider = "";

        internal string IconKey = "";

        internal string Summary = "";

        internal string DescriptionBadge = "";

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

        internal Rectangle BadgeBounds { get; private set; }

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
            if (detailsTitle != base.Title || detailsProvider != Provider || detailsDescription != base.Desc || detailsStatus != base.StatusText || detailsLock != base.LockText || detailsExtra != AdditionalDetails || detailsHost != accessibleDescriptionOffset)
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
            if (measured != null && (measured.Title != base.Title || measured.Provider != Provider || measured.Description != DisplayDescription || measured.Badge != DescriptionBadge || measured.Status != base.StatusText || measured.Lock != base.LockText))
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
                toggle.PerformanceConsole = false;
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

        internal string DisplayDescription
        {
            get { return string.IsNullOrEmpty(Summary) ? base.Desc : Summary; }
        }

        internal int MeasureHeight(int width)
        {
            return Measure(width).Height;
        }

        private Measurement Measure(int width)
        {
            int padding = Theme.S(width < Theme.S(500) ? 18 : 24);
            int iconSize = Theme.S(40);
            int textLeft = padding + iconSize + Theme.S(22);
            int availableWidth = Math.Max(1, width - padding - textLeft);
            int infoSize = Theme.S(24);
            int hostWidth = base.Hosted == null ? 0 : base.Hosted is Toggle ? Theme.S(48) : Math.Min(base.Hosted.Width, availableWidth);
            int hostHeight = base.Hosted == null ? 0 : base.Hosted is Toggle ? Theme.S(28) : Math.Max(Theme.S(28), base.Hosted.Height);
            Font titleFont = Theme.UI(13f, true);
            Font providerFont = Theme.UI(9f, false);
            Font bodyFont = Theme.UI(9.5f, false);
            Font statusFont = Theme.UI(9f, false);
            string description = DisplayDescription;
            if (measured != null && measured.Width == width && measured.Scale == Dpi.Scale
                && measured.Host == base.Hosted && measured.HostWidth == hostWidth && measured.HostHeight == hostHeight
                && measured.Title == base.Title && measured.Provider == Provider && measured.Description == description
                && measured.Badge == DescriptionBadge && measured.Status == base.StatusText && measured.Lock == base.LockText
                && object.ReferenceEquals(measured.TitleFont, titleFont) && object.ReferenceEquals(measured.ProviderFont, providerFont)
                && object.ReferenceEquals(measured.BodyFont, bodyFont) && object.ReferenceEquals(measured.StatusFont, statusFont))
            {
                return measured;
            }
            TextFormatFlags singleLine = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int statusNaturalWidth = TextRenderer.MeasureText(base.StatusText, statusFont, Size.Empty, singleLine).Width;
            int statusWidth = Math.Max(Theme.S(36), Math.Min(Theme.S(180), statusNaturalWidth));
            int controlWidth = hostWidth + Theme.S(20) + statusWidth;
            bool inline = base.Hosted != null && availableWidth - controlWidth - Theme.S(28) >= Theme.S(250);
            int textWidth = Math.Max(1, availableWidth - (inline ? controlWidth + Theme.S(28) : 0));
            int titleNaturalWidth = TextRenderer.MeasureText(base.Title, titleFont, Size.Empty, singleLine).Width;
            int titleWidth = Math.Max(1, Math.Min(titleNaturalWidth, textWidth - infoSize - Theme.S(6)));
            int titleHeight = TextHeight(base.Title, titleFont, titleWidth, Theme.S(24));
            Rectangle heading = new Rectangle(textLeft, 0, titleWidth, titleHeight);
            Rectangle info = new Rectangle(heading.Right + Theme.S(6), 0, infoSize, infoSize);
            int textHeight = Math.Max(titleHeight, infoSize);
            Rectangle provider = Rectangle.Empty;
            if (!string.IsNullOrEmpty(Provider))
            {
                int providerWidth = TextRenderer.MeasureText(Provider, providerFont, Size.Empty, singleLine).Width;
                int providerLeft = info.Right + Theme.S(20);
                if (providerLeft + providerWidth <= textLeft + textWidth)
                {
                    provider = new Rectangle(providerLeft, 0, providerWidth, textHeight);
                }
                else
                {
                    provider = new Rectangle(textLeft, textHeight + Theme.S(3), textWidth, TextHeight(Provider, providerFont, textWidth, Theme.S(16)));
                    textHeight = provider.Bottom;
                }
            }
            int descriptionNaturalWidth = TextRenderer.MeasureText(description, bodyFont, Size.Empty, singleLine).Width;
            int badgeWidth = string.IsNullOrEmpty(DescriptionBadge) ? 0 : TextRenderer.MeasureText(DescriptionBadge, Theme.UI(8f, false), Size.Empty, singleLine).Width + Theme.S(14);
            bool inlineBadge = badgeWidth > 0 && descriptionNaturalWidth + badgeWidth + Theme.S(10) <= textWidth;
            Rectangle descriptionLine = new Rectangle(textLeft, textHeight + Theme.S(6), inlineBadge ? descriptionNaturalWidth : textWidth,
                TextHeight(description, bodyFont, inlineBadge ? descriptionNaturalWidth : textWidth, Theme.S(18)));
            textHeight = descriptionLine.Bottom;
            Rectangle badge = Rectangle.Empty;
            if (badgeWidth > 0)
            {
                badgeWidth = Math.Min(badgeWidth, textWidth);
                badge = new Rectangle(inlineBadge ? descriptionLine.Right + Theme.S(10) : textLeft,
                    inlineBadge ? descriptionLine.Top : textHeight + Theme.S(5), badgeWidth, Theme.S(20));
                textHeight = Math.Max(textHeight, badge.Bottom);
            }
            Rectangle host = Rectangle.Empty;
            Rectangle status = Rectangle.Empty;
            Rectangle locked = Rectangle.Empty;
            int contentHeight = textHeight;
            if (base.Hosted != null)
            {
                int stateLeft = inline ? width - padding - controlWidth : textLeft;
                int stateWidth = inline ? statusWidth : Math.Max(1, availableWidth - hostWidth - Theme.S(28));
                int stateHeight = TextHeight(base.StatusText, statusFont, stateWidth, Theme.S(18));
                if (base.StatusText.Length > 0)
                {
                    status = new Rectangle(stateLeft, 0, stateWidth, stateHeight);
                }
                int lockHeight = TextHeight(base.LockText, statusFont, stateWidth, Theme.S(18));
                if (base.LockText.Length > 0)
                {
                    locked = new Rectangle(stateLeft, stateHeight + Theme.S(4), stateWidth, lockHeight);
                    stateHeight = locked.Bottom;
                }
                int controlsHeight = Math.Max(hostHeight, stateHeight);
                int controlsTop = inline ? Math.Max(0, (textHeight - controlsHeight) / 2) : textHeight + Theme.S(12);
                host = new Rectangle(width - padding - hostWidth, controlsTop + (controlsHeight - hostHeight) / 2, hostWidth, hostHeight);
                int stateTop = controlsTop + (controlsHeight - stateHeight) / 2;
                status = Offset(status, stateTop);
                locked = Offset(locked, stateTop);
                contentHeight = Math.Max(textHeight, controlsTop + controlsHeight);
            }
            else
            {
                if (base.StatusText.Length > 0)
                {
                    status = new Rectangle(textLeft, contentHeight + Theme.S(8), textWidth, TextHeight(base.StatusText, statusFont, textWidth, Theme.S(18)));
                    contentHeight = status.Bottom;
                }
                if (base.LockText.Length > 0)
                {
                    locked = new Rectangle(textLeft, contentHeight + Theme.S(4), textWidth, TextHeight(base.LockText, statusFont, textWidth, Theme.S(18)));
                    contentHeight = locked.Bottom;
                }
            }
            int height = Math.Max(Theme.S(80), contentHeight + Theme.S(28));
            int contentTop = (height - contentHeight) / 2;
            measured = new Measurement
            {
                Width = width,
                Height = height,
                Scale = Dpi.Scale,
                Host = base.Hosted,
                HostWidth = hostWidth,
                HostHeight = hostHeight,
                TitleFont = titleFont,
                ProviderFont = providerFont,
                BodyFont = bodyFont,
                StatusFont = statusFont,
                Title = base.Title,
                Provider = Provider,
                Description = description,
                Badge = DescriptionBadge,
                Status = base.StatusText,
                Lock = base.LockText,
                Icon = new Rectangle(padding, (height - iconSize) / 2, iconSize, iconSize),
                Heading = Offset(heading, contentTop),
                Info = Offset(info, contentTop),
                ProviderLine = Offset(provider, contentTop),
                DescriptionLine = Offset(descriptionLine, contentTop),
                BadgeBounds = Offset(badge, contentTop),
                StatusLine = Offset(status, contentTop),
                Locked = Offset(locked, contentTop),
                HostBounds = Offset(host, contentTop)
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
                BadgeBounds = Offset(measurement.BadgeBounds, distance);
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
            DsPaint.CardBackground(e.Graphics, this, Theme.Bg);
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
            DsPaint.TechFrame(graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(8), Backdrop.CardFill(this, Theme.Card), Col.Lerp(Theme.Card, Theme.Stroke, 0.65f));
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
            TextRenderer.DrawText(graphics, base.Title, Theme.UI(13f, true), TitleBounds, Theme.Fg, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            if (!ProviderBounds.IsEmpty)
            {
                if (ProviderBounds.Top == TitleBounds.Top)
                {
                    using (Pen divider = new Pen(Theme.Stroke))
                    {
                        int x = ProviderBounds.Left - Theme.S(10);
                        graphics.DrawLine(divider, x, ProviderBounds.Top + Theme.S(5), x, ProviderBounds.Bottom - Theme.S(5));
                    }
                }
                TextRenderer.DrawText(graphics, Provider, Theme.UI(9f, false), ProviderBounds, Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            if (DescriptionBounds.Height > 0)
            {
                TextRenderer.DrawText(graphics, DisplayDescription, Theme.UI(9.5f, false), DescriptionBounds, Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
            if (!BadgeBounds.IsEmpty)
            {
                DsPaint.TechFrame(graphics, BadgeBounds, Theme.S(3), Theme.Card, Theme.Stroke);
                TextRenderer.DrawText(graphics, DescriptionBadge, Theme.UI(8f, false), BadgeBounds, Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
            if (!StatusBounds.IsEmpty)
            {
                using (SolidBrush dot = new SolidBrush(base.StatusColor))
                {
                    int diameter = Theme.S(5);
                    graphics.FillEllipse(dot, StatusBounds.Left - Theme.S(12), StatusBounds.Top + (Theme.S(18) - diameter) / 2, diameter, diameter);
                }
                TextRenderer.DrawText(graphics, base.StatusText, Theme.UI(9f, false), StatusBounds, base.StatusColor, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
            if (!LockBounds.IsEmpty)
            {
                TextRenderer.DrawText(graphics, base.LockText, Theme.UI(9f, false), LockBounds, base.IsLocked ? Theme.Warning : Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
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
