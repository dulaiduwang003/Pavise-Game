using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal abstract class GameCardExtension : Control
    {
        private readonly List<Control> displayAncestors = new List<Control>();
        private Form displayHost;
        private bool displayActive;
        private bool displayHandleDestroyed;
        private bool displayDisposed;

        // 服务事件可能来自工作线程，只读取由界面生命周期发布的活动标志。
        protected bool DisplayRefreshActive { get { return Volatile.Read(ref displayActive); } }

        protected bool CanRefreshDisplay
        {
            get
            {
                Form host = FindForm();
                return !displayDisposed && !displayHandleDestroyed && !IsDisposed && !Disposing && IsHandleCreated
                    && host != null && !host.IsDisposed && !host.Disposing
                    && PanelForm.ShouldRunUi(Visible && host.Visible, host.WindowState);
            }
        }

        protected virtual void OnDisplayRefreshActivityChanged(bool active) { }

        protected virtual void OnDisplayRefreshHandleDestroyed() { }

        private void SyncDisplayActivity(bool rebind)
        {
            if (displayDisposed) return;
            if (rebind) WatchDisplayContext();
            bool active = CanRefreshDisplay;
            if (active == DisplayRefreshActive) return;
            Volatile.Write(ref displayActive, active);
            OnDisplayRefreshActivityChanged(active);
        }

        private void WatchDisplayContext()
        {
            UnwatchDisplayContext();
            if (displayHandleDestroyed || !IsHandleCreated) return;
            for (Control parent = Parent; parent != null; parent = parent.Parent)
            {
                if (parent.IsDisposed) continue;
                displayAncestors.Add(parent);
                parent.ParentChanged += OnDisplayParentChanged;
                parent.VisibleChanged += OnDisplayVisibilityChanged;
            }
            displayHost = FindForm();
            if (displayHost != null && !displayHost.IsDisposed)
            {
                displayHost.Resize += OnDisplayVisibilityChanged;
                displayHost.Disposed += OnDisplayHostDisposed;
            }
        }

        private void UnwatchDisplayContext()
        {
            foreach (Control parent in displayAncestors)
            {
                parent.ParentChanged -= OnDisplayParentChanged;
                parent.VisibleChanged -= OnDisplayVisibilityChanged;
            }
            displayAncestors.Clear();
            if (displayHost != null)
            {
                displayHost.Resize -= OnDisplayVisibilityChanged;
                displayHost.Disposed -= OnDisplayHostDisposed;
                displayHost = null;
            }
        }

        private void OnDisplayParentChanged(object sender, EventArgs e) { SyncDisplayActivity(true); }
        private void OnDisplayVisibilityChanged(object sender, EventArgs e) { SyncDisplayActivity(false); }
        private void OnDisplayHostDisposed(object sender, EventArgs e)
        {
            SyncDisplayActivity(false);
            UnwatchDisplayContext();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            SyncDisplayActivity(true);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncDisplayActivity(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            displayHandleDestroyed = false;
            base.OnHandleCreated(e);
            SyncDisplayActivity(true);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            displayHandleDestroyed = true;
            SyncDisplayActivity(false);
            OnDisplayRefreshHandleDestroyed();
            UnwatchDisplayContext();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                displayDisposed = true;
                Volatile.Write(ref displayActive, false);
                UnwatchDisplayContext();
            }
            base.Dispose(disposing);
        }

        public abstract int PreferredHeight { get; }

        public virtual int PreferredHeightForWidth(int width)
        {
            return PreferredHeight;
        }

        public virtual void SetSurface(Color surface)
        {
        }
    }
}
