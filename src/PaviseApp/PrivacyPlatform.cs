using System;

namespace PaviseApp
{
	internal sealed class PrivacyPlatform
	{
		internal int Build;

		internal string Edition;

		internal bool Managed;

		internal bool Enterprise
		{
			get
			{
				if (!(Edition ?? "").StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) && !(Edition ?? "").StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase))
				{
					return (Edition ?? "").StartsWith("Education", StringComparison.OrdinalIgnoreCase);
				}
				return true;
			}
		}

		internal bool PolicyCapable
		{
			get
			{
				if (!Enterprise)
				{
					return (Edition ?? "").StartsWith("Professional", StringComparison.OrdinalIgnoreCase);
				}
				return true;
			}
		}
	}
}
