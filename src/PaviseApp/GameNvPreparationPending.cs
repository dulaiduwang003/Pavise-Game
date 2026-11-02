using System;

namespace PaviseApp
{
	internal sealed class GameNvPreparationPending : InvalidOperationException
	{
		internal readonly GameNvPending State;

		internal GameNvPreparationPending(GameNvPending state)
			: base("Game NVIDIA preparation pending: " + state)
		{
			State = state;
		}
	}
}
