#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // Release notes are the easiest thing to miss at release time, this pins three things
        //   version bumped without notes, notes missing a language, and the latest entry not at the head of the table
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
            // The current version must have all three languages, this is the entry the UI fetches by Lang.Cur
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

            // Item falls back to Chinese when a translation is missing, so missing translations can only be detected via RawItem, guard that premise here
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
                // Latest at the head, dates may only stay equal or go earlier
                Eq(true, date <= previous);
                previous = date;
            }
        }
    }
}
#endif
