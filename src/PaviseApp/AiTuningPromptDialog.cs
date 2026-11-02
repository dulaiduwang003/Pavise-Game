using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class AiTuningPromptDialog : Form
    {
        private readonly string features, display;
        private readonly Func<string, string> readHardware;
        private readonly Action<string> copyText;
        private readonly Func<string> readCopiedText;
        private readonly int featureCount;
        private readonly System.Windows.Forms.Timer copyFeedback;
        private readonly DBPanel content;
        private readonly Label title, description, goalsLabel, status;
        private readonly TextBox goals;
        private readonly TextBox preview;
        private readonly LibraryDialogButton close, refresh, copy;
        private string hardware, completePrompt;
        private bool copying;
        private int readPending, generation;
        private volatile bool ended;
        private bool arranging;

        internal AiTuningPromptDialog(string featureDescription, string displayDeviceName)
            : this(featureDescription, displayDeviceName, AiTuningHardwareReport.Read,
                delegate(string value) { Clipboard.SetText(value, TextDataFormat.UnicodeText); },
                delegate { return Clipboard.GetText(TextDataFormat.UnicodeText); })
        {
        }

        internal AiTuningPromptDialog(string featureDescription, string displayDeviceName,
            Func<string, string> readHardware, Action<string> copyText, Func<string> readCopiedText)
        {
            if (readHardware == null) throw new ArgumentNullException("readHardware");
            if (copyText == null) throw new ArgumentNullException("copyText");
            if (readCopiedText == null) throw new ArgumentNullException("readCopiedText");
            if (string.IsNullOrWhiteSpace(featureDescription)) throw new ArgumentException("Missing feature description.", "featureDescription");
            features = featureDescription ?? "";
            display = displayDeviceName;
            this.readHardware = readHardware;
            this.copyText = copyText;
            this.readCopiedText = readCopiedText;
            featureCount = Regex.Matches(features, @"(?m)^\d+\.\s*").Count;
            Name = "AiTuningPromptDialog";
            Text = Pick("AI 调优提示词", "AI tuning prompt");
            AccessibleName = Text;
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Theme.UI(9f, false);
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            content = new DBPanel { Name = "AiTuningPromptContent", AutoScroll = true, BackColor = Theme.Bg, TabIndex = 0 };
            title = Label(Text, "AiTuningPromptTitle", Theme.UI(15f, true), Theme.Fg);
            description = Label(Pick("读取本机配置，整理全部功能与当前状态。复制全文给 AI，询问适合你的设置。",
                "Read this PC's configuration and list all features and current choices. Copy the complete prompt to your AI for advice."),
                "AiTuningPromptDescription", Theme.UI(9f, false), Theme.Dim);
            goalsLabel = Label(Pick("常玩游戏、游戏设置与调优取舍（可选）", "Games, game settings and tuning preferences (optional)"),
                "AiTuningPromptGoalsLabel", Theme.UI(9f, true), Theme.Fg);
            goals = new TextBox
            {
                Name = "AiPromptContext", Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                MaxLength = 8000, BackColor = Theme.Inset, ForeColor = Theme.Fg, Font = Theme.UI(9f, false),
                BorderStyle = BorderStyle.FixedSingle, TabIndex = 0, AccessibleName = goalsLabel.Text,
                AccessibleDescription = Pick("例如：CS2，1920×1080、不限帧，优先low帧；是否接受增热、功耗或减少安全防护，请自行填写。",
                    "For example: CS2, 1920x1080, uncapped, prioritize low FPS; specify your tradeoffs for heat, power and protection.")
            };
            preview = new TextBox
            {
                Name = "AiPromptPreview", ReadOnly = true, Multiline = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Inset, ForeColor = Theme.Fg, Font = Theme.UI(9f, false),
                HideSelection = false, MaxLength = int.MaxValue, TabIndex = 1,
                AccessibleName = Pick("完整 AI 调优提示词预览", "Complete AI tuning prompt preview")
            };
            status = Label(Pick("打开后读取硬件信息。", "Hardware information is read when this dialog opens."),
                "AiPromptStatus", Theme.UI(8.5f, false), Theme.Dim);
            close = new LibraryDialogButton(Pick("关闭", "Close"), false)
            { Name = "AiPromptClose", DialogResult = DialogResult.Cancel, TabIndex = 2 };
            refresh = new LibraryDialogButton(Pick("重新读取", "Read again"), false)
            { Name = "AiPromptRefresh", TabIndex = 3 };
            copy = new LibraryDialogButton(Pick("复制全部", "Copy all"), true)
            { Name = "AiPromptCopy", Enabled = false, TabIndex = 4 };
            copyFeedback = new System.Windows.Forms.Timer { Interval = 1800 };
            copyFeedback.Tick += delegate { ResetCopyFeedback(); };
            close.Click += delegate { Close(); };
            refresh.Click += delegate { RefreshHardware(); };
            copy.Click += delegate { CopyAll(); };
            goals.TextChanged += delegate { RebuildPreview(); };
            title.MouseDown += DragMove;
            content.Controls.AddRange(new Control[] { title, description, goalsLabel, goals, preview });
            Controls.AddRange(new Control[] { content, status, close, refresh, copy });
            CancelButton = close;
            Native.Dark(content);
            Native.Dark(goals);
            Native.Dark(preview);
            ClientSize = FitSize(Screen.FromPoint(Cursor.Position).WorkingArea);
            Arrange();
        }

        private static string Pick(string chinese, string english)
        { return PerformancePolicy.Text(chinese, english); }

        private static Label Label(string text, string name, Font font, Color color)
        {
            return new Label { Name = name, Text = text, Font = font, ForeColor = color,
                BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false };
        }

        private static Size FitSize(Rectangle available)
        {
            int width = Math.Max(1, Math.Min(Theme.S(850), available.Width - Theme.S(24)));
            int height = Math.Max(1, Math.Min(Theme.S(720), Math.Min(available.Height - Theme.S(24), (int)Math.Floor(available.Height * .88))));
            return new Size(width, height);
        }

        protected override void OnLoad(EventArgs e)
        {
            ClientSize = FitSize(Owner == null ? Screen.FromPoint(Cursor.Position).WorkingArea : Screen.FromControl(Owner).WorkingArea);
            base.OnLoad(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshHardware();
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            if (copy != null) Arrange();
        }

        private void Arrange()
        {
            if (arranging || content == null) return;
            arranging = true;
            try
            {
                int margin = Math.Min(Theme.S(20), Math.Max(0, ClientSize.Width / 20));
                int width = Math.Max(1, ClientSize.Width - 2 * margin);
                int buttonHeight = Math.Min(Theme.S(38), Math.Max(1, ClientSize.Height / 5));
                int bottom = Math.Max(0, ClientSize.Height - margin - buttonHeight);
                int gap = Math.Min(Theme.S(10), Math.Max(0, width / 25));
                int third = Math.Max(1, (width - 2 * gap) / 3);
                close.SetBounds(margin, bottom, third, buttonHeight);
                refresh.SetBounds(close.Right + gap, bottom, third, buttonHeight);
                copy.SetBounds(refresh.Right + gap, bottom, Math.Max(1, ClientSize.Width - margin - refresh.Right - gap), buttonHeight);
                int statusHeight = Math.Min(Theme.S(34), Math.Max(1, bottom / 4));
                status.SetBounds(margin, Math.Max(0, bottom - statusHeight - Theme.S(8)), width, statusHeight);
                content.SetBounds(margin, Theme.S(16), width, Math.Max(1, status.Top - Theme.S(26)));
                Point scroll = new Point(0, -content.AutoScrollPosition.Y);
                content.AutoScrollPosition = Point.Empty;
                int inner = Math.Max(1, width - SystemInformation.VerticalScrollBarWidth - Theme.S(4));
                int titleHeight = Measure(title, inner, Theme.S(32));
                title.SetBounds(0, 0, inner, titleHeight);
                description.SetBounds(0, title.Bottom + Theme.S(5), inner, Measure(description, inner, Theme.S(36)));
                goalsLabel.SetBounds(0, description.Bottom + Theme.S(10), inner, Measure(goalsLabel, inner, Theme.S(22)));
                goals.SetBounds(0, goalsLabel.Bottom + Theme.S(4), inner, Theme.S(46));
                int previewTop = goals.Bottom + Theme.S(12);
                preview.SetBounds(0, previewTop, inner, Math.Max(Theme.S(150), content.ClientSize.Height - previewTop - Theme.S(4)));
                content.AutoScrollMinSize = new Size(0, preview.Bottom + Theme.S(4));
                content.AutoScrollPosition = scroll;
            }
            finally { arranging = false; }
        }

        private static int Measure(Label label, int width, int minimum)
        {
            return Math.Max(minimum, TextRenderer.MeasureText(label.Text, label.Font,
                new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.S(2));
        }

        private void RefreshHardware()
        {
            if (ended || IsDisposed || Disposing || !IsHandleCreated || Interlocked.CompareExchange(ref readPending, 1, 0) != 0) return;
            int request = ++generation;
            ResetCopyFeedback();
            refresh.Enabled = false;
            copy.Enabled = false;
            SetStatus(Pick("正在读取本机配置…", "Reading this PC's configuration…"), false);
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    string result;
                    bool failed = false;
                    try
                    {
                        result = readHardware(display);
                        if (string.IsNullOrWhiteSpace(result)) { result = "硬件信息未读取成功；请保留未知项，不据此推断本机配置。"; failed = true; }
                    }
                    catch { result = "硬件信息读取失败；请保留未知项，不据此推断本机配置。"; failed = true; }
                    bool posted = false;
                    try
                    {
                        if (ended || IsDisposed || Disposing || !IsHandleCreated) return;
                        BeginInvoke((Action)delegate
                        {
                            if (ended || IsDisposed || Disposing || request != generation) return;
                            hardware = result;
                            Interlocked.Exchange(ref readPending, 0);
                            refresh.Enabled = true;
                            RebuildPreview();
                            if (failed) SetStatus(Pick("读取失败；可复制含未知项的提示词，或重新读取。", "Read failed. You can copy the prompt with unknown values or read again."), true);
                        });
                        posted = true;
                    }
                    catch (InvalidOperationException) { }
                    finally { if (!posted) Interlocked.Exchange(ref readPending, 0); }
                }))
                    HardwareQueueFailed();
            }
            catch { HardwareQueueFailed(); }
        }

        private void HardwareQueueFailed()
        {
            Interlocked.Exchange(ref readPending, 0);
            if (ended || IsDisposed || Disposing) return;
            refresh.Enabled = true;
            copy.Enabled = !copying && !string.IsNullOrEmpty(completePrompt);
            SetStatus(Pick("暂时无法读取，请点击重新读取。", "Unable to start reading. Please try Read again."), true);
        }

        private void RebuildPreview()
        {
            if (ended || hardware == null) return;
            ResetCopyFeedback();
            try
            {
                completePrompt = AiTuningPrompt.Build(hardware, features, goals.Text);
                preview.Text = completePrompt;
                preview.SelectionStart = 0;
                preview.SelectionLength = 0;
                preview.ScrollToCaret();
                copy.Enabled = !copying && Volatile.Read(ref readPending) == 0 && completePrompt.Length > 0;
                SetStatus(Pick("已生成 " + completePrompt.Length + " 字符 / " + featureCount + " 条功能说明。",
                    "Ready: " + completePrompt.Length + " characters / " + featureCount + " feature entries."), false);
            }
            catch
            {
                completePrompt = null;
                copy.Enabled = false;
                SetStatus(Pick("提示词生成失败，请重新读取后重试。", "Prompt generation failed. Read again to retry."), true);
            }
        }

        private void CopyAll()
        {
            if (ended || copying || Volatile.Read(ref readPending) != 0 || string.IsNullOrEmpty(completePrompt)) return;
            string original = completePrompt;
            ResetCopyFeedback();
            copying = true;
            copy.Enabled = false;
            copy.Text = Pick("正在复制…", "Copying…");
            SetStatus(Pick("正在写入并核对完整提示词…", "Writing and verifying the complete prompt…"), false);
            copy.Refresh();
            status.Refresh();
            try
            {
                // The preview is a view, never the authoritative copy source.
                copyText(original);
                string actual = readCopiedText();
                if (!string.Equals(original, actual, StringComparison.Ordinal))
                {
                    copy.Text = Pick("重试复制", "Retry copy");
                    SetStatus(Pick("复制完整性校验失败：预期 " + original.Length + " 字符，读回 " + (actual ?? "").Length + " 字符，请重试。",
                        "Copy verification failed: expected " + original.Length + ", received " + (actual ?? "").Length + " characters. Please retry."), true);
                    return;
                }
                copy.Text = Pick("已复制 ✓", "Copied ✓");
                SetStatus(Pick("已核对完整复制：" + original.Length + " 字符 / " + featureCount + " 条说明" + (original.Contains("[AuditResults]") ? "，含体检。" : "。"),
                    "Complete copy verified: " + original.Length + " characters / " + featureCount + " entries."), false);
                copyFeedback.Start();
            }
            catch
            {
                copy.Text = Pick("重试复制", "Retry copy");
                SetStatus(Pick("复制或核对失败，剪贴板可能正忙；请重试。", "Copy or verification failed; the clipboard may be busy. Please retry."), true);
            }
            finally { copying = false; if (!ended) copy.Enabled = Volatile.Read(ref readPending) == 0 && !string.IsNullOrEmpty(completePrompt); }
        }

        private void ResetCopyFeedback()
        {
            if (copyFeedback != null) copyFeedback.Stop();
            if (copy != null && !copy.IsDisposed) copy.Text = Pick("复制全部", "Copy all");
        }

        private void SetStatus(string value, bool warning)
        { status.Text = value; status.ForeColor = warning ? Theme.Warning : Theme.Dim; }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, 161, (IntPtr)2, IntPtr.Zero);
        }

        private void EndRequests()
        { if (ended) return; ended = true; Interlocked.Increment(ref generation); if (copyFeedback != null) copyFeedback.Stop(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        { EndRequests(); base.OnFormClosed(e); }

        protected override void Dispose(bool disposing)
        { if (disposing) { EndRequests(); if (copyFeedback != null) copyFeedback.Dispose(); } base.Dispose(disposing); }

        protected override void OnPaintBackground(PaintEventArgs e)
        { e.Graphics.Clear(Theme.Bg); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outline = new Pen(Theme.StrokeHi))
                e.Graphics.DrawRectangle(outline, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            using (var accent = new Pen(Theme.Accent, Math.Max(1, Theme.S(2))))
            {
                e.Graphics.DrawLine(accent, 1, 1, Math.Min(Width - 1, Theme.S(130)), 1);
                e.Graphics.DrawLine(accent, 1, Math.Max(1, Height - Theme.S(15)), Math.Min(Width - 1, Theme.S(15)), Height - 1);
            }
        }
    }
}
