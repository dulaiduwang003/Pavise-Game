using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class MaintenanceListPanel : DBPanel
	{
		private readonly Func<List<MaintenanceListItem>> load;

		private readonly Func<string, string> retry;

		private readonly PillButton refresh;

		private readonly Label summary;

		private readonly DBPanel rows;

		private readonly ToolTip tips = new ToolTip();

		private List<MaintenanceListItem> items = new List<MaintenanceListItem>();

		private bool busy;

		private bool loaded;

		private bool readable;

		private int generation;

		private string message = "";

		private static string TextFor(string zh, string en)
		{
			return PerformancePolicy.Text(zh, en);
		}

		internal MaintenanceListPanel(Func<List<MaintenanceListItem>> query, Func<string, string> retryItem)
		{
			if (query == null)
			{
				throw new ArgumentNullException("query");
			}
			load = query;
			retry = retryItem;
			base.Name = "maintenanceList";
			BackColor = Theme.Bg;
			base.Size = new Size(Theme.S(708), Theme.S(330));
			refresh = new PillButton(TextFor("刷新", "Refresh"))
			{
				Name = "maintenanceRefresh"
			};
			refresh.Click += delegate
			{
				RefreshItems();
			};
			summary = new Label
			{
				Name = "maintenanceSummary",
				BackColor = Theme.Bg,
				ForeColor = Theme.Dim,
				Font = Theme.UI(8.5f, false),
				AutoEllipsis = true
			};
			rows = new DBPanel
			{
				Name = "maintenanceRows",
				BackColor = Theme.Bg,
				AutoScroll = true
			};
			base.Controls.Add(refresh);
			base.Controls.Add(summary);
			base.Controls.Add(rows);
			base.Resize += delegate
			{
				LayoutChildren();
				RenderItems();
			};
			LayoutChildren();
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			if (!loaded)
			{
				loaded = true;
				RefreshItems();
			}
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			generation++;
			busy = false;
			loaded = false;
			base.OnHandleDestroyed(e);
		}

		private void LayoutChildren()
		{
			int num = Theme.S(8);
			int num2 = Theme.S(76);
			int num3 = Theme.S(32);
			refresh.SetBounds(Math.Max(0, base.Width - num2), 0, num2, num3);
			summary.SetBounds(0, 0, Math.Max(1, refresh.Left - num), num3);
			rows.SetBounds(0, num3 + num, Math.Max(1, base.Width), Math.Max(1, base.Height - num3 - num));
		}

		private void SetBusy(bool value)
		{
			busy = value;
			refresh.Busy = value;
			refresh.Enabled = !value;
			rows.Enabled = !value && readable;
		}

		private void Queue(Action work)
		{
			if (!ThreadPool.QueueUserWorkItem(delegate
			{
				work();
			}))
			{
				throw new InvalidOperationException("Maintenance task could not be queued");
			}
		}

		private void Post(Action result)
		{
			if (base.IsDisposed || base.Disposing || !base.IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke((MethodInvoker)delegate
				{
					if (!base.IsDisposed && !base.Disposing)
					{
						result();
					}
				});
			}
			catch (InvalidOperationException)
			{
			}
		}

		private void RefreshItems()
		{
			loaded = true;
			RunOperation(null);
		}

		private void RunOperation(Func<string> operation)
		{
			if (busy || base.IsDisposed || base.Disposing)
			{
				return;
			}
			int current = ++generation;
			SetBusy(true);
			summary.Text = TextFor("正在读取记录…", "Reading records…");
			summary.ForeColor = Theme.Dim;
			try
			{
				Queue(delegate
				{
					List<MaintenanceListItem> next = null;
					string failure = null;
					try
					{
						if (operation != null)
						{
							failure = operation();
						}
						next = load();
						if (next == null)
						{
							throw new InvalidOperationException("Record query returned no result");
						}
					}
					catch (Exception ex2)
					{
						failure = PsRunner.NormalizeError(ex2.Message);
					}
					Post(delegate
					{
						if (current == generation)
						{
							readable = next != null;
							if (readable)
							{
								items = next;
							}
							message = failure ?? "";
							SetBusy(false);
							RenderItems();
						}
					});
				});
			}
			catch (Exception ex)
			{
				readable = false;
				message = PsRunner.NormalizeError(ex.Message);
				SetBusy(false);
				RenderItems();
			}
		}

		private void RenderItems()
		{
			if (rows == null || base.IsDisposed)
			{
				return;
			}
			rows.SuspendLayout();
			try
			{
				foreach (Control item in new List<Control>(Children(rows)))
				{
					item.Dispose();
				}
				int num = 0;
				int num2 = 0;
				foreach (MaintenanceListItem item2 in items)
				{
					SettingCard settingCard = new SettingCard();
					settingCard.Name = "maintenanceRecord";
					settingCard.Title = item2.Title ?? "";
					settingCard.Desc = item2.Detail ?? "";
					settingCard.AccessibleName = item2.Title ?? "";
					settingCard.AccessibleDescription = (item2.Status ?? "") + "\n" + (item2.Detail ?? "");
					settingCard.TabStop = false;
					SettingCard settingCard2 = settingCard;
					settingCard2.SetBounds(0, num, Math.Max(Theme.S(150), rows.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - Theme.S(4)), Theme.S(108));
					settingCard2.SetStatus(item2.Status ?? "", Theme.Warning);
					tips.SetToolTip(settingCard2, item2.Detail ?? "");
					if (retry != null && item2.Retryable)
					{
						string key = item2.Key;
						PillButton pillButton = new PillButton(TextFor("重试一次", "Retry once"));
						pillButton.Name = "maintenanceRetry";
						pillButton.Bg = Theme.Card;
						PillButton pillButton2 = pillButton;
						pillButton2.Size = new Size(Theme.S(120), Theme.S(32));
						pillButton2.AccessibleDescription = TextFor("仅允许这项动作重试一次，失败后继续停用。", "Allows one retry for this action. It remains disabled if the retry fails.");
						pillButton2.Click += delegate
						{
							if (readable && !busy)
							{
								RunOperation(() => retry(key));
							}
						};
						settingCard2.Host(pillButton2);
					}
					rows.Controls.Add(settingCard2);
					num += settingCard2.Height + Theme.S(8);
					num2++;
				}
				if (num2 == 0 && readable)
				{
					Label label = new Label();
					label.Name = "maintenanceEmpty";
					label.AutoSize = false;
					label.Text = TextFor("暂无记录", "No records");
					label.Font = Theme.UI(11f, false);
					label.ForeColor = Theme.Dim;
					label.TextAlign = ContentAlignment.MiddleCenter;
					label.BackColor = Theme.Bg;
					label.Dock = DockStyle.Top;
					label.Height = Theme.S(70);
					Label value = label;
					rows.Controls.Add(value);
				}
				rows.AutoScrollMinSize = new Size(0, num);
				summary.Text = ((message.Length > 0) ? message : (TextFor("已停用 ", "Disabled ") + items.Count + TextFor(" 项 · 仅手动重试", " actions · manual retry only")));
				summary.ForeColor = ((message.Length > 0) ? Theme.Warning : Theme.Dim);
				tips.SetToolTip(summary, message);
				rows.Enabled = !busy && readable;
			}
			finally
			{
				rows.ResumeLayout();
			}
		}

		private static IEnumerable<Control> Children(Control parent)
		{
			foreach (Control control in parent.Controls)
			{
				yield return control;
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				generation++;
				tips.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
