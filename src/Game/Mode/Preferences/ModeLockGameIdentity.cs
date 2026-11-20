using System;

namespace PaviseApp
{
    // 模式锁只通过这个接口问游戏库 不直接碰 GameMode 的字段
    internal interface IModeLockGameHost
    {
        // 调用方持有游戏模式的 sync
        bool ProfileListedLocked(string profileId);
    }

    // 记住最后一局渲染进程的编号和创建时间 它还在运行就不许切换性能模式
    // 没有自己的锁 读写都在游戏模式的 sync 里 名字带 Locked 的方法要求调用方已经持有它 实例归 GameMode 所有
    internal sealed class ModeLockGameIdentity
    {
        private readonly IModeLockGameHost host;
        private int gamePid;
        private long gameCreation;
        private string gameProfileId;

        internal ModeLockGameIdentity(IModeLockGameHost host)
        {
            if (host == null) throw new ArgumentNullException("host");
            this.host = host;
        }

        // 游戏已经不在库里就不记 和删除游戏时清掉模式锁保持一致
        internal void RememberLocked(GameDetection detection)
        {
            if (detection != null && detection.RendererCreation > 0)
            {
                string id = ((detection.Profile != null) ? detection.Profile.Id : null);
                if (string.IsNullOrEmpty(id) || host.ProfileListedLocked(id))
                {
                    gamePid = detection.RendererPid;
                    gameCreation = detection.RendererCreation;
                    gameProfileId = id;
                }
            }
        }

        // 打不开进程而且不是因为它已经退出 或者读不到创建时间 都按还在运行处理 宁可多锁一会儿
        internal bool StillRunningLocked()
        {
            if (gamePid <= 4 || gameCreation <= 0)
            {
                return false;
            }
            IntPtr intPtr = Native.OpenProcess(1052672, false, gamePid);
            if (intPtr == IntPtr.Zero)
            {
                return !Native.LastOpenProcessFailureWasNoSuchProcess();
            }
            try
            {
                if (Native.WaitForSingleObject(intPtr, 0u) != 258u)
                {
                    return false;
                }
                long creation;
                long cpu;
                ulong io;
                return !Native.QueryProcessSample(intPtr, out creation, out cpu, out io) || creation == gameCreation;
            }
            finally
            {
                Native.CloseHandle(intPtr);
            }
        }

        internal void ClearForProfileLocked(string profileId)
        {
            if (!string.IsNullOrEmpty(profileId) && string.Equals(gameProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                gamePid = 0;
                gameCreation = 0L;
                gameProfileId = null;
            }
        }
    }
}
