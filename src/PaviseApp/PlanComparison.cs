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
				Label[] labelItems = new Label[3] { Caption, Value, Detail };
				foreach (Label label in labelItems)
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
				int remainingWidth = width - Theme.S(28);
				return Theme.S(38) + TextHeight(Value, remainingWidth) + Theme.S(6) + TextHeight(Detail, remainingWidth) + Theme.S(14);
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
						int width = Math.Max(1, base.Width);
						Caption.SetBounds(0, 0, width, TextHeight(Caption, width));
						Value.SetBounds(0, Caption.Bottom + Theme.S(6), width, TextHeight(Value, width));
						Detail.SetBounds(0, Value.Bottom + Theme.S(5), width, TextHeight(Detail, width));
					}
					else
					{
						int left = Theme.S(14);
						int width2 = Math.Max(1, base.Width - 2 * left);
						Caption.SetBounds(left, Theme.S(11), width2, Theme.S(19));
						Value.SetBounds(left, Theme.S(36), width2, TextHeight(Value, width2));
						Detail.SetBounds(left, Value.Bottom + Theme.S(6), width2, TextHeight(Detail, width2));
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
					bool compactValue = (saved.Compact = value);
					previewPane.Compact = compactValue;
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
			bool setStateSucceeded = current.SetState(currentCaption, currentValue, currentDetail, currentTone);
			if (saved.SetState(savedCaption, savedValue, savedDetail, savedTone) || setStateSucceeded)
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
				int height;
				if (compact && base.Width < Theme.S(310))
				{
					int scaledDimension = Theme.S(18);
					int height2 = saved.RequiredHeight(base.Width);
					int height3 = current.RequiredHeight(base.Width);
					saved.SetBounds(0, 0, base.Width, height2);
					current.SetBounds(0, height2 + scaledDimension, base.Width, height3);
					height = current.Bottom;
				}
				else
				{
					int scaledDimension2 = Theme.S(compact ? 24 : 12);
					int width = (base.Width - scaledDimension2) / 2;
					height = (compact ? Math.Max(saved.RequiredHeight(width), current.RequiredHeight(base.Width - width - scaledDimension2)) : Math.Max(current.RequiredHeight(width), saved.RequiredHeight(base.Width - width - scaledDimension2)));
					if (compact)
					{
						saved.SetBounds(0, 0, width, height);
						current.SetBounds(width + scaledDimension2, 0, base.Width - width - scaledDimension2, height);
					}
					else
					{
						current.SetBounds(0, 0, width, height);
						saved.SetBounds(width + scaledDimension2, 0, base.Width - width - scaledDimension2, height);
					}
				}
				if (base.Height != height)
				{
					base.Height = height;
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
					int top = saved.Bottom + Theme.S(9);
					e.Graphics.DrawLine(pen, 0, top, base.Width - 1, top);
				}
				else
				{
					int left = saved.Right + Theme.S(12);
					e.Graphics.DrawLine(pen, left, Theme.S(3), left, Math.Max(Theme.S(3), base.Height - Theme.S(3)));
				}
			}
		}
	}
}
