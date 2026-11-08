namespace PaviseApp
{
	internal static class App
	{
		public const string DisplayName = "PAVISE AUTO";

		public const string Version = "3.3.0.8";

		public const string Author = "bdth";

		public const string AuthorEmail = "2074055628@qq.com";

		public const string QqGroup = "1051472054";

		public const string QqGroup2 = "1101249532";

		public const string QqGroup3 = "383761286";

		public const string QqGroup4 = "166255062";

		public const string QqGroup5 = "1109874913";

		public const string Douyin = "44601770838";

		public const string WebsiteUrl = "https://pavise.club/";

		public const string WebsiteFallbackUrl = "https://pavise-website.2074055628.workers.dev/";

		public const string VersionFeedUrl = "https://pavise.club/version.json";

		public const string VersionFeedFallbackUrl = "https://pavise-website.2074055628.workers.dev/version.json";

		public const string RepoName = "dulaiduwang003/Pavise-Game";

		public const string RepoUrl = "https://github.com/dulaiduwang003/Pavise-Game";

		public const string ManualUpdateQuarkUrl = "https://pavise.club/changelog/#latest";

		public const string ManualUpdateGithubUrl = "https://github.com/dulaiduwang003/Pavise-Game/releases/latest";

		public static string ChangelogUrl
		{
			get
			{
				return "https://pavise.club/" + ((Lang.Cur == 1) ? "en/" : "") + "changelog/#latest";
			}
		}

		public static string VersionTag
		{
			get
			{
				return "v" + Version;
			}
		}
	}
}
