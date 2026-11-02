using System.Drawing;

namespace PaviseApp
{
	internal sealed class WorkspaceLayout
	{
		internal const int DefaultWidth = 1220;

		internal const int DefaultHeight = 760;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly int RailWidth;

		internal readonly int HeaderHeight;

		internal WorkspaceLayout(Size client, float scale)
		{
			Width = DefaultWidth;
			Height = DefaultHeight;
			RailWidth = 0;
			HeaderHeight = 60;
		}
	}
}
