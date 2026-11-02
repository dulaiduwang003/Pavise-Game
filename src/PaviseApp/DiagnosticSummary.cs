using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
	internal static class DiagnosticSummary
	{
		internal static LogEventSeverity Severity(string raw)
		{
			string text = (raw ?? "").Trim();
			DateTime result;
			if (text.Length >= 19 && DateTime.TryParseExact(text.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
			{
				text = text.Substring(19).Trim();
			}
			string body;
			return LogStreamView.ClassifyLine(text, out body);
		}

		internal static void CountIssues(string raw, out int warnings, out int errors)
		{
			warnings = (errors = 0);
			foreach (LogEventEntry item in LogStreamView.ParseEvents(raw))
			{
				LogEventSeverity severity = item.Severity;
				if (severity == LogEventSeverity.Warning)
				{
					warnings++;
				}
				if (severity == LogEventSeverity.Error)
				{
					errors++;
				}
			}
		}

		internal static string RecentContext(string raw)
		{
			List<LogEventEntry> list = LogStreamView.ParseEvents(raw);
			SortedSet<int> sortedSet = new SortedSet<int>();
			int num = 0;
			int num2 = list.Count - 1;
			while (num2 >= 0 && num < 8)
			{
				LogEventSeverity severity = list[num2].Severity;
				if (severity == LogEventSeverity.Warning || severity == LogEventSeverity.Error)
				{
					num++;
					for (int i = Math.Max(0, num2 - 2); i <= Math.Min(list.Count - 1, num2 + 2); i++)
					{
						sortedSet.Add(i);
					}
				}
				num2--;
			}
			if (num == 0)
			{
				for (int j = Math.Max(0, list.Count - 12); j < list.Count; j++)
				{
					sortedSet.Add(j);
				}
			}
			StringBuilder stringBuilder = new StringBuilder();
			int num3 = -1;
			foreach (int item in sortedSet)
			{
				string raw2 = list[item].Raw;
				if (num3 >= 0 && item > num3 + 1)
				{
					stringBuilder.AppendLine("…");
				}
				stringBuilder.AppendLine((raw2.Length > 1600) ? (raw2.Substring(0, 1600) + "…") : raw2);
				num3 = item;
			}
			if (stringBuilder.Length != 0)
			{
				return stringBuilder.ToString();
			}
			return Lang.T("workflow.diagnostic.nolog");
		}

		internal static string Build(GameMode mode, bool elevated, string raw)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("PAVISE " + App.Version + " · " + Lang.T("workflow.diagnostic.title"));
			stringBuilder.AppendLine("Time: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
			stringBuilder.AppendLine(string.Concat("Windows: ", Environment.OSVersion.Version, " · Build: ", Native.OsBuild(), " · 64-bit OS: ", Environment.Is64BitOperatingSystem));
			stringBuilder.AppendLine("Administrator: " + elevated + " · Language: " + Lang.Cur);
			stringBuilder.AppendLine("Theme: " + (Theme.LightMode ? "Light" : "Dark") + " · Accent: #" + Col.ToHex(Theme.Accent));
			stringBuilder.AppendLine("Game: " + (mode.ActiveGame ?? Lang.T("workflow.game.waiting")));
			stringBuilder.AppendLine("Guard: " + mode.Enabled + " · State: " + mode.StatusText);
			stringBuilder.AppendLine("Global policy: " + ModeButton.ModeName(mode.Preset));
			stringBuilder.AppendLine("Effective policy: " + ModeButton.ModeName(mode.ActivePreset) + " · Source: " + (mode.SessionPolicySourceName ?? Lang.T("mode.source.global")));
			stringBuilder.AppendLine("Boost: " + mode.BoostStatusText);
			stringBuilder.AppendLine("Mode switching locked: " + mode.ModeSwitchLocked);
			stringBuilder.AppendLine("Extreme freeze: " + mode.FreezeDiagnosticLine);
			stringBuilder.AppendLine("Job fallback: " + mode.BackgroundCpuCapStatus);
			stringBuilder.AppendLine("Auto cores: " + AutoCores.DiagnosticLine());
			GpuIrqState gpuIrqState = GpuIrqCheck.LoadState();
			stringBuilder.AppendLine(string.Concat("GPU IRQ check: ", gpuIrqState.Phase, (gpuIrqState.Phase == GpuIrqPhase.None) ? "" : (" · " + gpuIrqState.DeviceName + " · target " + IrqRelocate.MaskText(gpuIrqState.Target) + ((gpuIrqState.Reason.Length == 0) ? "" : (" · " + gpuIrqState.Reason)))));
			stringBuilder.AppendLine("Log recording: " + Logger.WritesEnabled);
			if (!Logger.WritesEnabled)
			{
				stringBuilder.AppendLine(Lang.T("v20.log.paused"));
			}
			stringBuilder.AppendLine();
			stringBuilder.AppendLine(Lang.T("workflow.diagnostic.context"));
			stringBuilder.Append(RecentContext(raw));
			string[] array = (raw ?? "").Replace("\r", "").Split('\n');
			List<string> list = new List<string>();
			int num = array.Length - 1;
			while (num >= 0 && list.Count < 80)
			{
				if (array[num].Contains("[FREEZE]") || array[num].Contains("[PLATFORM]"))
				{
					list.Add((array[num].Length > 1600) ? array[num].Substring(0, 1600) : array[num]);
				}
				num--;
			}
			if (list.Count > 0)
			{
				stringBuilder.AppendLine();
				stringBuilder.AppendLine("Protection / freeze events:");
				list.Reverse();
				foreach (string item in list)
				{
					stringBuilder.AppendLine(item);
				}
			}
			return stringBuilder.ToString();
		}
	}
}
