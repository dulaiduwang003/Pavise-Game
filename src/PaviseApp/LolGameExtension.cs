using System;
using System.IO;

namespace PaviseApp
{
    internal sealed class LolGameExtension : GameExtensionModule
    {
        private readonly object gate = new object();

        private LolOptimizationService service;

        private LolOptimizationService Service
        {
            get
            {
                lock (gate)
                {
                    if (service == null)
                    {
                        service = new LolOptimizationService();
                    }
                    return service;
                }
            }
        }

        public LolGameExtension()
        {
            Lang.Merge(LolLang.Table);
        }

        public override bool TryHandleArgs(string[] args)
        {
            return LolWatchdog.TryHandle(args);
        }

        public override void Start()
        {
            Service.Start();
        }

        public override bool NeedsProcessIdentity(string name)
        {
            return LolRuntimeProcesses.IsScanCandidateName(name);
        }

        public override void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            LolOptimizationService lolOptimizationService;
            lock (gate)
            {
                lolOptimizationService = service;
            }
            if (lolOptimizationService != null)
            {
                lolOptimizationService.NotifyProcessChanges(batch);
            }
        }

        public override bool AppliesTo(GameProfile profile)
        {
            if (profile == null)
            {
                return false;
            }
            if (!MentionsLol(profile.ExecutablePath) && !MentionsLol(profile.Root))
            {
                return MentionsLol(profile.Name);
            }
            return true;
        }

        internal static bool MentionsLol(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            if (text.IndexOf("LeagueClient", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("League of Legends", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return text.IndexOf("英雄联盟", StringComparison.Ordinal) >= 0;
            }
            return true;
        }

        public override GameCardExtension CreateCard(GameProfile profile)
        {
            LolOptimizationService lolOptimizationService = Service;
            if (!lolOptimizationService.Enabled)
            {
                lolOptimizationService.Enabled = true;
            }
            string root;
            string localPath = TryResolveProfileRoot(profile, out root) ? Path.Combine(root, "LeagueClient", "LeagueClient.exe") : profile.ExecutablePath;
            if (!lolOptimizationService.AdoptRootFromPath(localPath) && !lolOptimizationService.GetSnapshot().InstallationFound)
            {
                lolOptimizationService.RequestDiscovery();
            }
            return new LolCardPanel(lolOptimizationService);
        }

        internal static bool TryResolveProfileRoot(GameProfile profile, out string root)
        {
            root = null;
            if (profile == null) return false;
            string error;
            foreach (string input in new[] { profile.ExecutablePath, profile.Root })
            {
                if (LolAddonCleaner.TryResolveRoot(input, out root, out error)) return true;
            }
            return false;
        }

        public override void Shutdown(int waitMs)
        {
            LolCardPanel.WaitForFileOps(waitMs);
            LolOptimizationService lolOptimizationService;
            lock (gate)
            {
                lolOptimizationService = service;
                service = null;
            }
            if (lolOptimizationService != null)
            {
                lolOptimizationService.Dispose();
            }
        }
    }
}
