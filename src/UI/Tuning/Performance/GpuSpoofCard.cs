using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class GpuSpoofCard : PerformanceFeatureCard
    {
        private const int SettleSeconds = 15;
        private static int changeBusy;
        private static int systemRestartRequired;
        private static event Action<GpuSpoofSnapshot> PanelStateChanged;

        private readonly PerformanceProfilePicker gpuPicker;
        private readonly LinkLabel retryLink;
        private readonly System.Windows.Forms.Timer gameStateTimer = new System.Windows.Forms.Timer { Interval = 1000 };

        private GpuSpoofSnapshot last;
        private int reading;
        private bool refreshPending;
        private int readingGameState;
        private bool? gameBusy;
        private int snapshotVersion;
        private int gameProbeVersion;
        private bool pageActive;
        private volatile bool displayActive;
        private bool retryAvailable;
        private Form window;

        internal Func<bool> SessionBlocked { get; set; }
        internal PerformanceProfilePicker GpuPicker { get { return gpuPicker; } }
        internal LinkLabel RetryLink { get { return retryLink; } }
        protected override bool AlwaysShowStatus { get { return true; } }
        protected override LinkLabel StatusAction { get { return retryLink; } }
        protected override bool ShowStatusAction { get { return retryAvailable; } }
        internal override string EffectText
        {
            get { return Lang.T("env.gpu.spoof.effect") + "\r\n\r\n" + Lang.T("env.gpu.spoof.shader.tip"); }
        }

        private bool DisplayActive { get { return pageActive && Visible && IsHandleCreated && !Disposing && !IsDisposed; } }

#if PAVISE_SELFTEST
        internal Func<string, string, DlgKind, bool> ConfirmForTest;
        internal Action<string, string, bool> ShowResultForTest;
        internal bool GameStatusPollEnabledForTest { get { return gameStateTimer.Enabled; } }
        internal GpuSpoofSnapshot SnapshotForTest { get { return last; } }
#endif

        internal GpuSpoofCard()
        {
            Name = "EnvironmentGpuSpoofCard";
            Title = Lang.T("env.gpu.spoof.toggle");
            Desc = Lang.T("env.gpu.spoof.gpu.tip");
            IconKey = "GpuSpoof";
            CompactRow = true;
            PickerInHeader = true;
            gpuPicker = new PerformanceProfilePicker(new[] { 0, 1, 2, 3, 4, 5, 6 }, OptionTitle, OptionDescription,
                () => Lang.T("env.gpu.spoof.choose"), -1, PickerCaption, CanSelectOption)
            {
                Name = "environmentGpuSpoof",
                AccessibleName = Title,
                Enabled = false
            };
            Host(gpuPicker);
            gpuPicker.SelectionChanged += delegate { RequestGpuAction((GpuSpoofTier)gpuPicker.SelectedIndex); };
            retryLink = new LinkLabel
            {
                Name = "environmentGpuSpoofRetry",
                Text = Lang.T("env.gpu.spoof.retry"),
                AccessibleName = Lang.T("env.gpu.spoof.retry"),
                AccessibleRole = AccessibleRole.Link,
                Font = Theme.UI(8f, false),
                BackColor = Color.Transparent,
                UseMnemonic = false,
                LinkBehavior = LinkBehavior.AlwaysUnderline,
                TabIndex = 2,
                Visible = false
            };
            retryLink.LinkClicked += delegate { RequestGpuAction(null); };
            Controls.Add(retryLink);
            TrackChildHover(retryLink);
            gameStateTimer.Tick += delegate { QueueGameStateRead(); };
            PanelStateChanged += OnPanelStateChanged;
            SyncControls();
        }

        internal void SetPageActive(bool active)
        {
            if (pageActive == active) return;
            pageActive = active;
            UpdateDisplayActivity();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateDisplayActivity();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            window = FindForm();
            if (window != null) window.VisibleChanged += WindowVisibilityChanged;
            UpdateDisplayActivity();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (window != null) window.VisibleChanged -= WindowVisibilityChanged;
            window = null;
            displayActive = false;
            gameStateTimer.Stop();
            gameBusy = null;
            gameProbeVersion++;
            base.OnHandleDestroyed(e);
        }

        private void WindowVisibilityChanged(object sender, EventArgs e)
        {
            UpdateDisplayActivity();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pageActive = false;
                displayActive = false;
                PanelStateChanged -= OnPanelStateChanged;
                gameStateTimer.Stop();
            }
            base.Dispose(disposing);
            if (disposing) gameStateTimer.Dispose();
        }

        private void UpdateDisplayActivity()
        {
            if (gpuPicker == null || retryLink == null || IsDisposed) return;
            bool active = DisplayActive;
            if (active == displayActive) return;
            displayActive = active;
            gameProbeVersion++;
            gameBusy = null;
            if (!active)
            {
                gameStateTimer.Stop();
                SyncControls();
                return;
            }
            last = null;
            snapshotVersion++;
            SyncControls();
            gameStateTimer.Start();
            QueueRead(true);
            QueueGameStateRead();
        }

        private void OnPanelStateChanged(GpuSpoofSnapshot snapshot)
        {
            if (!DisplayActive) return;
            if (snapshot != null)
            {
                last = snapshot;
                snapshotVersion++;
            }
            SyncControls();
        }

        private static void PublishPanelState(GpuSpoofSnapshot snapshot)
        {
            Action<GpuSpoofSnapshot> handlers = PanelStateChanged;
            if (handlers != null) handlers(snapshot);
        }

        internal static bool NeedsAdapterRestart(GpuSpoofSnapshot s)
        {
            if (s == null || !s.CanRestart || !string.IsNullOrEmpty(s.Error)) return false;
            if (s.RecoveryRestartPending) return true;
            if (s.ReadbackMatches.HasValue) return !s.ReadbackMatches.Value;
            return s.On || s.GameSeesFake == true;
        }

        // 打开卡片时在后台刷新清单
        private void QueueRead(bool refreshInventory = false)
        {
            if (!DisplayActive) return;
            if (refreshInventory) refreshPending = true;
            if (Interlocked.CompareExchange(ref reading, 1, 0) != 0) return;
            bool refresh = refreshPending;
            int version = snapshotVersion;
            refreshPending = false;
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    GpuSpoofSnapshot snapshot;
                    try { snapshot = GpuSpoof.Read(refresh); }
                    catch (Exception ex) { snapshot = new GpuSpoofSnapshot { Error = ex.Message }; }
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            Interlocked.Exchange(ref reading, 0);
                            if (base.IsDisposed || !DisplayActive) return;
                            if (refreshPending)
                            {
                                QueueRead();
                                return;
                            }
                            if (version == snapshotVersion)
                            {
                                last = snapshot;
                                SyncControls();
                            }
                        });
                    }
                    catch
                    {
                        Interlocked.Exchange(ref reading, 0);
                    }
                }))
                {
                    Interlocked.Exchange(ref reading, 0);
                }
            }
            catch
            {
                Interlocked.Exchange(ref reading, 0);
            }
        }

        private void QueueGameStateRead()
        {
            if (!DisplayActive || Interlocked.CompareExchange(ref readingGameState, 1, 0) != 0) return;
            int version = gameProbeVersion;
            try
            {
                if (ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        Func<bool> sessionBlocked = SessionBlocked;
                        bool blocked = (sessionBlocked != null && sessionBlocked()) || GpuSpoof.GameRunning();
                        BeginInvoke((MethodInvoker)delegate
                        {
                            Interlocked.Exchange(ref readingGameState, 0);
                            if (!DisplayActive) return;
                            if (version != gameProbeVersion)
                            {
                                QueueGameStateRead();
                                return;
                            }
                            if (gameBusy == blocked) return;
                            gameBusy = blocked;
                            SyncControls();
                        });
                    }
                    catch
                    {
                        Interlocked.Exchange(ref readingGameState, 0);
                    }
                })) return;
            }
            catch
            {
            }
            Interlocked.Exchange(ref readingGameState, 0);
        }

        private void SyncControls()
        {
            bool busy = Volatile.Read(ref changeBusy) != 0;
            bool rebootRequired = Volatile.Read(ref systemRestartRequired) != 0;
            bool ready = !busy && !rebootRequired && gameBusy == false && last != null && string.IsNullOrEmpty(last.Error);
            if (last == null) gpuPicker.SetSilently((int)GpuSpoofTier.None);
            else if (string.IsNullOrEmpty(last.Error))
                gpuPicker.SetSilently(last.SelectedTier.HasValue ? (int)last.SelectedTier.Value : -1);
            gpuPicker.Enabled = ready && (last.Available || last.CanRestore);
            bool showRetry = ready && NeedsAdapterRestart(last);
            bool retryChanged = retryAvailable != showRetry;
            retryAvailable = showRetry;
            retryLink.Visible = showRetry;
            retryLink.Enabled = showRetry;
            retryLink.LinkColor = Theme.Accent;
            retryLink.VisitedLinkColor = Theme.Accent;
            retryLink.ActiveLinkColor = Theme.Fg;

            string state;
            Color color;
            StateOf(last, busy, gameBusy, rebootRequired, out state, out color);
            string names = StatusNames(last);
            string status = names.Length == 0 ? state : state + Environment.NewLine + names;
            gpuPicker.AccessibleDescription = gpuPicker.Enabled
                ? OptionTitle(gpuPicker.SelectedIndex) + Environment.NewLine + Desc : state;
            retryLink.AccessibleDescription = state;
            SetStatus(status, color);
            if (retryChanged && PresentationChanged != null) PresentationChanged();
            ArrangePresentation();
        }

        private static string OptionFallbackLabel(int index)
        {
            if (index < 0) return Lang.T("env.gpu.spoof.tier.mixed");
            return Lang.T(index == 0 ? "env.gpu.spoof.tier.none" : "env.gpu.spoof.name.unknown");
        }

        private string OptionTitle(int index)
        {
            List<string> names = OptionNames(index);
            return names.Count == 0 ? OptionFallbackLabel(index) : string.Join(Environment.NewLine, names);
        }

        private string PickerCaption(int index)
        {
            List<string> names = OptionNames(index);
            if (names.Count == 0) return OptionFallbackLabel(index);

            const string nvidia = "NVIDIA GeForce ";
            const string amd = "AMD ";
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (name.StartsWith(nvidia, StringComparison.Ordinal)) names[i] = name.Substring(nvidia.Length);
                else if (name.StartsWith(amd, StringComparison.Ordinal)) names[i] = name.Substring(amd.Length);
            }
            return string.Join(" / ", names);
        }

        private List<string> OptionNames(int index)
        {
            var names = new List<string>();
            if (index <= 0 || last == null) return names;
            GpuSpoofTier tier = (GpuSpoofTier)index;
            foreach (bool amd in new[] { false, true })
            foreach (GpuSpoofDevice device in last.Devices)
            {
                if (device.Amd != amd || (!device.Target && !device.Spoofed)) continue;
                string name = last.SelectedTier == tier && device.Spoofed && !string.IsNullOrEmpty(device.CurrentName)
                    ? device.CurrentName : GpuSpoof.TargetName(amd, last.Mobile, tier);
                if (!names.Contains(name)) names.Add(name);
            }
            return names;
        }

        private bool CanSelectOption(int index)
        {
            return index == 0 || (index > 0 && index <= (int)GpuSpoofTier.Flagship && last != null
                && last.IsTierAvailable((GpuSpoofTier)index));
        }

        private string OptionDescription(int index)
        {
            if (index < 0) return Lang.T("env.gpu.spoof.tier.mixed");
            if (index == 0) return Lang.T("env.gpu.spoof.tier.restore");
            if (CanSelectOption(index) || last == null) return "";
            return Lang.T(last.Available ? "env.gpu.spoof.gpu.err.same"
                : last.Uncertain ? "env.gpu.spoof.state.uncertain" : "env.gpu.spoof.state.none");
        }

        private bool NeedsTierChange(GpuSpoofTier tier)
        {
            if (tier == GpuSpoofTier.None) return last.CanRestore;
            return CanSelectOption((int)tier) && (last.SelectedTier != tier
                || Array.Exists(last.Devices, device => GpuSpoof.CanUseTier(device, last.Mobile, tier)
                    && !string.Equals(device.CurrentName, GpuSpoof.TargetName(device.Amd, last.Mobile, tier),
                        StringComparison.OrdinalIgnoreCase)));
        }

        private bool SessionBlocksChange()
        {
            if (SessionBlocked == null || !SessionBlocked()) return false;
            gameBusy = true;
            SyncControls();
            return true;
        }

        private void RequestGpuAction(GpuSpoofTier? tier)
        {
            if (!DisplayActive || last == null || gameBusy != false || SessionBlocksChange()
                || (tier.HasValue ? !gpuPicker.Enabled || !NeedsTierChange(tier.Value) : !retryAvailable)
                || Interlocked.CompareExchange(ref changeBusy, 1, 0) != 0)
            {
                SyncControls();
                return;
            }
            GpuSpoofSnapshot before = last;
            Control focus = gpuPicker.Focused ? gpuPicker : retryLink.Focused ? (Control)retryLink : null;
            SynchronizationContext dispatcher = SynchronizationContext.Current;
            SyncControls();
            PublishPanelState(null);
            // 等菜单关闭后再打开确认框
            if (!PostToUi(dispatcher, delegate { ConfirmGpuAction(tier, before, dispatcher, focus); }))
            {
                FinishChange(null, focus);
                gpuPicker.Enabled = false;
                QueueRead();
            }
        }

        private bool PostToUi(SynchronizationContext dispatcher, MethodInvoker callback)
        {
            try
            {
                if (dispatcher != null) dispatcher.Post(delegate { callback(); }, null);
                else BeginInvoke(callback);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void ConfirmGpuAction(GpuSpoofTier? tier, GpuSpoofSnapshot before, SynchronizationContext dispatcher, Control focus)
        {
            if (!DisplayActive || gameBusy != false || SessionBlocksChange())
            {
                FinishChange(null, focus);
                return;
            }
            string title = Lang.T(!tier.HasValue ? "env.gpu.spoof.retry"
                : tier == GpuSpoofTier.None ? "env.gpu.spoof.gpu.restore.title" : "env.gpu.spoof.gpu.enable.title");
            string prompt = Lang.T("env.gpu.spoof.gpu.confirm");
            const DlgKind kind = DlgKind.Info;
            bool confirmed;
            try
            {
#if PAVISE_SELFTEST
                if (ConfirmForTest != null) confirmed = ConfirmForTest(title, prompt, kind);
                else
#endif
                confirmed = PaviseDialog.Confirm(FindForm(), title, prompt, kind);
            }
            catch
            {
                FinishChange(null, focus);
                throw;
            }
            if (!confirmed || !DisplayActive || gameBusy != false || SessionBlocksChange())
            {
                FinishChange(null, focus);
                return;
            }

            RunGpuAction(tier, title, before, dispatcher, focus);
        }

        private static void FinishChange(GpuSpoofSnapshot snapshot, Control focus)
        {
            Interlocked.Exchange(ref changeBusy, 0);
            PublishPanelState(snapshot);
            if (focus != null && focus.CanFocus && Form.ActiveForm == focus.FindForm()) focus.Focus();
        }

        private void RunGpuAction(GpuSpoofTier? tier, string title, GpuSpoofSnapshot before,
            SynchronizationContext dispatcher, Control focus)
        {
            Func<bool> sessionBlocked = SessionBlocked;
            try
            {
                if (ThreadPool.QueueUserWorkItem(delegate
                {
                    GpuSpoofSnapshot snapshot = null;
                    GpuSpoofSnapshot written = null;
                    string error = "";
                    bool rebootRequired = false, restarted = false;
                    try
                    {
                        restarted = tier.HasValue
                            ? GpuSpoof.ChangeTier(tier.Value, out rebootRequired, out error, sessionBlocked)
                            : GpuSpoof.RestartAdapters(out rebootRequired, out error, sessionBlocked);
                        snapshot = written = GpuSpoof.Read();
                        for (int second = 0; displayActive && restarted && !rebootRequired
                            && !ReadbackConfirmed(snapshot) && second < SettleSeconds; second++)
                        {
                            Thread.Sleep(1000);
                            if (!displayActive) break;
                            snapshot = GpuSpoof.Read();
                        }
                        if (!restarted && string.IsNullOrEmpty(error)) error = Lang.T("env.gpu.spoof.state.error");
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        snapshot = GpuSpoof.Read();
                    }

                    if (rebootRequired) Volatile.Write(ref systemRestartRequired, 1);
                    if (!PostToUi(dispatcher, delegate
                    {
                        FinishChange(snapshot, focus);
                        if (!DisplayActive) return;
                        QueueGameStateRead();
                        string message = ResultText(before, written, tier, restarted, rebootRequired, error, snapshot);
                        bool warning = !string.IsNullOrEmpty(error) || rebootRequired || !ReadbackConfirmed(snapshot);
#if PAVISE_SELFTEST
                        if (ShowResultForTest != null)
                        {
                            ShowResultForTest(title, message, warning);
                            return;
                        }
#endif
                        if (warning) PaviseDialog.Warn(FindForm(), title, message);
                        else PaviseDialog.Info(FindForm(), title, message);
                    })) Interlocked.Exchange(ref changeBusy, 0);
                })) return;
            }
            catch (InvalidOperationException)
            {
            }
            FinishChange(null, focus);
        }

        private static bool ReadbackConfirmed(GpuSpoofSnapshot snapshot)
        {
            return snapshot != null && snapshot.ReadbackMatches == true && string.IsNullOrEmpty(snapshot.Error);
        }

        private static string NamePair(string from, string to)
        {
            if (string.IsNullOrEmpty(to) || string.Equals(from, to, StringComparison.Ordinal)) return from;
            if (string.IsNullOrEmpty(from)) return to;
            return from + " → " + to;
        }

        internal static string ResultText(GpuSpoofSnapshot before, GpuSpoofSnapshot written, GpuSpoofTier? tier,
            bool restarted, bool rebootRequired, string error, GpuSpoofSnapshot snapshot)
        {
            bool confirmed = ReadbackConfirmed(snapshot);
            bool apply = tier.HasValue && tier != GpuSpoofTier.None;
            bool restore = tier == GpuSpoofTier.None;
            List<string> names = new List<string>();
            if (written != null)
            {
                foreach (GpuSpoofDevice device in written.Devices)
                {
                    if (apply && !device.Target && !device.Spoofed) continue;
                    if (restore && before != null
                        && !Array.Exists(before.Devices, prior => prior.CanRestore
                            && string.Equals(prior.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase))
                        && device.Target && !restarted) continue;
                    GpuSpoofDevice seen = snapshot == null ? null : Array.Find(snapshot.Devices,
                        current => string.Equals(current.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase));
                    string name = confirmed && seen != null ? seen.VisibleName : device.CurrentName;
                    names.Add(NamePair(device.RealName, name));
                }
            }

            string list = string.Join("\n", names);
            string message;
            if (!string.IsNullOrEmpty(error))
                message = Lang.F("env.gpu.spoof.gpu.failed", error) + (list.Length > 0 ? "\n\n" + list : "");
            else if (rebootRequired)
                message = Lang.F("env.gpu.spoof.gpu.now.reboot", list);
            else if (!confirmed)
                message = Lang.F(apply ? "env.gpu.spoof.gpu.done.pending"
                    : restore ? "env.gpu.spoof.gpu.restored.pending" : "env.gpu.spoof.gpu.now.unchanged", list);
            else
                message = Lang.F(restarted ? "env.gpu.spoof.gpu.now.done"
                    : restore || (!tier.HasValue && !snapshot.On) ? "env.gpu.spoof.gpu.restored" : "env.gpu.spoof.gpu.done.active", list);
            return message;
        }

        internal static string StatusNames(GpuSpoofSnapshot snapshot)
        {
            if (snapshot == null || (!snapshot.On && snapshot.GameSeesFake != true)) return "";
            List<string> names = new List<string>();
            foreach (GpuSpoofDevice device in snapshot.Devices)
            {
                string real = string.IsNullOrEmpty(device.RealName) ? Lang.T("env.gpu.spoof.name.unknown") : device.RealName;
                string visible = string.IsNullOrEmpty(device.VisibleName) ? Lang.T("env.gpu.spoof.name.unknown") : device.VisibleName;
                names.Add(real + " → " + visible);
            }
            return string.Join(Environment.NewLine, names);
        }

        internal static void StateOf(GpuSpoofSnapshot s, bool busy, bool? gameBlocked, bool rebootRequired,
            out string text, out Color color)
        {
            if (busy)
            {
                text = Lang.T("env.gpu.spoof.state.busy");
                color = Theme.Accent;
            }
            else if (rebootRequired)
            {
                text = Lang.T("env.gpu.spoof.state.reboot");
                color = Theme.Warning;
            }
            else if (gameBlocked == true)
            {
                text = Lang.T("env.gpu.spoof.state.gamebusy");
                color = Theme.Warning;
            }
            else if (s == null || !gameBlocked.HasValue)
            {
                text = Lang.T("env.gpu.spoof.state.reading");
                color = Theme.Faint;
            }
            else if (!string.IsNullOrEmpty(s.Error))
            {
                text = Lang.T("env.gpu.spoof.state.error");
                color = Theme.Warning;
            }
            else if (s.Partial)
            {
                text = Lang.T("env.gpu.spoof.state.partial");
                color = Theme.Warning;
            }
            else if (s.On && s.ReadbackMatches == true)
            {
                text = Lang.T("env.gpu.spoof.state.active");
                color = Theme.Green;
            }
            else if (s.On)
            {
                // 待生效用警示色 和已生效的绿色分开
                text = Lang.T("env.gpu.spoof.state.pending");
                color = Theme.Warning;
            }
            else if (NeedsAdapterRestart(s))
            {
                text = Lang.T("env.gpu.spoof.state.restorepending");
                color = Theme.Warning;
            }
            else if (!s.CanRestart)
            {
                text = Lang.T(s.Uncertain ? "env.gpu.spoof.state.uncertain" : "env.gpu.spoof.state.none");
                color = Theme.Faint;
            }
            else if (s.CanRestore)
            {
                text = Lang.T("env.gpu.spoof.state.recoverable");
                color = Theme.Warning;
            }
            else
            {
                text = Lang.T("env.gpu.spoof.state.off");
                color = Theme.Faint;
            }
        }

    }
}
