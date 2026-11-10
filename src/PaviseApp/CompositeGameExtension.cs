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
            List<GameExtensionModule> gameExtensionModules = new List<GameExtensionModule>();
            if (parts != null)
            {
                foreach (GameExtensionModule gameExtensionModule in parts)
                {
                    if (gameExtensionModule != null)
                    {
                        gameExtensionModules.Add(gameExtensionModule);
                    }
                }
            }
            modules = gameExtensionModules.ToArray();
        }

        public override bool TryHandleArgs(string[] args)
        {
            foreach (GameExtensionModule gameExtensionModule in modules)
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
            foreach (GameExtensionModule gameExtensionModule in modules)
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
            foreach (GameExtensionModule gameExtensionModule in modules)
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
            foreach (GameExtensionModule gameExtensionModule in modules)
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
            foreach (GameExtensionModule gameExtensionModule in modules)
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
            foreach (GameExtensionModule gameExtensionModule in modules)
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
            foreach (GameExtensionModule gameExtensionModule in modules)
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
