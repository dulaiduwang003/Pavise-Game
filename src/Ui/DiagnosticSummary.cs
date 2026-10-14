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
            foreach (LogEventEntry entry in LogStreamView.ParseEvents(raw))
            {
                LogEventSeverity severity = entry.Severity;
                if (severity == LogEventSeverity.Warning) warnings++;
                if (severity == LogEventSeverity.Error) errors++;
            }
        }

        internal static string RecentContext(string raw)
        {
            List<LogEventEntry> events = LogStreamView.ParseEvents(raw);
            var selected = new SortedSet<int>();
            int issues = 0;
            for (int i = events.Count - 1; i >= 0 && issues < 8; i--)
            {
                LogEventSeverity severity = events[i].Severity;
                if (severity != LogEventSeverity.Warning && severity != LogEventSeverity.Error) continue;
                issues++;
                for (int j = Math.Max(0,i - 2); j <= Math.Min(events.Count - 1,i + 2); j++) selected.Add(j);
            }
            if (issues == 0)
                for (int i = Math.Max(0,events.Count - 12); i < events.Count; i++) selected.Add(i);
            var text = new StringBuilder();
            int previous = -1;
            foreach (int i in selected)
            {
                string rawEvent = events[i].Raw;
                if (previous >= 0 && i > previous + 1) text.AppendLine("…");
                text.AppendLine(rawEvent.Length > 1600 ? rawEvent.Substring(0,1600) + "…" : rawEvent);
                previous = i;
            }
            return text.Length == 0 ? Lang.T("workflow.diagnostic.nolog") : text.ToString();
        }

        internal static string Build(GameMode mode, bool elevated, string raw)
        {
            var text = new StringBuilder();
            text.AppendLine("PAVISE " + App.Version + " · " + Lang.T("workflow.diagnostic.title"));
            text.AppendLine("Time: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture));
            text.AppendLine("Windows: " + Environment.OSVersion.Version + " · Build: " + Native.OsBuild() + " · 64-bit OS: " + Environment.Is64BitOperatingSystem);
            text.AppendLine("Administrator: " + elevated + " · Language: " + Lang.Cur);
            text.AppendLine("Theme: " + (Theme.LightMode ? "Light" : "Dark") + " · Accent: #" + Col.ToHex(Theme.Accent));
            text.AppendLine("Game: " + (mode.ActiveGame ?? Lang.T("workflow.game.waiting")));
            text.AppendLine("Guard: " + mode.Enabled + " · State: " + mode.StatusText);
            text.AppendLine("Global policy: " + ModeButton.ModeName(mode.Preset));
            text.AppendLine("Effective policy: " + ModeButton.ModeName(mode.ActivePreset)
                + " · Source: " + (mode.SessionPolicySourceName ?? Lang.T("mode.source.global")));
            text.AppendLine("Boost: " + mode.BoostStatusText);
            text.AppendLine("Mode switching locked: " + mode.ModeSwitchLocked);
            text.AppendLine("Extreme freeze: " + mode.FreezeDiagnosticLine);
            text.AppendLine("Job fallback: " + mode.BackgroundCpuCapStatus);
            text.AppendLine("Auto cores: " + AutoCores.DiagnosticLine());
            GpuIrqState irq = GpuIrqCheck.LoadState();
            text.AppendLine("GPU IRQ check: " + irq.Phase + (irq.Phase == GpuIrqPhase.None ? ""
                : " · " + irq.DeviceName + " · target " + IrqRelocate.MaskText(irq.Target)
                    + (irq.Reason.Length == 0 ? "" : " · " + irq.Reason)));
            text.AppendLine("Log recording: " + Logger.WritesEnabled);
            if (!Logger.WritesEnabled) text.AppendLine(Lang.T("v20.log.paused"));
            text.AppendLine();
            text.AppendLine(Lang.T("workflow.diagnostic.context"));
            text.Append(RecentContext(raw));
            string[] events = (raw ?? "").Replace("\r", "").Split('\n');
            var recent = new List<string>();
            for (int i = events.Length - 1; i >= 0 && recent.Count < 80; i--)
                if (events[i].Contains("[FREEZE]") || events[i].Contains("[PLATFORM]"))
                    recent.Add(events[i].Length > 1600 ? events[i].Substring(0, 1600) : events[i]);
            if (recent.Count > 0)
            {
                text.AppendLine(); text.AppendLine("Protection / freeze events:");
                recent.Reverse(); foreach (string entry in recent) text.AppendLine(entry);
            }
            return text.ToString();
        }
    }
}
