// @author bdth 2074055628@qq.com
// 文件用途 把对局的开始 渲染进程更替与结束通知给扩展模块 扩展不自己扫游戏进程
using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // active 为真时带上当前档案与渲染进程身份 没有活动对局一律发一次"无对局"
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
