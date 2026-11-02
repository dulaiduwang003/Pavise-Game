using System;

namespace PaviseApp
{
	internal static class GameExtension
	{
		public static readonly GameExtensionModule Current = Load();

		public static bool Present
		{
			get
			{
				return Current != null;
			}
		}

		private static void Create(ref GameExtensionModule module)
		{
			module = new CompositeGameExtension(new LolGameExtension());
		}

		private static GameExtensionModule Load()
		{
			GameExtensionModule module = null;
			try
			{
				Create(ref module);
			}
			catch
			{
				module = null;
			}
			return module;
		}

		public static bool TryHandleArgs(string[] args)
		{
			try
			{
				return Current != null && Current.TryHandleArgs(args);
			}
			catch
			{
				return false;
			}
		}

		public static void Start()
		{
			try
			{
				if (Current != null)
				{
					Current.Start();
				}
			}
			catch (Exception ex)
			{
				Logger.Log("扩展模块启动失败 " + ex.Message);
			}
		}

		public static bool NeedsProcessIdentity(string name)
		{
			try
			{
				return Current != null && Current.NeedsProcessIdentity(name);
			}
			catch
			{
				return false;
			}
		}

		public static void NotifyProcessChanges(ProcessChangeBatch batch)
		{
			try
			{
				if (Current != null)
				{
					Current.NotifyProcessChanges(batch);
				}
			}
			catch
			{
			}
		}

		public static void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active)
		{
			try
			{
				if (Current != null)
				{
					Current.NotifySession(profile, rendererPid, rendererCreation, active);
				}
			}
			catch
			{
			}
		}

		public static GameCardExtension CreateCard(GameProfile profile)
		{
			try
			{
				if (profile == null)
				{
					return null;
				}
				return (Current != null && Current.AppliesTo(profile)) ? Current.CreateCard(profile) : null;
			}
			catch (Exception ex)
			{
				Logger.Log("扩展卡片创建失败 " + ex.Message);
				return null;
			}
		}

		public static void Shutdown(int waitMs)
		{
			try
			{
				if (Current != null)
				{
					Current.NotifySession(null, 0, 0, false);
					Current.Shutdown(waitMs);
				}
			}
			catch
			{
			}
		}
	}
}
