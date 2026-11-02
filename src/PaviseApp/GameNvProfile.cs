using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PaviseApp
{
	internal sealed class GameNvProfile
	{
		internal readonly string Name;

		internal readonly string ScopeHash;

		internal readonly uint Predefined;

		internal readonly uint SettingCount;

		internal readonly uint LocalSettingCount;

		internal readonly bool CompleteLocalSettings;

		internal readonly bool CompleteApplications;

		internal readonly ReadOnlyCollection<GameNvApplication> Applications;

		internal string Key
		{
			get
			{
				return GameNvText.Hash(Encoding.UTF8.GetBytes(Name.ToUpperInvariant() + "\n" + Predefined.ToString(CultureInfo.InvariantCulture)));
			}
		}

		internal GameNvProfile(string name, uint predefined, uint settingCount, IEnumerable<GameNvApplication> applications, bool completeApplications = true, uint? localSettingCount = null, bool completeLocalSettings = true)
		{
			if (string.IsNullOrEmpty(name) || name.Length > 2047 || predefined > 1 || applications == null)
			{
				throw new InvalidDataException("Invalid game NVIDIA profile metadata");
			}
			Name = name;
			Predefined = predefined;
			SettingCount = settingCount;
			CompleteApplications = completeApplications;
			LocalSettingCount = localSettingCount ?? settingCount;
			CompleteLocalSettings = completeLocalSettings;
			Applications = Array.AsReadOnly(applications.ToArray());
			if (Applications.Count > 64 || Applications.Any((GameNvApplication x) => x == null))
			{
				throw new InvalidDataException("Unbounded NVIDIA application membership");
			}
			ScopeHash = GameNvText.Hash(Encoding.UTF8.GetBytes(Name + "\n" + Predefined + "\n" + CompleteApplications + "\n" + string.Join("\n", Applications.Select((GameNvApplication x) => x.Text).OrderBy((string x) => x, StringComparer.Ordinal))));
		}
	}
}
