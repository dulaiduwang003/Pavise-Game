#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // 更新说明是发版时最容易漏的一环 这里卡住三件事
        //   版本号涨了却没写说明 说明缺一门语言 以及最新一条不在表首
        internal static void RunReleaseNotesRegressionTests()
        {
            ReleaseNotesCoverCurrentVersion();
            ReleaseNotesAreComplete();
            ReleaseNotesOrderAndDates();
        }

        private static void ReleaseNotesCoverCurrentVersion()
        {
            ReleaseNote current = ReleaseNotes.Current;
            Eq(true, current != null);
            Eq(App.Version, current.Version);
            Eq(true, current.Count > 0);
            Eq("v" + App.Version, current.Tag);
            // 当前版本必须三语齐全 界面按 Lang.Cur 取的就是这一条
            for (int i = 0; i < current.Count; i++)
            {
                Eq(3, current.RawLanguages(i));
                for (int lang = 0; lang < 3; lang++)
                    Eq(true, !string.IsNullOrEmpty(current.RawItem(i, lang)));
            }
        }

        private static void ReleaseNotesAreComplete()
        {
            List<string> missing = ReleaseNotes.MissingTranslations();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "release notes missing translations: " + string.Join(", ", missing.ToArray()));

            // Item 在缺译时回落中文 所以缺译只能靠 RawItem 查 这里守住这个前提
            int prev = Lang.Cur;
            try
            {
                ReleaseNote current = ReleaseNotes.Current;
                for (int lang = 0; lang < 3; lang++)
                {
                    Lang.Cur = lang;
                    Eq(current.RawItem(0, lang), current.Item(0));
                }
            }
            finally { Lang.Cur = prev; }
        }

        private static void ReleaseNotesOrderAndDates()
        {
            Eq(true, ReleaseNotes.All.Length > 0);
            Eq(App.Version, ReleaseNotes.All[0].Version);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime previous = DateTime.MaxValue;
            foreach (ReleaseNote note in ReleaseNotes.All)
            {
                Eq(true, seen.Add(note.Version));
                DateTime date;
                Eq(true, DateTime.TryParseExact(note.Date, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out date));
                // 表首最新 日期只许持平或更早
                Eq(true, date <= previous);
                previous = date;
            }
        }
    }
}
#endif
