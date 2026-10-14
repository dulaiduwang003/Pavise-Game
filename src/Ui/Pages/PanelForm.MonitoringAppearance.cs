using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private MonitoringAdjustmentSlider sliderMonitoringScale, sliderMonitoringTransparency, sliderMonitoringX, sliderMonitoringY, sliderMonitoringSpacing;
        private Label lblMonitoringScale, lblMonitoringTransparency, lblMonitoringX, lblMonitoringY, lblMonitoringSpacing;
        private Label lblMonitoringScaleCaption, lblMonitoringPositionHint;
        private PillButton btnMonitoringScreenPreview, btnMonitoringAppearanceReset;
        private MonitoringSelector pickMonitoringLayout, pickMonitoringScreen, pickMonitoringResolutionPreset;
        private MonitoringDesktopPreview monitoringDesktopPreview;
        private int monitoringPreviewScreenIndex;
        private string monitoringPreviewScreenName;
        private Screen[] monitoringPreviewScreens = new Screen[0];
        private int monitoringDraftOpacity, monitoringDraftX, monitoringDraftY, monitoringDraftScale, monitoringDraftSpacing;
        private bool monitoringAppearanceEditing;
        private bool monitoringPositionExpanded;
        private Panel monitoringPositionDetails;
        private PillButton btnMonitoringPositionDetails;
        private bool monitoringDisplaySyncAttached;
        private int monitoringDisplaySyncPending;
        private bool monitoringForegroundCheckPending;
#if PAVISE_SELFTEST
        internal Func<int> MonitoringForegroundProcessForTest;
