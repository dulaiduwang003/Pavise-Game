// @author bdth 2074055628@qq.com
// 文件用途 选一台设备之后 在这里挑它的中断要落到哪几个核上
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class IrqPinDialog : Form
    {
        private const int DlgW = 620;

        private readonly CoreMatrix matrix;
        private readonly Label lblPick;
        private readonly PillButton btnOk;
        private readonly PillButton btnCancel;
        private readonly Toggle swPriority;
        private readonly Panel scrollBody;
        private readonly Label titleLabel, closeLabel;
        private readonly int headerHeight, footerHeight, naturalClientHeight;
        private readonly Dictionary<Control, int> afterMatrixOffsets =
            new Dictionary<Control, int>();
        private readonly Dictionary<Label, int> labelLogicalLeft =
            new Dictionary<Label, int>();
        private readonly List<Label> beforeMatrixLabels = new List<Label>();
        private readonly Dictionary<Label, Rectangle> beforeMatrixBounds = new Dictionary<Label, Rectangle>();
        private int bodyContentHeight, bodyTailPadding, matrixLogicalTop;
        private bool fittedToWorkArea;
        private bool clockWasSuspended;

        public ulong Chosen { get; private set; }
        public bool RaisePriority { get { return swPriority != null && swPriority.Checked; } }

        public IrqPinDialog(IrqDevice d) : this(d, null) { }

        internal IrqPinDialog(IrqDevice d, IrqPinSession session)
        {
            session = session ?? new IrqPinSession();
            IrqDriverRecord driver = session.Driver;
            var measured = new IrqDevice { Dpc = driver == null ? 0 : driver.Dpc,
                MaxUs = driver == null ? 0 : driver.DpcMaxUs };
            Text = Lang.T("irqpin.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;

            int y = 18;
            titleLabel = new Label();
            titleLabel.Text = Lang.T("irqpin.title");
            titleLabel.ForeColor = Theme.Fg; titleLabel.BackColor = Theme.Bg; titleLabel.Font = Theme.UI(14f, true);
            titleLabel.UseCompatibleTextRendering = false;
            titleLabel.SetBounds(Theme.S(22), Theme.S(y), Theme.S(DlgW - 100), Theme.S(30));
            titleLabel.MouseDown += DragMove;
            Controls.Add(titleLabel);

            closeLabel = new Label();
            closeLabel.Text = "✕";
            closeLabel.ForeColor = Theme.Dim; closeLabel.BackColor = Theme.Bg;
            closeLabel.TextAlign = ContentAlignment.MiddleCenter;
            closeLabel.Cursor = Cursors.Hand;
            closeLabel.SetBounds(Theme.S(DlgW - 46), Theme.S(16), Theme.S(26), Theme.S(26));
            closeLabel.Click += delegate { Close(); };
            Controls.Add(closeLabel);
            y += 34;

            headerHeight = Theme.S(y);
            footerHeight = Theme.S(52);
            scrollBody = new Panel();
            scrollBody.Name = "irqPinScrollBody";
            scrollBody.BackColor = Theme.Bg;
            scrollBody.AutoScroll = true;
            scrollBody.TabStop = true;
            Controls.Add(scrollBody);
            y = 0;

            scrollBody.Controls.Add(Line(d == null ? "" : d.Name, y, 22, Theme.UI(10f, true), Theme.Fg, 24));
            y += 26;
            string stat = driver != null
                ? Lang.F("irqpin.stat", driver.DpcMaxUs.ToString("F0"),
                    IrqDeviceInventory.GradeText(IrqDeviceInventory.Grade(measured)),
                    IrqRelocate.MaskText(session.SeenMask))
                : Lang.T("irqpin.session.nodevice");
            scrollBody.Controls.Add(Line(stat, y, 22, Theme.UI(8.8f, false), Theme.Dim, 22));
            y += 26;

            // 预算占比 + 超时次数 + 驱动汇总/框架汇总 原来挂在中断页 lblIrqDetail 上 现在搬进这里
            if (d != null && driver != null)
            {
                var extra = new System.Text.StringBuilder();
                extra.Append(IrqDeviceInventory.BudgetText(driver.DpcMaxUs));
                if (driver.Over1Ms > 0) extra.Append("  ").Append(Lang.F("irq.d.over1ms", driver.Over1Ms));
                else if (driver.Over500Us > 0) extra.Append("  ").Append(Lang.F("irq.d.over500", driver.Over500Us));
                if (d.SharedStats) extra.Append("  ").Append(Lang.T("irq.d.shared"));
                scrollBody.Controls.Add(Line(extra.ToString(), y, 22, Theme.UI(8.3f, false), Theme.Dim, 22));
                y += 24;
                if (d.FrameworkStats)
                {
                    scrollBody.Controls.Add(Line(Lang.F("irq.d.framework", d.StatsDriver), y, 22,
                        Theme.UI(8.3f, false), Theme.Faint, 22));
                    y += 24;
                }
            }

            string worth = null;
            Color worthColor = Theme.Faint;
            if (d != null && driver != null)
            {
                if (d.ActionableWorth)
                {
                    worth = Lang.T("irqpin.matchsuggest");
                    worthColor = Theme.Accent;
                }
                else
                {
                    IrqGrade gr = IrqDeviceInventory.Grade(measured);
                    if (gr == IrqGrade.Fine) worth = Lang.T("irqpin.notworth");
                    else if (gr == IrqGrade.Long) worth = Lang.T("irqpin.maybe");
                }
            }
            if (worth != null)
            {
                scrollBody.Controls.Add(Line(worth, y, 22, Theme.UI(8.3f, false), worthColor, 22));
                y += 28;
            }

            if (d != null && d.InputRisk)
            {
                scrollBody.Controls.Add(Line(Lang.T("irq.d.input"), y, 22, Theme.UI(8.3f, false), Theme.Danger, 22));
                y += 26;
            }

            // MSI-X/RSS 多消息 与 StorPort 跟随发起核 两种 钉核可能无效 的提示
            if (d != null && d.MultiMessageRisk)
            {
                scrollBody.Controls.Add(Line(d.MessageCount > 1 ? Lang.F("irq.d.multimsg", d.MessageCount)
                        : Lang.T("irq.d.multimsg.unknown"),
                    y, 22, Theme.UI(8.3f, false), Theme.Dim, 22));
                y += 24;
            }
            if (d != null && d.CompletionFollowsIssuer)
            {
                scrollBody.Controls.Add(Line(Lang.T("irq.d.storagedpc"), y, 22, Theme.UI(8.3f, false), Theme.Dim, 22));
                y += 24;
            }

            // 已钉住设备的落点状态 由系统环境页管理 / 已生效 / 已写入待验证(附落点提示)
            if (d != null && d.ManagedElsewhere)
            {
                scrollBody.Controls.Add(Line(Lang.T("irq.d.owned"), y, 22, Theme.UI(8.3f, false), Theme.Faint, 22));
                y += 24;
            }
            else if (d != null && d.IsPinned)
            {
                if (d.Effective)
                {
                    scrollBody.Controls.Add(Line(Lang.F("irq.d.effective", IrqRelocate.MaskText(d.Mask)),
                        y, 22, Theme.UI(8.8f, false), Theme.Accent, 22));
                    y += 24;
                }
                else if (d.PlacementMismatch)
                {
                    scrollBody.Controls.Add(Line(Lang.F("irq.d.written", IrqRelocate.MaskText(d.Mask)),
                        y, 22, Theme.UI(8.8f, false), Theme.Danger, 40));
                    y += 42;
                    scrollBody.Controls.Add(Line(Lang.T("irq.tip.mismatch"), y, 22,
                        Theme.UI(8.3f, false), Theme.Danger, 40));
                    y += 42;
                }
                else if (d.Unverified)
                {
                    bool unknownBoot = d.RebootState == IrqRebootState.Unknown;
                    scrollBody.Controls.Add(Line(unknownBoot ? IrqDeviceInventory.PolicyText(d)
                        : Lang.F("irq.d.written", IrqRelocate.MaskText(d.Mask)),
                        y, 22, Theme.UI(8.8f, false), Theme.Faint, 40));
                    y += 42;
                    // 对局观测关着的话打多少局都不会记录 待验证会一直挂着 这里把坑说破
                    scrollBody.Controls.Add(Line(Lang.T(unknownBoot
                            ? "irq.tip.bootunknown" : IrqSessionProbe.EnabledSetting
                                ? "irq.tip.unverified" : "irq.tip.unverified.probeoff"),
                        y, 22, Theme.UI(8.3f, false), Theme.Faint, 40));
                    y += 42;
                }
                else
                {
                    scrollBody.Controls.Add(Line(Lang.F("irq.d.written", IrqRelocate.MaskText(d.Mask)),
                        y, 22, Theme.UI(8.8f, false), Theme.Danger, 40));
                    y += 42;
                }
            }

            string when = "—";
            try { when = new DateTime(session.StartUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm"); }
            catch { }
            Label sourceLabel = Line(session.Available
                ? Lang.F("irqpin.session.source", session.GameName, when, session.DurationSeconds)
                : Lang.T("irqpin.session.none"), y, 22, Theme.UI(8.8f, true), Theme.Dim, 24);
            sourceLabel.Name = "irqLoadSource";
            sourceLabel.AutoEllipsis = true;
            scrollBody.Controls.Add(sourceLabel);
            y += 26;
            string coverage = !session.Available && session.Exclusion != IrqSessionExclusion.None
                ? IrqPageStatus.ExclusionText(session.Exclusion)
                : session.Loads.Count == 0 ? Lang.T("irqpin.session.noload")
                : Lang.F("irqpin.session.coverage", session.MinimumCoverage.ToString("F0"),
                    session.MaximumCoverage.ToString("F0"));
            Label coverageLabel = Line(coverage, y, 22, Theme.UI(8.3f, false), Theme.Faint, 36);
            coverageLabel.Name = "irqLoadCoverage";
            scrollBody.Controls.Add(coverageLabel);
            y += 38;
            scrollBody.Controls.Add(Line(Lang.T("irqpin.howto") + (session.GameMask == 0
                ? "\n" + Lang.T("irqpin.session.unknownmask") : ""),
                y, 22, Theme.UI(8.3f, false), Theme.Faint, 36));
            y += 40;

            matrix = new CoreMatrix();
            matrix.PrimaryTag = Lang.T("irq.tag.target");
            matrix.MarkExclusive = false;
            // 固定的同局观测 不采桌面负载 也不拿当前模式推测历史游戏核
            matrix.Annotate = true;
            matrix.SeenMask = session.SeenMask;
            matrix.ObservedGameMask = session.GameMask;
            matrix.SetLoads(session.Loads);
            int mtxW = Theme.S(DlgW - 44);
            int mtxH = matrix.LayoutFor(mtxW);
            matrix.SetBounds(Theme.S(22), Theme.S(y), mtxW, mtxH);
            ulong start = d != null && d.IsPinned ? d.Mask : 0UL;
            matrix.Selected = start;
            Chosen = start;
            matrix.SelectionChanged = OnPicked;
            scrollBody.Controls.Add(matrix);
            y += Dpi.U(mtxH) + 10;

            lblPick = Line("", y, 22, Theme.UI(8.8f, false), Theme.Dim, 22);
            scrollBody.Controls.Add(lblPick);
            y += 30;

            swPriority = new Toggle();
            swPriority.Checked = d != null
                && (d.DevicePriorityHigh || IrqPriorityTweak.AppliedTo(d.InstanceId));
            swPriority.Enabled = d != null && !d.DevicePriorityHigh;
            swPriority.Location = new Point(Theme.S(22), Theme.S(y));
            scrollBody.Controls.Add(swPriority);
            scrollBody.Controls.Add(Line(d != null && d.DevicePriorityHigh
                    ? Lang.T("irqpin.prio.already") : Lang.T("irqpin.prio"),
                y + 4, 76, Theme.UI(8.5f, false), Theme.Dim, 30));
            y += 38;


            btnCancel = new PillButton(Lang.T("irqpin.cancel"), BtnKind.Normal);
            btnCancel.Bg = Theme.Bg;
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);

            btnOk = new PillButton(Lang.T("irqpin.ok"), BtnKind.Primary);
            btnOk.Bg = Theme.Bg;
            btnOk.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(btnOk);

            bodyContentHeight = Theme.S(y);
            matrixLogicalTop = matrix.Top;
            int initialMatrixBottom = matrix.Bottom;
            int deepestBottom = 0;
            foreach (Control control in scrollBody.Controls)
            {
                var label = control as Label;
                if (label != null)
                {
                    labelLogicalLeft[label] = label.Left;
                    if (label.Top < matrixLogicalTop)
                    { beforeMatrixLabels.Add(label); beforeMatrixBounds[label] = label.Bounds; }
                }
                if (control != matrix && control.Top >= initialMatrixBottom)
                    afterMatrixOffsets[control] = control.Top - initialMatrixBottom;
                if (control.Bottom > deepestBottom) deepestBottom = control.Bottom;
            }
            bodyTailPadding = Math.Max(0, bodyContentHeight - deepestBottom);
            beforeMatrixLabels.Sort(delegate(Label a, Label b) {
                return beforeMatrixBounds[a].Top.CompareTo(beforeMatrixBounds[b].Top); });
            scrollBody.AutoScrollMinSize = new Size(0, bodyContentHeight);
            naturalClientHeight = headerHeight + bodyContentHeight + footerHeight;
            ClientSize = new Size(Theme.S(DlgW), naturalClientHeight);
            LayoutViewportAndFooter(0);
            naturalClientHeight = headerHeight + bodyContentHeight + footerHeight;
            ClientSize = new Size(Theme.S(DlgW), naturalClientHeight);
            LayoutViewportAndFooter(0);
            MouseDown += DragMove;
            OnPicked(Chosen);
        }

        private static Label Line(string text, int y, int x, Font f, Color c, int h)
        {
            var l = new Label();
            l.Text = text; l.Font = f; l.ForeColor = c; l.BackColor = Theme.Bg;
            l.UseCompatibleTextRendering = false;
            l.SetBounds(Theme.S(x), Theme.S(y), Theme.S(DlgW - x * 2), Theme.S(h));
            return l;
        }

        private void LayoutViewportAndFooter(int keepScrollY)
        {
            int viewH = Math.Max(1, ClientSize.Height - headerHeight - footerHeight);
            scrollBody.SetBounds(0, headerHeight, ClientSize.Width, viewH);
            LayoutBodyForViewport(keepScrollY);

            int margin = Math.Min(Theme.S(22), Math.Max(0, ClientSize.Width / 10));
            int gap = Math.Min(Theme.S(10), Math.Max(0, ClientSize.Width / 30));
            int right = Math.Max(0, ClientSize.Width - margin);
            int footerY = ClientSize.Height - footerHeight;
            int available = Math.Max(0, right - margin - gap);
            int okW = Math.Min(Theme.S(140), available * 14 / 27);
            int cancelW = Math.Min(Theme.S(130), Math.Max(0, available - okW));
            // 宽度够时维持原设计宽 窄屏则同比收缩 任何情况下都不让取消键跑到负坐标
            if (available >= Theme.S(270))
            {
                okW = Theme.S(140);
                cancelW = Theme.S(130);
            }
            btnOk.SetBounds(right - okW, footerY, okW, Theme.S(34));
            btnCancel.SetBounds(right - okW - gap - cancelW, footerY, cancelW, Theme.S(34));

            closeLabel.Left = Math.Max(0, ClientSize.Width - Theme.S(46));
            titleLabel.Width = Math.Max(1, closeLabel.Left - titleLabel.Left - Theme.S(12));
        }

        private void LayoutBodyForViewport(int keepScrollY)
        {
            if (matrix == null || scrollBody.ClientSize.Width <= 0) return;

            scrollBody.SuspendLayout();
            try
            {
                // WinForms 会在 Form 缩高时自动把活动 CoreMatrix 滚进视口 布局必须先回到
                // 逻辑原点 否则 control.Top/Bottom 是减过滚动量的显示坐标 会把内容高度算短
                scrollBody.AutoScrollPosition = Point.Empty;
                bool needsVertical = bodyContentHeight > scrollBody.ClientSize.Height;
                LayoutBodyWidth(BodyViewportWidth(needsVertical));
                // 窄屏会让核心矩阵多折几行 可能正好从不用滚变成得滚
                // 第二次按最终状态永久预留滚动条宽 避免纵滚条出现后再挤出横滚条
                bool finalNeedsVertical = bodyContentHeight > scrollBody.ClientSize.Height;
                if (finalNeedsVertical != needsVertical)
                    LayoutBodyWidth(BodyViewportWidth(finalNeedsVertical));

            }
            finally { scrollBody.ResumeLayout(true); }

            // ResumeLayout(false) 会把旧宽屏的 DisplayRectangle.Width 留下来
            // 即使所有子控件已经收窄也会伪造横向滚动条 先让 AutoScroll
            // 按新控件边界完整重算 再使用最终 ClientSize 恢复/夹取纵向位置
            scrollBody.PerformLayout();
            int maxScrollY = Math.Max(0, bodyContentHeight - scrollBody.ClientSize.Height);
            scrollBody.AutoScrollPosition = new Point(0,
                Math.Min(Math.Max(0, keepScrollY), maxScrollY));
            scrollBody.PerformLayout();
        }

        private int BodyViewportWidth(bool reserveVerticalScrollbar)
        {
            int width = scrollBody.ClientSize.Width;
            if (reserveVerticalScrollbar) width -= SystemInformation.VerticalScrollBarWidth;
            return Math.Max(1, width);
        }

        private void LayoutBodyWidth(int viewportWidth)
        {
            // DPI 缩放或者窄屏下文字会折行 从原始坐标重新排
            // 不要在上一次布局的偏移上继续累加
            foreach (KeyValuePair<Label, int> item in labelLogicalLeft)
            {
                int left = Math.Min(item.Value, Math.Max(0, viewportWidth - 1));
                item.Key.Left = left;
                item.Key.Width = Math.Max(1, viewportWidth - left - Math.Min(item.Value, Theme.S(76)));
            }
            int extraHeight = 0;
            foreach (Label label in beforeMatrixLabels)
            {
                Rectangle original = beforeMatrixBounds[label];
                int needed = label.AutoEllipsis ? original.Height : TextRenderer.MeasureText(label.Text,
                    label.Font, new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
                label.Top = original.Top + extraHeight;
                label.Height = Math.Max(original.Height, needed);
                extraHeight += label.Height - original.Height;
            }
            int inset = Math.Min(Theme.S(22), Math.Max(0, (viewportWidth - 1) / 2));
            int matrixWidth = Math.Max(1, viewportWidth - inset * 2);
            int matrixHeight = matrix.LayoutFor(matrixWidth);
            matrix.SetBounds(inset, matrixLogicalTop + extraHeight, matrixWidth, matrixHeight);

            foreach (KeyValuePair<Control, int> item in afterMatrixOffsets)
                item.Key.Top = matrix.Bottom + item.Value;

            int deepestBottom = 0;
            foreach (Control control in scrollBody.Controls)
            {
                var label = control as Label;
                if (label != null)
                {
                    int originalLeft;
                    if (!labelLogicalLeft.TryGetValue(label, out originalLeft))
                        originalLeft = label.Left;
                    int left = Math.Min(originalLeft, Math.Max(0, viewportWidth - 1));
                    label.Left = left;
                    label.Width = Math.Max(1,
                        viewportWidth - left - Math.Min(originalLeft, Theme.S(76)));
                }
                // 即使 WinForms 因活动控件临时滚动 仍按逻辑坐标计算内容底部
                int logicalBottom = control.Bottom - scrollBody.AutoScrollPosition.Y;
                if (logicalBottom > deepestBottom) deepestBottom = logicalBottom;
            }
            bodyContentHeight = deepestBottom + bodyTailPadding;
            scrollBody.AutoScrollMinSize = new Size(0, bodyContentHeight);
        }

        // 固定标题和底部操作区 只压缩中间滚动视口 工作区与控件尺寸都已经是设备像素
        // 这里不能再套 Theme.S 否则高 DPI 下会二次缩放并重新越界
        internal void FitToWorkingArea(Rectangle workArea)
        {
            if (workArea.Width <= 0 || workArea.Height <= 0) return;

            // 第一次显示永远从设备说明顶部开始 用户已经滚动后跨屏/重排则保留并夹取位置
            bool firstFit = !fittedToWorkArea;
            if (firstFit)
            {
                // CoreMatrix 是第一个可选子控件 Form 已 Show 后直接缩高
                // WinForms 会为了让它可见而自动滚到正文中段 首次拟合前
                // 先把焦点放在固定 footer 正文才能稳定留在顶部
                try { ActiveControl = btnCancel; btnCancel.Select(); } catch { }
            }
            int keepScrollY = fittedToWorkArea
                ? Math.Max(0, -scrollBody.AutoScrollPosition.Y) : 0;
            int chromeW = Math.Max(0, Width - ClientSize.Width);
            int chromeH = Math.Max(0, Height - ClientSize.Height);
            int maxClientW = Math.Max(1, workArea.Width - chromeW);
            int maxClientH = Math.Max(1, workArea.Height - chromeH);
            int targetW = Math.Min(Theme.S(DlgW), maxClientW);
            int targetH = Math.Min(naturalClientHeight, maxClientH);
            if (ClientSize.Width != targetW || ClientSize.Height != targetH)
                ClientSize = new Size(targetW, targetH);
            LayoutViewportAndFooter(keepScrollY);
            if (firstFit)
            {
                try
                {
                    ActiveControl = btnCancel;
                    btnCancel.Select();
                    scrollBody.AutoScrollPosition = Point.Empty;
                    scrollBody.PerformLayout();
                }
                catch { }
            }
            fittedToWorkArea = true;

            int left = Left, top = Top;
            if (left + Width > workArea.Right) left = workArea.Right - Width;
            if (top + Height > workArea.Bottom) top = workArea.Bottom - Height;
            if (left < workArea.Left) left = workArea.Left;
            if (top < workArea.Top) top = workArea.Top;
            if (left != Left || top != Top) Location = new Point(left, top);
        }

        private void OnPicked(ulong mask)
        {
            ulong keep = IrqRelocate.Sanitize(mask);
            Chosen = keep;
            btnOk.Enabled = keep != 0;
            lblPick.Text = keep != 0
                ? Lang.F("irqpin.picked", IrqRelocate.MaskText(keep))
                : Lang.T("irqpin.badpick");
            lblPick.ForeColor = keep != 0 ? Theme.Accent : Theme.Danger;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // base.OnShown 会按 Tab 顺序选中滚动区里的 CoreMatrix 先移到
            // 固定 footer 否则随后的工作区限高会自动跳过顶部风险信息
            try { ActiveControl = btnCancel; btnCancel.Select(); } catch { }
            // 内容自适应后弹窗可能较高:居中显示时把整窗夹回工作区 保证底部开关/按钮不被屏幕边缘切掉
            // 截图路径用 Manual 定位(-20000 离屏) 不参与夹取 免得把离屏窗拽回可见区
            if (StartPosition != FormStartPosition.Manual) ClampToWorkArea();
            clockWasSuspended = UiClock.Borrow();
            Fx.EnterForm(this);
        }

        private void ClampToWorkArea()
        {
            try
            {
                FitToWorkingArea(Screen.FromControl(this).WorkingArea);
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            UiClock.Return(clockWasSuspended);
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
                // SendMessage 在原生移动循环结束后才返回 若从高屏拖到较矮副屏 按目标屏
                // 重新压缩正文视口 不能只依赖首次 OnShown 的那一次夹取
                if (StartPosition != FormStartPosition.Manual) ClampToWorkArea();
            }
        }
    }
}
