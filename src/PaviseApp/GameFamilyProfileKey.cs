using System;
using System.IO;

namespace PaviseApp
{
	internal struct GameFamilyProfileKey : IEquatable<GameFamilyProfileKey>
	{
		private readonly string id;

		private readonly string root;

		private readonly string executable;

		private readonly string learned;

		private readonly bool force;

		internal GameFamilyProfileKey(GameProfile profile)
		{
			id = profile.Id;
			root = profile.Root;
			executable = profile.ExecutablePath;
			learned = profile.LearnedExecutablePath;
			force = profile.ForceTrigger;
		}

		internal string CreateRootPrefix()
		{
			if (!string.IsNullOrEmpty(root))
			{
				string text = root.TrimEnd('\\');
				if (GameFamilyHistory.CanonicalPath(text) && !string.Equals(text, Path.GetPathRoot(text).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
				{
					return text + "\\";
				}
			}
			return null;
		}

		internal bool Owns(string path, string rootPrefix)
		{
			if (!string.IsNullOrEmpty(path))
			{
				if ((string.IsNullOrEmpty(executable) || !Equal(executable, path)) && (string.IsNullOrEmpty(learned) || !Equal(learned, path)))
				{
					if (rootPrefix != null)
					{
						return path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
					}
					return false;
				}
				return true;
			}
			return false;
		}

		public bool Equals(GameFamilyProfileKey other)
		{
			if (force == other.force && Equal(id, other.id) && Equal(root, other.root) && Equal(executable, other.executable))
			{
				return Equal(learned, other.learned);
			}
			return false;
		}

		public override bool Equals(object value)
		{
			if (value is GameFamilyProfileKey)
			{
				return Equals((GameFamilyProfileKey)value);
			}
			return false;
		}

		public override int GetHashCode()
		{
			int num = (force ? 1 : 0);
			num = num * 31 + Hash(id);
			num = num * 31 + Hash(root);
			num = num * 31 + Hash(executable);
			return num * 31 + Hash(learned);
		}

		private static bool Equal(string a, string b)
		{
			return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
		}

		private static int Hash(string value)
		{
			return StringComparer.OrdinalIgnoreCase.GetHashCode(value ?? "");
		}
	}
}
