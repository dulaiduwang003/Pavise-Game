using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    // 逐 exe 启动设置只通过这个接口读游戏模式的状态和请它记录 不直接碰 GameMode 的字段
    internal interface IExeLaunchSettingHost
    {
        // 就是游戏模式自己的 sync 条目 检查时间和状态都在这把锁下读写
        object SyncRoot { get; }
        bool Stopping { get; }
        bool ProfileStoreSaveFailed { get; }
        // 自动忽略列表没读成 或者游戏库事务还有没恢复完的部分
        bool LibraryRecoveryPending { get; }
        GameDetection ActiveDetection { get; }
        // 名字带 Locked 的成员要求调用方已经持有 SyncRoot
        IEnumerable<GameProfile> ProfilesLocked { get; }
        GameProfile FindProfileLocked(string profileId);
        void RecordAutomation(string key, string state);
    }

    // 一种按 exe 写进系统的设置 游戏库逐游戏勾选 下次启动游戏生效
    internal sealed class ExeLaunchSetting
    {
        internal readonly string AutomationKey;

        // 有游戏勾选且全部写好时记下的状态
        internal readonly string ConfiguredState;

        internal readonly Func<GameProfile, bool> Wanted;

        // 读不出记录时抛异常 这一轮整体按失败处理
        internal readonly Func<string[]> TrackedExes;

        internal readonly Func<string, bool, bool> SetForExe;

        internal ExeLaunchSetting(string automationKey, string configuredState, Func<GameProfile, bool> wanted, Func<string[]> trackedExes, Func<string, bool, bool> setForExe)
        {
            AutomationKey = automationKey;
            ConfiguredState = configuredState;
            Wanted = wanted;
            TrackedExes = trackedExes;
            SetForExe = setForExe;
        }

        internal static readonly ExeLaunchSetting FullscreenOptimizations = new ExeLaunchSetting(
            GameMode.AutomaticFsoKey, "fso-configured", profile => profile.DisableFullscreenOptimizations, FsoTweak.TrackedExes, FsoTweak.SetForExe);

        internal static readonly ExeLaunchSetting ControlFlowGuard = new ExeLaunchSetting(
            GameMode.AutomaticCfgKey, "cfg-configured", profile => profile.DisableControlFlowGuard, CfgTweak.TrackedExes, CfgTweak.SetForExe);
    }

    // 按游戏库里勾选的游戏和本局确认的渲染进程写入逐 exe 设置 已经不需要的按记录撤销 实例归 GameMode 所有
    // 全屏优化和 CFG 各走一条通道 一条失败不拖累另一条
    internal sealed class ExeLaunchSettingCoordinator
    {
        private sealed class Entry
        {
            internal bool Wanted;

            internal bool Configured;

            internal int Failures;

            internal long CheckAt;
        }

        private sealed class Lane
        {
            internal readonly ExeLaunchSetting Setting;

            internal readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            internal long CheckAt;

            internal string State = "";

            internal Lane(ExeLaunchSetting setting)
            {
                Setting = setting;
            }
        }

        private const long TicksPerSecond = 10000000L;

        private readonly IExeLaunchSettingHost host;

        private readonly Lane[] lanes;

        internal ExeLaunchSettingCoordinator(IExeLaunchSettingHost host)
            : this(host, ExeLaunchSetting.FullscreenOptimizations, ExeLaunchSetting.ControlFlowGuard)
        {
        }

        internal ExeLaunchSettingCoordinator(IExeLaunchSettingHost host, params ExeLaunchSetting[] settings)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (settings == null || settings.Length == 0) throw new ArgumentException("settings");
            this.host = host;
            lanes = new Lane[settings.Length];
            for (int i = 0; i < settings.Length; i++)
            {
                lanes[i] = new Lane(settings[i]);
            }
        }

        // 调用方持有宿主的 SyncRoot 没有这条通道时返回 null
        internal string State(string automationKey)
        {
            foreach (Lane lane in lanes)
            {
                if (lane.Setting.AutomationKey == automationKey)
                {
                    return lane.State;
                }
            }
            return null;
        }

        internal void Invalidate()
        {
            lock (host.SyncRoot)
            {
                foreach (Lane lane in lanes)
                {
                    lane.CheckAt = 0L;
                    foreach (Entry entry in lane.Entries.Values)
                    {
                        entry.CheckAt = 0L;
                    }
                }
            }
        }

        internal void Sync(bool discoverTargets = false, long nowTicks = -1L)
        {
            if (nowTicks < 0)
            {
                nowTicks = DateTime.UtcNow.Ticks;
            }
            lock (host.SyncRoot)
            {
                if (host.Stopping || host.ProfileStoreSaveFailed || host.LibraryRecoveryPending)
                {
                    return;
                }
                GameDetection detection = host.ActiveDetection;
                GameProfile activeProfile = (detection == null || detection.Profile == null) ? null : host.FindProfileLocked(detection.Profile.Id);
                bool rendererConfirmed = detection != null && detection.Profile != null && detection.RendererCandidateSelected && !detection.RendererSafetyOnly && !detection.RequiresGpuConfirm && activeProfile != null && RendererHandoffTracker.SameProfile(detection.Profile, activeProfile);
                foreach (Lane lane in lanes)
                {
                    if (host.Stopping || host.ProfileStoreSaveFailed)
                    {
                        return;
                    }
                    string renderer = rendererConfirmed && lane.Setting.Wanted(activeProfile) ? detection.RendererPath : null;
                    SyncLane(lane, discoverTargets ? renderer : null, renderer, nowTicks);
                }
            }
        }

        private void SyncLane(Lane lane, string discovered, string renderer, long nowTicks)
        {
            if ((string.IsNullOrEmpty(discovered) || lane.Entries.ContainsKey(discovered)) && nowTicks < lane.CheckAt)
            {
                return;
            }
            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile profile in host.ProfilesLocked)
            {
                if (lane.Setting.Wanted(profile))
                {
                    AddPath(wanted, profile.ExecutablePath);
                }
            }
            AddPath(wanted, renderer);
            HashSet<string> targets = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            bool restoredAny = false;
            try
            {
                foreach (string tracked in lane.Setting.TrackedExes())
                {
                    targets.Add(tracked);
                    restoredAny |= !wanted.Contains(tracked);
                }
            }
            catch
            {
                lane.CheckAt = nowTicks + 60 * TicksPerSecond;
                SetState(lane, "failed");
                return;
            }
            foreach (string path in new List<string>(lane.Entries.Keys))
            {
                if (!targets.Contains(path))
                {
                    lane.Entries.Remove(path);
                }
            }
            bool allConfigured = true;
            lane.CheckAt = nowTicks + 60 * TicksPerSecond;
            foreach (string path in targets)
            {
                if (host.Stopping || host.ProfileStoreSaveFailed)
                {
                    return;
                }
                bool wantedNow = wanted.Contains(path);
                Entry entry;
                if (!lane.Entries.TryGetValue(path, out entry))
                {
                    entry = new Entry { Wanted = wantedNow };
                    lane.Entries[path] = entry;
                }
                if (entry.Wanted != wantedNow)
                {
                    entry.Wanted = wantedNow;
                    entry.Configured = false;
                    entry.Failures = 0;
                    entry.CheckAt = 0L;
                }
                if (nowTicks >= entry.CheckAt)
                {
                    entry.Configured = lane.Setting.SetForExe(path, wantedNow);
                    entry.Failures = entry.Configured ? 0 : Math.Min(entry.Failures + 1, 5);
                    int delaySeconds = entry.Configured ? 60 : Math.Min(300, 30 << (entry.Failures - 1));
                    entry.CheckAt = nowTicks + delaySeconds * TicksPerSecond;
                }
                allConfigured &= entry.Configured;
                lane.CheckAt = Math.Min(lane.CheckAt, entry.CheckAt);
            }
            SetState(lane, !allConfigured ? "failed" : wanted.Count > 0 ? lane.Setting.ConfiguredState : restoredAny ? "restored" : "");
        }

        private void SetState(Lane lane, string state)
        {
            lane.State = state;
            host.RecordAutomation(lane.Setting.AutomationKey, state);
        }

        internal static void AddPath(HashSet<string> paths, string path)
        {
            if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(GameProfileStore.NormalizePath(path), path, StringComparison.OrdinalIgnoreCase) && GamePerformanceTargetPolicy.AllowsGameTarget(path) && File.Exists(path))
            {
                paths.Add(path);
            }
        }
    }
}
