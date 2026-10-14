// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void NotifyExtensionSession(bool active)
        {
            GameProfile profile = null;
            int pid = 0;
            long creation = 0;
            if (active)
            {
                lock (sync)
                {
                    if (this.active && activeDetection != null)
                    {
                        profile = activeDetection.Profile;
                        pid = activeDetection.RendererPid;
                        creation = activeDetection.RendererCreation;
                    }
                }
            }
            GameExtension.NotifySession(profile, pid, creation, profile != null);
        }
    }
}
