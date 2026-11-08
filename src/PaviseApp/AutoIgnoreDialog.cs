using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class AutoIgnoreDialog : Form
	{
		private sealed class AutoIgnoreContentPanel : WorkspacePanel
		{
			protected override void OnPaintBackground(PaintEventArgs e)
			{
				e.Graphics.Clear(Theme.Card);
			}
		}

		private sealed class AutoIgnoreListSurface : RoundPanel
		{
			internal bool ShowEmpty;

			protected override void OnPaintBackground(PaintEventArgs e)
			{
				e.Graphics.Clear(Theme.Card);
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
				using (GraphicsPath path = Theme.TechPath(new Rectangle(0, 0, base.Width - 1, base.Height - 1), Theme.S(10)))
				{
					using (SolidBrush brush = new SolidBrush(Theme.Inset))
					{
						e.Graphics.FillPath(brush, path);
					}
					using (Pen pen = new Pen(Theme.StrokeHi))
					{
						e.Graphics.DrawPath(pen, path);
					}
				}
				if (!ShowEmpty)
				{
					return;
				}
				int num = base.Width / 2;
				int num2 = base.Height / 2 - Theme.S(47);
				using (Pen pen2 = new Pen(Theme.Accent, Math.Max(1, Theme.S(2))))
				{
					e.Graphics.DrawLines(pen2, new Point[6]
					{
						new Point(num - Theme.S(15), num2),
						new Point(num + Theme.S(9), num2),
						new Point(num + Theme.S(16), num2 + Theme.S(7)),
						new Point(num + Theme.S(16), num2 + Theme.S(29)),
						new Point(num - Theme.S(15), num2 + Theme.S(29)),
						new Point(num - Theme.S(15), num2)
					});
					e.Graphics.DrawLines(pen2, new Point[3]
					{
						new Point(num - Theme.S(8), num2 + Theme.S(15)),
						new Point(num - Theme.S(2), num2 + Theme.S(21)),
						new Point(num + Theme.S(8), num2 + Theme.S(10))
					});
				}
			}
		}

		private sealed class AutoIgnoreActionButton : Button
		{
			private readonly bool primary;

			private bool hover;

			internal AutoIgnoreActionButton(string text, bool emphasized)
			{
				primary = emphasized;
				Text = text;
				Font = Theme.UI(9f, true);
				base.AccessibleName = text;
				base.FlatStyle = FlatStyle.Flat;
				base.FlatAppearance.BorderSize = 0;
				Cursor = Cursors.Hand;
				SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			}

			protected override void OnMouseEnter(EventArgs e)
			{
				base.OnMouseEnter(e);
				hover = true;
				Invalidate();
			}

			protected override void OnMouseLeave(EventArgs e)
			{
				base.OnMouseLeave(e);
				hover = false;
				Invalidate();
			}

			protected override void OnEnter(EventArgs e)
			{
				base.OnEnter(e);
				Invalidate();
			}

			protected override void OnLeave(EventArgs e)
			{
				base.OnLeave(e);
				Invalidate();
			}

			protected override void OnEnabledChanged(EventArgs e)
			{
				base.OnEnabledChanged(e);
				Invalidate();
			}

			protected override void OnPaintBackground(PaintEventArgs e)
			{
				e.Graphics.Clear(Theme.Card);
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				// Opaque Button painting must clear the full buffer, including clipped corners.
				e.Graphics.Clear(Theme.Card);
				e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
				Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
				Color color = ((!base.Enabled) ? Theme.Inset : (primary ? Theme.Accent : (hover ? Theme.CardHover : Theme.Inset)));
				Color foreColor = ((!base.Enabled) ? Theme.Faint : (primary ? Theme.OnAccent : Theme.Fg));
				using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(8)))
				{
					using (SolidBrush brush = new SolidBrush(color))
					{
						e.Graphics.FillPath(brush, path);
					}
					using (Pen pen = new Pen((base.Enabled && primary) ? Theme.Accent : Theme.StrokeHi))
					{
						e.Graphics.DrawPath(pen, path);
					}
				}
				TextRenderer.DrawText(e.Graphics, Text, Font, rectangle, foreColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
				if (Focused && ShowFocusCues)
				{
					ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rectangle, -Theme.S(5), -Theme.S(5)), foreColor, color);
				}
			}
		}

		private sealed class AutoIgnoreCloseButton : Button
		{
			internal AutoIgnoreCloseButton()
			{
				base.FlatStyle = FlatStyle.Flat;
				base.FlatAppearance.BorderSize = 0;
				Cursor = Cursors.Hand;
				SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			}

			protected override void OnEnter(EventArgs e)
			{
				base.OnEnter(e);
				Invalidate();
			}

			protected override void OnLeave(EventArgs e)
			{
				base.OnLeave(e);
				Invalidate();
			}

			protected override void OnPaintBackground(PaintEventArgs e)
			{
				e.Graphics.Clear(Theme.Card);
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				// Opaque Button painting must not retain a previous buffer's glyph.
				e.Graphics.Clear(Theme.Card);
				int num = Theme.S(11);
				int num2 = base.Width - num;
				using (Pen pen = new Pen(Focused ? Theme.Accent : Theme.Dim, Math.Max(1, Theme.S(1))))
				{
					e.Graphics.DrawLine(pen, num, num, num2, num2);
					e.Graphics.DrawLine(pen, num2, num, num, num2);
				}
				if (Focused && ShowFocusCues)
				{
					ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(base.ClientRectangle, -Theme.S(3), -Theme.S(3)), Theme.Accent, Theme.Card);
				}
			}
		}

		private readonly GameMode mode;

		private readonly WorkspacePanel content = new AutoIgnoreContentPanel();

		private readonly Label title;

		private readonly Label description;

		private readonly Label status;

		private readonly Label pathTitle;

		private readonly Label empty;

		private readonly Label emptyHint;

		private readonly Label selectionHint;

		private readonly TechListBox list = new TechListBox();

		private readonly AutoIgnoreListSurface listWrap = new AutoIgnoreListSurface();

		private readonly TextBox path = new TextBox();

		private readonly AutoIgnoreActionButton resume;

		private readonly AutoIgnoreActionButton close;

		private readonly AutoIgnoreCloseButton dismiss = new AutoIgnoreCloseButton();

		private int changeBusy;

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams createParams = base.CreateParams;
				createParams.ClassStyle |= 131072;
				return createParams;
			}
		}

		internal AutoIgnoreDialog(GameMode gameMode)
		{
			mode = gameMode;
			base.AutoScaleMode = AutoScaleMode.None;
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.ShowInTaskbar = false;
			BackColor = Theme.Card;
			ForeColor = Theme.Fg;
			Font = Theme.UI(9f, false);
			DoubleBuffered = true;
			base.KeyPreview = true;
			base.Opacity = 1.0;
			Text = Lang.T("autoignore.title");
			base.AccessibleName = Text;
			title = MakeLabel(Text, "autoIgnoreTitle", Theme.UI(15f, true), Theme.Fg);
			description = MakeLabel(Lang.T("autoignore.desc"), "autoIgnoreDescription", Theme.UI(9f, false), Theme.Dim);
			status = MakeLabel("", "autoIgnoreStatus", Theme.UI(8.8f, false), Theme.Dim);
			status.AutoEllipsis = true;
			pathTitle = MakeLabel(Lang.T("autoignore.path"), "autoIgnorePathTitle", Theme.UI(8.5f, true), Theme.Fg);
			empty = MakeLabel(Lang.T("autoignore.empty"), "autoIgnoreEmpty", Theme.UI(12f, true), Theme.Fg);
			empty.TextAlign = ContentAlignment.MiddleCenter;
			emptyHint = MakeLabel(Lang.T("autoignore.emptyhint"), "autoIgnoreEmptyHint", Theme.UI(9f, false), Theme.Dim);
			emptyHint.TextAlign = ContentAlignment.TopCenter;
			selectionHint = MakeLabel(Lang.T("autoignore.select"), "autoIgnoreSelectionHint", Theme.UI(8.8f, false), Theme.Dim);
			list.Name = "autoIgnoreList";
			list.AccessibleName = Text;
			Theme.StyleList(list, false);
			list.BackColor = Theme.Inset;
			list.ItemHeight = Math.Min(255, Theme.S(62));
			list.Dock = DockStyle.Fill;
			list.TabIndex = 0;
			list.DrawItem += DrawEntry;
			TechListBox techListBox = list;
			EventHandler value = delegate
			{
				UpdateSelection();
			};
			techListBox.SelectedIndexChanged += value;
			listWrap.Name = "autoIgnoreListWrap";
			listWrap.Fill = Theme.Card;
			listWrap.Border = Theme.Stroke;
			listWrap.Padding = new Padding(Theme.S(10));
			listWrap.Controls.AddRange(new Control[3] { list, empty, emptyHint });
			path.Name = "autoIgnorePath";
			path.AccessibleName = pathTitle.Text;
			path.ReadOnly = true;
			path.Multiline = true;
			path.WordWrap = true;
			path.ScrollBars = ScrollBars.Vertical;
			path.BackColor = Theme.Inset;
			path.ForeColor = Theme.Fg;
			path.Font = Theme.UI(8.8f, false);
			path.TabIndex = 1;
			path.BorderStyle = BorderStyle.FixedSingle;
			Native.Dark(path);
			path.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				if (e.Control && e.KeyCode == Keys.A)
				{
					path.SelectAll();
					e.SuppressKeyPress = true;
				}
			};
			resume = new AutoIgnoreActionButton(Lang.T("autoignore.resume"), true)
			{
				Name = "autoIgnoreResume",
				TabIndex = 2
			};
			close = new AutoIgnoreActionButton(Lang.T("autoignore.close"), false)
			{
				Name = "autoIgnoreClose",
				DialogResult = DialogResult.Cancel,
				TabIndex = 3
			};
			resume.Click += delegate
			{
				ResumeSelected();
			};
			close.Click += delegate
			{
				Close();
			};
			dismiss.Name = "autoIgnoreDismiss";
			dismiss.AccessibleName = close.Text;
			dismiss.TabIndex = 4;
			dismiss.Click += delegate
			{
				Close();
			};
			base.CancelButton = close;
			base.AcceptButton = resume;
			content.Name = "autoIgnoreContent";
			content.AutoScroll = true;
			content.BackColor = Theme.Card;
			Native.Dark(content);
			content.Controls.AddRange(new Control[7] { title, description, listWrap, pathTitle, path, selectionHint, dismiss });
			base.Controls.AddRange(new Control[4] { content, status, resume, close });
			title.MouseDown += DragMove;
			content.MouseDown += DragMove;
			Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
			base.ClientSize = FitSize(workingArea);
			LayoutContent();
			RefreshEntries(null);
		}

		private static Label MakeLabel(string text, string name, Font font, Color color)
		{
			Label label = new Label();
			label.Text = text;
			label.Name = name;
			label.Font = font;
			label.ForeColor = color;
			label.BackColor = Color.Transparent;
			label.UseMnemonic = false;
			return label;
		}

		private void LayoutContent()
		{
			Point autoScrollPosition = new Point(0, -content.AutoScrollPosition.Y);
			content.AutoScrollPosition = Point.Empty;
			int num = Theme.S(24);
			int num2 = base.ClientSize.Width - num * 2;
			int num3 = base.ClientSize.Height - Theme.S(64);
			int num4 = Theme.S(40);
			int num5 = (num2 - Theme.S(12)) / 2;
			close.SetBounds(num, num3, num5, num4);
			resume.SetBounds(close.Right + Theme.S(12), num3, num2 - num5 - Theme.S(12), num4);
			status.SetBounds(num, num3 - Theme.S(32), num2, Theme.S(26));
			content.SetBounds(Theme.S(2), Theme.S(2), base.ClientSize.Width - Theme.S(4), Math.Max(1, status.Top - Theme.S(14)));
			int num6 = Math.Max(Theme.S(20), SystemInformation.VerticalScrollBarWidth + 2);
			int num7 = num2 - num6;
			int num8 = Math.Max(Theme.S(36), TextRenderer.MeasureText(title.Text, title.Font, new Size(num7 - Theme.S(42), int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(4));
			title.SetBounds(num, Theme.S(20), num7 - Theme.S(42), num8);
			dismiss.SetBounds(num + num7 - Theme.S(32), Theme.S(20), Theme.S(32), Theme.S(32));
			int num9 = TextRenderer.MeasureText(description.Text, description.Font, new Size(num7, int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(6);
			description.SetBounds(num, title.Bottom + Theme.S(8), num7, num9);
			int num10 = description.Bottom + Theme.S(16);
			bool flag = list.SelectedItem != null;
			int num11 = (flag ? Theme.S(128) : ((list.Items.Count > 0) ? Theme.S(48) : Theme.S(20)));
			int num12 = Math.Max(Theme.S(146), content.Height - num10 - num11);
			listWrap.SetBounds(num, num10, num7, num12);
			empty.SetBounds(Theme.S(20), num12 / 2 - Theme.S(6), num7 - Theme.S(40), Theme.S(28));
			emptyHint.SetBounds(Theme.S(24), empty.Bottom + Theme.S(5), num7 - Theme.S(48), Theme.S(48));
			pathTitle.SetBounds(num, listWrap.Bottom + Theme.S(12), num7, Theme.S(22));
			path.SetBounds(num, pathTitle.Bottom + Theme.S(4), num7, Theme.S(66));
			selectionHint.SetBounds(num, listWrap.Bottom + Theme.S(12), num7, Theme.S(28));
			TextBox textBox = path;
			bool visible = (pathTitle.Visible = flag);
			textBox.Visible = visible;
			selectionHint.Visible = !flag && list.Items.Count > 0;
			content.AutoScrollMinSize = new Size(0, (flag ? path.Bottom : (selectionHint.Visible ? selectionHint.Bottom : listWrap.Bottom)) + Theme.S(16));
			content.AutoScrollPosition = autoScrollPosition;
		}

		protected override void OnClientSizeChanged(EventArgs e)
		{
			base.OnClientSizeChanged(e);
			if (close != null)
			{
				LayoutContent();
			}
		}

		protected override void OnLoad(EventArgs e)
		{
			if (base.Owner != null)
			{
				Rectangle workingArea = Screen.FromControl(base.Owner).WorkingArea;
				base.ClientSize = FitSize(workingArea);
			}
			base.OnLoad(e);
		}

		private static Size FitSize(Rectangle available)
		{
			return new Size(Math.Max(1, Math.Min(Theme.S(720), available.Width - 32)), Math.Max(1, Math.Min(Theme.S(550), available.Height - 32)));
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			if (list.Visible)
			{
				list.Focus();
			}
			else
			{
				close.Focus();
			}
		}

		private void DragMove(object sender, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				Native.ReleaseCapture();
				Native.SendMessage(base.Handle, 161, (IntPtr)2, IntPtr.Zero);
			}
		}

		private static string BaseName(string executable)
		{
			try
			{
				return Path.GetFileName(executable);
			}
			catch
			{
				return executable;
			}
		}

		private void DrawEntry(object sender, DrawItemEventArgs e)
		{
			if (e.Index < 0 || e.Index >= list.Items.Count)
			{
				return;
			}
			string executable = (string)list.Items[e.Index];
			bool flag = (e.State & DrawItemState.Selected) != 0;
			using (SolidBrush brush = new SolidBrush(flag ? Col.Lerp(Theme.Inset, Theme.Accent, Theme.LightMode ? 0.13f : 0.16f) : Theme.Inset))
			{
				e.Graphics.FillRectangle(brush, e.Bounds);
			}
			if (flag)
			{
				using (SolidBrush brush2 = new SolidBrush(Theme.Accent))
				{
					e.Graphics.FillRectangle(brush2, e.Bounds.Left, e.Bounds.Top + Theme.S(7), Theme.S(3), e.Bounds.Height - Theme.S(14));
				}
			}
			TextRenderer.DrawText(e.Graphics, BaseName(executable), Theme.UI(9.2f, true), new Rectangle(e.Bounds.Left + Theme.S(14), e.Bounds.Top + Theme.S(7), e.Bounds.Width - Theme.S(28), Theme.S(23)), Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
			TextRenderer.DrawText(e.Graphics, executable, Theme.UI(8.5f, false), new Rectangle(e.Bounds.Left + Theme.S(14), e.Bounds.Top + Theme.S(32), e.Bounds.Width - Theme.S(28), Theme.S(22)), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
			using (Pen pen = new Pen(Theme.Stroke))
			{
				e.Graphics.DrawLine(pen, e.Bounds.Left + Theme.S(14), e.Bounds.Bottom - 1, e.Bounds.Right - Theme.S(14), e.Bounds.Bottom - 1);
			}
			if (flag)
			{
				e.DrawFocusRectangle();
			}
		}

		private void RefreshEntries(string keep)
		{
			List<string> autoAddIgnoredPaths = mode.GetAutoAddIgnoredPaths();
			list.BeginUpdate();
			try
			{
				list.Items.Clear();
				foreach (string item in autoAddIgnoredPaths)
				{
					list.Items.Add(item);
				}
				if (keep != null)
				{
					for (int i = 0; i < list.Items.Count; i++)
					{
						if (string.Equals((string)list.Items[i], keep, StringComparison.OrdinalIgnoreCase))
						{
							list.SelectedIndex = i;
							break;
						}
					}
				}
				if (list.SelectedIndex < 0 && list.Items.Count > 0)
				{
					list.SelectedIndex = 0;
				}
			}
			finally
			{
				list.EndUpdate();
			}
			Label label = empty;
			bool visible = (emptyHint.Visible = autoAddIgnoredPaths.Count == 0);
			label.Visible = visible;
			list.Visible = autoAddIgnoredPaths.Count > 0;
			listWrap.ShowEmpty = autoAddIgnoredPaths.Count == 0;
			status.Text = Lang.F("autoignore.count", autoAddIgnoredPaths.Count);
			status.ForeColor = Theme.Dim;
			UpdateSelection();
		}

		private void UpdateSelection()
		{
			string text = (list.SelectedItem as string) ?? "";
			if (path.Text != text)
			{
				path.Text = text;
				path.Select(0, 0);
				path.ScrollToCaret();
			}
			resume.Enabled = Interlocked.CompareExchange(ref changeBusy, 0, 0) == 0 && list.SelectedItem != null;
			LayoutContent();
		}

		private void QueueWork(Action action)
		{
			if (!ThreadPool.QueueUserWorkItem(delegate
			{
				action();
			}))
			{
				throw new InvalidOperationException("Work queue rejected");
			}
		}

		private void PostResult(Action action)
		{
			BeginInvoke((MethodInvoker)delegate
			{
				action();
			});
		}

		private void ResumeSelected()
		{
			string selected = list.SelectedItem as string;
			if (selected == null || base.IsDisposed || base.Disposing || Interlocked.CompareExchange(ref changeBusy, 1, 0) != 0)
			{
				return;
			}
			resume.Enabled = false;
			status.Text = Lang.T("autoignore.busy");
			status.ForeColor = Theme.Fg;
			try
			{
				QueueWork(delegate
				{
					bool saved = false;
					try
					{
						saved = mode.RemoveAutoAddIgnoredPath(selected);
					}
					catch
					{
					}
					try
					{
						PostResult(delegate
						{
							try
							{
								if (!base.IsDisposed && !base.Disposing)
								{
									RefreshEntries(selected);
									bool flag = mode.GetAutoAddIgnoredPaths().Exists((string p) => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase));
									status.Text = Lang.T((saved || !flag) ? "autoignore.removed" : "autoignore.failed");
									status.ForeColor = ((saved || !flag) ? Theme.Accent : Theme.Warning);
								}
							}
							finally
							{
								Interlocked.Exchange(ref changeBusy, 0);
								if (!base.IsDisposed && !base.Disposing)
								{
									UpdateSelection();
								}
							}
						});
					}
					catch
					{
						Interlocked.Exchange(ref changeBusy, 0);
					}
				});
			}
			catch
			{
				Interlocked.Exchange(ref changeBusy, 0);
				UpdateSelection();
				status.Text = Lang.T("autoignore.failed");
				status.ForeColor = Theme.Warning;
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (Pen pen = new Pen(Theme.StrokeHi))
			{
				e.Graphics.DrawRectangle(pen, 0, 0, base.Width - 1, base.Height - 1);
			}
			using (Pen pen2 = new Pen(Theme.Accent, Math.Max(1, Theme.S(2))))
			{
				e.Graphics.DrawLine(pen2, Theme.S(1), Theme.S(1), Theme.S(130), Theme.S(1));
				e.Graphics.DrawLine(pen2, base.Width - Theme.S(16), Theme.S(1), base.Width - Theme.S(1), Theme.S(16));
				e.Graphics.DrawLine(pen2, Theme.S(1), base.Height - Theme.S(16), Theme.S(16), base.Height - Theme.S(1));
			}
			using (Pen pen3 = new Pen(Theme.StrokeHi))
			{
				e.Graphics.DrawLine(pen3, Theme.S(24), status.Top - Theme.S(7), base.Width - Theme.S(24), status.Top - Theme.S(7));
			}
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			e.Graphics.Clear(Theme.Card);
		}
	}
}
