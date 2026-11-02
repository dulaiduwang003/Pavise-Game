using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class GameFamilyEvidence
	{
		internal sealed class Member
		{
			internal readonly long Creation;

			internal readonly string Path;

			internal Member(long creation, string path)
			{
				Creation = creation;
				Path = path;
			}
		}

		private readonly Dictionary<GameFamilyProfileKey, Dictionary<int, Member>> profiles;

		internal static readonly GameFamilyEvidence Empty = new GameFamilyEvidence(new Dictionary<GameFamilyProfileKey, Dictionary<int, Member>>());

		internal GameFamilyEvidence(Dictionary<GameFamilyProfileKey, Dictionary<int, Member>> source)
		{
			profiles = new Dictionary<GameFamilyProfileKey, Dictionary<int, Member>>();
			foreach (KeyValuePair<GameFamilyProfileKey, Dictionary<int, Member>> item in source)
			{
				profiles.Add(item.Key, new Dictionary<int, Member>(item.Value));
			}
		}

		internal bool Contains(GameProfile profile, int pid, long creation, string path)
		{
			if (profile == null || string.IsNullOrEmpty(profile.Id) || pid <= 4 || creation <= 0 || string.IsNullOrEmpty(path))
			{
				return false;
			}
			Dictionary<int, Member> value;
			Member value2;
			if (profiles.TryGetValue(new GameFamilyProfileKey(profile), out value) && value.TryGetValue(pid, out value2) && value2.Creation == creation)
			{
				return string.Equals(value2.Path, path, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}
}
