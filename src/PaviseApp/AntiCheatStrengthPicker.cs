using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class AntiCheatStrengthPicker : FxControl
    {
        private sealed class StrengthAccessibility : FxAccessibleObject
        {
            private readonly AntiCheatStrengthPicker owner;

            private readonly AccessibleObject[] children = new AccessibleObject[3];

            public override string Value
            {
                get
                {
                    return owner.SelectedLabel;
                }
                set
                {
                    for (int i = 0; i < 3; i++)
                    {
                        if (owner.Label(i) == value || (i + 1).ToString() == value)
                        {
                            owner.Activate(i, true);
                            break;
                        }
                    }
                }
            }

            public override string DefaultAction
            {
                get
                {
                    return PerformancePolicy.Text("选择", "Select");
                }
            }

            internal StrengthAccessibility(AntiCheatStrengthPicker picker)
                : base(picker)
            {
                owner = picker;
            }

            public override void DoDefaultAction()
            {
                owner.Activate(owner.index, true);
            }

            public override int GetChildCount()
            {
                return 3;
            }

            public override AccessibleObject GetChild(int row)
            {
                object instance;
                if (row >= 0 && row <= 2)
                {
                    instance = children[row];
                    if (instance == null)
                    {
                        return children[row] = new StrengthChoiceAccessibility(owner, row);
                    }
                }
                else
                {
                    instance = null;
                }
                return (AccessibleObject)instance;
            }

            public override AccessibleObject GetSelected()
            {
                return GetChild(owner.index);
            }

            public override AccessibleObject GetFocused()
            {
                if (!owner.Focused)
                {
                    return null;
                }
                return GetChild(owner.index);
            }
        }

        private sealed class StrengthChoiceAccessibility : AccessibleObject
        {
            private readonly AntiCheatStrengthPicker owner;

            private readonly int index;

            public override string Name
            {
                get
                {
                    return owner.Label(index);
                }
                set
                {
                }
            }

            public override string Value
            {
                get
                {
                    return owner.Label(index);
                }
            }

            public override string Description
            {
                get
                {
                    return Lang.T("ac.mode." + ((index == 0) ? "gentle" : ((index == 1) ? "balanced" : "isolated")) + ".d");
                }
            }

            public override AccessibleRole Role
            {
                get
                {
                    return AccessibleRole.RadioButton;
                }
            }

            public override AccessibleObject Parent
            {
                get
                {
                    return owner.AccessibilityObject;
                }
            }

            public override Rectangle Bounds
            {
                get
                {
                    if (!owner.IsHandleCreated)
                    {
                        return Rectangle.Empty;
                    }
                    return owner.RectangleToScreen(owner.GetRowBounds(index));
                }
            }

            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates accessibleStates = AccessibleStates.Focusable | AccessibleStates.Selectable;
                    if (owner.index == index)
                    {
                        accessibleStates |= AccessibleStates.Selected | AccessibleStates.Checked;
                    }
                    if (owner.Focused && owner.index == index)
                    {
                        accessibleStates |= AccessibleStates.Focused;
                    }
                    if (!owner.Enabled || owner.IsDisposed)
                    {
                        accessibleStates |= AccessibleStates.Unavailable;
                    }
                    if (owner.Busy)
                    {
                        accessibleStates |= AccessibleStates.Busy;
                    }
                    if (!owner.Visible)
                    {
                        accessibleStates |= AccessibleStates.Invisible | AccessibleStates.Offscreen;
                    }
                    return accessibleStates;
                }
            }

            public override string DefaultAction
            {
                get
                {
                    return PerformancePolicy.Text("选择", "Select");
                }
            }

            internal StrengthChoiceAccessibility(AntiCheatStrengthPicker picker, int row)
            {
                owner = picker;
                index = row;
            }

            public override void DoDefaultAction()
            {
                owner.Activate(index, true);
            }

            public override void Select(AccessibleSelection flags)
            {
                if (owner.CanInteract)
                {
                    if ((flags & AccessibleSelection.TakeFocus) != 0)
                    {
                        owner.Focus();
                    }
                    if ((flags & (AccessibleSelection.TakeFocus | AccessibleSelection.TakeSelection | AccessibleSelection.AddSelection)) != 0)
                    {
                        owner.Activate(index, true);
                    }
                }
            }
        }

        internal string[] Labels;

        internal Action<int> IndexChanged;

        private int index = 1;

        private int hoverRow = -1;

        private int pressedRow = -1;

        private readonly Motion[] glows = new Motion[3];

        private Rectangle[] rows = new Rectangle[3];

        private int measuredWidth = -1;

        private float measuredScale;

        private Font measuredFont;

        private string[] measuredLabels;

        private int contentHeight;

        private bool horizontal;

        internal bool Horizontal
        {
            get
            {
                return horizontal;
            }
            set
            {
                if (horizontal != value)
                {
                    horizontal = value;
                    measuredWidth = -1;
                    Invalidate();
                    PerformLayout();
                }
            }
        }

        internal int Index
        {
            get
            {
                return index;
            }
            set
            {
                if (value >= 0 && value <= 2 && value != index)
                {
                    index = value;
                    NotifySelection();
                    Invalidate();
                }
            }
        }

        internal string SelectedLabel
        {
            get
            {
                return Label(index);
            }
        }

        internal Rectangle[] RowBounds
        {
            get
            {
                EnsureRows(base.Width);
                return (Rectangle[])rows.Clone();
            }
        }

        internal Rectangle GetRowBounds(int row)
        {
            EnsureRows(base.Width);
            if (row >= 0 && row <= 2)
            {
                return rows[row];
            }
            return Rectangle.Empty;
        }

        internal AntiCheatStrengthPicker()
        {
            Labels = new string[3]
            {
                Lang.T("ac.mode.gentle"),
                Lang.T("ac.mode.balanced"),
                Lang.T("ac.mode.isolated")
            };
            for (int i = 0; i < glows.Length; i++)
            {
                glows[i].Speed = 0.26f;
            }
            base.AccessibleRole = AccessibleRole.List;
            base.AccessibleName = Lang.T("ac.mode");
            base.TabStop = true;
            Bg = Theme.Card;
            base.Size = new Size(Theme.S(230), Theme.S(180));
        }

        private string Label(int row)
        {
            if (Labels == null || row >= Labels.Length || Labels[row] == null)
            {
                object instance;
                switch (row)
                {
                default:
                    instance = "isolated";
                    break;
                case 1:
                    instance = "balanced";
                    break;
                case 0:
                    instance = "gentle";
                    break;
                }
                return Lang.T("ac.mode." + (string)instance);
            }
            return Labels[row];
        }

        internal int MinimumContentHeight(int width)
        {
            EnsureRows(width);
            return contentHeight;
        }

        private void EnsureRows(int width)
        {
            Font font = Theme.UI(10f, true);
            bool measurementsCurrent = measuredWidth == width && measuredScale == Dpi.Scale && object.ReferenceEquals(measuredFont, font) && measuredLabels != null;
            if (measurementsCurrent)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (measuredLabels[i] != Label(i))
                    {
                        measurementsCurrent = false;
                        break;
                    }
                }
            }
            if (measurementsCurrent)
            {
                return;
            }
            measuredWidth = width;
            measuredScale = Dpi.Scale;
            measuredFont = font;
            measuredLabels = new string[3];
            int ensureRowsValue = 0;
            int scaledDimension = Theme.S(6);
            int lowerBound = Math.Max(1, (width - scaledDimension * 2) / 3);
            int lowerBound2 = Math.Max(1, (Horizontal ? lowerBound : width) - Theme.S(64));
            int rowHeight = Theme.S(52);
            for (int j = 0; j < 3; j++)
            {
                measuredLabels[j] = Label(j);
                int height = TextRenderer.MeasureText(measuredLabels[j], font, new Size(lowerBound2, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
                int sLimit = Math.Max(Theme.S(52), height + Theme.S(24));
                if (Horizontal)
                {
                    rowHeight = Math.Max(rowHeight, sLimit);
                    continue;
                }
                rows[j] = new Rectangle(0, ensureRowsValue, Math.Max(1, width), sLimit);
                ensureRowsValue += sLimit + scaledDimension;
            }
            if (Horizontal)
            {
                for (int k = 0; k < 3; k++)
                {
                    int scaledK = k * (lowerBound + scaledDimension);
                    rows[k] = new Rectangle(scaledK, 0, Math.Max(1, (k == 2) ? (width - scaledK) : lowerBound), rowHeight);
                }
                contentHeight = rowHeight;
            }
            else
            {
                contentHeight = ensureRowsValue - scaledDimension;
            }
        }

        private int Hit(Point point)
        {
            EnsureRows(base.Width);
            for (int i = 0; i < 3; i++)
            {
                if (rows[i].Contains(point))
                {
                    return i;
                }
            }
            return -1;
        }

        private void NotifySelection()
        {
            if (base.IsHandleCreated)
            {
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                AccessibilityNotifyClients(AccessibleEvents.Selection, index + 1);
            }
        }

        private void Activate(int value, bool explicitActivation = false)
        {
            if (base.CanInteract && value >= 0 && value <= 2 && (value != index || explicitActivation))
            {
                index = value;
                NotifySelection();
                Invalidate();
                if (IndexChanged != null)
                {
                    IndexChanged(value);
                }
            }
        }

        protected override bool StepAll()
        {
            bool stepAllSucceeded = base.StepAll();
            for (int i = 0; i < glows.Length; i++)
            {
                stepAllSucceeded |= glows[i].Step();
            }
            return stepAllSucceeded;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hitResult = Hit(e.Location);
            if (hitResult != hoverRow)
            {
                hoverRow = hitResult;
                for (int i = 0; i < 3; i++)
                {
                    glows[i].To((i == hitResult) ? 1 : 0);
                }
                UiClock.Wake();
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverRow = -1;
            for (int i = 0; i < 3; i++)
            {
                glows[i].To(0f);
            }
            UiClock.Wake();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && base.CanInteract)
            {
                Focus();
                pressedRow = Hit(e.Location);
                base.Capture = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                int hitResult = Hit(e.Location);
                int pressedRowSnapshot = pressedRow;
                pressedRow = -1;
                base.Capture = false;
                if (hitResult >= 0 && hitResult == pressedRowSnapshot)
                {
                    Activate(hitResult, true);
                }
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!base.Capture)
            {
                pressedRow = -1;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys keys = keyData & Keys.KeyCode;
            bool arrowKey = Horizontal
                ? keys == Keys.Left || keys == Keys.Right
                : keys == Keys.Up || keys == Keys.Down;
            return arrowKey || keys == Keys.Home || keys == Keys.End
                || DsKeys.IsActivation(keys) || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Modifiers != 0)
            {
                return;
            }
            int value;
            if (e.KeyCode == (Keys)(Horizontal ? 37 : 38))
            {
                value = Math.Max(0, index - 1);
            }
            else if (e.KeyCode == (Keys)(Horizontal ? 39 : 40))
            {
                value = Math.Min(2, index + 1);
            }
            else if (e.KeyCode == Keys.Home)
            {
                value = 0;
            }
            else if (e.KeyCode == Keys.End)
            {
                value = 2;
            }
            else
            {
                if (!DsKeys.IsActivation(e.KeyCode))
                {
                    return;
                }
                value = index;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            Activate(value, DsKeys.IsActivation(e.KeyCode));
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new StrengthAccessibility(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            FillBg(graphics);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            EnsureRows(base.Width);
            for (int i = 0; i < 3; i++)
            {
                Rectangle rectangle = rows[i];
                rectangle.Width = Math.Max(1, rectangle.Width - 1);
                rectangle.Height = Math.Max(1, rectangle.Height - 1);
                bool isSelected = index == i;
                Color color = (isSelected ? Col.Lerp(Theme.Card, Theme.Accent, Theme.LightMode ? 0.08f : 0.12f) : Col.Lerp(Theme.Card, Theme.CardHover, glows[i].Value * 0.65f));
                Color stroke = (isSelected ? Col.Lerp(Theme.Stroke, Theme.Accent, 0.82f) : Col.Lerp(Theme.Stroke, Theme.StrokeHi, glows[i].Value));
                DsPaint.TechFrame(graphics, rectangle, Theme.S(7), color, stroke);
                Color color2 = ((!base.Enabled) ? Theme.Faint : (isSelected ? Theme.Accent : Theme.Dim));
                Font labelFont = Theme.UI(10f, isSelected);
                int labelWidth = Math.Min(TextRenderer.MeasureText(Label(i), labelFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width, Math.Max(1, rectangle.Width - Theme.S(64)));
                int groupWidth = Theme.S(28) + labelWidth;
                int groupLeft = Horizontal ? rectangle.Left + (rectangle.Width - groupWidth) / 2 : rectangle.Left + Theme.S(18);
                int iOffset = i + 1;
                int topOffset = rectangle.Top + (rectangle.Height - (iOffset - 1) * Theme.S(5)) / 2;
                using (Pen pen = new Pen(color2, Math.Max(1f, Dpi.Scale * 1.5f)))
                {
                    for (int j = 0; j < iOffset; j++)
                    {
                        graphics.DrawLine(pen, groupLeft, topOffset + j * Theme.S(5), groupLeft + Theme.S(16), topOffset + j * Theme.S(5));
                    }
                }
                TextRenderer.DrawText(graphics, Label(i), labelFont, new Rectangle(groupLeft + Theme.S(28), rectangle.Top + Theme.S(10), labelWidth, rectangle.Height - Theme.S(20)), (!base.Enabled) ? Theme.Faint : (isSelected ? Theme.Fg : Theme.Dim), TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                if (Horizontal && isSelected && rectangle.Width - groupWidth >= Theme.S(48))
                {
                    int checkLeft = rectangle.Right - Theme.S(22);
                    int checkTop = rectangle.Top + rectangle.Height / 2;
                    using (Pen check = new Pen(color2, Math.Max(1f, Dpi.Scale * 1.5f)))
                    {
                        graphics.DrawLines(check, new Point[] { new Point(checkLeft, checkTop), new Point(checkLeft + Theme.S(3), checkTop + Theme.S(3)), new Point(checkLeft + Theme.S(9), checkTop - Theme.S(3)) });
                    }
                }
                if (base.WantsFocusCue && isSelected)
                {
                    ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(rectangle, -Theme.S(4), -Theme.S(4)), Theme.Accent, color);
                }
            }
            PaintBusy(graphics, base.ClientRectangle, null);
        }
    }
}
