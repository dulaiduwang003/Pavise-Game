using System;
using System.Linq;
using System.Text.RegularExpressions;
namespace PaviseApp {
    internal static class PortugueseIntegrationChecks {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        public static void Main() {
            Lang.Cur = 3;
            Check(PerformancePolicy.Text("关闭", "Off") == "Desativado", "Performance labels must use PT-BR");
            Check(DsLabels.Pick("关闭", "Off") == "Desativado", "Design labels must use PT-BR");
            Check(LaboratoryActionText.Pick("关闭", "Off", "無効") == "Desativado", "Laboratory labels must use PT-BR");
            Check(SecurityCompatibilityPresentation.T("关闭", "Off", "無効") == "Desativado", "Security labels must use PT-BR");
            Check(ServicePauser.StateName(4) == "running", "Diagnostic service state must stay canonical");
            Settings.UseTransientStoreForCurrentProcess();
            string features = AiTuningFeatureCatalog.Describe(null);
            Check(!Regex.IsMatch(features, @"[\u4e00-\u9fff]"), "Feature catalog must not contain untranslated Chinese labels");
            string prompt = AiTuningPrompt.Build("CPU exemplo", "Recurso exemplo", "Fazer live em português");
            Check(prompt.Contains("Configuração do computador"), "AI prompt heading must use PT-BR");
            Check(prompt.Contains("Fazer live em português"), "AI prompt must preserve user text");
            string session = "2026-11-09 22:00:00  INFO SESSION [session-a] start | Game\n"
                + "2026-11-09 22:00:01  WARN AUTO [session-a] GmBoost | restore-failed\n"
                + "2026-11-09 22:00:02  INFO AUTO [session-a] AutoTimerTick | restored\n"
                + "2026-11-09 22:00:03  PASS SUPPRESSION success action=background-applied count=1 targets=helper.exe(pid=42,birth=123)\n"
                + "2026-11-09 22:00:04  INFO SESSION [session-a] end | Game\n";
            var events = LogStreamView.ParseEvents(session);
            var canonical = ActionLogProjection.Project(events, 4, "session-a");
            var displayed = ActionLogProjection.ProjectForDisplay(events, 4, "session-a");
            Check(canonical.Any(x => x.Message.Contains("恢复未完成")), "Canonical action projection must remain Chinese");
            Check(displayed.All(x => !Regex.IsMatch(x.Message + x.Module, @"[\u4e00-\u9fff]")), "Translated action summaries must not contain untranslated labels");
            Check(displayed.Any(x => x.Message.Contains("Restauração incompleta")), "Action display must use PT-BR");
            Check(ActionLogProjection.Export(session, "session-a").Contains("Pavise 会话动作记录"), "Export must retain canonical format");
            var canonicalAgain = ActionLogProjection.Project(events, 4, "session-a");
            Check(canonicalAgain.Select(x => x.Message).SequenceEqual(canonical.Select(x => x.Message)), "Localized display must not leak into later canonical projections");
            var note = new ReleaseNote("1", "", new[] { new[] { "中文", "English release note" } });
            Check(note.Item(0) == "English release note", "Missing PT-BR release notes must fall back to English");
            Lang.Cur = 1;
            Check(PerformancePolicy.Text("关闭", "Off") == "Off", "English must remain unchanged");
            Lang.Cur = 0;
            Check(PerformancePolicy.Text("关闭", "Off") == "关闭", "Chinese must remain unchanged");
            Console.WriteLine("PASS PT-BR integration checks");
        }
    }
}
