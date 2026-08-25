// @author bdth 2074055628@qq.com
// 文件用途 自动入库 开关在游戏库页 前台全屏且GPU主导的陌生游戏自动加入目标库 移除即永久忽略

using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // 20% 比渲染进程选举的 10% 更严 自动写库的误报代价高于漏报
        private const double AutoAddMinUtilization = 20.0;
        private const int AutoAddGateMs = 30000;
        private const int AutoAddRejectMinutes = 10;
        private const int AutoAddRejectCacheLimit = 64;

        private string autoIgnorePath;
        private volatile bool autoAddOn;
        private long autoAddGateTicks;
        private readonly HashSet<string> autoAddIgnore =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> autoAddRejectUntil =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public bool AutoAddFullscreen
        {
            get { return autoAddOn; }
            set
            {
                if (autoAddOn == value) return;
                autoAddOn = value;
                Settings.Save("GmAutoAdd", value);
                Logger.Log(Lang.T(value ? "log.autoadd.1" : "log.autoadd.2"));
            }
        }

        private void TryAutoAddForegroundGame()
        {
            if (!autoAddOn || stopping || !enabled) return;
            bool sessionActive;
            lock (sync) sessionActive = active;
            if (sessionActive) return;

            int pid;
            if (!GameSessionDetector.TryForegroundFullscreen(out pid)
                || pid <= 0 || pid == selfPid)
                return;
            long now = DateTime.UtcNow.Ticks;
            if (now < autoAddGateTicks) return;
            autoAddGateTicks = now + AutoAddGateMs * TimeSpan.TicksPerMillisecond;

            GameProcessSnapshot identity;
            if (!GameSessionDetector.TryCaptureProcessIdentity(pid, selfSession, out identity))
                return;
            if (!GameSessionDetector.IsLibraryCandidate(identity.Name, identity.Path, windowsPrefix))
                return;
            if (GamePlatformCatalog.IsPlatformProcess(identity.Name, identity.Path)) return;
            string path = identity.Path;
            lock (sync)
            {
                if (autoAddIgnore.Contains(path)) return;
                long until;
                if (autoAddRejectUntil.TryGetValue(path, out until) && now < until) return;
                foreach (GameProfile profile in profiles)
                    if (SameLibraryPath(profile.ExecutablePath, path)
                        || SameLibraryPath(profile.LearnedExecutablePath, path)
                        || profile.ContainsPath(path))
                        return;
            }

            Dictionary<int, double> util = GpuEvidence.Sample3D(
                GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs,
                delegate { return stopping || panicReq; });
            if (util == null) { RememberAutoAddReject(path, now); return; }
            double candidate;
            if (!util.TryGetValue(pid, out candidate)) candidate = 0;
            if (candidate < AutoAddMinUtilization)
            { RememberAutoAddReject(path, now); return; }
            foreach (KeyValuePair<int, double> kv in util)
            {
                if (kv.Key == pid || kv.Value <= candidate) continue;
                if (!IsCompositorPid(kv.Key)) { RememberAutoAddReject(path, now); return; }
            }

            // 采样耗时 1.4 秒 期间前台可能已经易主 再确认一次才有资格写库
            int foregroundNow;
            if (!GameSessionDetector.TryForegroundFullscreen(out foregroundNow)
                || foregroundNow != pid)
                return;

            string error;
            if (!AddGameExecutableCore(null, path, null, true, out error))
            { RememberAutoAddReject(path, now); return; }
            Logger.Log(Lang.T("log.autoadd.3") + (int)candidate + Lang.T("log.autoadd.4")
                + identity.Name + Lang.T("log.autoadd.5") + path + Lang.T("log.autoadd.6"));
        }

        private void RememberAutoAddReject(string path, long now)
        {
            lock (sync)
            {
                if (autoAddRejectUntil.Count >= AutoAddRejectCacheLimit)
                {
                    var expired = new List<string>();
                    foreach (KeyValuePair<string, long> kv in autoAddRejectUntil)
                        if (kv.Value <= now) expired.Add(kv.Key);
                    foreach (string key in expired) autoAddRejectUntil.Remove(key);
                    if (autoAddRejectUntil.Count >= AutoAddRejectCacheLimit)
                        autoAddRejectUntil.Clear();
                }
                autoAddRejectUntil[path] = now
                    + AutoAddRejectMinutes * TimeSpan.TicksPerMinute;
            }
        }

        private bool IsCompositorPid(int pid)
        {
            string name;
            long creation;
            return TryIdentity(pid, out name, out creation)
                && string.Equals(name, "dwm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameLibraryPath(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private void LoadAutoIgnore()
        {
            try
            {
                if (!File.Exists(autoIgnorePath)) return;
                foreach (string line in File.ReadAllLines(autoIgnorePath))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    autoAddIgnore.Add(t);
                }
            }
            catch { }
        }

        private void SaveAutoIgnoreLocked()
        {
            try
            {
                var lines = new List<string>();
                lines.Add(Lang.T("t.autoadd.1"));
                var sorted = new List<string>(autoAddIgnore);
                sorted.Sort(StringComparer.OrdinalIgnoreCase);
                lines.AddRange(sorted);
                AtomicFile.WriteLines(autoIgnorePath, lines.ToArray(), Lang.T("t.autoadd.2"));
            }
            catch (Exception ex) { Logger.LogFailure(Lang.T("log.autoadd.7"), ex); }
        }
    }
}
