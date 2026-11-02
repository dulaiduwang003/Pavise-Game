using System;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class GameLibraryEventArgs : EventArgs
	{
		public readonly GameLibraryItem Item;

		public readonly Control Anchor;

		public GameLibraryEventArgs(GameLibraryItem item, Control anchor = null)
		{
			Item = item;
			Anchor = anchor;
		}
	}
}
