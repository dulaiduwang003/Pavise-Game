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
            get
            {
                if (introduction == null || introduction.Length == 0)
                {
                    return "";
                }
                int language = Lang.Cur;
                if (language < 0 || language >= introduction.Length || string.IsNullOrEmpty(introduction[language]))
                {
                    return introduction[0];
                }
                return introduction[language];
            }
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
            string[] textEntries = items[index];
            if (textEntries == null || textEntries.Length == 0)
            {
                return "";
            }
            int cur = Lang.Cur;
            if (cur < 0 || cur >= textEntries.Length || string.IsNullOrEmpty(textEntries[cur]))
            {
                return textEntries[0];
            }
            return textEntries[cur];
        }
    }
}
