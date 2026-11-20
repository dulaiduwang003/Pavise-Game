using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Linq;
namespace PaviseApp {
    internal static class Settings {
        private static readonly Dictionary<string,string> Values = new Dictionary<string,string>();
        public static string LoadStr(string key, string fallback) { string value; return Values.TryGetValue(key, out value) ? value : fallback; }
        public static void SaveStr(string key, string value) { Values[key] = value; }
    }
    internal static class PortugueseLanguageChecks {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static bool Formats(string template) {
            object[] args = new object[100];
            for (int i = 0; i < args.Length; i++) args[i] = 1;
            try { string.Format(template, args); return true; }
            catch (FormatException) { return false; }
        }
        public static void Main() {
            Check(Lang.IsRegistered(Lang.Portuguese), "PT-BR must be registered as a language");
            Check(Lang.IndexOf("pt-BR") == Lang.Portuguese, "PT-BR must resolve to its registered index");
            Check(!Lang.IsRegistered(2), "The retired Japanese index must remain unavailable");
            Settings.SaveStr("Lang", "ja");
            Lang.Init();
            Check(Lang.Cur == Lang.Chinese, "The retired language setting must fall back to Chinese");
            Lang.Set(Lang.Portuguese);
            Check(Lang.Cur == Lang.Portuguese, "PT-BR must be selectable");
            Check(Settings.LoadStr("Lang", "") == "pt-BR", "PT-BR setting must persist");
            Lang.Cur = Lang.Chinese; Lang.Init();
            Check(Lang.Cur == Lang.Portuguese, "PT-BR must be restored on startup");
            Check(Lang.T("set.lang") == "Idioma", "Language label must be in Portuguese");
            Check(Lang.T("lib.family.suppress") == "Limitar processos relacionados", "Renamed game family option must be in Portuguese");
            Check(Lang.DiagnosticLanguage == Lang.Chinese, "PT-BR diagnostics must select canonical Chinese");
            Check(Lang.TranslateEnglish("游戏库：已忽略 ") == "游戏库：已忽略 ", "Chinese diagnostic text must not be translated");
            Check(Lang.TranslateChinese("未命名游戏") == "Jogo sem nome", "Prompt descriptions must be translated");
            Check(Lang.TranslateChinese("开关") == "Opção de ativação", "Prompt controls must not disappear");
            Check(Lang.TranslateChinese("竞技") == "Competitivo", "Competitive mode must be correctly named");
            Check(Lang.TranslateChinese("关闭") == "Desativado", "Disabled state must be correctly named");
            Check(Lang.TranslateEnglish("Close") == "Fechar", "Direct text must be translated");
            Check(Lang.TranslateEnglish("__unknown__") == "__unknown__", "Unknown direct text should fall back to English");
            Check(Lang.T("notes.sub").Contains("{0}"), "format placeholders must be preserved");
            Check(Lang.Ui("log.category.actions") == "Registro de ações", "Log screen labels must be translated");
            Check(Lang.T("log.category.actions", Lang.Portuguese) == Lang.T("log.category.actions", Lang.Chinese), "Log protocol must retain its canonical language");
            Check(Lang.T("auto.log.started", Lang.Portuguese) == Lang.T("auto.log.started", Lang.Chinese), "Chinese log invariant must remain unchanged");
            var translated = (Dictionary<string, string>)typeof(Lang).GetField("PortugueseMessages", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            foreach (string key in new[] { "log.latest.session", "log.category.actions", "log.category.diagnostics", "log.actions.hint", "log.diagnostics.hint", "log.latest.empty", "log.export.file", "log.export.actions.title", "log.export.diagnostics.title", "log.export.actions.detail", "log.export.diagnostics.detail", "log.export.actions.saved", "log.export.diagnostics.saved", "log.export.filter", "log.export.failed", "v20.log.sub", "v20.log.refresh", "v20.log.paused" })
            {
                string expectedTranslation;
                Check(translated.TryGetValue(key, out expectedTranslation) && Lang.Ui(key) == expectedTranslation,
                    "Log UI label missing translation: " + key);
            }
            Lang.Merge(LolLang.Table);
            var catalog = (Dictionary<string, string[]>)typeof(Lang).GetField("M", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            foreach (var entry in catalog) {
                if (entry.Key.StartsWith("log.", StringComparison.Ordinal) || entry.Key.IndexOf(".log.", StringComparison.Ordinal) >= 0) continue;
                Check(translated.ContainsKey(entry.Key), "Missing PT-BR translation: " + entry.Key);
                if (entry.Value.Length < 2) continue;
                Check(entry.Value[1].Trim().Length == 0 || translated[entry.Key].Trim().Length > 0, "Empty PT-BR translation: " + entry.Key);
                var source = Regex.Matches(entry.Value[1], @"\{\d+(?:[, :][^{}]*)?\}");
                var target = Regex.Matches(translated[entry.Key], @"\{\d+(?:[, :][^{}]*)?\}");
                Check(source.Count == target.Count, "Placeholder count differs: " + entry.Key);
                if (Formats(entry.Value[1])) Check(Formats(translated[entry.Key]), "Invalid .NET format: " + entry.Key);
                var expected = new List<string>(); var actual = new List<string>();
                foreach (Match item in source) expected.Add(item.Value);
                foreach (Match item in target) actual.Add(item.Value);
                expected.Sort(StringComparer.Ordinal); actual.Sort(StringComparer.Ordinal);
                Check(string.Join("|", expected.ToArray()) == string.Join("|", actual.ToArray()), "Placeholder differs: " + entry.Key);
            }
            var direct = (Dictionary<string, string>)typeof(Lang).GetField("PortugueseDirectMessages", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            foreach (var pair in direct) {
                Check(pair.Key.Trim().Length == 0 || pair.Value.Trim().Length > 0, "Empty direct translation: " + pair.Key);
                if (Formats(pair.Key)) Check(Formats(pair.Value), "Invalid direct .NET format: " + pair.Key);
                var original = Regex.Matches(pair.Key, @"\{\d+(?:[, :][^{}]*)?\}");
                var localized = Regex.Matches(pair.Value, @"\{\d+(?:[, :][^{}]*)?\}");
                var expected = new List<string>(); var actual = new List<string>();
                foreach (Match item in original) expected.Add(item.Value);
                foreach (Match item in localized) actual.Add(item.Value);
                expected.Sort(StringComparer.Ordinal); actual.Sort(StringComparer.Ordinal);
                Check(string.Join("|", expected.ToArray()) == string.Join("|", actual.ToArray()), "Direct text placeholder differs: " + pair.Key);
                Check(Regex.Match(pair.Key, @"^\s*").Value == Regex.Match(pair.Value, @"^\s*").Value, "Leading whitespace differs: " + pair.Key);
                Check(Regex.Match(pair.Key, @"\s*$").Value == Regex.Match(pair.Value, @"\s*$").Value, "Trailing whitespace differs: " + pair.Key);
            }
            var promptFragments = (Dictionary<string, string>)typeof(Lang).GetField("PortuguesePromptMessages", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            foreach (var pair in promptFragments) {
                Check(pair.Key.Trim().Length == 0 || !string.IsNullOrWhiteSpace(pair.Value), "Empty prompt translation: " + pair.Key);
                Check(pair.Key.Count(c => c == '\n') == pair.Value.Count(c => c == '\n'), "Prompt line breaks changed: " + pair.Key);
                Check(!pair.Value.Contains("QPX") && !pair.Value.Contains("[PAVISE"), "Unresolved translation marker: " + pair.Key);
            }
            Lang.Set(Lang.English); Check(Settings.LoadStr("Lang", "") == "en", "English setting changed");
            Check(Lang.TranslateChinese("未命名游戏") == "未命名游戏", "Other languages must preserve source prompt strings");
            Lang.Set(Lang.Chinese); Check(Settings.LoadStr("Lang", "") == "zh", "Chinese setting changed");
            Console.WriteLine("PASS Portuguese language checks");
        }
    }
}
