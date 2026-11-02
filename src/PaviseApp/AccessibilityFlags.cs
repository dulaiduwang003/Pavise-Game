namespace PaviseApp
{
	internal static class AccessibilityFlags
	{
		public const int On = 1;

		public const int Available = 2;

		public const int HotkeyActive = 4;

		public static bool TryParse(string raw, out int flags)
		{
			flags = 0;
			if (string.IsNullOrEmpty(raw))
			{
				return false;
			}
			return int.TryParse(raw.Trim(), out flags);
		}

		public static bool NeedsFix(int flags)
		{
			if ((flags & 1) == 0)
			{
				return (flags & 4) != 0;
			}
			return true;
		}

		public static int Sanitize(int flags)
		{
			return flags & -6;
		}

		public static bool IsOn(int flags)
		{
			return (flags & 1) != 0;
		}

		public static bool HotkeyOn(int flags)
		{
			return (flags & 4) != 0;
		}
	}
}
