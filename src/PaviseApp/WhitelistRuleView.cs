namespace PaviseApp
{
	internal sealed class WhitelistRuleView
	{
		public readonly WhitelistRule Rule;

		public readonly int CurrentMatches;

		public readonly bool Required;

		public readonly string PlatformId;

		public string Key
		{
			get
			{
				if (PlatformId != null)
				{
					return "platform:" + PlatformId;
				}
				return Rule.Key;
			}
		}

		public WhitelistRuleView(WhitelistRule rule, int currentMatches, bool required = false, string platformId = null)
		{
			Rule = rule;
			CurrentMatches = currentMatches;
			Required = required;
			PlatformId = platformId;
		}

		public override string ToString()
		{
			if (PlatformId != null)
			{
				return "[" + Lang.T("white.platform.badge") + "] " + PlatformRuntimePolicy.DisplayName(PlatformId);
			}
			string whiteKindFamilyText = ((Rule.Kind == WhitelistRuleKind.ApplicationFamily) ? Lang.T("white.kind.family") : ((Rule.Kind == WhitelistRuleKind.ExactPath) ? Lang.T("white.kind.path") : Lang.T("white.kind.name")));
			return "[" + whiteKindFamilyText + "] " + Rule.Value + " " + ((CurrentMatches < 0) ? Lang.T("white.matches.pending") : Lang.F("white.matches", CurrentMatches)) + (Required ? (" " + Lang.T("white.required.badge")) : "");
		}
	}
}
