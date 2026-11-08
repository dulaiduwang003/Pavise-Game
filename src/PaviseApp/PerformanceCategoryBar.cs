using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PerformanceCategoryBar : Control
	{
		private readonly List<PerformanceActionButton> buttons = new List<PerformanceActionButton>();
		private readonly string[] fullLabels;
		private readonly ToolTip tips = new ToolTip();

		private int selected;

		internal Action<int> SelectionChanged;

		internal Rectangle[] TabBounds
		{
			get
			{
				return buttons.ConvertAll((PerformanceActionButton button) => button.Bounds).ToArray();
			}
		}

		internal PerformanceCategoryBar(string[] labels)
		{
			fullLabels = (string[])labels.Clone();
			BackColor = Theme.Bg;
			base.AccessibleRole = AccessibleRole.PageTabList;
			for (int i = 0; i < labels.Length; i++)
			{
				int index = i;
				PerformanceActionButton button = new PerformanceActionButton(labels[i])
				{
					AccessibleRole = AccessibleRole.PageTab,
					TabIndex = i,
					IsCategoryTab = true,
					CategoryNumber = i + 1
				};
				button.AccessibleName = labels[i];
				button.Click += delegate
				{
					Select(index, true);
				};
				button.KeyDown += delegate(object sender, KeyEventArgs e)
				{
					if (!e.Handled && e.Modifiers == Keys.None && button.CanInteract)
					{
						int selectedSnapshot = selected;
						if (e.KeyCode == Keys.Right)
						{
							selectedSnapshot = Math.Min(buttons.Count - 1, selected + 1);
						}
						else if (e.KeyCode == Keys.Left)
						{
							selectedSnapshot = Math.Max(0, selected - 1);
						}
						else if (e.KeyCode == Keys.Home)
						{
							selectedSnapshot = 0;
						}
						else
						{
							if (e.KeyCode != Keys.End)
							{
								return;
							}
							selectedSnapshot = buttons.Count - 1;
						}
						e.Handled = true;
						e.SuppressKeyPress = true;
						Select(selectedSnapshot, true);
						buttons[selectedSnapshot].Focus();
					}
				};
				buttons.Add(button);
				tips.SetToolTip(button, labels[i]);
				base.Controls.Add(button);
			}
			Select(0, false);
		}

		internal void Select(int index, bool notify)
		{
			if (index >= 0 && index < buttons.Count)
			{
				bool matchesSelected = selected != index;
				selected = index;
				for (int i = 0; i < buttons.Count; i++)
				{
					buttons[i].Kind = ((i == index) ? BtnKind.Primary : BtnKind.Normal);
					buttons[i].CurrentCategory = i == index;
					buttons[i].AccessibleDescription = PerformancePolicy.Text((i == index) ? "当前分类" : "切换分类", (i == index) ? "Current category" : "Switch category");
					buttons[i].TabStop = i == index;
					buttons[i].Invalidate();
				}
				if (matchesSelected && notify && SelectionChanged != null)
				{
					SelectionChanged(index);
				}
			}
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			if (buttons.Count == 0 || Width < 1) return;
			int gap = Theme.S(6);
			int total = gap * Math.Max(0, buttons.Count - 1);
			int[] widths = new int[buttons.Count];
			for (int i = 0; i < buttons.Count; i++)
			{
				widths[i] = Math.Max(Theme.S(124), TextRenderer.MeasureText(fullLabels[i], Theme.UI(10f, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + Theme.S(44));
				total += widths[i];
			}
			bool compact = total > Width;
			float textSize = 10f;
			string[] shortLabels = Lang.Cur == 0 ? new[] { "游戏", "后台", "图形", "系统", "实验" } : new[] { "Play", "BG", "GPU", "Sys", "Lab" };
			if (compact)
			{
				// Measure each label independently to avoid clipping at high DPI.
				while (true)
				{
					total = gap * Math.Max(0, buttons.Count - 1);
					for (int i = 0; i < buttons.Count; i++)
					{
						widths[i] = TextRenderer.MeasureText(shortLabels[i], Theme.UI(textSize, true), Size.Empty,
							TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + Theme.S(12);
						total += widths[i];
					}
					if (total <= Width || textSize <= 8f) break;
					textSize -= 0.5f;
				}
			}
			int x = 0;
			int spare = compact ? Math.Max(0, Width - total) / buttons.Count : 0;
			for (int j = 0; j < buttons.Count; j++)
			{
				buttons[j].CompactCategory = compact;
				buttons[j].Text = compact ? shortLabels[j] : fullLabels[j];
				buttons[j].CategoryTextSize = textSize;
				int width = widths[j] + spare;
				if (compact && j == buttons.Count - 1) width = Math.Max(1, Width - x);
				buttons[j].SetBounds(x, 0, width, Height);
				x += width + gap;
			}
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			WorkspaceCanvasSurface.Paint(e.Graphics, this, e.ClipRectangle);
			using (Pen pen = new Pen(Col.Alpha(Theme.Stroke, 135)))
			{
				e.Graphics.DrawLine(pen, 0, base.Height - 1, base.Width, base.Height - 1);
			}
		}
		protected override void Dispose(bool disposing)
		{
			if (disposing) tips.Dispose();
			base.Dispose(disposing);
		}
	}
}
