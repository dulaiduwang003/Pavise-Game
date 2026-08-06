// @author bdth 2074055628@qq.com
// 文件用途 自动入库 前台全屏加GPU主导的陌生游戏自动加入目标库 移除即永久忽略

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private const int AutoAddGateMs = 30000;
        private const int AutoAddRejectMinutes = 10;
        private const int AutoAddRejectCacheLimit = 64;

        private string autoIgnorePath;
        private long autoAddGateTicks;
        private readonly HashSet<string> autoAddIgnore =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> autoAddRejectUntil =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public event Action<string> GameAutoAdded;
        public event Action LibraryChanged;

        private void RaiseLibraryChanged()
        {
            Action handler = LibraryChanged;
            if (handler != null) { try { handler(); } catch { } }
        }

        private void TryAutoAddForegroundGame()
        {
            if (stopping || !enabled) return;
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
            if (candidate < GpuEvidence.MinAutoAddUtilization)
            { RememberAutoAddReject(path, now); return; }
            foreach (KeyValuePair<int, double> kv in util)
            {
                if (kv.Key == pid || kv.Value <= candidate) continue;
                if (!IsCompositorPid(kv.Key)) { RememberAutoAddReject(path, now); return; }
            }

            int foregroundNow;
            if (!GameSessionDetector.TryForegroundFullscreen(out foregroundNow)
                || foregroundNow != pid)
                return;

            string error;
            if (!AddGameExecutableCore(null, path, null, true, out error))
            { RememberAutoAddReject(path, now); return; }
            string display = identity.Name;
            lock (sync)
                foreach (GameProfile profile in profiles)
                    if (SameLibraryPath(profile.ExecutablePath, path)) { display = profile.Name; break; }
            Logger.Log("自动入库：检测到前台全屏 + GPU 3D 主导（" + (int)candidate + "%）的游戏进程 "
                + identity.Name + "，已加入目标库（" + path + "）；不需要的话在游戏库移除，之后不会再自动加入");
            Action<string> handler = GameAutoAdded;
            if (handler != null) { try { handler(display); } catch { } }
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
                lines.Add("# 从游戏库移除过的路径不再自动入库；重新手动添加会自动解除。");
                var sorted = new List<string>(autoAddIgnore);
                sorted.Sort(StringComparer.OrdinalIgnoreCase);
                lines.AddRange(sorted);
                AtomicFile.WriteLines(autoIgnorePath, lines.ToArray(), "自动入库忽略表");
            }
            catch (Exception ex) { Logger.LogFailure("自动入库忽略表保存失败", ex); }
        }
    }
}
