// @author bdth 2074055628@qq.com
// File purpose Notifies extension modules of match start, renderer process handover and match end, extensions don't scan game processes themselves
using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // active true carries the current profile and renderer identity, with no active match always send one no-match notification
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
