using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class DataOperationProgressCard : DBPanel
	{
		internal readonly Label Status;

		internal DataOperationProgressCard()
		{
			BackColor = Theme.Card;
			base.BorderStyle = BorderStyle.FixedSingle;
			base.Size = new Size(Theme.S(560), Theme.S(178));
			Label label = new Label
			{
				Name = "title",
				Text = Lang.T("data.operation.title"),
				ForeColor = Theme.Fg,
				BackColor = Color.Transparent,
				Font = Theme.UI(16f, true)
			};
			label.SetBounds(Theme.S(24), Theme.S(20), Theme.S(510), Theme.S(34));
			Status = new Label
			{
				Name = "phase",
				ForeColor = Theme.Warning,
				BackColor = Color.Transparent,
				Font = Theme.UI(11f, false),
				AutoEllipsis = true
			};
			Status.SetBounds(Theme.S(24), Theme.S(64), Theme.S(510), Theme.S(52));
			Label label2 = new Label
			{
				Name = "note",
				Text = Lang.T("data.operation.note"),
				ForeColor = Theme.Faint,
				BackColor = Color.Transparent,
				Font = Theme.UI(9f, false)
			};
			label2.SetBounds(Theme.S(24), Theme.S(127), Theme.S(510), Theme.S(30));
			base.Controls.Add(label);
			base.Controls.Add(Status);
			base.Controls.Add(label2);
		}
	}
}
