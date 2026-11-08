using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class AcProtectionGroup
	{
		public readonly string Key;

		public readonly string[] Procs;

		public readonly string[] Prefixes;

		public readonly string[] Directories;

		public string Name
		{
			get
			{
				return Lang.T("ac." + Key + ".n");
			}
		}

		public AcProtectionGroup(string key, string[] procs, string[] prefixes, string[] directories)
		{
			Key = key;
			Procs = procs;
			Prefixes = prefixes;
			Directories = directories;
		}

		public string[] PatternsForDisplay()
		{
			List<string> list = new List<string>(Procs);
			string[] prefixes = Prefixes;
			foreach (string entryText in prefixes)
			{
				list.Add(entryText + "*");
			}
			string[] directories = Directories;
			foreach (string entryText2 in directories)
			{
				list.Add("\\" + entryText2 + "\\*");
			}
			return list.ToArray();
		}
	}
}
