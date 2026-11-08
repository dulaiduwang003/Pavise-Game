using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using System.Xml;

namespace PaviseApp
{
    internal sealed class SecurityCompatibilityWarningIcon : Control
    {
        internal const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><polygon points=\"11,2 13,2 23,20 22,22 2,22 1,20\" fill=\"none\" stroke=\"#E7B735\" stroke-width=\"1.35\" stroke-linejoin=\"miter\"/><line x1=\"12\" y1=\"8\" x2=\"12\" y2=\"14.5\" stroke=\"#E7B735\" stroke-width=\"1.8\" stroke-linecap=\"butt\"/><rect x=\"11.1\" y=\"17.5\" width=\"1.8\" height=\"1.8\" fill=\"#E7B735\"/></svg>";
        private static readonly PointF[] triangle;
        private static readonly PointF lineStart, lineEnd;
        private static readonly RectangleF dot;
        private static readonly float outlineWidth, lineWidth;
        private static readonly Color outline;
        private readonly ToolTip tip;
        private bool hovered;

        static SecurityCompatibilityWarningIcon()
        {
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(Svg);
            var polygon = (XmlElement)document.DocumentElement.ChildNodes[0];
            var line = (XmlElement)document.DocumentElement.ChildNodes[1];
            var square = (XmlElement)document.DocumentElement.ChildNodes[2];
            string[] pairs = polygon.GetAttribute("points").Split(' ');
            triangle = new PointF[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] xy = pairs[i].Split(',');
                triangle[i] = new PointF(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture));
            }
            outline = ColorTranslator.FromHtml(polygon.GetAttribute("stroke"));
            outlineWidth = Number(polygon, "stroke-width"); lineWidth = Number(line, "stroke-width");
            lineStart = new PointF(Number(line, "x1"), Number(line, "y1"));
            lineEnd = new PointF(Number(line, "x2"), Number(line, "y2"));
            dot = new RectangleF(Number(square, "x"), Number(square, "y"), Number(square, "width"), Number(square, "height"));
        }

        internal SecurityCompatibilityWarningIcon()
        {
            Name = "overviewSecurityWarning";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand; TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            tip = new ToolTip { InitialDelay = 350, ReshowDelay = 150, AutoPopDelay = 10000, ShowAlways = true };
        }

        internal void SetDescription(string title, string summary)
        { AccessibleName = title; AccessibleDescription = summary; tip.SetToolTip(this, summary); }

        private static float Number(XmlElement element, string key)
        { return float.Parse(element.GetAttribute(key), CultureInfo.InvariantCulture); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            GraphicsState saved = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float size = Math.Max(1, Math.Min(Theme.S(24), Math.Min(Width, Height) - Theme.S(8)));
                e.Graphics.TranslateTransform((Width - size) / 2f, (Height - size) / 2f);
                e.Graphics.ScaleTransform(size / 24f, size / 24f);
                Color ink = Theme.LightMode ? Color.FromArgb(169, 119, 8) : outline;
                using (var pen = new Pen(ink, outlineWidth))
                { pen.LineJoin = LineJoin.Miter; e.Graphics.DrawPolygon(pen, triangle); }
                using (var pen = new Pen(ink, lineWidth))
                { pen.StartCap = pen.EndCap = LineCap.Flat; e.Graphics.DrawLine(pen, lineStart, lineEnd); }
                using (var brush = new SolidBrush(ink)) e.Graphics.FillRectangle(brush, dot);
            }
            finally { e.Graphics.Restore(saved); }
            if (hovered || DsFocus.Wants(Focused, ShowFocusCues, Enabled))
                DsFocus.Draw(e.Graphics, DsFocus.Inset(ClientRectangle, 2), Theme.Warning, Theme.Bg);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { return DsKeys.IsActivationInput(keyData) || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!DsKeys.IsPlainActivation(e) || !DsInteraction.CanActivate(this)) return;
            e.Handled = e.SuppressKeyPress = true; OnClick(EventArgs.Empty);
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new WarningAccessibleObject(this); }
        private sealed class WarningAccessibleObject : ControlAccessibleObject
        {
            private readonly SecurityCompatibilityWarningIcon icon;
            internal WarningAccessibleObject(SecurityCompatibilityWarningIcon owner) : base(owner) { icon = owner; }
            public override string DefaultAction { get { return SecurityCompatibilityPresentation.T("查看处理方法", "View guidance", "対処方法を表示"); } }
            public override void DoDefaultAction() { if (DsInteraction.CanActivate(icon)) icon.OnClick(EventArgs.Empty); }
        }
        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    }
}
