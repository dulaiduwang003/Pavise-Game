namespace PaviseApp
{
	internal abstract class GameExtensionModule
	{
		public virtual bool TryHandleArgs(string[] args)
		{
			return false;
		}

		public virtual void Start()
		{
		}

		public virtual bool NeedsProcessIdentity(string name)
		{
			return false;
		}

		public virtual void NotifyProcessChanges(ProcessChangeBatch batch)
		{
		}

		public virtual void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active)
		{
		}

		public virtual bool AppliesTo(GameProfile profile)
		{
			return false;
		}

		public virtual GameCardExtension CreateCard(GameProfile profile)
		{
			return null;
		}

		public virtual void Shutdown(int waitMs)
		{
		}
	}
}
