using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;

namespace PaviseApp
{
	internal sealed class PrivacyOptionsPanel : DBPanel
	{
		private sealed class Row
		{
			internal PrivacyAction Action;

			internal Toggle Toggle;

			internal SettingCard Card;
		}

		private readonly List<Row> rows = new List<Row>();

		private readonly Dictionary<string, PrivacyResult> displayStates = new Dictionary<string, PrivacyResult>();

		private readonly Func<bool> mayModify;

		private readonly Action<string> notify;

		private readonly bool elevated;

		private readonly PerformanceFeatureGrid performanceGrid;

		private bool busy;

		private bool loaded;

		private bool layingOut;

		private bool refreshing;

		private bool refreshCancelled;

		private Row pendingRow;

		private bool pendingDesired;

		private int generation;

		private bool InspectionVisible
		{
			get
			{
				return base.Visible;
			}
		}

		internal PrivacyOptionsPanel(Func<bool> mayModify, Action<string> notify, bool elevated, Predicate<PrivacyAction> include = null, bool performanceLayout = false)
		{
			this.mayModify = mayModify;
			this.notify = notify;
			this.elevated = elevated;
			AutoScroll = false;
			BackColor = Theme.Bg;
			if (performanceLayout)
			{
				BackColor = Theme.Card;
				performanceGrid = new PerformanceFeatureGrid
				{
					AutoSizeContent = true
				};
				performanceGrid.ContentSizeChanged = Relayout;
				base.Controls.Add(performanceGrid);
			}
			PrivacyAction[] all = PrivacyActionCatalog.All;
			foreach (PrivacyAction privacyAction in all)
			{
				if (include == null || include(privacyAction))
				{
					Row row = new Row
					{
						Action = privacyAction
					};
					row.Toggle = new Toggle
					{
						Name = "privacyToggle" + privacyAction.Key,
						Bg = Theme.Card,
						AccessibleName = privacyAction.Title,
						AccessibleDescription = privacyAction.Detail
					};
					row.Toggle.Size = new Size(Theme.S(46), Theme.S(24));
					Row captured = row;
					row.Toggle.CheckedChanged += delegate
					{
						Change(captured);
					};
					row.Card = new PerformanceFeatureCard
					{
						Name = "privacyCard" + privacyAction.Key,
						Title = privacyAction.Title,
						Desc = privacyAction.Detail,
						Channel = rows.Count + 1,
						HostTop = true
					};
					((PerformanceFeatureCard)row.Card).PresentationChanged = Relayout;
					row.Card.Host(row.Toggle);
					((performanceGrid == null) ? ((DBPanel)this) : ((DBPanel)performanceGrid)).Controls.Add(row.Card);
					rows.Add(row);
				}
			}
			if (performanceGrid != null)
			{
				performanceGrid.Complete();
			}
			SyncCached();
			Relayout();
		}

		private static string PrivacyText(string zh, string en)
		{
			return PerformancePolicy.Text(zh, en);
		}

		internal void PrependPerformanceCard(PerformanceFeatureCard card)
		{
			if (card == null) throw new ArgumentNullException("card");
			if (performanceGrid == null) throw new InvalidOperationException("A performance layout is required.");
			performanceGrid.Controls.Add(card);
			performanceGrid.Controls.SetChildIndex(card, 0);
			Relayout();
		}

		private bool CanModify()
		{
			try
			{
				return mayModify == null || mayModify();
			}
			catch
			{
				return false;
			}
		}

		private PrivacyResult State(Row row)
		{
			PrivacyResult value;
			if (!displayStates.TryGetValue(row.Action.Key, out value))
			{
				PrivacyResult privacyResult = new PrivacyResult();
				privacyResult.State = "unverified";
				privacyResult.SelectedTarget = "";
				return privacyResult;
			}
			value.Busy = PrivacyControl.Busy;
			return value;
		}

