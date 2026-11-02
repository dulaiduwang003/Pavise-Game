namespace PaviseApp
{
	internal sealed class GameNvProfileDecision
	{
		internal readonly GameNvPending State;

		internal readonly GameNvValueSource Source;

		internal GameNvProfileDecision(GameNvPending state, GameNvValueSource source)
		{
			State = state;
			Source = source;
		}
	}
}
