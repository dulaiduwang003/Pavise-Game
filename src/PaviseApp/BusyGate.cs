using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class BusyGate
    {
        private readonly Control control;

        private readonly IBusyInteractive target;

        private readonly bool disableWhileBusy;

        private bool held;

        private bool wasEnabled;

        public bool Held
        {
            get
            {
                return held;
            }
        }

        public BusyGate(Control control, bool disableWhileBusy)
        {
            if (control == null)
            {
                throw new ArgumentNullException("control");
            }
            this.control = control;
            target = control as IBusyInteractive;
            this.disableWhileBusy = disableWhileBusy;
        }

        public bool TryEnter()
        {
            if (held || !DsInteraction.CanActivate(control))
            {
                return false;
            }
            held = true;
            wasEnabled = control.Enabled;
            if (target != null)
            {
                target.Busy = true;
            }
            if (disableWhileBusy)
            {
                control.Enabled = false;
            }
            return true;
        }

        public void Leave()
        {
            if (!held)
            {
                return;
            }
            held = false;
            if (!control.IsDisposed)
            {
                if (target != null)
                {
                    target.Busy = false;
                }
                if (disableWhileBusy)
                {
                    control.Enabled = wasEnabled;
                }
            }
        }
    }
}
