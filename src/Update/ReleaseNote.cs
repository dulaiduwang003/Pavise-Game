namespace PaviseApp
{
    internal sealed class ReleaseNote
    {
        public readonly string Version;

        public readonly string Date;

        private readonly string[][] items;

        private readonly string[] introduction;

        public string Introduction
        {
            get { return Localized(introduction); }
        }

        public string Tag
        {
            get
            {
                return "v" + Version;
            }
        }

        public int Count
        {
            get
            {
                if (items != null)
                {
                    return items.Length;
                }
                return 0;
            }
        }

        public ReleaseNote(string version, string date, string[][] entries)
            : this(version, date, null, entries)
        {
        }

        public ReleaseNote(string version, string date, string[] opening, string[][] entries)
        {
            Version = version;
            Date = date;
            introduction = opening;
            items = entries;
        }

        public string Item(int index)
        {
            if (items == null || index < 0 || index >= items.Length)
            {
                return "";
            }
            return Localized(items[index]);
        }

        private static string Localized(string[] entries)
        {
            if (entries == null || entries.Length == 0) return "";
            int language = Lang.Effective;
            if (language >= 0 && language < entries.Length && !string.IsNullOrEmpty(entries[language]))
                return entries[language];
            if (language == Lang.Portuguese && entries.Length > Lang.English && !string.IsNullOrEmpty(entries[Lang.English]))
                return Lang.TranslateEnglish(entries[Lang.English]);
            return entries[Lang.Chinese];
        }
    }
}
