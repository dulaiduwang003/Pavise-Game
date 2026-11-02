namespace PaviseApp
{
	internal sealed class GameLibraryItem
	{
		public GameProfile Profile;

		public bool Running;

		public bool RendererObserved;

		public GameLibraryItem(GameProfile profile, bool running, bool observed)
		{
			Profile = profile;
			Running = running;
			RendererObserved = observed;
		}

		public override string ToString()
		{
			if (Profile != null)
			{
				return Profile.Name;
			}
			return "";
		}
	}
}
