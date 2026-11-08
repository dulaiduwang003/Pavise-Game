using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal abstract class FxControl : Control, IBusyInteractive
	{
		protected class FxAccessibleObject : ControlAccessibleObject
		{
			private readonly FxControl fx;

			public override AccessibleStates State
			{
				get
				{
					AccessibleStates accessibleStates = base.State;
					if (fx.busy)
					{
						accessibleStates |= AccessibleStates.Busy;
					}
					return accessibleStates;
				}
			}

			public FxAccessibleObject(FxControl owner)
				: base(owner)
			{
				fx = owner;
			}
		}

		protected Motion hover;

		protected Motion press;

		protected bool pressed;

		public Color Bg = Color.Empty;

		private bool busy;

		private float busyPhase;

		protected Color ParentBg
		{
			get
			{
				if (base.Parent == null)
				{
					return Theme.Bg;
				}
				return base.Parent.BackColor;
			}
		}

		protected Color EffBg
		{
			get
			{
				if (!(Bg != Color.Empty))
				{
					return ParentBg;
				}
				return Bg;
			}
		}

		public bool Busy
		{
			get
			{
				return busy;
			}
			set
			{
				if (busy != value)
				{
					busy = value;
					busyPhase = 0f;
					pressed = false;
					press.To(0f);
					if (value)
					{
						UiClock.Wake();
					}
					Invalidate();
					if (base.IsHandleCreated)
					{
						AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
					}
					EventHandler busyChanged = this.BusyChanged;
					if (busyChanged != null)
					{
						busyChanged(this, EventArgs.Empty);
					}
				}
			}
		}

		public bool CanInteract
		{
			get
			{
				return DsInteraction.CanActivate(this, busy);
			}
		}

		protected float BusyPhase
		{
			get
			{
				return busyPhase;
			}
		}

		protected DsState State
		{
			get
			{
				DsState result = default(DsState);
				result.Enabled = base.Enabled;
				result.Busy = busy;
				result.Focused = Focused;
				result.Hover = hover.Value;
				result.Press = press.Value;
				return result;
			}
		}

		protected bool WantsFocusCue
		{
			get
			{
				return DsFocus.Wants(Focused, ShowFocusCues, CanInteract);
			}
		}

		public event EventHandler BusyChanged;

		protected FxControl()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			Cursor = Cursors.Hand;
			hover.Speed = 0.28f;
			press.Speed = 0.42f;
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			UiClock.Frame += OnFrame;
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			UiClock.Frame -= OnFrame;
			base.OnHandleDestroyed(e);
		}

		protected virtual bool StepAll()
		{
			bool stepSucceeded = hover.Step();
			bool stepSucceeded2 = press.Step();
			bool result = StepBusy();
			if (!stepSucceeded && !stepSucceeded2)
			{
				return result;
			}
			return true;
		}

		protected bool StepBusy()
		{
			if (!busy || UiClock.Frozen || UiClock.Suspended || !base.Visible)
			{
				return false;
			}
			busyPhase += 0.018f * UiClock.DeltaScale;
			if (busyPhase >= 1f)
			{
				busyPhase -= 1f;
			}
			UiClock.Wake(4);
			return true;
		}

		protected virtual void OnFrame(object s, EventArgs e)
		{
			if (StepAll())
			{
				Invalidate();
			}
		}

		protected override void OnGotFocus(EventArgs e)
		{
			base.OnGotFocus(e);
			Invalidate();
		}

		protected override void OnLostFocus(EventArgs e)
		{
			base.OnLostFocus(e);
			Invalidate();
		}

		protected override void OnMouseEnter(EventArgs e)
		{
			base.OnMouseEnter(e);
			hover.To(1f);
			UiClock.Wake();
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			pressed = false;
			press.To(0f);
			hover.To(0f);
			UiClock.Wake();
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (e.Button == MouseButtons.Left && !busy)
			{
				pressed = true;
				press.Set(1f);
				UiClock.Wake();
				Invalidate();
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			pressed = false;
			press.To(0f);
			UiClock.Wake();
			Invalidate();
		}

		protected override void OnClick(EventArgs e)
		{
			if (CanInteract)
			{
				base.OnClick(e);
			}
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new FxAccessibleObject(this);
		}

		protected void FillBg(Graphics g)
		{
			DsPaint.FillBackground(g, this, EffBg);
		}

		protected void PaintBusy(Graphics g, Rectangle bounds, GraphicsPath clip)
		{
			if (!busy)
			{
				return;
			}
			GraphicsState gstate = g.Save();
			try
			{
				if (clip != null)
				{
					g.SetClip(clip, CombineMode.Intersect);
				}
				DsPaint.BusyRail(g, bounds, busyPhase, Theme.Accent);
			}
			finally
			{
				g.Restore(gstate);
			}
		}
	}
}
