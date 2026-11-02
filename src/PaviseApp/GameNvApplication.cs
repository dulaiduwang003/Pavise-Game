using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PaviseApp
{
	internal sealed class GameNvApplication
	{
		internal readonly string Name;

		internal readonly string Friendly;

		internal readonly string Launcher;

		internal readonly string FileInFolder;

		internal readonly string CommandLine;

		internal readonly uint Predefined;

		internal readonly uint Flags;

		internal string Text
		{
			get
			{
				Dictionary<string, string> dictionary = new Dictionary<string, string>();
				dictionary.Add("name", Name);
				dictionary.Add("friendly", Friendly);
				dictionary.Add("launcher", Launcher);
				dictionary.Add("folder", FileInFolder);
				dictionary.Add("command", CommandLine);
				dictionary.Add("flags", Flags.ToString(CultureInfo.InvariantCulture));
				dictionary.Add("predefined", Predefined.ToString(CultureInfo.InvariantCulture));
				return Convert.ToBase64String(GameNvText.Encode(dictionary));
			}
		}

		internal GameNvApplication(string name, string friendly = "", string launcher = "", string fileInFolder = "", uint flags = 0u, string commandLine = "", uint predefined = 0u)
		{
			if (predefined > 1)
			{
				throw new InvalidDataException("Unknown NVIDIA application provenance");
			}
			Name = name ?? "";
			Friendly = friendly ?? "";
			Launcher = launcher ?? "";
			FileInFolder = fileInFolder ?? "";
			Flags = flags;
			CommandLine = commandLine ?? "";
			Predefined = predefined;
		}

		internal bool IsPlainExactPath(string path)
		{
			if (string.Equals(Name, path, StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(Name) && Launcher.Length == 0 && FileInFolder.Length == 0 && Flags == 0)
			{
				return CommandLine.Length == 0;
			}
			return false;
		}
	}
}
