using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PowerFlyout : RoundPanel
	{
		private readonly Label title;

		private readonly Label sub;

		private readonly List<PowerChoiceRow> rows = new List<PowerChoiceRow>();

		public Action<string> Chosen;

		public PowerFlyout()
		{
			UseBackdrop = false;
			Fill = Theme.Card;
			Border = Theme.StrokeHi;
			BackColor = Theme.Bg;
			Radius = Theme.S(14);
			AccentEdge = true;
			title = new Label
			{
				Text = Lang.T("plan.pick.title"),
				ForeColor = Theme.Fg,
				BackColor = Theme.Card,
				Font = Theme.UI(11f, true),
				AutoEllipsis = true
			};
			title.UseCompatibleTextRendering = false;
			title.SetBounds(Theme.S(18), Theme.S(13), Theme.S(356), Theme.S(25));
			sub = new Label
			{
				ForeColor = Theme.Dim,
				BackColor = Theme.Card,
				Font = Theme.UI(7.8f, false),
				AutoEllipsis = true
			};
			sub.UseCompatibleTextRendering = false;
			sub.SetBounds(Theme.S(18), Theme.S(38), Theme.S(356), Theme.S(42));
			base.Controls.AddRange(new Control[2] { title, sub });
		}

		public void Open(string effectiveId)
		{
			foreach (PowerChoiceRow row in rows)
			{
				row.Dispose();
			}
			rows.Clear();
			sub.Text = Lang.F((effectiveId == "off") ? "plan.fly.off" : "plan.fly.cur", PowerPlan.CurrentPlanLabel());
			List<string> list = new List<string>();
			list.Add("managed");
			list.Add("off");
			List<string> list2 = list;
			List<string> list3 = new List<string>();
			list3.Add(Lang.T("plan.pick.auto"));
			list3.Add(Lang.T("plan.pick.off"));
			List<string> list4 = list3;
			List<MenuBadge> list5 = new List<MenuBadge>();
			list5.Add(new MenuBadge(Lang.T("plan.pick.managed"), true));
			list5.Add(null);
			List<MenuBadge> list6 = list5;
			foreach (PowerPlanEntry item in PowerPlan.ListUserPlans())
			{
				list2.Add(item.Id.ToString());
				list4.Add(item.Name);
				list6.Add(new MenuBadge(Lang.T("plan.pick.local"), false));
			}
			int num = Theme.S(88);
			for (int i = 0; i < list2.Count; i++)
			{
				bool on = string.Equals(list2[i], effectiveId, StringComparison.OrdinalIgnoreCase);
				PowerChoiceRow powerChoiceRow = new PowerChoiceRow(list2[i], list4[i], list6[i], on);
				powerChoiceRow.SetBounds(Theme.S(14), num, Theme.S(368), Theme.S(44));
				powerChoiceRow.Chosen = delegate(string id)
				{
					if (Chosen != null)
					{
						Chosen(id);
					}
				};
				base.Controls.Add(powerChoiceRow);
				rows.Add(powerChoiceRow);
				num += Theme.S(50);
			}
			base.Height = num + Theme.S(12);
		}
	}
}