		private PrivacyResult ReadCompletedState(Row row)
		{
			return PrivacyControl.State(row.Action.Key, "");
		}

		internal void SyncCached()
		{
			if (base.IsDisposed || base.Disposing)
			{
				return;
			}
			foreach (Row row in rows)
			{
				PrivacyResult privacyResult = State(row);
				bool matchesConfigured = privacyResult.Configured || privacyResult.HasRecovery;
				row.Toggle.SetSilently((row == pendingRow) ? pendingDesired : matchesConfigured);
				row.Toggle.Busy = busy || privacyResult.Busy;
				row.Toggle.Enabled = !busy && !privacyResult.Busy && CanModify() && (privacyResult.Configured || privacyResult.HasRecovery || (privacyResult.State == "restored" && !privacyResult.Managed) || (privacyResult.Supported && !privacyResult.Managed && privacyResult.State != "stopped" && privacyResult.State != "unverified"));
				string descValue = row.Action.Detail;
				if (!string.IsNullOrEmpty(privacyResult.Detail) && !string.Equals(descValue, privacyResult.Detail, StringComparison.Ordinal))
				{
					descValue = descValue + "\n" + privacyResult.Detail;
				}
				row.Card.Desc = descValue;
				row.Card.AccessibleDescription = descValue;
				bool anyConditionMet = privacyResult.State == "failed" || privacyResult.State == "stopped" || privacyResult.State == "restore-failed" || privacyResult.State == "unverified" || (privacyResult.State == "partial" && matchesConfigured && !privacyResult.Limited) || (privacyResult.State == "ready" && privacyResult.Configured);
				string status = Status(privacyResult);
				row.Card.SetStatus(status, anyConditionMet ? Theme.Warning : (matchesConfigured ? Theme.Accent : Theme.Dim));
				PerformanceFeatureCard performanceFeatureCard = row.Card as PerformanceFeatureCard;
				if (performanceFeatureCard != null)
				{
					performanceFeatureCard.StatusRequiresAttention = anyConditionMet || privacyResult.Managed || privacyResult.Limited || !privacyResult.Supported;
				}
			}
			Relayout();
		}

		internal static string Status(PrivacyResult value)
		{
			if (value.Busy)
			{
				return PrivacyText("正在处理", "Working");
			}
			if (!value.Configured && value.HasRecovery && (value.State == "configured" || value.State == "partial" || value.State == "ready" || value.State == "pending-app-restart"))
			{
				return PrivacyText("保留恢复记录 · 关闭开关可还原", "Recovery record retained · turn off to restore");
			}
			switch (value.State)
			{
			case "configured":
				if (!value.Configured)
				{
					return PrivacyText("未开启 · 系统已符合此设置", "Off · system already matches");
				}
				return PrivacyText("已开启 · 配置已核实", "On · configuration verified");
			case "partial":
				if (!value.Configured)
				{
					if (!value.Limited)
					{
						return PrivacyText("未开启 · 系统已有部分限制", "Off · some restrictions already exist");
					}
					return PrivacyText("未开启 · 系统已有相关配置", "Off · existing settings detected");
				}
				if (!value.Limited)
				{
					return PrivacyText("已开启 · 当前仅部分设置匹配", "On · only some settings match");
				}
				return PrivacyText("已开启 · 适用范围有限", "On · limited scope");
			case "pending-reboot":
			case "restart-required":
				return PrivacyText("配置已保存 · 需重启", "Configuration saved · restart required");
			case "pending-app-restart":
				return PrivacyText("配置已写入 · 重启相关应用", "Configuration written · restart affected apps");
			case "restore-pending-app-restart":
				return PrivacyText("原配置已恢复 · 重启相关应用", "Original configuration restored · restart affected apps");
			case "restore-pending-reboot":
				return PrivacyText("原配置已恢复 · 需重启", "Original configuration restored · restart required");
			case "restored":
				return PrivacyText("原配置已恢复", "Original configuration restored");
			case "restore-failed":
				return PrivacyText("恢复未完成 · 原值保留", "Restore incomplete · originals retained");
			case "managed":
				return PrivacyText("由系统或组织管理", "Managed by Windows or your organization");
			case "unsupported":
				return UnavailableStatus(value.UnavailableReason);
			case "stopped":
				return PrivacyText("故障 · 已停用", "Fault · disabled");
			case "failed":
				return PrivacyText("操作失败 · 原值保留", "Operation failed · originals retained");
			case "off":
				return PrivacyText("未启用", "Off");
			case "ready":
				if (!value.Configured)
				{
					return PrivacyText("未开启", "Off");
				}
				return PrivacyText("已开启 · 当前设置不匹配", "On · current settings do not match");
			default:
				return PrivacyText("暂时无法读取状态", "Unable to read status");
			}
		}

