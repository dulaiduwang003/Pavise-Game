using System;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class ModePickerPanel : RoundPanel
	{
		private readonly ModeChoice[] choices;

		private readonly Label source;

		public Action<PerformancePreset> ModeChosen;

		public ModePickerPanel()
		{
			UseBackdrop = false;
			Fill = Theme.Card;
			Border = Theme.StrokeHi;
			BackColor = Theme.Bg;
			Radius = Theme.S(14);
			AccentEdge = true;
			Label label = new Label
			{
				Text = Lang.T("mode.global.title"),
				ForeColor = Theme.Fg,
				BackColor = Theme.Card,
				Font = Theme.UI(11f, true),
				AutoEllipsis = true
			};
			label.UseCompatibleTextRendering = false;
			label.SetBounds(Theme.S(18), Theme.S(13), Theme.S(356), Theme.S(25));
			source = new Label
			{
				Text = Lang.T("mode.global.sub"),
				ForeColor = Theme.Dim,
				BackColor = Theme.Card,
				Font = Theme.UI(7.8f, false),
				AutoEllipsis = true
			};
			source.UseCompatibleTextRendering = false;
			source.SetBounds(Theme.S(18), Theme.S(38), Theme.S(356), Theme.S(19));
			base.Controls.AddRange(new Control[2] { label, source });
			PerformancePreset[] array = PresetValue.VisibleOrder();
			choices = new ModeChoice[array.Length];
			for (int i = 0; i < array.Length; i++)
			{
				choices[i] = new ModeChoice(array[i]);
			}
			for (int j = 0; j < choices.Length; j++)
			{
				choices[j].SetBounds(Theme.S(14), Theme.S(66 + j * 70), Theme.S(368), Theme.S(62));
				choices[j].Chosen = Choose;
				base.Controls.Add(choices[j]);
			}
		}

		private void Choose(PerformancePreset mode)
		{
			if (ModeChosen != null)
			{
				ModeChosen(mode);
			}
		}

		public void Sync(PerformancePreset global)
		{
			for (int i = 0; i < choices.Length; i++)
			{
				choices[i].SetSelected(choices[i].Mode == global);
			}
			source.Text = Lang.T("mode.global.sub");
		}
	}
}
