using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PlanComparison : DBPanel
	{
		private sealed class PreviewPane : DBPanel
		{
			internal readonly Label Caption = new Label();

			internal readonly Label Value = new Label();

			internal readonly Label Detail = new Label();

			private Color tone;

			private bool compact;

			internal bool Compact
			{
				get
				{
					return compact;
				}
				set
				{
					if (compact != value)
					{
						compact = value;
						ArrangeContents();
						Invalidate();
					}
				}
			}

			internal PreviewPane()
			{
				BackColor = Theme.Card;
				Label[] array = new Label[3] { Caption, Value, Detail };
				foreach (Label label in array)
				{
					label.BackColor = Color.Transparent;
					label.AutoSize = false;
					label.UseMnemonic = false;
					base.Controls.Add(label);
				}
				Caption.Font = Theme.UI(8.5f, false);
				Caption.ForeColor = Theme.Dim;
				Value.Font = Theme.UI(11f, true);
				Detail.Font = Theme.UI(8.5f, false);
				Detail.ForeColor = Theme.Dim;
			}

			internal bool SetState(string caption, string value, string detail, Color color)
			{
				if (Caption.Text == (caption ?? "") && Value.Text == (value ?? "") && Detail.Text == (detail ?? "") && tone == color)
				{
					return false;
				}
				Caption.Text = caption ?? "";
				Value.Text = value ?? "";
				Detail.Text = detail ?? "";
				tone = color;
				Value.ForeColor = color;
				base.AccessibleName = Caption.Text;
				base.AccessibleDescription = Value.Text + " · " + Detail.Text;
				ArrangeContents();
				Invalidate();
				return true;
			}

			private static int TextHeight(Label label, int width)
			{
				return TextRenderer.MeasureText((label.Text.Length == 0) ? "Ag" : label.Text, label.Font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Height;
			}

			internal int RequiredHeight(int width)
			{
				if (compact)
				{
					return TextHeight(Caption, width) + Theme.S(6) + TextHeight(Value, width) + Theme.S(5) + TextHeight(Detail, width) + Theme.S(4);
				}
				int num = width - Theme.S(28);
				return Theme.S(38) + TextHeight(Value, num) + Theme.S(6) + TextHeight(Detail, num) + Theme.S(14);
			}

			protected override void OnSizeChanged(EventArgs e)
			{
				base.OnSizeChanged(e);
				ArrangeContents();
			}

			private void ArrangeContents()
			{
				if (Caption != null && Value != null && Detail != null)
				{
					if (compact)
					{
						int num = Math.Max(1, base.Width);
						Caption.SetBounds(0, 0, num, TextHeight(Caption, num));
						Value.SetBounds(0, Caption.Bottom + Theme.S(6), num, TextHeight(Value, num));
						Detail.SetBounds(0, Value.Bottom + Theme.S(5), num, TextHeight(Detail, num));
					}
					else
					{
						int num2 = Theme.S(14);
						int num3 = Math.Max(1, base.Width - 2 * num2);
						Caption.SetBounds(num2, Theme.S(11), num3, Theme.S(19));
						Value.SetBounds(num2, Theme.S(36), num3, TextHeight(Value, num3));
						Detail.SetBounds(num2, Value.Bottom + Theme.S(6), num3, TextHeight(Detail, num3));
					}
				}
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				base.OnPaint(e);
				if (compact)
				{
					return;
				}
				Rectangle r = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
				if (r.Width <= 0 || r.Height <= 0)
				{
					return;
				}
				e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
				Theme.FillRound(e.Graphics, r, Theme.S(6), Col.Lerp(Theme.Card, tone, 0.045f));
				using (GraphicsPath path = Theme.Rounded(r, Theme.S(6)))
				{
					using (Pen pen = new Pen(Theme.Stroke))
					{
						e.Graphics.DrawPath(pen, path);
					}
				}
			}

			protected override void OnPaintBackground(PaintEventArgs e)
			{
				if (!compact)
				{
					base.OnPaintBackground(e);
					return;
				}
				using (SolidBrush brush = new SolidBrush(Theme.Card))
				{
					e.Graphics.FillRectangle(brush, base.ClientRectangle);
				}
			}
		}

		private readonly PreviewPane current = new PreviewPane();

		private readonly PreviewPane saved = new PreviewPane();

		private bool arranging;

		private bool compact;

		internal bool Compact
		{
			get
			{
				return compact;
			}
			set
			{
				if (compact != value)
				{
					compact = value;
					PreviewPane previewPane = current;
					bool flag2 = (saved.Compact = value);
					previewPane.Compact = flag2;
					Arrange();
					Invalidate();
				}
			}
		}

		internal string CurrentText
		{
			get
			{
				return current.Value.Text + "\n" + current.Detail.Text;
			}
		}

		internal string SavedText
		{
			get
			{
				return saved.Value.Text + "\n" + saved.Detail.Text;
			}
		}

		internal PlanComparison()
		{
			BackColor = Theme.Card;
			base.AccessibleRole = AccessibleRole.Grouping;
			current.Name = "CurrentPlan";
			saved.Name = "SavedPlan";
			base.Controls.Add(current);
			base.Controls.Add(saved);
		}

		internal void SetState(string currentCaption, string currentValue, string currentDetail, Color currentTone, string savedCaption, string savedValue, string savedDetail, Color savedTone)
		{
			bool flag = current.SetState(currentCaption, currentValue, currentDetail, currentTone);
			if (saved.SetState(savedCaption, savedValue, savedDetail, savedTone) || flag)
			{
				Arrange();
			}
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Arrange();
		}

		private void Arrange()
		{
			if (arranging || current == null || saved == null || base.Width < (compact ? 1 : Theme.S(80)))
			{
				return;
			}
			arranging = true;
			try
			{
				int num4;
				if (compact && base.Width < Theme.S(310))
				{
					int num = Theme.S(18);
					int num2 = saved.RequiredHeight(base.Width);
					int num3 = current.RequiredHeight(base.Width);
					saved.SetBounds(0, 0, base.Width, num2);
					current.SetBounds(0, num2 + num, base.Width, num3);
					num4 = current.Bottom;
				}
				else
				{
					int num5 = Theme.S(compact ? 24 : 12);
					int num6 = (base.Width - num5) / 2;
					num4 = (compact ? Math.Max(saved.RequiredHeight(num6), current.RequiredHeight(base.Width - num6 - num5)) : Math.Max(current.RequiredHeight(num6), saved.RequiredHeight(base.Width - num6 - num5)));
					if (compact)
					{
						saved.SetBounds(0, 0, num6, num4);
						current.SetBounds(num6 + num5, 0, base.Width - num6 - num5, num4);
					}
					else
					{
						current.SetBounds(0, 0, num6, num4);
						saved.SetBounds(num6 + num5, 0, base.Width - num6 - num5, num4);
					}
				}
				if (base.Height != num4)
				{
					base.Height = num4;
				}
				if (compact)
				{
					Invalidate();
				}
			}
			finally
			{
				arranging = false;
			}
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			if (!compact)
			{
				base.OnPaintBackground(e);
				return;
			}
			using (SolidBrush brush = new SolidBrush(Theme.Card))
			{
				e.Graphics.FillRectangle(brush, base.ClientRectangle);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			if (!compact)
			{
				return;
			}
			using (Pen pen = new Pen(Col.Lerp(Theme.Card, Theme.StrokeHi, 0.65f)))
			{
				if (base.Width < Theme.S(310))
				{
					int num = saved.Bottom + Theme.S(9);
					e.Graphics.DrawLine(pen, 0, num, base.Width - 1, num);
				}
				else
				{
					int num2 = saved.Right + Theme.S(12);
					e.Graphics.DrawLine(pen, num2, Theme.S(3), num2, Math.Max(Theme.S(3), base.Height - Theme.S(3)));
				}
			}
		}
	}
}
