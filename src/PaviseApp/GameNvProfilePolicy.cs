using System.IO;

namespace PaviseApp
{
	internal static class GameNvProfilePolicy
	{
		internal const string Rule = "game-nv-preserve-profile-rules-and-user-overrides-1";

		internal static GameNvValueSource Source(GameNvSetting value)
		{
			if (value == null || !value.Resolved)
			{
				return GameNvValueSource.Unavailable;
			}
			bool matchesCurrentPredefined = value.CurrentPredefined != 0;
			switch (value.Location)
			{
			case 0u:
				if (!matchesCurrentPredefined)
				{
					return GameNvValueSource.ProfileUser;
				}
				return GameNvValueSource.ProfilePredefined;
			case 1u:
				if (!matchesCurrentPredefined)
				{
					return GameNvValueSource.GlobalUser;
				}
				return GameNvValueSource.GlobalPredefined;
			case 2u:
				if (!matchesCurrentPredefined)
				{
					return GameNvValueSource.BaseUser;
				}
				return GameNvValueSource.BasePredefined;
			case 3u:
				if (!matchesCurrentPredefined)
				{
					return GameNvValueSource.DefaultUser;
				}
				return GameNvValueSource.DefaultPredefined;
			default:
				throw new InvalidDataException("Unknown game NVIDIA current-value source");
			}
		}

		internal static GameNvProfileDecision Evaluate(uint id, uint requested, GameNvSetting value)
		{
			GameNvPrelaunchPlan.RequireId(id);
			if (requested != 1)
			{
				throw new InvalidDataException("Unsupported automatic game NVIDIA policy value");
			}
			GameNvValueSource gameNvValueSource = Source(value);
			if (gameNvValueSource == GameNvValueSource.Unavailable)
			{
				return new GameNvProfileDecision(GameNvPending.Observation, gameNvValueSource);
			}
			if (value.Value == requested)
			{
				return new GameNvProfileDecision(GameNvPending.AlreadyMatches, gameNvValueSource);
			}
			if (gameNvValueSource == GameNvValueSource.ProfileUser)
			{
				return new GameNvProfileDecision(GameNvPending.UserPreference, gameNvValueSource);
			}
			if (id == 549528094 && gameNvValueSource == GameNvValueSource.ProfilePredefined)
			{
				return new GameNvProfileDecision((value.Value == 0 || value.Value == 2) ? GameNvPending.ProfileRule : GameNvPending.Observation, gameNvValueSource);
			}
			return new GameNvProfileDecision(GameNvPending.Ready, gameNvValueSource);
		}
	}
}
