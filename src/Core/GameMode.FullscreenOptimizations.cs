using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal const string AutomaticFsoKey = "AutoDisableFullscreenOptimizations";
        private sealed class AutomaticFsoEntry
        {
            internal bool Disable;
            internal bool Configured;
            internal int Failures;
            internal long CheckAt;
        }
        private readonly Dictionary<string, AutomaticFsoEntry> automaticFsoEntries =
            new Dictionary<string, AutomaticFsoEntry>(StringComparer.OrdinalIgnoreCase);
        private long automaticFsoCheckAt;
        private string automaticFsoState = "";

        private void InvalidateAutomaticFullscreenOptimizations()
        {
            lock (sync)
            {
                automaticFsoCheckAt = 0;
                foreach (AutomaticFsoEntry entry in automaticFsoEntries.Values) entry.CheckAt = 0;
            }
        }

        internal bool SetProfileFullscreenOptimizations(string profileId, bool on)
        {
            // 先保存选择再写 Windows，原生失败时保留重试意图。
            return SetProfileOverride(profileId, PolicyCatalog.KeyGameFullscreenOptimizations, on ? "1" : "0");
        }

        internal void SyncAutomaticFullscreenOptimizations(bool discoverTargets = false, long nowTicks = -1)
        {
            if (nowTicks < 0) nowTicks = DateTime.UtcNow.Ticks;
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed
                    || autoIgnoreLoadFailed
                    || (libraryIgnoreTransaction != null && libraryIgnoreTransaction.RecoveryPending)) return;
                GameDetection renderer = activeDetection;
                GameProfile rendererProfile = renderer == null || renderer.Profile == null
                    ? null : FindProfileLocked(renderer.Profile.Id);
                bool verifiedRenderer = renderer != null && renderer.Profile != null && renderer.RendererCandidateSelected
                    && !renderer.RendererSafetyOnly && !renderer.RequiresGpuConfirm
                    && rendererProfile != null && rendererProfile.DisableFullscreenOptimizations
                    && RendererHandoffTracker.SameProfile(renderer.Profile, rendererProfile);
                bool newRenderer = discoverTargets && verifiedRenderer && !string.IsNullOrEmpty(renderer.RendererPath)
                    && !automaticFsoEntries.ContainsKey(renderer.RendererPath);
                if (!newRenderer && nowTicks < automaticFsoCheckAt) return;

                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (GameProfile profile in profiles)
                {
                    if (!profile.DisableFullscreenOptimizations) continue;
                    AddAutomaticFsoPath(paths, profile.ExecutablePath);
                    AddAutomaticFsoPath(paths, profile.LearnedExecutablePath);
                }
                if (verifiedRenderer)
                    AddAutomaticFsoPath(paths, renderer.RendererPath);

                var targets = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                bool restoring = false;
                try
                {
                    foreach (string path in FsoTweak.TrackedExes())
                    {
                        targets.Add(path);
                        if (!paths.Contains(path)) restoring = true;
                    }
                }
                catch
                {
                    // 收据不可读不能视为空表。
                    automaticFsoCheckAt = nowTicks + TimeSpan.TicksPerMinute;
                    automaticFsoState = "failed";
                    RecordAutomation(AutomaticFsoKey, automaticFsoState);
                    return;
                }
                foreach (string path in new List<string>(automaticFsoEntries.Keys))
                    if (!targets.Contains(path)) automaticFsoEntries.Remove(path);

                bool allConfigured = true;
                automaticFsoCheckAt = nowTicks + TimeSpan.TicksPerMinute;
                foreach (string path in targets)
                {
                    if (stopping || ProfileStoreSaveFailed) return;
                    bool disable = paths.Contains(path);
                    AutomaticFsoEntry entry;
                    if (!automaticFsoEntries.TryGetValue(path, out entry))
                    {
                        entry = new AutomaticFsoEntry { Disable = disable };
                        automaticFsoEntries[path] = entry;
                    }
                    if (entry.Disable != disable)
                    {
                        entry.Disable = disable; entry.Configured = false;
                        entry.Failures = 0; entry.CheckAt = 0;
                    }
                    if (nowTicks >= entry.CheckAt)
                    {
                        entry.Configured = FsoTweak.SetForExe(path, disable);
                        entry.Failures = entry.Configured ? 0 : Math.Min(entry.Failures + 1, 5);
                        int delaySeconds = entry.Configured ? 60 : Math.Min(300, 30 << (entry.Failures - 1));
                        entry.CheckAt = nowTicks + delaySeconds * TimeSpan.TicksPerSecond;
                    }
                    allConfigured &= entry.Configured;
                    automaticFsoCheckAt = Math.Min(automaticFsoCheckAt, entry.CheckAt);
                }
                automaticFsoState = !allConfigured ? "failed" : paths.Count > 0 ? "fso-configured" : restoring ? "restored" : "";
                RecordAutomation(AutomaticFsoKey, automaticFsoState);
            }
        }

        private void AddAutomaticFsoPath(HashSet<string> paths, string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)
                || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(GameProfileStore.NormalizePath(path), path, StringComparison.OrdinalIgnoreCase)
                || !GamePerformanceTargetPolicy.AllowsGameTarget(path)
                || !File.Exists(path)) return;
            paths.Add(path);
        }
    }
}