		private static string UnavailableStatus(string reason)
		{
			switch (reason)
			{
			case "system":
				return PrivacyText("需要较新的系统", "Requires a newer Windows version");
			case "edition":
				return PrivacyText("此版本类型不提供该策略", "This Windows edition does not provide this policy");
			case "component":
				return PrivacyText("系统未安装此组件", "This component is not installed");
			case "memory":
				return PrivacyText("内存容量或可用余量不足", "Insufficient memory capacity or available memory");
			case "external":
				return PrivacyText("现有配置无法安全还原", "The existing configuration cannot be safely restored");
			case "interface":
				return PrivacyText("系统未提供可用接口", "No supported system interface is available");
			case "unimplemented":
				return PrivacyText("此功能尚未开放", "This feature is not yet available");
			default:
				return PrivacyText("此项目暂不可用", "This option is currently unavailable");
			}
		}

		private void Change(Row row)
		{
			bool @checked = row.Toggle.Checked;
			if (busy || base.IsDisposed || !row.Toggle.Enabled || row.Toggle.Busy || !CanModify())
			{
				SyncCached();
				Inform(PrivacyText("请在退局、恢复及维护操作结束后修改。", "Change this after leaving the match and finishing recovery and maintenance."));
				return;
			}
			if (!elevated)
			{
				SyncCached();
				Inform(Lang.T("env.performance.needadmin"));
				return;
			}
			string selectedTarget = "";
			int ticket = generation;
			pendingRow = row;
			pendingDesired = @checked;
			busy = true;
			SyncCached();
			Action completed = delegate
			{
				PrivacyResult result = ReadCompletedState(row);
				Post(ticket, delegate
				{
					displayStates[row.Action.Key] = result;
					busy = false;
					pendingRow = null;
					SyncCached();
				});
			};
			bool setSucceeded;
			try
			{
				setSucceeded = PrivacyControl.Set(row.Action.Key, @checked, selectedTarget, completed);
			}
			catch
			{
				setSucceeded = false;
			}
			if (!setSucceeded)
			{
				busy = false;
				pendingRow = null;
				SyncCached();
				Inform(PrivacyText("暂时无法执行，请稍后重试。", "The request could not start. Please retry shortly."));
			}
		}

		private void Inform(string message)
		{
			if (notify != null && !base.IsDisposed && !base.Disposing)
			{
				notify(message);
			}
		}

		internal void RefreshIfNeeded()
		{
			if (!loaded)
			{
				RefreshAll();
			}
		}

		private void RefreshAll()
		{
			if (!busy && !base.IsDisposed && !base.Disposing && base.IsHandleCreated && InspectionVisible && CanModify())
			{
				int ticket = generation;
				busy = true;
				refreshing = true;
				refreshCancelled = false;
				SyncCached();
				RefreshRow(0, ticket);
			}
		}

		private void FinishRefresh(bool completed)
		{
			bool matchesCanModify = refreshCancelled && InspectionVisible && CanModify();
			busy = false;
			refreshing = false;
			refreshCancelled = false;
			loaded = completed;
			SyncCached();
			if (matchesCanModify)
			{
				RefreshAll();
			}
		}

