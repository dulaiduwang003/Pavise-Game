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
            string line = (raw ?? "").Trim();
            DateTime stamp;
            if (line.Length >= 19 && DateTime.TryParseExact(line.Substring(0,19),"yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,DateTimeStyles.None,out stamp)) line = line.Substring(19).Trim();
            string body;
            return LogStreamView.ClassifyLine(line,out body);
        }

        internal static void CountIssues(string raw, out int warnings, out int errors)
        {
            warnings = errors = 0;
            foreach (string line in (raw ?? "").Split('\n'))
            {
                LogEventSeverity severity = Severity(line);
                if (severity == LogEventSeverity.Warning) warnings++;
                if (severity == LogEventSeverity.Error) errors++;
            }
        }

        // Keep only the context around the most recent problem; diagnostic copy never starts sampling or changes any tuning setting
        internal static string RecentContext(string raw)
        {
            string[] lines = (raw ?? "").Replace("\r","").Split('\n');
            var selected = new SortedSet<int>();
            int issues = 0;
            for (int i = lines.Length - 1; i >= 0 && issues < 8; i--)
            {
                LogEventSeverity severity = Severity(lines[i]);
                if (severity != LogEventSeverity.Warning && severity != LogEventSeverity.Error) continue;
                issues++;
                for (int j = Math.Max(0,i - 2); j <= Math.Min(lines.Length - 1,i + 2); j++) selected.Add(j);
            }
            if (issues == 0)
                for (int i = Math.Max(0,lines.Length - 12); i < lines.Length; i++) selected.Add(i);
            var text = new StringBuilder();
            int previous = -1;
            foreach (int i in selected)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                if (previous >= 0 && i > previous + 1) text.AppendLine("…");
                text.AppendLine(lines[i].Length > 1600 ? lines[i].Substring(0,1600) + "…" : lines[i]);
                previous = i;
            }
            return text.Length == 0 ? Lang.T("workflow.diagnostic.nolog") : text.ToString();
        }

        internal static string Build(GameMode mode, bool elevated, string raw)
        {
            var text = new StringBuilder();
            text.AppendLine("PAVISE " + App.Version + " · " + Lang.T("workflow.diagnostic.title"));
            text.AppendLine("Time: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture));
            text.AppendLine("Windows: " + Environment.OSVersion.Version + " · 64-bit OS: " + Environment.Is64BitOperatingSystem);
            text.AppendLine("Administrator: " + elevated + " · Language: " + Lang.Cur);
            text.AppendLine("Theme: " + (Theme.LightMode ? "Light" : "Dark") + " · Accent: #" + Col.ToHex(Theme.Accent));
            text.AppendLine("Game: " + (mode.ActiveGame ?? Lang.T("workflow.game.waiting")));
            text.AppendLine("Guard: " + mode.Enabled + " · State: " + mode.StatusText);
            text.AppendLine("Global policy: " + ModeButton.ModeName(mode.Preset));
            text.AppendLine("Effective policy: " + ModeButton.ModeName(mode.ActivePreset)
                + " · Source: " + (mode.SessionPolicySourceName ?? Lang.T("mode.source.global")));
            text.AppendLine("Boost: " + mode.BoostStatusText);
            CoreSchedulingPlan plan = CoreScheduling.LoadGlobal();
            text.AppendLine("Saved global core plan (not runtime verification): " + (plan.ReadFailed ? "unreadable" : plan.Encode()));
            PolicySnapshot policy = mode.PolicyForDiagnostics;
            if (policy != null)
                text.AppendLine("Session requested core plan (not runtime verification): " + policy.CorePlan.Encode());
            text.AppendLine("Affinity guard: " + mode.AffinityGuardOn + " · IRQ observation enabled: " + IrqSessionProbe.EnabledSetting);
            text.AppendLine("IRQ observation: " + mode.IrqObservationStatusText);
            text.AppendLine("Log recording: " + Logger.WritesEnabled);
            if (!Logger.WritesEnabled) text.AppendLine(Lang.T("v20.log.paused"));
            text.AppendLine();
            text.AppendLine(Lang.T("workflow.diagnostic.context"));
            text.Append(RecentContext(raw));
            return text.ToString();
        }
    }
}
