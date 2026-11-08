using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PaviseDialog : Form
	{
		private const int DlgW = 468;

		private const int PadX = 26;

		private const int TagTop = 17;

		private const int TagHeight = 16;

		private const int TitleTop = 43;

		private const int BodyTop = 75;

		private const int BtnH = 34;

		private const int BtnGap = 10;

		private const int BottomPad = 22;

		private readonly string title;

		private readonly string body;

		private readonly DlgKind kind;

		private readonly bool cancellable;

		private readonly bool requiredAction;

		private bool requiredActionCompleted;

		private PillButton secondaryActionButton;

		private float sweep;

		private Rectangle titleRect;

		private const TextFormatFlags BodyTextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

		private sealed class DialogBodyText : Control
		{
			internal DialogBodyText(string text)
			{
				Name = "DialogBodyText";
				Text = text;
				Font = DsType.Font(DsText.BodyLarge);
				ForeColor = Theme.Dim;
				BackColor = Theme.Bg;
				AccessibleRole = AccessibleRole.StaticText;
				AccessibleName = text;
				TabStop = false;
				DoubleBuffered = true;
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
				TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, BackColor, BodyTextFlags);
			}

			protected override void OnMouseDown(MouseEventArgs e)
			{
				base.OnMouseDown(e);
				if (Parent != null) Parent.Focus();
			}

			protected override void OnMouseWheel(MouseEventArgs e)
			{
				DialogContentPanel viewport = Parent as DialogContentPanel;
				if (viewport != null) viewport.ScrollWheel(e);
				else base.OnMouseWheel(e);
			}
		}

		private sealed class DialogContentPanel : Panel
		{
			internal DialogContentPanel()
			{
				Name = "DialogContentViewport";
				AutoScroll = true;
				BackColor = Theme.Bg;
				DoubleBuffered = true;
				SetStyle(ControlStyles.Selectable, true);
				TabStop = true;
				AccessibleRole = AccessibleRole.Pane;
				Native.Dark(this);
			}

			private void ScrollTo(int y)
			{
				int maximum = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
				AutoScrollPosition = new Point(0, Math.Max(0, Math.Min(maximum, y)));
			}

			internal void ScrollWheel(MouseEventArgs e)
			{
				int lines = SystemInformation.MouseWheelScrollLines;
				int distance = lines < 0 ? ClientSize.Height : Math.Max(1, lines) * DsType.LineHeight(DsText.BodyLarge);
				if (lines != 0) ScrollTo(-AutoScrollPosition.Y - e.Delta * distance / 120);
				HandledMouseEventArgs handled = e as HandledMouseEventArgs;
				if (handled != null) handled.Handled = true;
			}

			protected override void OnMouseWheel(MouseEventArgs e)
			{
				ScrollWheel(e);
			}

			protected override void OnMouseDown(MouseEventArgs e)
			{
				base.OnMouseDown(e);
				Focus();
			}

			protected override bool IsInputKey(Keys keyData)
			{
				switch (keyData & Keys.KeyCode)
				{
				case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End:
					return true;
				default: return base.IsInputKey(keyData);
				}
			}

			protected override void OnKeyDown(KeyEventArgs e)
			{
				int y = -AutoScrollPosition.Y;
				switch (e.KeyCode)
				{
				case Keys.Up: y -= DsType.LineHeight(DsText.BodyLarge); break;
				case Keys.Down: y += DsType.LineHeight(DsText.BodyLarge); break;
				case Keys.PageUp: y -= ClientSize.Height; break;
				case Keys.PageDown: y += ClientSize.Height; break;
				case Keys.Home: y = 0; break;
				case Keys.End: y = AutoScrollMinSize.Height; break;
				default: base.OnKeyDown(e); return;
				}
				ScrollTo(y);
				e.Handled = e.SuppressKeyPress = true;
			}
		}

		private static int GetHeightLimit(Rectangle workingArea)
		{
			return Math.Max(1, Math.Min(Theme.S(640), Math.Min((int)(workingArea.Height * 0.8), workingArea.Height - Theme.S(40))));
		}

		private static Rectangle GetWorkingArea(IWin32Window owner)
		{
			try
			{
				if (owner != null && owner.Handle != IntPtr.Zero) return Screen.FromHandle(owner.Handle).WorkingArea;
			}
			catch (InvalidOperationException) { }
			return Screen.FromPoint(Cursor.Position).WorkingArea;
		}

		private Color KindColor
		{
			get
			{
				switch (kind)
				{
				case DlgKind.Danger:
					return DsPalette.Ink(DsTone.Danger);
				case DlgKind.Warn:
					return DsPalette.Alert;
				case DlgKind.Success:
					return DsPalette.Ink(DsTone.Success);
				default:
					return DsPalette.Ink(DsTone.Accent);
				}
			}
		}

		private string KindTag
		{
			get
			{
				switch (kind)
				{
				case DlgKind.Danger:
					return Lang.T("dlg.tag.danger");
				case DlgKind.Warn:
					return Lang.T("dlg.tag.warn");
				case DlgKind.Success:
					return Lang.T("dlg.tag.done");
				default:
					return Lang.T("dlg.tag.info");
				}
			}
		}

		private PaviseDialog(string dlgTitle, string dlgBody, DlgKind dlgKind, string okText, string cancelText, Control extra, int width, Func<bool> action = null, Action onFailure = null)
			: this(dlgTitle, dlgBody, dlgKind, okText, cancelText, extra, width, action, onFailure, null)
		{
		}

		private PaviseDialog(string dlgTitle, string dlgBody, DlgKind dlgKind, string okText, string cancelText, Control extra, int width, Func<bool> action, Action onFailure, IWin32Window owner)
		{
			PaviseDialog paviseDialog = this;
			title = dlgTitle ?? "";
			body = dlgBody ?? "";
			kind = dlgKind;
			cancellable = cancelText != null;
			requiredAction = action != null;
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.MaximizeBox = false;
			base.MinimizeBox = false;
			base.ShowInTaskbar = false;
			base.AutoScaleMode = AutoScaleMode.None;
			Text = title;
			AccessibleName = title;
			BackColor = Theme.Bg;
			ForeColor = Theme.Fg;
			Font = DsType.Font(DsText.BodyLarge);
			DoubleBuffered = true;
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.KeyPreview = true;
			Rectangle workingArea = GetWorkingArea(owner);
			int maxLimit = Math.Min(Theme.S(width), Math.Max(1, workingArea.Width - Theme.S(40)));
			int widthValue = Math.Max(1, maxLimit - Theme.S(26) * 2);
			Font headingFont = DsType.Font(DsText.Heading, true);
			int titleHeight = TextRenderer.MeasureText(title, headingFont, new Size(widthValue, int.MaxValue), BodyTextFlags).Height;
			int headingLine = TextRenderer.MeasureText("Ag", headingFont, Size.Empty, TextFormatFlags.NoPadding).Height;
			titleRect = new Rectangle(Theme.S(26), Theme.S(43), widthValue, Math.Max(Theme.S(22), Math.Min(titleHeight, headingLine * 2)));
			int contentTop = Math.Max(Theme.S(75), titleRect.Bottom + Theme.S(8));
			int height = MeasureBody(widthValue);
			int sOffset = Theme.S(34) + Theme.S(22) + Theme.S(16);
			int available = Math.Max(1, GetHeightLimit(workingArea) - contentTop - sOffset);
			int extraGap = extra != null && height > 0 ? Theme.S(12) : 0;
			if (extra != null) extra.Width = widthValue;
			int fullContentHeight = height + extraGap + (extra == null ? 0 : extra.Height);
			int innerWidth = widthValue;
			if (fullContentHeight > available)
			{
				innerWidth = Math.Max(1, widthValue - SystemInformation.VerticalScrollBarWidth);
				height = MeasureBody(innerWidth);
				if (extra != null) extra.Width = innerWidth;
				fullContentHeight = height + extraGap + (extra == null ? 0 : extra.Height);
			}
			int viewportHeight = Math.Min(available, Math.Max(1, fullContentHeight));
			int sOffsetOffset = contentTop + viewportHeight + sOffset;
			base.ClientSize = new Size(maxLimit, sOffsetOffset);
			DialogContentPanel viewport = new DialogContentPanel();
			viewport.SetBounds(Theme.S(26), contentTop, widthValue, viewportHeight);
			DialogBodyText bodyText = new DialogBodyText(body);
			bodyText.SetBounds(0, 0, innerWidth, height);
			viewport.Controls.Add(bodyText);
			if (extra != null)
			{
				extra.Location = new Point(0, height + extraGap);
				viewport.Controls.Add(extra);
			}
			viewport.AutoScrollMinSize = new Size(0, fullContentHeight);
			base.Controls.Add(viewport);
			PillButton ok = new PillButton(okText ?? Lang.T("dlg.ok"), (kind != DlgKind.Danger) ? BtnKind.Primary : BtnKind.Danger);
			ok.Size = new Size(Theme.S(108), Theme.S(34));
			ok.Name = "dialogAccept";
			BusyGate gate = new BusyGate(ok, true);
			Action<bool> finishAction = delegate(bool completed)
			{
				if (!paviseDialog.IsDisposed && !paviseDialog.Disposing)
				{
					if (!completed)
					{
						try
						{
							if (onFailure != null)
							{
								onFailure();
							}
							return;
						}
						finally
						{
							gate.Leave();
						}
					}
					paviseDialog.requiredActionCompleted = true;
					gate.Leave();
					paviseDialog.DialogResult = DialogResult.OK;
					paviseDialog.Close();
				}
			};
			ok.Click += delegate
			{
				if (!paviseDialog.requiredAction)
				{
					paviseDialog.DialogResult = DialogResult.OK;
					paviseDialog.Close();
				}
				else if (!paviseDialog.requiredActionCompleted && paviseDialog.IsHandleCreated && gate.TryEnter())
				{
					try
					{
						Task.Factory.StartNew(action).ContinueWith(delegate(Task<bool> finished)
						{
							bool completed2 = finished.Status == TaskStatus.RanToCompletion && finished.Result;
							if (finished.IsFaulted && finished.Exception != null)
							{
								completed2 = false;
							}
							if (paviseDialog.IsDisposed || paviseDialog.Disposing || !paviseDialog.IsHandleCreated)
							{
								return;
							}
							try
							{
								paviseDialog.BeginInvoke((MethodInvoker)delegate
								{
									finishAction(completed2);
								});
							}
							catch (InvalidOperationException)
							{
							}
						}, TaskScheduler.Default);
					}
					catch
					{
						finishAction(false);
					}
				}
			};
			base.Controls.Add(ok);
			PillButton pillButton = null;
			if (cancellable)
			{
				pillButton = new PillButton(cancelText, BtnKind.Normal);
				pillButton.Bg = Theme.Card;
				pillButton.Size = new Size(Theme.S(96), Theme.S(34));
				pillButton.Name = "dialogCancel";
				pillButton.Click += delegate
				{
					base.DialogResult = DialogResult.Cancel;
					Close();
				};
				base.Controls.Add(pillButton);
			}
			int remainingS = sOffsetOffset - Theme.S(22) - Theme.S(34);
			int remainingWidth = maxLimit - Theme.S(26) - ok.Width;
			ok.Location = new Point(remainingWidth, remainingS);
			if (pillButton != null)
			{
				pillButton.Location = new Point(remainingWidth - Theme.S(10) - pillButton.Width, remainingS);
			}
			base.AcceptButton = null;
			base.KeyDown += delegate(object s, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Escape)
				{
					if (paviseDialog.requiredAction)
					{
						bool handled = (e.SuppressKeyPress = true);
						e.Handled = handled;
					}
					else
					{
						paviseDialog.DialogResult = ((!paviseDialog.cancellable) ? DialogResult.OK : DialogResult.Cancel);
						paviseDialog.Close();
					}
				}
				else if (e.KeyCode == Keys.Return)
				{
					if (paviseDialog.requiredAction)
					{
						bool handled2 = (e.SuppressKeyPress = true);
						e.Handled = handled2;
						ok.PerformClick();
					}
					else if (paviseDialog.secondaryActionButton != null && paviseDialog.secondaryActionButton.ContainsFocus)
					{
						e.SuppressKeyPress = e.Handled = true;
						paviseDialog.secondaryActionButton.PerformClick();
					}
					else
					{
						paviseDialog.DialogResult = DialogResult.OK;
						paviseDialog.Close();
					}
				}
			};
			Timer timer = new Timer();
			timer.Interval = 33;
			timer.Tick += delegate
			{
				sweep += 0.012f;
				if (sweep > 1f)
				{
					sweep -= 1f;
				}
				Invalidate(new Rectangle(0, 0, base.Width, Theme.S(4)));
			};
			timer.Start();
			base.Disposed += delegate
			{
				timer.Stop();
				timer.Dispose();
			};
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			Fx.EnterForm(this);
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			base.OnFormClosing(e);
			if (requiredAction && !requiredActionCompleted && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing)
			{
				e.Cancel = true;
				base.DialogResult = DialogResult.None;
			}
		}

		internal static PaviseDialog CreateRequiredRestart(Func<bool> restart)
		{
			return CreateRequiredRestart(restart, null);
		}

		private static PaviseDialog CreateRequiredRestart(Func<bool> restart, IWin32Window owner)
		{
			if (restart == null)
			{
				throw new ArgumentNullException("restart");
			}
			Label feedback = new Label
			{
				Name = "restartError",
				AutoSize = false,
				Font = DsType.Font(DsText.Body),
				ForeColor = Theme.Warning,
				BackColor = Theme.Bg,
				Width = Theme.S(416),
				Height = Theme.S(58)
			};
			return new PaviseDialog(Lang.T("vbs.restart.title"), Lang.T("vbs.restart.body"), DlgKind.Warn, Lang.T("vbs.restart.now"), null, feedback, 468, restart, delegate
			{
				feedback.Text = Lang.T("vbs.restart.failed");
			}, owner);
		}

		internal static bool RequireRestart(IWin32Window owner)
		{
			using (PaviseDialog paviseDialog = CreateRequiredRestart(SystemRestart.Request, owner))
			{
				return ((owner != null) ? paviseDialog.ShowDialog(owner) : paviseDialog.ShowDialog()) == DialogResult.OK;
			}
		}

		private int MeasureBody(int width)
		{
			if (body.Length == 0) return 0;
			Font font = DsType.Font(DsText.BodyLarge);
			Size size = TextRenderer.MeasureText(body, font, new Size(Math.Max(1, width), int.MaxValue), BodyTextFlags);
			return Math.Max(Theme.S(28), size.Height + Theme.S(8));
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
			Color kindColor = KindColor;
			Rectangle rect = new Rectangle(0, 0, base.Width, base.Height);
			using (SolidBrush brush = new SolidBrush(Theme.Bg))
			{
				graphics.FillRectangle(brush, rect);
			}
			int height = Theme.S(4);
			using (SolidBrush brush2 = new SolidBrush(kindColor))
			{
				graphics.FillRectangle(brush2, 0, 0, base.Width, height);
			}
			using (LinearGradientBrush brush3 = new LinearGradientBrush(DsPaint.GradientRect(new Rectangle(0, 0, base.Width, height)), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(150, 255, 255, 255), LinearGradientMode.Horizontal))
			{
				int scaledDimension = Theme.S(150);
				Rectangle rect2 = new Rectangle((int)(sweep * (float)(base.Width + scaledDimension)) - scaledDimension, 0, scaledDimension, height);
				Region clip = graphics.Clip;
				graphics.SetClip(new Rectangle(0, 0, base.Width, height), CombineMode.Replace);
				graphics.FillRectangle(brush3, rect2);
				graphics.Clip = clip;
			}
			int scaledDimension2 = Theme.S(9);
			int scaledDimension3 = Theme.S(74);
			Point[] points = new Point[4]
			{
				new Point(Theme.S(26) + scaledDimension2, Theme.S(17)),
				new Point(Theme.S(26) + scaledDimension3 + scaledDimension2, Theme.S(17)),
				new Point(Theme.S(26) + scaledDimension3 - scaledDimension2, Theme.S(33)),
				new Point(Theme.S(26) - scaledDimension2, Theme.S(33))
			};
			using (SolidBrush brush4 = new SolidBrush(Color.FromArgb(38, kindColor)))
			{
				graphics.FillPolygon(brush4, points);
			}
			using (Pen pen = new Pen(Color.FromArgb(120, kindColor), DsSpace.Line(1)))
			{
				graphics.DrawPolygon(pen, points);
			}
			Font font = DsType.Font(DsText.Tag, true);
			TextRenderer.DrawText(graphics, KindTag, font, new Rectangle(Theme.S(26), Theme.S(17), scaledDimension3, Theme.S(16)), kindColor,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
			Font font2 = DsType.Font(DsText.Heading, true);
			TextRenderer.DrawText(graphics, title, font2, titleRect, Theme.Fg, Theme.Bg, BodyTextFlags | TextFormatFlags.EndEllipsis);
			int top = base.ClientSize.Height - Theme.S(22) - Theme.S(34) - Theme.S(14);
			using (Pen pen2 = new Pen(Theme.Stroke))
			{
				graphics.DrawLine(pen2, Theme.S(26), top, base.Width - Theme.S(26), top);
			}
			using (Pen pen3 = new Pen(Theme.StrokeHi))
			{
				graphics.DrawRectangle(pen3, 0, 0, base.Width - 1, base.Height - 1);
			}
		}

		private static DialogResult Run(IWin32Window owner, string title, string body, DlgKind kind, string okText, string cancelText)
		{
			using (PaviseDialog paviseDialog = new PaviseDialog(title, body, kind, okText, cancelText, null, 468, null, null, owner))
			{
				Form form = owner as Form;
				if (form == null || !form.Visible)
				{
					paviseDialog.StartPosition = FormStartPosition.CenterScreen;
				}
				return (owner != null) ? paviseDialog.ShowDialog(owner) : paviseDialog.ShowDialog();
			}
		}

		public static void Info(IWin32Window owner, string title, string body)
		{
			Run(owner, title, body, DlgKind.Info, null, null);
		}

		public static void Warn(IWin32Window owner, string title, string body)
		{
			Run(owner, title, body, DlgKind.Warn, null, null);
		}

		public static void Error(IWin32Window owner, string title, string body)
		{
			Run(owner, title, body, DlgKind.Danger, null, null);
		}

		public static void Success(IWin32Window owner, string title, string body)
		{
			Run(owner, title, body, DlgKind.Success, null, null);
		}

		public static void Show(IWin32Window owner, string title, string body, Control content, int width)
		{
			using (PaviseDialog paviseDialog = new PaviseDialog(title, body, DlgKind.Info, null, null, content, width, null, null, owner))
			{
				Form form = owner as Form;
				if (form == null || !form.Visible)
				{
					paviseDialog.StartPosition = FormStartPosition.CenterScreen;
				}
				if (owner != null)
				{
					paviseDialog.ShowDialog(owner);
				}
				else
				{
					paviseDialog.ShowDialog();
				}
			}
		}

		internal static PaviseDialog CreateNoticeWithSecondaryAction(IWin32Window owner, string title, string body,
			string secondaryText, string secondaryFailureText, Func<bool> secondaryAction, int width)
		{
			if (secondaryAction == null) throw new ArgumentNullException("secondaryAction");
			var dialog = new PaviseDialog(title, body, DlgKind.Info, null, null, null, width, null, null, owner);
			var ok = (PillButton)dialog.Controls["dialogAccept"];
			var secondary = new PillButton(secondaryText, BtnKind.Normal) { Name = "dialogSecondary", Bg = Theme.Card, TabIndex = 0 };
			int textWidth = Math.Max(TextRenderer.MeasureText(secondaryText, secondary.Font).Width,
				TextRenderer.MeasureText(secondaryFailureText, secondary.Font).Width);
			int buttonWidth = Math.Max(Theme.S(108), textWidth + Theme.S(32));
			secondary.SetBounds(ok.Left - Theme.S(BtnGap) - buttonWidth, ok.Top, buttonWidth, ok.Height);
			ok.TabIndex = 1;
			secondary.Click += delegate
			{
				if (!secondaryAction()) { secondary.Text = secondaryFailureText; return; }
				dialog.DialogResult = DialogResult.OK;
				dialog.Close();
			};
			dialog.Controls.Add(secondary);
			dialog.secondaryActionButton = secondary;
			return dialog;
		}

		public static void ShowWithSecondaryAction(IWin32Window owner, string title, string body,
			string secondaryText, string secondaryFailureText, Func<bool> secondaryAction, int width)
		{
			using (var dialog = CreateNoticeWithSecondaryAction(owner, title, body, secondaryText, secondaryFailureText, secondaryAction, width))
			{
				Form form = owner as Form;
				if (form == null || !form.Visible) dialog.StartPosition = FormStartPosition.CenterScreen;
				if (owner != null) dialog.ShowDialog(owner);
				else dialog.ShowDialog();
			}
		}

		public static bool Confirm(IWin32Window owner, string title, string body, DlgKind kind)
		{
			return Run(owner, title, body, kind, Lang.T("dlg.confirm"), Lang.T("dlg.cancel")) == DialogResult.OK;
		}

		public static bool Confirm(IWin32Window owner, string title, string body, DlgKind kind, string okText, string cancelText)
		{
			return Run(owner, title, body, kind, okText, cancelText) == DialogResult.OK;
		}

		public static bool Confirm(IWin32Window owner, string title, string body, DlgKind kind, Control content, int width)
		{
			using (PaviseDialog paviseDialog = new PaviseDialog(title, body, kind, Lang.T("dlg.confirm"), Lang.T("dlg.cancel"), content, width, null, null, owner))
			{
				Form form = owner as Form;
				if (form == null || !form.Visible)
				{
					paviseDialog.StartPosition = FormStartPosition.CenterScreen;
				}
				return ((owner != null) ? paviseDialog.ShowDialog(owner) : paviseDialog.ShowDialog()) == DialogResult.OK;
			}
		}

		public static string Prompt(IWin32Window owner, string title, string body, string initial)
		{
			TextBox box = Theme.MakeTextBox(0, 0, Theme.S(468) - Theme.S(26) * 2);
			box.Text = initial ?? "";
			using (PaviseDialog paviseDialog = new PaviseDialog(title, body, DlgKind.Info, Lang.T("dlg.confirm"), Lang.T("dlg.cancel"), box, 468, null, null, owner))
			{
				Form form = owner as Form;
				if (form == null || !form.Visible)
				{
					paviseDialog.StartPosition = FormStartPosition.CenterScreen;
				}
				paviseDialog.Shown += delegate
				{
					box.Focus();
					box.SelectAll();
				};
				DialogResult dialogResult = ((owner != null) ? paviseDialog.ShowDialog(owner) : paviseDialog.ShowDialog());
				return (dialogResult == DialogResult.OK) ? box.Text.Trim() : null;
			}
		}
	}
}
