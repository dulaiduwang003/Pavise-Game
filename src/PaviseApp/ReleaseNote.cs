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
			string[] array = items[index];
			if (array == null || array.Length == 0)
			{
				return "";
			}
			int cur = Lang.Cur;
			if (cur < 0 || cur >= array.Length || string.IsNullOrEmpty(array[cur]))
			{
				return array[0];
			}
			return array[cur];
		}
	}
}
