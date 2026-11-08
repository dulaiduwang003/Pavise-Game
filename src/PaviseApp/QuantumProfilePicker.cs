using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class QuantumProfilePicker : FxControl
	{
		private sealed class ProfileList : Control
		{
			private static readonly int[] order = new int[9] { 0, 1, 8, 2, 3, 4, 5, 6, 7 };

			private int active;

			private int selected;

			private int hoverRow = -1;

			private int pressedRow = -1;

			private int offset;

			private int dragY = -1;

			private int dragOffset;

			private bool keyboard = true;

			private readonly int row = Theme.S(56);

			private readonly int header = Theme.S(32);

			private readonly int footer = Theme.S(28);

			internal Func<bool> CanInteract;

			private bool Interactive
			{
				get
				{
					if (!base.IsDisposed && base.Enabled)
					{
						if (CanInteract != null)
						{
							return CanInteract();
						}
						return true;
					}
					return false;
				}
			}

			private int ViewHeight
			{
				get
				{
					return Math.Max(1, base.Height - header - footer);
				}
			}

			private int MaxOffset
			{
				get
				{
					return Math.Max(0, row * order.Length - ViewHeight);
				}
			}

			private int ThumbHeight
			{
				get
				{
					return Math.Min(ViewHeight, Math.Max(Theme.S(20), ViewHeight * ViewHeight / (row * order.Length)));
				}
			}

			internal event Action<int> Picked;

			internal event Action Cancelled;

			internal event Action<bool> Tabbed;

			internal ProfileList(int selected)
			{
				this.selected = selected;
				active = Array.IndexOf(order, selected);
				base.Size = new Size(Theme.S(458), header + row * order.Length + footer);
				SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
				Cursor = Cursors.Hand;
				base.TabStop = true;
				base.AccessibleRole = AccessibleRole.List;
				base.AccessibleName = PerformancePolicy.Text("选择前台时间片", "Choose foreground time slice");
			}

			internal void SetSelected(int index)
			{
				selected = index;
				Invalidate();
			}

			internal void EnsureActiveVisible()
			{
				if (active * row < offset)
				{
					offset = active * row;
				}
				if ((active + 1) * row > offset + ViewHeight)
				{
					offset = (active + 1) * row - ViewHeight;
				}
				offset = Math.Max(0, Math.Min(MaxOffset, offset));
			}

			private int Hit(Point point)
			{
				if (point.X < 0 || point.X >= base.Width - Theme.S(8) || point.Y < header || point.Y >= base.Height - footer)
				{
					return -1;
				}
				int rowRatio = (point.Y - header + offset) / row;
				if (rowRatio >= order.Length)
				{
					return -1;
				}
				return rowRatio;
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				Graphics graphics = e.Graphics;
				graphics.Clear(Theme.Card);
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				TextRenderer.DrawText(graphics, PerformancePolicy.Text("按使用场景选择", "Choose for your workload"), Theme.UI(9f, true), new Rectangle(Theme.S(12), 0, base.Width - Theme.S(24), header), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
				GraphicsState gstate = graphics.Save();
				graphics.SetClip(new Rectangle(0, header, base.Width, ViewHeight));
				for (int i = 0; i < order.Length; i++)
				{
					int onPaintValue = order[i];
					int top = header + i * row - offset;
					bool matchesOnPaintValue = onPaintValue == selected;
					bool matchesI = (keyboard ? (i == active) : (i == hoverRow));
					Rectangle r = new Rectangle(Theme.S(5), top + Theme.S(2), base.Width - Theme.S(16), row - Theme.S(4));
					if (matchesOnPaintValue || matchesI)
					{
						Theme.FillRound(graphics, r, Theme.S(6), matchesOnPaintValue ? Col.Lerp(Theme.Card, Theme.Accent, matchesI ? 0.16f : 0.09f) : Theme.CardHover);
					}
					if (matchesI)
					{
						using (Pen pen = new Pen(Theme.StrokeHi))
						{
							using (GraphicsPath path = Theme.Rounded(r, Theme.S(6)))
							{
								graphics.DrawPath(pen, path);
							}
						}
					}
					if (i == 3)
					{
						using (Pen pen2 = new Pen(Theme.Stroke))
						{
							graphics.DrawLine(pen2, Theme.S(12), top, base.Width - Theme.S(12), top);
						}
					}
					TextRenderer.DrawText(graphics, QuantumProfiles.Title(onPaintValue), Theme.UI(10f, true), new Rectangle(Theme.S(14), top + Theme.S(6), base.Width - Theme.S(55), Theme.S(21)), matchesOnPaintValue ? Theme.Accent : Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
					TextRenderer.DrawText(graphics, QuantumProfiles.Description(onPaintValue), Theme.UI(9f, false), new Rectangle(Theme.S(14), top + Theme.S(30), base.Width - Theme.S(35), Theme.S(20)), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
					if (matchesOnPaintValue)
					{
						TextRenderer.DrawText(graphics, "✓", Theme.UI(11f, true), new Rectangle(base.Width - Theme.S(37), top + Theme.S(6), Theme.S(22), Theme.S(21)), Theme.Accent);
					}
				}
				if (MaxOffset > 0)
				{
					int thumbHeight = ThumbHeight;
					Theme.FillRound(graphics, new Rectangle(base.Width - Theme.S(6), header + offset * (ViewHeight - thumbHeight) / MaxOffset, Theme.S(3), thumbHeight), Theme.S(2), Theme.StrokeHi);
				}
				graphics.Restore(gstate);
				TextRenderer.DrawText(graphics, PerformancePolicy.Text("↑ ↓ 浏览   Enter 选择   Esc 取消", "↑ ↓ Browse   Enter Select   Esc Cancel"), Theme.UI(8.5f, false), new Rectangle(Theme.S(12), base.Height - footer, base.Width - Theme.S(24), footer), Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
			}

			protected override void OnMouseMove(MouseEventArgs e)
			{
				if (!Interactive)
				{
					return;
				}
				base.OnMouseMove(e);
				if (dragY >= 0)
				{
					offset = Math.Max(0, Math.Min(MaxOffset, dragOffset + (e.Y - dragY) * MaxOffset / Math.Max(1, ViewHeight - ThumbHeight)));
					Invalidate();
					return;
				}
				int hitResult = Hit(e.Location);
				if (hitResult != hoverRow)
				{
					hoverRow = hitResult;
					keyboard = false;
					Invalidate();
				}
			}

			protected override void OnMouseLeave(EventArgs e)
			{
				base.OnMouseLeave(e);
				hoverRow = -1;
				Invalidate();
			}

			protected override void OnMouseDown(MouseEventArgs e)
			{
				if (!Interactive)
				{
					return;
				}
				base.OnMouseDown(e);
				if (e.Button != MouseButtons.Left)
				{
					return;
				}
				if (MaxOffset > 0 && e.X >= base.Width - Theme.S(10) && e.Y >= header && e.Y < base.Height - footer)
				{
					int headerOffset = header + offset * (ViewHeight - ThumbHeight) / MaxOffset;
					if (e.Y < headerOffset || e.Y > headerOffset + ThumbHeight)
					{
						offset = Math.Max(0, Math.Min(MaxOffset, (e.Y - header - ThumbHeight / 2) * MaxOffset / Math.Max(1, ViewHeight - ThumbHeight)));
					}
					dragY = e.Y;
					dragOffset = offset;
					pressedRow = -1;
					keyboard = false;
					hoverRow = -1;
					base.Capture = true;
					Invalidate();
				}
				else
				{
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
					dragY = -1;
					base.Capture = false;
					if (Interactive && hitResult >= 0 && hitResult == pressedRowSnapshot && this.Picked != null)
					{
						this.Picked(order[hitResult]);
					}
				}
			}

			protected override void OnMouseCaptureChanged(EventArgs e)
			{
				base.OnMouseCaptureChanged(e);
				if (!base.Capture)
				{
					pressedRow = -1;
					dragY = -1;
				}
			}

			protected override void OnMouseWheel(MouseEventArgs e)
			{
				if (Interactive)
				{
					base.OnMouseWheel(e);
					offset = Math.Max(0, Math.Min(MaxOffset, offset - Math.Sign(e.Delta) * row * 2));
					keyboard = false;
					hoverRow = Hit(e.Location);
					Invalidate();
					HandledMouseEventArgs handledMouseEventArgs = e as HandledMouseEventArgs;
					if (handledMouseEventArgs != null)
					{
						handledMouseEventArgs.Handled = true;
					}
				}
			}

			protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
			{
				if (!Interactive)
				{
					return base.ProcessCmdKey(ref msg, keyData);
				}
				int processedCount = active;
				switch (keyData)
				{
				case Keys.Down:
					processedCount++;
					break;
				case Keys.Up:
					processedCount--;
					break;
				case Keys.Home:
					processedCount = 0;
					break;
				case Keys.End:
					processedCount = order.Length - 1;
					break;
				case Keys.Next:
					processedCount += Math.Max(1, ViewHeight / row);
					break;
				case Keys.Prior:
					processedCount -= Math.Max(1, ViewHeight / row);
					break;
				case Keys.Return:
				case Keys.Space:
					if (this.Picked != null)
					{
						this.Picked(order[active]);
					}
					return true;
				case Keys.Escape:
				case Keys.F4:
				case Keys.Up | Keys.Alt:
					if (this.Cancelled != null)
					{
						this.Cancelled();
					}
					return true;
				case Keys.Tab:
				case Keys.Tab | Keys.Shift:
					if (this.Tabbed != null)
					{
						this.Tabbed(keyData == Keys.Tab);
					}
					return true;
				default:
					return base.ProcessCmdKey(ref msg, keyData);
				}
				active = Math.Max(0, Math.Min(order.Length - 1, processedCount));
				keyboard = true;
				EnsureActiveVisible();
				Invalidate();
				return true;
			}
		}

		private int selected;

		private ToolStripDropDown popup;

		private ProfileList list;

		private bool open;

		private readonly DropdownHost dropdown;

		internal int SelectedIndex
		{
			get
			{
				return selected;
			}
		}

		internal event EventHandler SelectionChanged;

		internal void SetSilently(int index)
		{
			if (!base.IsDisposed && index >= 0 && index < QuantumProfiles.Values.Length)
			{
				selected = index;
				base.AccessibleDescription = QuantumProfiles.Description(index);
				if (list != null && !list.IsDisposed)
				{
					list.SetSelected(index);
				}
				if (base.IsHandleCreated)
				{
					AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
				}
				Invalidate();
			}
		}

		internal QuantumProfilePicker()
		{
			base.Size = new Size(Theme.S(230), Theme.S(38));
			base.TabStop = true;
			base.AccessibleRole = AccessibleRole.ComboBox;
			dropdown = new DropdownHost(this)
			{
				AlignRight = true
			};
			DropdownHost dropdownHost = dropdown;
			EventHandler value = delegate
			{
				open = false;
				if (!base.IsDisposed)
				{
					Invalidate();
				}
			};
			dropdownHost.Closed += value;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			FillBg(e.Graphics);
			e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (GraphicsPath graphicsPath = Theme.Rounded(new Rectangle(0, 0, base.Width - 1, base.Height - 1), Theme.S(7)))
			{
				using (SolidBrush brush = new SolidBrush(Col.Lerp(Theme.Inset, Theme.CardHover, open ? 1f : hover.Value)))
				{
					e.Graphics.FillPath(brush, graphicsPath);
				}
				using (Pen pen = new Pen((base.Enabled && (open || Focused)) ? Theme.Accent : Col.Lerp(Theme.Stroke, Theme.StrokeHi, hover.Value)))
				{
					e.Graphics.DrawPath(pen, graphicsPath);
				}
				PaintBusy(e.Graphics, base.ClientRectangle, graphicsPath);
			}
			TextRenderer.DrawText(e.Graphics, QuantumProfiles.Title(selected), Theme.UI(9.5f, false), new Rectangle(Theme.S(11), 0, base.Width - Theme.S(40), base.Height), base.Enabled ? Theme.Fg : Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | ((base.Width < Theme.S(200)) ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine));
			int remainingWidth = base.Width - Theme.S(21);
			int heightRatio = base.Height / 2;
			using (Pen pen2 = new Pen(base.Enabled ? Theme.Accent : Theme.Dim, Theme.S(2)))
			{
				int scaledDimension = Theme.S(4);
				int onPaintValue = ((!open) ? 1 : (-1));
				e.Graphics.DrawLines(pen2, new Point[3]
				{
					new Point(remainingWidth - scaledDimension, heightRatio - onPaintValue * scaledDimension / 2),
					new Point(remainingWidth, heightRatio + onPaintValue * scaledDimension / 2),
					new Point(remainingWidth + scaledDimension, heightRatio - onPaintValue * scaledDimension / 2)
				});
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

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left && base.CanInteract)
			{
				dropdown.NoteMouseDown();
				Focus();
			}
			base.OnMouseDown(e);
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			if (e.Button == MouseButtons.Left)
			{
				dropdown.NoteMouseUp();
			}
		}

		protected override void OnClick(EventArgs e)
		{
			if (!base.CanInteract)
			{
				return;
			}
			base.OnClick(e);
			if (base.CanInteract)
			{
				if (dropdown.ConsumeCloseClick() || open)
				{
					Close();
				}
				else
				{
					Open();
				}
			}
		}

		protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
		{
			if (dropdown.HandleCommandKey(keyData, false, delegate
			{
				if (open)
				{
					Close();
				}
				else
				{
					Open();
				}
			}))
			{
				return true;
			}
			return base.ProcessCmdKey(ref msg, keyData);
		}

		private void Open()
		{
			if (!base.CanInteract || !base.Visible || open)
			{
				return;
			}
			ContextMenuStrip next = new ContextMenuStrip
			{
				Padding = Padding.Empty,
				Margin = Padding.Empty,
				BackColor = Theme.Card,
				AutoSize = false,
				AutoClose = true,
				ShowImageMargin = false,
				ShowCheckMargin = false,
				DropShadowEnabled = false
			};
			popup = next;
			ProfileList profileList = new ProfileList(selected);
			profileList.CanInteract = () => base.CanInteract && object.ReferenceEquals(dropdown.Menu, next);
			ProfileList profileList2 = (list = profileList);
			Rectangle workingArea = Screen.FromControl(this).WorkingArea;
			Rectangle rectangle = RectangleToScreen(base.ClientRectangle);
			int val = Math.Max(0, workingArea.Bottom - rectangle.Bottom - Theme.S(6));
			int val2 = Math.Max(0, rectangle.Top - workingArea.Top - Theme.S(6));
			Size size = DsPopup.Fit(new Size(Theme.S(460), profileList2.Height + 2), workingArea, Theme.S(12), 1);
			int val3 = Math.Min(profileList2.Height, Math.Max(val2, val) - 2);
			profileList2.Size = new Size(Math.Max(1, size.Width - 2), Math.Max(1, Math.Min(size.Height - 2, Math.Max(Theme.S(80), val3))));
			profileList2.EnsureActiveVisible();
			next.Items.Add(new ToolStripControlHost(profileList2)
			{
				Margin = Padding.Empty,
				Padding = Padding.Empty,
				AutoSize = false,
				Size = profileList2.Size
			});
			next.Size = new Size(profileList2.Width + 2, profileList2.Height + 2);
			profileList2.Picked += delegate(int index)
			{
				if (base.CanInteract && object.ReferenceEquals(dropdown.Menu, next))
				{
					Close();
					if (base.CanInteract)
					{
						Focus();
						SetSilently(index);
						if (this.SelectionChanged != null)
						{
							this.SelectionChanged(this, EventArgs.Empty);
						}
					}
				}
			};
			profileList2.Cancelled += delegate
			{
				if (base.CanInteract && object.ReferenceEquals(dropdown.Menu, next))
				{
					Close();
					if (base.CanInteract)
					{
						Focus();
					}
				}
			};
			profileList2.Tabbed += delegate(bool forward)
			{
				if (base.CanInteract && object.ReferenceEquals(dropdown.Menu, next))
				{
					Close();
					if (base.CanInteract)
					{
						Focus();
						if (base.Parent != null)
						{
							base.Parent.SelectNextControl(this, forward, true, true, true);
						}
					}
				}
			};
			dropdown.Show(next);
			open = object.ReferenceEquals(dropdown.Menu, next);
			if (open && base.CanInteract)
			{
				profileList2.Focus();
			}
			Invalidate();
		}

		private void Close()
		{
			dropdown.Close();
			open = false;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				dropdown.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
