using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class Lang
    {
        public static int Cur;

        internal static readonly object ChangeGate = new object();

        // 语言下标就是词条数组的列号 2 号列是弃用的日文 旧词条里还留着 新语言从 3 号开始登记 免得误用那批旧文字
        internal const int Chinese = 0;
        internal const int English = 1;
        internal const int FirstAddedLanguage = 3;
        internal const int Portuguese = FirstAddedLanguage;

        // 登记表和译文都是整份替换 读的线程不用加锁
        private static volatile string[] codes = { "zh", "en" };
        private static volatile Dictionary<int, Dictionary<string, string>> translations = new Dictionary<int, Dictionary<string, string>>();

        // 本线程临时改用另一种语言取文案 日志按中文原文定级别时用 只影响 T F Text Format 不改 Cur
        [System.ThreadStatic] private static bool overriding;
        [System.ThreadStatic] private static int overridden;

        internal static int Effective { get { return overriding ? overridden : Cur; } }

        // 葡萄牙语只用于界面和动作视图 技术记录继续用原有的中文格式
        internal static int DiagnosticLanguage
        {
            get
            {
                int language = Effective;
                return language == Portuguese ? Chinese : language;
            }
        }

        internal static System.IDisposable Override(int language)
        {
            var scope = new OverrideScope(overriding, overridden);
            overriding = true;
            overridden = language;
            return scope;
        }

        private sealed class OverrideScope : System.IDisposable
        {
            private readonly bool wasOverriding;
            private readonly int previous;
            private bool closed;
            internal OverrideScope(bool wasOverriding, int previous) { this.wasOverriding = wasOverriding; this.previous = previous; }
            public void Dispose()
            {
                if (closed) return;
                closed = true;
                overriding = wasOverriding;
                overridden = previous;
            }
        }

        private static readonly Dictionary<string, string[]> M = CreateMessages();

        static Lang()
        {
            Register(Portuguese, "pt-BR", PortugueseDirectMessages);
        }

        private static Dictionary<string, string[]> CreateMessages()
        {
            var messages = new Dictionary<string, string[]>();
            AddCommonMessages(messages);
            AddSettingsMessages(messages);
            AddLibraryMessages(messages);
            AddGuardMessages(messages);
            AddSchedulingMessages(messages);
            AddPowerMessages(messages);
            AddCheckupMessages(messages);
            AddPerformanceMessages(messages);
            AddGpuSpoofMessages(messages);
            AddLogsMessages(messages);
            return messages;
        }

        public static void Init()
        {
            int index = IndexOf(Settings.LoadStr("Lang", "zh"));
            Cur = index < 0 ? Chinese : index;
        }

        public static void Set(int i)
        {
            lock (ChangeGate)
            {
                Cur = IsRegistered(i) ? i : Chinese;
                Settings.SaveStr("Lang", CodeOf(Cur));
            }
        }

        internal static bool IsRegistered(int index)
        {
            string[] known = codes;
            return index >= 0 && index < known.Length && known[index] != null;
        }

        internal static int IndexOf(string code)
        {
            string[] known = codes;
            for (int index = 0; index < known.Length; index++) if (known[index] != null && known[index] == code) return index;
            return -1;
        }

        internal static string CodeOf(int index)
        { return IsRegistered(index) ? codes[index] : codes[Chinese]; }

        // 加一种语言 词条数组补上这一列 内联双写的译文以英文模板为索引一次给全 日志词表另在 LogText 登记
        internal static void Register(int index, string code, IDictionary<string, string> translatedFromEnglish)
        {
            if (index < FirstAddedLanguage || string.IsNullOrEmpty(code)) throw new System.ArgumentException("language index or code");
            lock (ChangeGate)
            {
                if (IsRegistered(index) || IndexOf(code) >= 0) throw new System.InvalidOperationException("language already registered: " + code);
                string[] known = codes;
                var next = new string[System.Math.Max(known.Length, index + 1)];
                System.Array.Copy(known, next, known.Length);
                next[index] = code;
                var table = new Dictionary<int, Dictionary<string, string>>(translations);
                table[index] = new Dictionary<string, string>(translatedFromEnglish ?? new Dictionary<string, string>(), System.StringComparer.Ordinal);
                translations = table;
                codes = next;
            }
        }

        public static string T(string key)
        { return T(key, Effective); }

        public static string T(string key, int language)
        {
            if (language == Portuguese && key != null
                && (key.StartsWith("log.", System.StringComparison.Ordinal)
                    || key.IndexOf(".log.", System.StringComparison.Ordinal) >= 0))
            {
                language = Chinese;
            }
            return Ui(key, language);
        }

        // 界面控件使用译文 日志记录保留原有语言
        internal static string Ui(string key)
        {
            return Ui(key, Effective);
        }

        private static string Ui(string key, int language)
        {
            string[] value;
            if (key == null || !M.TryGetValue(key, out value)) return key;

            string translated;
            return language == Portuguese && PortugueseMessages.TryGetValue(key, out translated)
                ? translated : Pick(value, language);
        }

        internal static string TranslateEnglish(string value)
        {
            string translated;
            return value != null && PortugueseDirectMessages.TryGetValue(value, out translated) ? translated : value;
        }

        internal static string TranslateChinese(string value)
        {
            string translated;
            return Effective == Portuguese && value != null && PortuguesePromptMessages.TryGetValue(value, out translated)
                ? translated : value;
        }

        // 缺这一列时先退回英文再退回中文
        private static string Pick(string[] value, int language)
        {
            if (language >= 0 && language < value.Length && value[language] != null) return value[language];
            return value.Length > English && value[English] != null ? value[English] : value[0];
        }

        // 内联双写 中文和英文直接取 其他语言拿英文模板去查登记时给的译文 查不到用英文
        internal static string Text(string chinese, string english)
        {
            int language = Effective;
            if (language == Chinese) return chinese;
            if (language == English) return english;
            Dictionary<string, string> table;
            string translated;
            if (english != null && translations.TryGetValue(language, out table) && table.TryGetValue(english, out translated)) return translated;
            return english;
        }

        // 带参数的文案要传模板 先拼好再传的话其他语言按模板查不到译文
        internal static string Format(string chinese, string english, params object[] args)
        {
            return string.Format(Text(chinese, english), args);
        }

        public static string F(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        public static bool HasKey(string key)
        {
            if (key != null)
            {
                return M.ContainsKey(key);
            }
            return false;
        }

        public static void Merge(IDictionary<string, string[]> extra)
        {
            if (extra == null)
            {
                return;
            }
            foreach (KeyValuePair<string, string[]> item in extra)
            {
                if (item.Key != null && item.Value != null && item.Value.Length > 0)
                {
                    M[item.Key] = item.Value;
                }
            }
        }
    }
}