#endif

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr GetMonitoringForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        private static extern uint GetMonitoringWindowProcessId(IntPtr window, out uint processId);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")]
        private static extern uint GetMonitoringProcessId();

        private bool MonitoringOwnsForeground()
        {
#if PAVISE_SELFTEST
            if (MonitoringForegroundProcessForTest != null)
                return MonitoringForegroundProcessForTest() == GetMonitoringProcessId();
#endif
            IntPtr foreground = GetMonitoringForegroundWindow();
            uint processId;
            return foreground != IntPtr.Zero && GetMonitoringWindowProcessId(foreground, out processId) != 0
                && processId == GetMonitoringProcessId();
        }

        private void QueueMonitoringPreviewForegroundCheck()
        {
            if (monitoringForegroundCheckPending || monitoringDesktopPreview == null || IsDisposed || Disposing || !IsHandleCreated) return;
            monitoringForegroundCheckPending = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate {
                    monitoringForegroundCheckPending = false;
                    if (!IsDisposed && !Disposing && monitoringDesktopPreview != null && !MonitoringOwnsForeground())
                        CloseMonitoringDesktopPreview();
                });
            }
            catch (InvalidOperationException) { monitoringForegroundCheckPending = false; }
        }

        private int BuildMonitoringAppearanceEditor(Control parent, int y)
        {
            monitoringAppearanceEditing = false;
            const int controlsWidth = 280, columnGap = 18;
            int previewWidth = ScrollContentW - 36 - controlsWidth - columnGap;
            int controlsX = 18 + previewWidth + columnGap;
            var editor = MakeConsolePanel(parent, 6, y, ScrollContentW, 316, true);
            monitoringAppearancePanel = editor;
            editor.Name = "monitoringLayoutCard";
            CardLabel(editor, MonitoringText("悬浮窗预览", "Overlay preview"), 18, 13, 300, 25, 10f, true, Theme.Fg);
            CardLabel(editor, MonitoringText("示例数据 · 实时预览", "Sample data · live preview"),
                ScrollContentW - 252, 13, 234, 25, 8.5f, false, Theme.Dim);
            pickMonitoringLayout = new MonitoringSelector { Name = "monitoringLayoutSelector", TabIndex = 0 };
            pickMonitoringLayout.AccessibleName = MonitoringText("悬浮窗布局", "Overlay layout");
            pickMonitoringLayout.SetItems(new[] { MonitoringPreview.LayoutName(MonitoringLayout.TopLeft),
                MonitoringPreview.LayoutName(MonitoringLayout.TopRight), MonitoringPreview.LayoutName(MonitoringLayout.TopBar) });
            pickMonitoringLayout.SetBounds(Theme.S(18), Theme.S(46), Theme.S(140), Theme.S(32));
            pickMonitoringLayout.SelectionChanged += delegate(int index) {
                if (!AllowMonitoringInteraction()) return;
                monitoringAppearanceEditing = false;
                SetMonitoringResult(MonitoringPreferences.TrySetLayout((MonitoringLayout)index));
            };
            editor.Controls.Add(pickMonitoringLayout);
            btnMonitoringAppearanceReset = new PillButton(MonitoringText("恢复外观", "Reset appearance"));
            btnMonitoringAppearanceReset.Name = "monitoringAppearanceReset";
            btnMonitoringAppearanceReset.TabIndex = 4;
            btnMonitoringAppearanceReset.SetBounds(Theme.S(746), Theme.S(46), Theme.S(ScrollContentW - 764), Theme.S(32));
            btnMonitoringAppearanceReset.Click += delegate {
                if (!AllowMonitoringInteraction()) return;
                monitoringAppearanceEditing = false;
                SetMonitoringResult(MonitoringPreferences.TryResetAppearance());
            };
            editor.Controls.Add(btnMonitoringAppearanceReset);

            pickMonitoringScreen = new MonitoringSelector { Name = "monitoringPreviewScreen", TabIndex = 1 };
            pickMonitoringScreen.SetBounds(Theme.S(170), Theme.S(46), Theme.S(234), Theme.S(32));
            pickMonitoringScreen.AccessibleName = MonitoringText("预览显示器", "Preview display");
            pickMonitoringScreen.SelectionChanged += delegate(int index) { SelectMonitoringPreviewScreen(index); };
            editor.Controls.Add(pickMonitoringScreen);
            pickMonitoringResolutionPreset = new MonitoringSelector { Name = "monitoringResolutionPreset", TabIndex = 2 };
            pickMonitoringResolutionPreset.AccessibleName = MonitoringText("分辨率预设", "Resolution preset");
            pickMonitoringResolutionPreset.AccessibleDescription = MonitoringText("按游戏分辨率应用推荐字号和间距，不改变系统分辨率。", "Apply recommended text size and spacing for the game resolution. Display resolution is unchanged.");
            pickMonitoringResolutionPreset.SetItems(new[] { MonitoringText("一键适配", "Quick fit"), "1080p", "2K / 1440p", "4K / 2160p" });
            pickMonitoringResolutionPreset.SetBounds(Theme.S(416), Theme.S(46), Theme.S(132), Theme.S(32));
            pickMonitoringResolutionPreset.SelectionChanged += delegate(int index) {
                if (!AllowMonitoringInteraction() || index < 1 || index > 3) return;
                monitoringAppearanceEditing = false;
                SetMonitoringResult(MonitoringPreferences.TryApplyResolutionPreset(index == 1 ? 1080 : index == 2 ? 1440 : 2160));
            };
            monitoringTips.SetToolTip(pickMonitoringResolutionPreset, pickMonitoringResolutionPreset.AccessibleDescription);
            editor.Controls.Add(pickMonitoringResolutionPreset);
            btnMonitoringScreenPreview = new PillButton(MonitoringText("在屏幕上预览", "Preview on display"));
            btnMonitoringScreenPreview.Name = "monitoringScreenPreview";
            btnMonitoringScreenPreview.TabIndex = 3;
            btnMonitoringScreenPreview.SetBounds(Theme.S(560), Theme.S(46), Theme.S(174), Theme.S(32));
            btnMonitoringScreenPreview.AccessibleDescription = MonitoringText("右键收起屏幕预览。", "Right-click to close the display preview.");
            btnMonitoringScreenPreview.Click += delegate { ToggleMonitoringDesktopPreview(); };
            editor.Controls.Add(btnMonitoringScreenPreview);

            monitoringPreview = new MonitoringPreview { Name = "monitoringPreview" };
            monitoringPreview.SetBounds(Theme.S(18), Theme.S(84), Theme.S(previewWidth), Theme.S(208));
            monitoringPreview.PositionChanging += delegate(int x, int top) { UpdateMonitoringPositionDraft(x, top, true); };
            monitoringPreview.PositionEdited += delegate(int x, int top) { CommitMonitoringPosition(x, top); };
            editor.Controls.Add(monitoringPreview);

            var controls = MakeConsolePanel(editor, controlsX, 84, controlsWidth, 208, false);
            controls.Name = "monitoringAppearanceControls";
            controls.Fill = Theme.CardHover; controls.Border = Color.Empty;
            CardLabel(controls, MonitoringText("外观与位置", "Appearance and position"), 16, 13, controlsWidth - 32, 25, 9.5f, true, Theme.Fg);
            sliderMonitoringScale = BuildMonitoringAdjustmentRow(controls, "monitoringScale", MonitoringText("文字大小", "Text size"),
                40, controlsWidth, MonitoringPreferences.MinimumScalePercent, MonitoringPreferences.MaximumScalePercent, out lblMonitoringScale);
            sliderMonitoringTransparency = BuildMonitoringAdjustmentRow(controls, "monitoringTransparency", MonitoringText("背景透明度", "Background transparency"),
                96, controlsWidth, 0, 100, out lblMonitoringTransparency);
            btnMonitoringPositionDetails = new PillButton("") { Name = "monitoringPositionDetailsToggle" };
            btnMonitoringPositionDetails.SetBounds(Theme.S(16), Theme.S(156), Theme.S(controlsWidth - 32), Theme.S(30));
            btnMonitoringPositionDetails.Click += delegate {
                if (!AllowMonitoringInteraction()) return;
                monitoringPositionExpanded = !monitoringPositionExpanded;
                SyncMonitoringPositionPresentation(); ReflowMonitoringSettings();
            };
            controls.Controls.Add(btnMonitoringPositionDetails);
            lblMonitoringPositionHint = CardLabel(controls, MonitoringText("拖动左侧预览定位", "Drag the preview to position"),
                16, 188, controlsWidth - 32, 20, 8f, false, Theme.Dim);
            monitoringPositionDetails = new Panel { Name = "monitoringPositionDetails", BackColor = Color.Transparent };
            monitoringPositionDetails.SetBounds(Theme.S(18), Theme.S(304), Theme.S(ScrollContentW - 36), Theme.S(96));
            editor.Controls.Add(monitoringPositionDetails);
            int positionWidth = (ScrollContentW - 60) / 3;
            var horizontal = new Panel { Name = "monitoringPositionControls", BackColor = controls.Fill };
            horizontal.SetBounds(0, 0, Theme.S(positionWidth), Theme.S(76));
            monitoringPositionDetails.Controls.Add(horizontal);
            var vertical = new Panel { Name = "monitoringPositionControls", BackColor = controls.Fill };
            vertical.SetBounds(Theme.S(positionWidth + 12), 0, Theme.S(positionWidth), Theme.S(76));
            monitoringPositionDetails.Controls.Add(vertical);
            sliderMonitoringX = BuildMonitoringAdjustmentRow(horizontal, "monitoringPositionX", MonitoringText("水平偏移", "Horizontal offset"),
                10, positionWidth, -100, 100, out lblMonitoringX);
            sliderMonitoringY = BuildMonitoringAdjustmentRow(vertical, "monitoringPositionY", MonitoringText("垂直偏移", "Vertical offset"),
                10, positionWidth, -100, 100, out lblMonitoringY);
            var spacing = new Panel { Name = "monitoringSpacingControls", BackColor = controls.Fill };
            spacing.SetBounds(Theme.S((positionWidth + 12) * 2), 0, Theme.S(positionWidth), Theme.S(76));
            monitoringPositionDetails.Controls.Add(spacing);
            sliderMonitoringSpacing = BuildMonitoringAdjustmentRow(spacing, "monitoringSpacing", MonitoringText("指标间距", "Metric spacing"),
                10, positionWidth, MonitoringPreferences.MinimumSpacingPercent, MonitoringPreferences.MaximumSpacingPercent, out lblMonitoringSpacing);
            sliderMonitoringSpacing.ValueChanged += delegate {
                if (!AllowMonitoringInteraction()) return;
                monitoringAppearanceEditing = true; monitoringDraftSpacing = sliderMonitoringSpacing.Value;
                UpdateMonitoringAppearanceDraft(true);
            };
            sliderMonitoringSpacing.ValueCommitted += delegate {
                if (!AllowMonitoringInteraction()) return;
                monitoringAppearanceEditing = false;
                SetMonitoringResult(MonitoringPreferences.TrySetSpacingPercent(monitoringDraftSpacing));
            };
            CardLabel(monitoringPositionDetails, MonitoringText("拖动预览或使用方向键微调位置", "Drag the preview or use arrow keys to fine-tune position"),
                0, 78, ScrollContentW - 36, 18, 8f, false, Theme.Dim);
            sliderMonitoringScale.ValueChanged += delegate {
                if (!AllowMonitoringInteraction()) return;
                monitoringAppearanceEditing = true; monitoringDraftScale = sliderMonitoringScale.Value;
                UpdateMonitoringAppearanceDraft(true);
            };
            sliderMonitoringTransparency.ValueChanged += delegate {
                if (!AllowMonitoringInteraction()) return;
                monitoringAppearanceEditing = true; monitoringDraftOpacity = 100 - sliderMonitoringTransparency.Value;
                UpdateMonitoringAppearanceDraft(true);
            };
            sliderMonitoringX.ValueChanged += delegate { UpdateMonitoringPositionDraft(sliderMonitoringX.Value * 10, monitoringDraftY, true); };
            sliderMonitoringY.ValueChanged += delegate { UpdateMonitoringPositionDraft(monitoringDraftX, sliderMonitoringY.Value * 10, true); };
            sliderMonitoringScale.ValueCommitted += delegate {
                if (!AllowMonitoringInteraction()) return;
                monitoringAppearanceEditing = false;
                SetMonitoringResult(MonitoringPreferences.TrySetScalePercent(monitoringDraftScale));
            };
            sliderMonitoringTransparency.ValueCommitted += delegate {
                if (!AllowMonitoringInteraction()) return;
                monitoringAppearanceEditing = false;
                SetMonitoringResult(MonitoringPreferences.TrySetBackgroundOpacity(monitoringDraftOpacity));
            };
            sliderMonitoringX.ValueCommitted += delegate { CommitMonitoringPosition(monitoringDraftX, monitoringDraftY); };
            sliderMonitoringY.ValueCommitted += delegate { CommitMonitoringPosition(monitoringDraftX, monitoringDraftY); };
            if (!monitoringDisplaySyncAttached)
            {
                monitoringDisplaySyncAttached = true;
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnMonitoringDisplaySettingsChanged;
                Disposed += delegate {
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnMonitoringDisplaySettingsChanged;
                    monitoringDisplaySyncAttached = false;
                };
            }
            SyncMonitoringPositionPresentation();
            return y + 326;
        }

        private void SyncMonitoringPositionPresentation()
        {
            monitoringPositionDetails.Visible = monitoringPositionExpanded;
            monitoringAppearancePanel.Height = Theme.S(monitoringPositionExpanded ? 412 : 316);
            btnMonitoringPositionDetails.Text = monitoringPositionExpanded
                ? MonitoringText("收起微调 ∧", "Hide layout controls ∧") : MonitoringText("布局微调 ∨", "Fine-tune layout ∨");
            btnMonitoringPositionDetails.AccessibleName = btnMonitoringPositionDetails.Text;
        }

        private void OnMonitoringDisplaySettingsChanged(object sender, EventArgs args)
        {
            if (IsDisposed || Disposing || runtimeShutdownBusy || !IsHandleCreated
                || System.Threading.Interlocked.CompareExchange(ref monitoringDisplaySyncPending, 1, 0) != 0) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate {
                    System.Threading.Interlocked.Exchange(ref monitoringDisplaySyncPending, 0);
                    if (IsDisposed || Disposing || runtimeShutdownBusy || monitoringPreview == null || monitoringPreview.IsDisposed) return;
                    // 两个预览共用同一次显示器回退选择。
                    Screen selected = SelectedMonitoringPreviewScreen();
                    if (monitoringDesktopPreview != null && !monitoringDesktopPreview.IsDisposed)
                        ShowMonitoringDesktopPreview(monitoringDesktopPreview, selected);
                    SyncMonitoringAppearance();
                });
            }
            catch (InvalidOperationException) { System.Threading.Interlocked.Exchange(ref monitoringDisplaySyncPending, 0); }
        }

        private MonitoringAdjustmentSlider BuildMonitoringAdjustmentRow(Control editor, string name, string caption,
            int y, int width, int minimum, int maximum, out Label value)
        {
            Label heading = CardLabel(editor, caption, 16, y, width - 112, 22, 8.5f, false, Theme.Dim);
            heading.TextAlign = ContentAlignment.MiddleLeft;
            heading.Name = name + "Caption";
            if (name == "monitoringScale") lblMonitoringScaleCaption = heading;
            var slider = new MonitoringAdjustmentSlider { Name = name, Minimum = minimum, Maximum = maximum, AccessibleName = caption };
            slider.SetBounds(Theme.S(6), Theme.S(y + 23), Theme.S(width - 12), Theme.S(28));
            editor.Controls.Add(slider);
            value = CardLabel(editor, "", width - 88, y - 1, 72, 24, 11f, true, Theme.Accent);
            value.Name = name + "Value"; value.TextAlign = ContentAlignment.MiddleRight;
            return slider;
        }

        private void SyncMonitoringAppearance()
        {
            if (monitoringPreview == null || sliderMonitoringTransparency == null) return;
            Screen screen = SelectedMonitoringPreviewScreen();
            monitoringPreview.SetViewport(screen.Bounds.Width, screen.Bounds.Height);
            if (!monitoringAppearanceEditing)
                MonitoringPreferences.GetAppearance(out monitoringDraftOpacity, out monitoringDraftX, out monitoringDraftY, out monitoringDraftScale, out monitoringDraftSpacing);
            UpdateMonitoringAppearanceDraft(true);
            Screen[] screens = Screen.AllScreens;
            monitoringPreviewScreens = screens;
            var screenLabels = new string[screens.Length];
            for (int i = 0; i < screens.Length; i++)
                screenLabels[i] = MonitoringText("屏幕 ", "Display ") + (i + 1) + " · "
                    + screens[i].Bounds.Width + " × " + screens[i].Bounds.Height;
            pickMonitoringScreen.SetItems(screenLabels);
            pickMonitoringScreen.SetSilently(monitoringPreviewScreenIndex);
            pickMonitoringScreen.AccessibleDescription = MonitoringText("选择预览所在的显示器：", "Choose the preview display: ") + screen.DeviceName;
            pickMonitoringScreen.Enabled = screens.Length > 1 && MonitoringControlsAvailable;
            btnMonitoringScreenPreview.Text = monitoringDesktopPreview != null && !monitoringDesktopPreview.IsDisposed
                ? MonitoringText("收起屏幕预览", "Close display preview") : MonitoringText("在屏幕上预览", "Preview on display");
            btnMonitoringScreenPreview.Kind = monitoringDesktopPreview != null && !monitoringDesktopPreview.IsDisposed ? BtnKind.Primary : BtnKind.Normal;
            btnMonitoringScreenPreview.Enabled = MonitoringControlsAvailable;
        }

        private void UpdateMonitoringAppearanceDraft(bool desktop)
        {
            if (sliderMonitoringTransparency == null || sliderMonitoringTransparency.IsDisposed) return;
            bool centered = MonitoringPreferences.Layout == MonitoringLayout.TopBar;
            string scaleCaption = MonitoringText("文字大小", "Text size");
            lblMonitoringScaleCaption.Text = scaleCaption;
            sliderMonitoringScale.AccessibleName = scaleCaption;
            lblMonitoringPositionHint.Text = MonitoringText("拖动预览定位 · 方向键微调", "Drag to position · Arrow keys");
            monitoringTips.SetToolTip(monitoringPreview, MonitoringText("位置示意预览，点击屏幕预览查看实际字号。", "Position thumbnail. Use Preview on display to see the actual text size."));
            sliderMonitoringScale.SetSilently(monitoringDraftScale);
            sliderMonitoringSpacing.SetSilently(monitoringDraftSpacing);
            lblMonitoringSpacing.Text = monitoringDraftSpacing + "%";
            int preset = monitoringDraftScale == 60 && monitoringDraftSpacing == 85 ? 1
                : monitoringDraftScale == 65 && monitoringDraftSpacing == 90 ? 2 : monitoringDraftScale == 80 && monitoringDraftSpacing == 100 ? 3 : 0;
            pickMonitoringResolutionPreset.SetSilently(preset);
            sliderMonitoringTransparency.SetSilently(100 - monitoringDraftOpacity);
            sliderMonitoringX.SetSilently(centered ? 0 : (int)Math.Round(monitoringDraftX / 10.0));
            sliderMonitoringX.Enabled = !centered;
            sliderMonitoringX.AccessibleDescription = centered ? MonitoringText("顶部横条始终水平居中。", "The top bar stays horizontally centered.") : "";
            sliderMonitoringY.SetSilently((int)Math.Round(monitoringDraftY / 10.0));
            lblMonitoringScale.Text = monitoringDraftScale + "%";
            lblMonitoringTransparency.Text = (100 - monitoringDraftOpacity) + "%";
            lblMonitoringX.Text = centered ? MonitoringText("居中", "Centered")
                : (monitoringDraftX / 10.0).ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.InvariantCulture) + "%";
            lblMonitoringX.Width = Theme.S(centered ? 100 : 72);
            lblMonitoringX.Left = lblMonitoringX.Parent.ClientSize.Width - Theme.S(16) - lblMonitoringX.Width;
            Control horizontalCaption = sliderMonitoringX.Parent.Controls["monitoringPositionXCaption"];
            horizontalCaption.Width = lblMonitoringX.Left - horizontalCaption.Left - Theme.S(8);
            lblMonitoringX.Font = Theme.UI(centered ? 9f : 11f, true);
            lblMonitoringX.ForeColor = centered ? Theme.Dim : Theme.Accent;
            lblMonitoringY.Text = (monitoringDraftY / 10.0).ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.InvariantCulture) + "%";
            monitoringPreview.SetPreferences(MonitoringPreferences.Layout, MonitoringPreferences.Metrics,
                monitoringDraftOpacity, centered ? 0 : monitoringDraftX, monitoringDraftY, monitoringDraftScale, monitoringDraftSpacing);
            if (desktop && monitoringDesktopPreview != null && !monitoringDesktopPreview.IsDisposed)
                monitoringDesktopPreview.UpdatePreview(MonitoringPreferences.Layout, MonitoringPreferences.Metrics,
                    monitoringDraftOpacity, centered ? 0 : monitoringDraftX, monitoringDraftY, null, monitoringDraftScale, monitoringDraftSpacing);
        }

        private void UpdateMonitoringPositionDraft(int x, int y, bool desktop)
        {
            if (!AllowMonitoringInteraction()) return;
            monitoringAppearanceEditing = true;
            if (MonitoringPreferences.Layout != MonitoringLayout.TopBar) monitoringDraftX = x;
            monitoringDraftY = y;
            UpdateMonitoringAppearanceDraft(desktop);
        }
        private void CommitMonitoringPosition(int x, int y)
        {
            if (!AllowMonitoringInteraction()) return;
            monitoringAppearanceEditing = false;
            SetMonitoringResult(MonitoringPreferences.TrySetPosition(
                MonitoringPreferences.Layout == MonitoringLayout.TopBar ? MonitoringPreferences.OffsetX : x, y));
        }
        private Screen SelectedMonitoringPreviewScreen()
        {
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
                if (screens[i].DeviceName == monitoringPreviewScreenName) { monitoringPreviewScreenIndex = i; return screens[i]; }
            Screen chosen = IsHandleCreated ? Screen.FromControl(this) : Screen.PrimaryScreen;
            for (int i = 0; i < screens.Length; i++) if (screens[i].DeviceName == chosen.DeviceName) monitoringPreviewScreenIndex = i;
            monitoringPreviewScreenName = chosen.DeviceName;
            return chosen;
        }
        private void SelectMonitoringPreviewScreen(int index)
        {
            if (!AllowMonitoringInteraction()) return;
            if (index < 0 || index >= monitoringPreviewScreens.Length) { SyncMonitoringAppearance(); return; }
            string deviceName = monitoringPreviewScreens[index].DeviceName;
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
                if (screens[i].DeviceName == deviceName)
                {
                    monitoringPreviewScreenIndex = i; monitoringPreviewScreenName = deviceName;
                    if (monitoringDesktopPreview != null && !monitoringDesktopPreview.IsDisposed)
                        ShowMonitoringDesktopPreview(monitoringDesktopPreview, screens[i]);
                    break;
                }
            SyncMonitoringAppearance();
        }
        private void ShowMonitoringDesktopPreview(MonitoringDesktopPreview preview, Screen screen)
        {
            // 显示模式变更不能把编辑预览置于游戏之上。
            if (!MonitoringOwnsForeground()) { CloseMonitoringDesktopPreview(); return; }
#if PAVISE_SELFTEST
            preview.ShowPreview(screen, true);
#else
            preview.ShowPreview(screen);
#endif
        }
        private void ToggleMonitoringDesktopPreview()
        {
            if (runtimeShutdownBusy || IsDisposed || Disposing) return;
            if (!AllowMonitoringInteraction()) return;
            if (monitoringDesktopPreview != null && !monitoringDesktopPreview.IsDisposed) { CloseMonitoringDesktopPreview(); return; }
            var preview = new MonitoringDesktopPreview();
            monitoringDesktopPreview = preview;
            preview.PositionChanging += delegate(int x, int y) { UpdateMonitoringPositionDraft(x, y, false); };
            preview.PositionEdited += delegate(int x, int y) { CommitMonitoringPosition(x, y); };
            preview.PreviewClosed += delegate {
                if (monitoringDesktopPreview == preview) monitoringDesktopPreview = null;
                monitoringAppearanceEditing = false;
                if (!IsDisposed && !Disposing && monitoringPreview != null && !monitoringPreview.IsDisposed) SyncMonitoringAppearance();
            };
            try { UpdateMonitoringAppearanceDraft(true); ShowMonitoringDesktopPreview(preview, SelectedMonitoringPreviewScreen()); SyncMonitoringAppearance(); }
            catch
            {
                CloseMonitoringDesktopPreview();
                monitoringMessage = MonitoringText("屏幕预览无法显示，请重试。", "The display preview could not open. Try again.");
                monitoringMessageError = true; SyncMonitoringSettings();
            }
        }
        private void CloseMonitoringDesktopPreview()
        {
            CloseMonitoringSelectors();
            MonitoringDesktopPreview preview = monitoringDesktopPreview;
            monitoringDesktopPreview = null; monitoringAppearanceEditing = false;
            if (preview != null) preview.Dispose();
            if (btnMonitoringScreenPreview != null && !btnMonitoringScreenPreview.IsDisposed)
            { btnMonitoringScreenPreview.Text = MonitoringText("在屏幕上预览", "Preview on display"); btnMonitoringScreenPreview.Kind = BtnKind.Normal; }
        }
        private void CloseMonitoringSelectors()
        {
            if (pickMonitoringLayout != null && !pickMonitoringLayout.IsDisposed) pickMonitoringLayout.CloseDropDown();
            if (pickMonitoringScreen != null && !pickMonitoringScreen.IsDisposed) pickMonitoringScreen.CloseDropDown();
            if (pickMonitoringResolutionPreset != null && !pickMonitoringResolutionPreset.IsDisposed) pickMonitoringResolutionPreset.CloseDropDown();
        }
    }
}
