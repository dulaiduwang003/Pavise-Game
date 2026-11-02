namespace PaviseApp
{
	internal sealed class MenuBadge
	{
		public readonly string Text;

		public readonly bool Strong;

		public MenuBadge(string text, bool strong)
		{
			Text = text;
			Strong = strong;
		}
	}
}
