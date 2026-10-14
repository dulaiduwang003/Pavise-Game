using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly PerformanceActionCoordinator performanceActions = new PerformanceActionCoordinator();
        private string[] performanceBackground = new string[0];
        private void SyncPerformanceAutomation(string[] background = null)
        {
            bool guard, match, visualMatch; string[] backgroundSnapshot; var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
            {
                guard = AutomaticPolicy.Enabled && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed
                    && !autoIgnoreLoadFailed && (libraryIgnoreTransaction == null || !libraryIgnoreTransaction.RecoveryPending);
                match = guard && active;
                visualMatch = MatchVisualEffects.Enabled && enabled && active && !stopping && !panicReq
                    && !ProfileStoreSaveFailed;
                if (background != null) performanceBackground = background;
                if (!match) performanceBackground = new string[0];
                if (guard)
                {
                    foreach (GameProfile profile in profiles)
                    {
                        AddGamePerformancePath(games, roots, profile.ExecutablePath, profile.Root);
                        AddGamePerformancePath(games, roots, profile.LearnedExecutablePath, profile.Root);
                    }
                    GameDetection renderer = activeDetection;
                    if (renderer != null && renderer.Profile != null && renderer.RendererCandidateSelected
                        && !renderer.RendererSafetyOnly && !renderer.RequiresGpuConfirm
                        && RendererHandoffTracker.SameProfile(renderer.Profile, FindProfileLocked(renderer.Profile.Id)))
                    {
                        AddGamePerformancePath(games, roots, renderer.RendererPath, renderer.Profile.Root);
                    }
                }
                backgroundSnapshot = (string[])performanceBackground.Clone();
            }
            performanceActions.Queue(guard, match, new List<string>(games).ToArray(), backgroundSnapshot, roots, visualMatch);
        }
        internal bool SetVisualEffectsReduction(bool value)
        {
            lock (sync)
                if (stopping || !Settings.Save(PerformanceActionCatalog.VisualEffects, value)) return false;
            SyncPerformanceAutomation();
            RequestPolicyApply();
            return true;
        }
        private void AddGamePerformancePath(HashSet<string> games, Dictionary<string, string> roots, string path, string root)
        {
            if (!GamePerformanceTargetPolicy.AllowsGameTarget(path)) return;
            AddAutomaticFsoPath(games, path);
            if (games.Contains(path)) roots[path] = root;
        }
        // NVIDIA 按镜像名称识别程序，不同目录的同名文件也需保护。
        private bool BackgroundFpsProtectedLocked(string name, string path)
        {
            if (AutoGpuProtectedLocked(name, path)) return true;
            string image = WhitelistRule.NormalizeName(Path.GetFileName(path));
            foreach (GameProfile profile in profiles)
                if (SamePerformanceImage(image, profile.ExecutablePath) || SamePerformanceImage(image, profile.LearnedExecutablePath)) return true;
            if (activeDetection != null && SamePerformanceImage(image, activeDetection.RendererPath)) return true;
            foreach (WhitelistRule rule in whiteRules)
                if (string.Equals(image, WhitelistRule.NormalizeName(rule.Value), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static bool SamePerformanceImage(string image, string path)
        { return !string.IsNullOrEmpty(path) && string.Equals(image, WhitelistRule.NormalizeName(Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase); }
        internal string PerformanceExecutionState(string key)
        { return performanceActions.State(key); }
    }
}
