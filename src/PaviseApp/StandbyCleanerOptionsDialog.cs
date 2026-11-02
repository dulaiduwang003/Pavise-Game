using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class StandbyCleanerOptionsDialog : Form
	{
		private readonly TextBox listBox;

		private readonly TextBox freeBox;

		private readonly TextBox pollingBox;

		private readonly Label errorLabel;

		private readonly LibraryDialogButton cancelButton;

		private readonly Func<StandbyCleanerOptions, bool> save;

		private readonly int dividerY;

		internal StandbyCleanerOptionsDialog(StandbyCleanerOptions initial, Func<StandbyCleanerOptions, bool> saveOptions, bool repairing = false)
		{
			if (saveOptions == null)
			{
				throw new ArgumentNullException("saveOptions");
			}
			save = saveOptions;
			base.AutoScaleMode = AutoScaleMode.None;
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.MinimizeBox = false;
			base.MaximizeBox = false;
			base.ShowInTaskbar = false;
			BackColor = Theme.Bg;
			ForeColor = Theme.Fg;
			Font = Theme.UI(9f, false);
			DoubleBuffered = true;
			Text = Lang.T("standbycleaner.options.title");
			base.AccessibleName = Text;
			Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
			int num = Math.Min(Theme.S(620), Math.Max(Theme.S(360), workingArea.Width - Theme.S(40)));
			int num2 = Theme.S(26);
			int num3 = num - num2 * 2;
			Label label = MakeLabel("MEMORY // OPTIONS", Theme.Mono(7.3f), Theme.Accent);
			label.SetBounds(num2, Theme.S(21), num3, Theme.S(19));
			Label label2 = MakeLabel(Text, Theme.UI(15f, true), Theme.Fg);
			int val = Measure(Text, label2.Font, num3);
			label2.SetBounds(num2, Theme.S(48), num3, Math.Max(Theme.S(34), val));
			DBPanel dBPanel = new DBPanel
			{
				BackColor = Theme.Bg,
				AutoScroll = true,
				TabStop = false
			};
			int num4 = num3 - SystemInformation.VerticalScrollBarWidth - Theme.S(4);
			int num5 = 0;
			if (repairing)
			{
				AddBodyText(dBPanel, Lang.T("standbycleaner.options.repair"), num4, ref num5);
			}
			AddBodyText(dBPanel, Lang.T("standbycleaner.options.hint"), num4, ref num5);
			num5 = AddPresetRow(dBPanel, num4, num5);
			listBox = AddField(dBPanel, "standbycleaner.options.list", "standbycleaner.options.list.sub", num4, 0, ref num5);
			freeBox = AddField(dBPanel, "standbycleaner.options.free", "standbycleaner.options.free.sub", num4, 1, ref num5);
			pollingBox = AddField(dBPanel, "standbycleaner.options.poll", "standbycleaner.options.poll.sub", num4, 2, ref num5);
			AddBodyText(dBPanel, Lang.T("standbycleaner.options.units"), num4, ref num5);
			dBPanel.AutoScrollMinSize = new Size(0, num5);
			Native.Dark(dBPanel);
			errorLabel = MakeLabel("", Theme.UI(9f, false), Color.FromArgb(255, 176, 32));
			errorLabel.AccessibleName = Lang.T("standbycleaner.options.invalid");
			int num6 = Math.Max(Measure(Lang.T("standbycleaner.options.invalid"), errorLabel.Font, num3), Measure(Lang.T("standbycleaner.options.savefailed"), errorLabel.Font, num3)) + Theme.S(4);
			int num7 = label2.Bottom + Theme.S(16);
			int num8 = num7 + num6 + Theme.S(92);
			int num9 = Math.Min(num5, Math.Max(Theme.S(40), workingArea.Height - Theme.S(40) - num8));
			dBPanel.SetBounds(num2, num7, num3, num9);
			errorLabel.SetBounds(num2, dBPanel.Bottom + Theme.S(8), num3, num6);
			int num10 = errorLabel.Bottom + Theme.S(24);
			int num11 = Theme.S(36);
			dividerY = num10 - Theme.S(13);
			cancelButton = new LibraryDialogButton(Lang.T("dlg.cancel"), false);
			cancelButton.SetBounds(num - num2 - Theme.S(232), num10, Theme.S(108), num11);
			cancelButton.TabIndex = 6;
			cancelButton.DialogResult = DialogResult.Cancel;
			LibraryDialogButton libraryDialogButton = new LibraryDialogButton(Lang.T("standbycleaner.options.save"), true);
			libraryDialogButton.SetBounds(num - num2 - Theme.S(108), num10, Theme.S(108), num11);
			libraryDialogButton.TabIndex = 7;
			libraryDialogButton.Click += delegate
			{
				SaveInputs();
			};
			base.ClientSize = new Size(num, num10 + num11 + Theme.S(24));
			base.Controls.AddRange(new Control[6] { label, label2, dBPanel, errorLabel, cancelButton, libraryDialogButton });
			base.AcceptButton = libraryDialogButton;
			base.CancelButton = cancelButton;
			SetInputs((initial != null && initial.IsValid) ? initial : StandbyCleanerOptions.Default);
			base.ActiveControl = listBox;
		}

		private int AddPresetRow(Control parent, int width, int y)
		{
			Label label = MakeLabel(Lang.T("standbycleaner.options.preset"), Theme.UI(9f, true), Theme.Fg);
			label.SetBounds(0, y, width, Measure(label.Text, label.Font, width) + Theme.S(4));
			parent.Controls.Add(label);
			y = label.Bottom + Theme.S(6);
			string[] array = new string[3] { "standbycleaner.options.preset.light", "standbycleaner.options.preset.standard", "standbycleaner.options.preset.heavy" };
			StandbyCleanerOptions[] array2 = new StandbyCleanerOptions[3]
			{
				StandbyCleanerOptions.Conservative,
				StandbyCleanerOptions.Default,
				StandbyCleanerOptions.Aggressive
			};
			int num = Theme.S(10);
			int num2 = (width - num * 2) / 3;
			int num3 = Theme.S(30);
			int num4 = 0;
			for (int i = 0; i < array.Length; i++)
			{
				StandbyCleanerOptions preset = array2[i];
				PillButton pillButton = new PillButton(Lang.T(array[i]), BtnKind.Normal);
				pillButton.SetBounds(num4, y, num2, num3);
				pillButton.TabIndex = 3 + i;
				pillButton.AccessibleName = Lang.T(array[i]);
				pillButton.Click += delegate
				{
					SetInputs(preset);
					errorLabel.Text = "";
				};
				parent.Controls.Add(pillButton);
				num4 += num2 + num;
			}
			return y + num3 + Theme.S(14);
		}

		internal static bool TryParseInput(string list, string free, string polling, out StandbyCleanerOptions options)
		{
			options = null;
			int value;
			int value2;
			int value3;
			if (!TryInteger(list, 0, 1048576, out value) || !TryInteger(free, 0, 1048576, out value2) || !TryInteger(polling, 250, 300000, out value3))
			{
				return false;
			}
			options = new StandbyCleanerOptions(value, value2, value3);
			return true;
		}

		private static bool TryInteger(string text, int minimum, int maximum, out int value)
		{
			if (int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum)
			{
				return value <= maximum;
			}
			return false;
		}

		private void SetInputs(StandbyCleanerOptions value)
		{
			listBox.Text = value.ListMegabytes.ToString(CultureInfo.InvariantCulture);
			freeBox.Text = value.FreeMegabytes.ToString(CultureInfo.InvariantCulture);
			pollingBox.Text = value.PollingMilliseconds.ToString(CultureInfo.InvariantCulture);
		}

		private void SaveInputs()
		{
			StandbyCleanerOptions options;
			if (!TryParseInput(listBox.Text, freeBox.Text, pollingBox.Text, out options))
			{
				errorLabel.Text = Lang.T("standbycleaner.options.invalid");
				return;
			}
			bool flag;
			try
			{
				flag = save(options);
			}
			catch
			{
				flag = false;
			}
			if (!flag)
			{
				errorLabel.Text = Lang.T("standbycleaner.options.savefailed");
				return;
			}
			base.DialogResult = DialogResult.OK;
			Close();
		}

		private static int Measure(string text, Font font, int width)
		{
			return TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height;
		}

		private static Label MakeLabel(string text, Font font, Color color)
		{
			Label label = new Label();
			label.Text = text;
			label.Font = font;
			label.ForeColor = color;
			label.BackColor = Color.Transparent;
			label.UseCompatibleTextRendering = false;
			label.UseMnemonic = false;
			return label;
		}

		private static void AddBodyText(Control parent, string text, int width, ref int y)
		{
			Label label = MakeLabel(text, Theme.UI(9f, false), Theme.Dim);
			label.SetBounds(0, y, width, Measure(text, label.Font, width) + Theme.S(4));
			parent.Controls.Add(label);
			y = label.Bottom + Theme.S(14);
		}

		private static TextBox AddField(Control parent, string titleKey, string detailKey, int width, int tabIndex, ref int y)
		{
			int num = Theme.S(136);
			int num2 = width - num - Theme.S(16);
			Label label = MakeLabel(Lang.T(titleKey), Theme.UI(9f, true), Theme.Fg);
			int val = Math.Max(Theme.S(24), Measure(label.Text, label.Font, num2));
			label.SetBounds(0, y, num2, val);
			TextBox textBox = Theme.MakeTextBox(width - num, y, num);
			textBox.TextAlign = HorizontalAlignment.Right;
			textBox.MaxLength = 10;
			textBox.TabIndex = tabIndex;
			textBox.AccessibleName = Lang.T(titleKey);
			Label label2 = MakeLabel(Lang.T(detailKey), Theme.UI(9f, false), Theme.Dim);
			label2.SetBounds(0, y + Math.Max(val, textBox.Height) + Theme.S(3), width, Measure(label2.Text, label2.Font, width) + Theme.S(4));
			parent.Controls.AddRange(new Control[3] { label, textBox, label2 });
			y = label2.Bottom + Theme.S(14);
			return textBox;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (GraphicsPath path = Theme.TechPath(new Rectangle(0, 0, base.Width - 1, base.Height - 1), Theme.S(12)))
			{
				using (Pen pen = new Pen(Theme.StrokeHi))
				{
					graphics.DrawPath(pen, path);
				}
			}
			using (Pen pen2 = new Pen(Theme.Accent, Math.Max(1, Theme.S(2))))
			{
				graphics.DrawLine(pen2, Theme.S(1), Theme.S(1), Theme.S(116), Theme.S(1));
			}
			using (Pen pen3 = new Pen(Theme.Stroke))
			{
				graphics.DrawLine(pen3, Theme.S(26), dividerY, base.Width - Theme.S(26), dividerY);
			}
		}
	}
}