		private void RefreshRow(int index, int ticket)
		{
			if (ticket != generation || base.IsDisposed || base.Disposing)
			{
				return;
			}
			if (refreshCancelled || !InspectionVisible || !CanModify())
			{
				FinishRefresh(false);
				return;
			}
			if (index >= rows.Count)
			{
				FinishRefresh(true);
				return;
			}
			Row row = rows[index];
			string target = "";
			if (!RefreshOne(row, target, delegate
			{
				PrivacyResult result = ReadCompletedState(row);
				Post(ticket, delegate
				{
					displayStates[row.Action.Key] = result;
					RefreshRow(index + 1, ticket);
				});
			}))
			{
				FinishRefresh(false);
			}
		}

		private bool RefreshOne(Row row, string target, Action completed)
		{
			try
			{
				return PrivacyControl.Refresh(row.Action.Key, target, delegate
				{
					completed();
				});
			}
			catch
			{
				return false;
			}
		}

		private void Post(int ticket, Action work)
		{
			if (base.IsDisposed || base.Disposing || !base.IsHandleCreated || ticket != generation)
			{
				return;
			}
			Action method = delegate
			{
				if (!base.IsDisposed && !base.Disposing && base.IsHandleCreated && ticket == generation)
				{
					work();
				}
			};
			try
			{
				BeginInvoke(method);
			}
			catch (InvalidOperationException)
			{
			}
			catch (Win32Exception)
			{
			}
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			if (!base.Visible || loaded)
			{
				return;
			}
			BeginInvoke((Action)delegate
			{
				if (!base.IsDisposed && base.Visible && !loaded)
				{
					RefreshAll();
				}
			});
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			if (!base.Visible)
			{
				loaded = false;
				if (refreshing)
				{
					refreshCancelled = true;
				}
			}
			else if (base.IsHandleCreated && !busy)
			{
				RefreshAll();
			}
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			generation++;
			busy = false;
			pendingRow = null;
			loaded = false;
			refreshing = false;
			refreshCancelled = false;
			base.OnHandleDestroyed(e);
		}

		protected override void Dispose(bool disposing)
		{
			generation++;
			base.Dispose(disposing);
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Relayout();
		}

		private void Relayout()
		{
			if (layingOut || rows.Count == 0)
			{
				return;
			}
			layingOut = true;
			try
			{
				if (performanceGrid != null)
				{
					performanceGrid.SetBounds(0, 0, Math.Max(0, base.Width), performanceGrid.Height);
					performanceGrid.Reflow();
					base.Height = Math.Max(Theme.S(4), performanceGrid.ContentHeight);
					return;
				}
				int widthLimit = Math.Max(Theme.S(250), base.Width);
				int scaledDimension = Theme.S(14);
				int columnCountResult = PerformanceFeatureGrid.ColumnCount(widthLimit);
				int width = (widthLimit - (columnCountResult - 1) * scaledDimension) / columnCountResult;
				int top = 0;
				int processedCount = 0;
				int relayoutValue = 0;
				foreach (Row row in rows)
				{
					if (processedCount == columnCountResult)
					{
						top += relayoutValue + scaledDimension;
						processedCount = 0;
						relayoutValue = 0;
					}
					PerformanceFeatureCard performanceFeatureCard = (PerformanceFeatureCard)row.Card;
					int val = performanceFeatureCard.PresentationHeight(width);
					performanceFeatureCard.SetBounds(processedCount * (width + scaledDimension), top, width, val);
					performanceFeatureCard.ArrangePresentation();
					relayoutValue = Math.Max(relayoutValue, val);
					processedCount++;
				}
				base.Height = Math.Max(Theme.S(4), top + relayoutValue + Theme.S(4));
			}
			finally
			{
				layingOut = false;
			}
		}
	}
}
