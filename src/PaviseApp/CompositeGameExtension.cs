using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class CompositeGameExtension : GameExtensionModule
	{
		private readonly GameExtensionModule[] modules;

		public GameExtensionModule[] Modules
		{
			get
			{
				return (GameExtensionModule[])modules.Clone();
			}
		}

		public CompositeGameExtension(params GameExtensionModule[] parts)
		{
			List<GameExtensionModule> list = new List<GameExtensionModule>();
			if (parts != null)
			{
				foreach (GameExtensionModule gameExtensionModule in parts)
				{
					if (gameExtensionModule != null)
					{
						list.Add(gameExtensionModule);
					}
				}
			}
			modules = list.ToArray();
		}

		public override bool TryHandleArgs(string[] args)
		{
			GameExtensionModule[] modulesSnapshot = modules;
			foreach (GameExtensionModule gameExtensionModule in modulesSnapshot)
			{
				try
				{
					if (gameExtensionModule.TryHandleArgs(args))
					{
						return true;
					}
				}
				catch
				{
				}
			}
			return false;
		}

		public override void Start()
		{
			GameExtensionModule[] modulesSnapshot = modules;
			foreach (GameExtensionModule gameExtensionModule in modulesSnapshot)
			{
				try
				{
					gameExtensionModule.Start();
				}
				catch (Exception ex)
				{
					Logger.Log("扩展模块启动失败 " + ex.Message);
				}
			}
		}

		public override bool NeedsProcessIdentity(string name)
		{
			GameExtensionModule[] modulesSnapshot = modules;
			foreach (GameExtensionModule gameExtensionModule in modulesSnapshot)
			{
				try
				{
					if (gameExtensionModule.NeedsProcessIdentity(name))
					{
						return true;
					}
				}
				catch
				{
				}
			}
			return false;
		}

		public override void NotifyProcessChanges(ProcessChangeBatch batch)
		{
			GameExtensionModule[] modulesSnapshot = modules;
			foreach (GameExtensionModule gameExtensionModule in modulesSnapshot)
			{
				try
				{
					gameExtensionModule.NotifyProcessChanges(batch);
				}
				catch
				{
				}
			}
		}

		public override void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active)
		{
			GameExtensionModule[] modulesSnapshot = modules;
			foreach (GameExtensionModule gameExtensionModule in modulesSnapshot)
			{
				try
				{
					gameExtensionModule.NotifySession(profile, rendererPid, rendererCreation, active);
				}
				catch
				{
				}
			}
		}

		public override bool AppliesTo(GameProfile profile)
		{
			return Owner(profile) != null;
		}

		public override GameCardExtension CreateCard(GameProfile profile)
		{
			GameExtensionModule gameExtensionModule = Owner(profile);
			if (gameExtensionModule != null)
			{
				return gameExtensionModule.CreateCard(profile);
			}
			return null;
		}

		internal GameExtensionModule Owner(GameProfile profile)
		{
			if (profile == null)
			{
				return null;
			}
			GameExtensionModule[] modulesSnapshot = modules;
			foreach (GameExtensionModule gameExtensionModule in modulesSnapshot)
			{
				try
				{
					if (gameExtensionModule.AppliesTo(profile))
					{
						return gameExtensionModule;
					}
				}
				catch
				{
				}
			}
			return null;
		}

		public override void Shutdown(int waitMs)
		{
			GameExtensionModule[] modulesSnapshot = modules;
			foreach (GameExtensionModule gameExtensionModule in modulesSnapshot)
			{
				try
				{
					gameExtensionModule.Shutdown(waitMs);
				}
				catch
				{
				}
			}
		}
	}
}
