using System;
using System.Linq;
using System.Text.RegularExpressions;
namespace PaviseApp {
    internal static class PortugueseIntegrationChecks {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        private static void CheckAntiCheatCopy(int language, string summary, string aceScope, string protectedServices,
            string cadence, string releaseNote)
        {
            using (Lang.Override(language))
            {
                Check(Lang.T("lib.ace.off").Contains(summary) && Lang.T("lib.ace.on").Contains("ACE"),
                    "Deep tuning summaries must describe ACE");
                foreach (string key in new[] { "lib.ace.tip", "lib.ace.confirm" })
                {
                    string copy = Lang.T(key);
                    Check(copy.Contains(aceScope) && copy.Contains(protectedServices) && copy.Contains("Job") && !copy.Contains(cadence),
                        "Anti-cheat option must describe ACE-only suppression and protection-only services: " + key);
                }
                ReleaseNote note = ReleaseNotes.Current;
                Check(Enumerable.Range(0, note.Count).Select(note.Item).Count(line => line == releaseNote) == 1,
                    "Current release notes must include the localized anti-cheat scope exactly once");
            }
        }

        public static void Main() {
            Settings.UseTransientStoreForCurrentProcess();
            Logger.WritesEnabled = false;
            Lang.Set(Lang.Portuguese);
            Check(PerformancePolicy.Text("关闭", "Off") == "Desativado", "Performance labels must use PT-BR");
            Check(DsLabels.Pick("关闭", "Off") == "Desativado", "Design labels must use PT-BR");
            Check(LaboratoryActionText.Pick("关闭", "Off", "無効") == "Desativado", "Laboratory labels must use PT-BR");
            Check(Lang.T("irq.fail.denied").StartsWith("O sistema negou a captura de interrupções", StringComparison.Ordinal), "Interrupt capture security notice must use PT-BR");
            using (Lang.Override(Lang.Chinese))
            {
                Check(ServicePauser.StateName(4) == "运行中", "Chinese service state must remain stable");
            }
            Check(ServicePauser.StateName(4) == "运行中", "PT-BR diagnostic service state must stay canonical");
            string features = AiTuningFeatureCatalog.Describe(null);
            Check(!Regex.IsMatch(features, @"[\u4e00-\u9fff]"), "Feature catalog must not contain untranslated Chinese labels");
            Check(features.Contains("[GameFamilyBackground]") && features.Contains("Limitar processos relacionados"),
                "Feature catalog must use the current game family option");
            Check(features.Contains("[EnvPerformanceBundleV1]") && !features.Contains("[EnvNtfsLastAccessV1]"),
                "Feature catalog must reflect the current environment options");
            string affinity = features.Split('\n').Single(line => line.Contains("[GmGameSchedulingV1]"));
            Check(affinity.Contains("Um núcleo de renderização") && affinity.Contains("Manual") && !affinity.Contains("Automático"),
                "Portuguese affinity catalog must list the supported modes");
            Check(affinity.Contains("amostragem leve dos eventos de apresentação de quadros"),
                "Portuguese affinity catalog must describe lightweight presentation sampling");
            string threadBoost = features.Split('\n').Single(line => line.Contains("[" + ThreadBoostSelection.Key + "]"));
            Check(threadBoost.Contains("reserva uma sessão de rastreamento do sistema ao iniciar"),
                "Portuguese thread boost catalog must describe the reserved trace session");
            ReleaseNote affinityRelease = ReleaseNotes.Current;
            Check(Enumerable.Range(0, affinityRelease.Count).Select(affinityRelease.Item)
                .Count(line => line == "O modo Automático foi removido da afinidade da thread de renderização. Permanecem os modos Um núcleo de renderização e Manual.") == 1,
                "The Portuguese affinity removal must appear once in the current release notes");
            string[] renderSamplingNotes =
            {
                "渲染单核改用轻量采样，系统采样会话不足时也能识别渲染线程。",
                "Single render core now uses lightweight sampling so it can identify the render thread even when system trace sessions are scarce.",
                "O modo Um núcleo de renderização agora usa amostragem leve para identificar a thread de renderização mesmo quando há poucas sessões de coleta do sistema disponíveis."
            };
            string[] threadBoostSamplingNotes =
            {
                "游戏线程提优预留系统采样会话，减少名额不足导致的识别失败。",
                "Game thread boost reserves a system trace session to reduce identification failures when sessions are scarce.",
                "O aumento de prioridade das threads do jogo reserva uma sessão de rastreamento do sistema para reduzir falhas de identificação quando há poucas sessões disponíveis."
            };
            string[] gpuSpoofNotes =
            {
                "环境页新增显卡伪装，可将独显名称显示为低端、高端或旗舰型号。",
                "Added GPU spoofing to Environment to display discrete GPU names as entry-level, high-end, or flagship models.",
                "A página Ambiente ganhou o disfarce da GPU, que permite exibir os nomes das GPUs dedicadas como modelos de entrada, de alto desempenho ou topo de linha."
            };
            int[] noteLanguages = { Lang.Chinese, Lang.English, Lang.Portuguese };
            for (int i = 0; i < noteLanguages.Length; i++)
            {
                using (Lang.Override(noteLanguages[i]))
                {
                    var notes = Enumerable.Range(0, affinityRelease.Count).Select(affinityRelease.Item).ToArray();
                    Check(notes.Count(line => line == renderSamplingNotes[i]) == 1, "The render sampling release note must appear once per language");
                    Check(notes.Count(line => line == threadBoostSamplingNotes[i]) == 1, "The thread boost reservation release note must appear once per language");
                    Check(notes.Count(line => line == gpuSpoofNotes[i]) == 1, "The GPU spoofing release note must appear once per language");
                }
            }
            string feature = features.Split('\n').Single(line => line.Contains("[" + AntiCheatSuppressionPolicy.Key + "]"));
            Check(feature.Contains("somente aos processos ACE") && feature.Contains("EasyAntiCheat, BattlEye")
                && feature.Contains("recebem apenas proteção") && !feature.Contains("a cada segundo")
                && !feature.Contains("bloqueia aumentos temporários de prioridade"),
                "Feature catalog must describe ACE-only suppression and protection for EasyAntiCheat and BattlEye");
            Check(Lang.T("ac.pergame") == "Supressão por jogo na Biblioteca · Ajuste avançado",
                "Whitelist anti-cheat scope must use PT-BR");
            CheckAntiCheatCopy(Lang.Portuguese, "somente ACE", "somente aos processos ACE instalados",
                "EasyAntiCheat, BattlEye, Vanguard e componentes incluídos na pasta do jogo recebem apenas proteção", "a cada segundo",
                "A supressão de anti-cheat se aplica somente ao ACE e usa objetos Job para fixar a prioridade e a afinidade da CPU.");
            CheckAntiCheatCopy(Lang.English, "ACE only", "only to ACE processes installed",
                "EasyAntiCheat, BattlEye, Vanguard and game-bundled components receive protection only", "every second",
                "Anti-cheat suppression applies only to ACE and now uses Jobs to lock priority and CPU affinity.");
            CheckAntiCheatCopy(Lang.Chinese, "仅适用于 ACE", "只在本游戏对局中压制装在正式位置的 ACE 进程",
                "EasyAntiCheat、BattlEye、Vanguard 和游戏目录自带的组件仅保护", "每秒核验",
                "反作弊压制只作用于 ACE，改用 Job 锁定优先级和核心。");
            string prompt = AiTuningPrompt.Build("CPU exemplo", features, "Fazer live em português");
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
            Lang.Cur = Lang.English;
            Check(PerformancePolicy.Text("关闭", "Off") == "Off", "English must remain unchanged");
            Lang.Cur = Lang.Chinese;
            Check(PerformancePolicy.Text("关闭", "Off") == "关闭", "Chinese must remain unchanged");
            Console.WriteLine("PASS PT-BR integration checks");
        }
    }
}
