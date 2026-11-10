namespace PaviseApp
{
    internal sealed class ReleaseNote
    {
        public readonly string Version;

        public readonly string Date;

        private readonly string[][] items;

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
        {
            Version = version;
            Date = date;
            items = entries;
        }

        public string Item(int index)
        {
            if (items == null || index < 0 || index >= items.Length)
            {
                return "";
            }
            string[] releaseItem = items[index];
            if (releaseItem == null || releaseItem.Length == 0)
            {
                return "";
            }
            int cur = Lang.Cur;
            if (cur < 0 || cur >= releaseItem.Length || string.IsNullOrEmpty(releaseItem[cur]))
            {
                return releaseItem[0];
            }
            return releaseItem[cur];
        }
    }
}
