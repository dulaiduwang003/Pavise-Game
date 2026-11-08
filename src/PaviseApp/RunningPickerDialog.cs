using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class RunningPickerDialog : Form
	{
		internal sealed class Entry
		{
			public string Path;

			public string Title;

			public long Memory;

			public int Count;

			public bool Checked;

			public Bitmap Icon;
		}

		private sealed class ScanResult
		{
			public int Generation;

			public List<Entry> Entries;
		}

		private readonly HashSet<string> known;

		private readonly List<Entry> all = new List<Entry>();

		private readonly List<Entry> shown = new List<Entry>();

		private readonly ListBox list = new TechListBox();

		private readonly TextBox search = new TextBox();

		private readonly Label status = new Label();

		private readonly PillButton confirm;

		public readonly List<string> Selected = new List<string>();

		private readonly string confirmLabel;

		private readonly object scanGate = new object();

		private bool closed;

		private int scanGeneration;

		private ScanResult pendingScan;

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams createParams = base.CreateParams;
				createParams.ClassStyle |= 131072;
				return createParams;
			}
		}

		public RunningPickerDialog(HashSet<string> alreadyListed, string confirmText = null, string titleText = null, string descriptionText = null)
		{
			confirmLabel = confirmText ?? Lang.T("white.pick.add");
			known = ((alreadyListed == null) ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(alreadyListed, StringComparer.OrdinalIgnoreCase));
			Text = titleText ?? Lang.T("white.pick.title");
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.ShowInTaskbar = false;
			base.ClientSize = new Size(Theme.S(700), Theme.S(540));
			BackColor = Theme.Bg;
			ForeColor = Theme.Fg;
			Font = Theme.UI(9.5f, false);
			Label label = new Label();
			label.Text = Text;
			label.ForeColor = Theme.Fg;
			label.BackColor = Theme.Bg;
			label.Font = Theme.UI(14f, true);
			label.SetBounds(Theme.S(22), Theme.S(18), Theme.S(600), Theme.S(30));
			label.MouseDown += DragMove;
			Label label2 = new Label();
			label2.Text = "✕";
			label2.ForeColor = Theme.Dim;
			label2.BackColor = Theme.Bg;
			label2.SetBounds(Theme.S(662), Theme.S(12), Theme.S(26), Theme.S(26));
			label2.TextAlign = ContentAlignment.MiddleCenter;
			label2.Cursor = Cursors.Hand;
			label2.Click += delegate
			{
				base.DialogResult = DialogResult.Cancel;
				Close();
			};
			Label label3 = new Label();
			label3.Text = descriptionText ?? Lang.T("white.pick.sub");
			label3.ForeColor = Theme.Dim;
			label3.BackColor = Theme.Bg;
			label3.Font = Theme.UI(8.5f, false);
			label3.SetBounds(Theme.S(22), Theme.S(50), Theme.S(656), Theme.S(22));
			search.SetBounds(Theme.S(22), Theme.S(80), Theme.S(656), Theme.S(30));
			search.BackColor = Theme.Card;
			search.ForeColor = Theme.Fg;
			search.BorderStyle = BorderStyle.FixedSingle;
			search.TextChanged += delegate
			{
				ApplyFilter();
			};
			RoundPanel roundPanel = new RoundPanel();
			roundPanel.SetBounds(Theme.S(22), Theme.S(120), Theme.S(656), Theme.S(340));
			roundPanel.BackColor = Theme.Bg;
			roundPanel.Fill = Theme.Card;
			roundPanel.Border = Theme.Stroke;
			roundPanel.Radius = Theme.S(12);
			roundPanel.Padding = new Padding(Theme.S(8));
			list.Dock = DockStyle.Fill;
			Theme.StyleList(list, false);
			list.ItemHeight = Math.Min(255, Theme.S(52));
			list.DrawItem += DrawEntry;
			list.MouseDown += OnListMouseDown;
			roundPanel.Controls.Add(new TechListScrollHost((TechListBox)list)
			{
				Dock = DockStyle.Fill
			});
			status.SetBounds(Theme.S(22), Theme.S(470), Theme.S(400), Theme.S(24));
			status.ForeColor = Theme.Dim;
			status.BackColor = Theme.Bg;
			status.Font = Theme.UI(8.5f, false);
			status.Text = Lang.T("white.pick.busy");
			confirm = new PillButton(confirmLabel, BtnKind.Primary);
			confirm.SetBounds(Theme.S(478), Theme.S(492), Theme.S(200), Theme.S(36));
			confirm.Enabled = false;
			confirm.Click += delegate
			{
				foreach (Entry item in all)
				{
					if (item.Checked)
					{
						Selected.Add(item.Path);
					}
				}
				base.DialogResult = DialogResult.OK;
				Close();
			};
			PillButton pillButton = new PillButton(Lang.T("btn.cancel"));
			pillButton.SetBounds(Theme.S(262), Theme.S(492), Theme.S(200), Theme.S(36));
			pillButton.Click += delegate
			{
				base.DialogResult = DialogResult.Cancel;
				Close();
			};
			base.Controls.AddRange(new Control[8] { label, label2, label3, search, roundPanel, status, confirm, pillButton });
			base.MouseDown += DragMove;
			base.KeyPreview = true;
			base.KeyDown += delegate(object s, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Escape)
				{
					base.DialogResult = DialogResult.Cancel;
					Close();
				}
			};
			base.Shown += delegate
			{
				BeginScan();
			};
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			using (Pen pen = new Pen(Theme.Accent))
			{
				e.Graphics.DrawRectangle(pen, 0, 0, base.Width - 1, base.Height - 1);
			}
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			Native.RoundCorners(base.Handle);
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			Fx.EnterForm(this);
		}

		private void DragMove(object s, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				Native.ReleaseCapture();
				Native.SendMessage(base.Handle, 161, (IntPtr)2, IntPtr.Zero);
			}
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			CancelScan();
			base.OnFormClosed(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				CancelScan();
				DisposeEntries(all);
				all.Clear();
				shown.Clear();
			}
			base.Dispose(disposing);
		}

		private void CancelScan()
		{
			ScanResult scanResult;
			lock (scanGate)
			{
				closed = true;
				scanGeneration++;
				scanResult = pendingScan;
				pendingScan = null;
			}
			if (scanResult != null)
			{
				DisposeEntries(scanResult.Entries);
			}
		}

		private bool ScanCanceled(int generation)
		{
			lock (scanGate)
			{
				return closed || generation != scanGeneration;
			}
		}

		private static void DisposeEntries(IEnumerable<Entry> entries)
		{
			if (entries == null)
			{
				return;
			}
			foreach (Entry entry in entries)
			{
				if (entry != null && entry.Icon != null)
				{
					try
					{
						entry.Icon.Dispose();
					}
					catch
					{
					}
					entry.Icon = null;
				}
			}
		}

		private void BeginScan()
		{
			int generation;
			ScanResult scanResult;
			lock (scanGate)
			{
				if (closed)
				{
					return;
				}
				generation = ++scanGeneration;
				scanResult = pendingScan;
				pendingScan = null;
			}
			if (scanResult != null)
			{
				DisposeEntries(scanResult.Entries);
			}
			ThreadPool.QueueUserWorkItem(delegate
			{
				List<Entry> list = null;
				try
				{
					try
					{
						list = Scan(known, () => ScanCanceled(generation));
					}
					catch
					{
					}
					if (list != null)
					{
						foreach (Entry item in list)
						{
							if (ScanCanceled(generation))
							{
								return;
							}
							item.Icon = LoadIcon(item.Path);
						}
					}
					if (!ScanCanceled(generation))
					{
						ScanResult result = new ScanResult
						{
							Generation = generation,
							Entries = list
						};
						if (QueueScanResult(result))
						{
							list = null;
						}
					}
				}
				finally
				{
					DisposeEntries(list);
				}
			});
		}

		private bool QueueScanResult(ScanResult result)
		{
			lock (scanGate)
			{
				if (closed || result.Generation != scanGeneration)
				{
					return false;
				}
				pendingScan = result;
				try
				{
					BeginInvoke((MethodInvoker)delegate
					{
						ApplyScanResult(result);
					});
					return true;
				}
				catch
				{
					if (object.ReferenceEquals(pendingScan, result))
					{
						pendingScan = null;
					}
					return false;
				}
			}
		}

		private void ApplyScanResult(ScanResult result)
		{
			lock (scanGate)
			{
				if (!object.ReferenceEquals(pendingScan, result))
				{
					return;
				}
				pendingScan = null;
				if (closed || result.Generation != scanGeneration)
				{
					DisposeEntries(result.Entries);
					return;
				}
				if (result.Entries == null)
				{
					status.Text = Lang.T("white.pick.failed");
					confirm.Enabled = false;
					return;
				}
				DisposeEntries(all);
				all.Clear();
				all.AddRange(result.Entries);
				result.Entries = null;
			}
			status.Text = ((all.Count == 0) ? Lang.T("white.pick.none") : Lang.F("white.pick.count", all.Count));
			confirm.Enabled = false;
			ApplyFilter();
		}

		internal static List<Entry> Scan(HashSet<string> exclude)
		{
			return Scan(exclude, null);
		}

		private static List<Entry> Scan(HashSet<string> exclude, Func<bool> canceled)
		{
			List<RunningProgram> list = RunningProgramDiscovery.ScanForWhitelist(canceled);
			if (list == null)
			{
				return null;
			}
			List<Entry> list2 = new List<Entry>();
			foreach (RunningProgram item in list)
			{
				if (canceled != null && canceled())
				{
					return null;
				}
				if (exclude == null || !exclude.Contains(item.Path))
				{
					list2.Add(new Entry
					{
						Path = item.Path,
						Title = item.Title,
						Memory = item.Memory,
						Count = item.ProcessIds.Count
					});
				}
			}
			list2.Sort((Entry a, Entry b) => b.Memory.CompareTo(a.Memory));
			return list2;
		}

		private static Bitmap LoadIcon(string path)
		{
			try
			{
				using (Icon icon = Icon.ExtractAssociatedIcon(path))
				{
					return icon.ToBitmap();
				}
			}
			catch
			{
				return null;
			}
		}

		private void ApplyFilter()
		{
			string textTrimmed = search.Text.Trim();
			shown.Clear();
			foreach (Entry item in all)
			{
				if (textTrimmed.Length <= 0 || item.Title.IndexOf(textTrimmed, StringComparison.OrdinalIgnoreCase) >= 0 || item.Path.IndexOf(textTrimmed, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					shown.Add(item);
				}
			}
			list.BeginUpdate();
			list.Items.Clear();
			foreach (Entry item2 in shown)
			{
				list.Items.Add(item2);
			}
			list.EndUpdate();
		}

		private void OnListMouseDown(object sender, MouseEventArgs e)
		{
			if (e.Button != MouseButtons.Left)
			{
				return;
			}
			int indexFromPointResult = list.IndexFromPoint(e.Location);
			if (indexFromPointResult < 0 || indexFromPointResult >= shown.Count)
			{
				return;
			}
			shown[indexFromPointResult].Checked = !shown[indexFromPointResult].Checked;
			list.Invalidate();
			int processedCount = 0;
			foreach (Entry item in all)
			{
				if (item.Checked)
				{
					processedCount++;
				}
			}
			confirm.Text = ((processedCount > 0) ? Lang.F("white.pick.add.n", processedCount) : confirmLabel);
			confirm.Enabled = processedCount > 0;
			confirm.Invalidate();
		}

		private void DrawEntry(object sender, DrawItemEventArgs e)
		{
			if (e.Index < 0 || e.Index >= shown.Count)
			{
				return;
			}
			Entry entry = shown[e.Index];
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle bounds = e.Bounds;
			bool anyConditionMet = (e.State & DrawItemState.Selected) != 0 || e.Index == Theme.HoverIndex(list);
			using (SolidBrush brush = new SolidBrush(entry.Checked ? Theme.Sel : (anyConditionMet ? Theme.CardHover : Theme.Card)))
			{
				graphics.FillRectangle(brush, bounds);
			}
			int scaledDimension = Theme.S(16);
			Rectangle r = new Rectangle(bounds.Left + Theme.S(14), bounds.Top + (bounds.Height - scaledDimension) / 2, scaledDimension, scaledDimension);
			using (GraphicsPath path = Theme.Rounded(r, Theme.S(4)))
			{
				using (SolidBrush brush2 = new SolidBrush(entry.Checked ? Theme.Accent : Theme.Bg))
				{
					graphics.FillPath(brush2, path);
				}
				using (Pen pen = new Pen(entry.Checked ? Theme.Accent : Theme.StrokeHi))
				{
					graphics.DrawPath(pen, path);
				}
			}
			if (entry.Checked)
			{
				using (Pen pen2 = new Pen(Theme.OnAccent, Math.Max(1.4f, Theme.S(2))))
				{
					graphics.DrawLines(pen2, new Point[3]
					{
						new Point(r.Left + Theme.S(4), r.Top + Theme.S(8)),
						new Point(r.Left + Theme.S(7), r.Top + Theme.S(11)),
						new Point(r.Left + Theme.S(12), r.Top + Theme.S(5))
					});
				}
			}
			int scaledDimension2 = Theme.S(26);
			Rectangle rect = new Rectangle(r.Right + Theme.S(12), bounds.Top + (bounds.Height - scaledDimension2) / 2, scaledDimension2, scaledDimension2);
			if (entry.Icon != null)
			{
				graphics.DrawImage(entry.Icon, rect);
			}
			int scaledDimension3 = Theme.S(90);
			int rightOffset = rect.Right + Theme.S(12);
			int remainingS = bounds.Right - rightOffset - scaledDimension3 - Theme.S(24);
			TextRenderer.DrawText(graphics, entry.Title, Theme.UI(9.2f, true), new Rectangle(rightOffset, bounds.Top + Theme.S(6), remainingS, Theme.S(20)), Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			TextRenderer.DrawText(graphics, entry.Path, Theme.UI(7.6f, false), new Rectangle(rightOffset, bounds.Top + Theme.S(25), remainingS, Theme.S(18)), Theme.Faint, TextFormatFlags.PathEllipsis | TextFormatFlags.VerticalCenter);
			string formatMemoryOffset = FormatMemory(entry.Memory) + ((entry.Count > 1) ? (" " + Lang.F("white.pick.procs", entry.Count)) : "");
			TextRenderer.DrawText(graphics, formatMemoryOffset, Theme.UI(7.8f, false), new Rectangle(bounds.Right - scaledDimension3 - Theme.S(14), bounds.Top, scaledDimension3, bounds.Height), Theme.Dim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
		}

		internal static string FormatMemory(long bytes)
		{
			return RunningProgram.FormatMemory(bytes);
		}
	}
}
