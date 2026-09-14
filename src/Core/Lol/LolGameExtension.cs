// @author bdth 2074055628@qq.com
// File purpose Plug the League of Legends enhancement into the extension module slot; when the game library recognizes a League entry, attach the enhancement area to its card
using System;
using System.Threading;

namespace PaviseApp
{
    internal static partial class GameExtension
    {
        static partial void Create(ref GameExtensionModule module)
        {
            // League of Legends first, its card is more complete; other WeGame games go to generic shell removal
            module = new CompositeGameExtension(new LolGameExtension(), new WeGameGameExtension());
        }
    }

    internal sealed class LolGameExtension : GameExtensionModule
    {
        private readonly object gate = new object();
        private LolOptimizationService service;

        public LolGameExtension()
        {
            Lang.Merge(LolLang.Table);
        }

        private LolOptimizationService Service
        {
            get
            {
                lock (gate)
                {
                    if (service == null) service = new LolOptimizationService();
                    return service;
                }
            }
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
            LolOptimizationService current;
            lock (gate) current = service;
            if (current != null) current.NotifyProcessChanges(batch);
        }

        public override bool AppliesTo(GameProfile profile)
        {
            if (profile == null) return false;
            return MentionsLol(profile.ExecutablePath) || MentionsLol(profile.Root) || MentionsLol(profile.Name);
        }

        internal static bool MentionsLol(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("LeagueClient", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("League of Legends", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("英雄联盟", StringComparison.Ordinal) >= 0;
        }

        public override GameCardExtension CreateCard(GameProfile profile)
        {
            LolOptimizationService svc = Service;
            if (!svc.Enabled) svc.Enabled = true;
            if (!svc.AdoptRootFromPath(profile.ExecutablePath) && !svc.GetSnapshot().InstallationFound)
                svc.RequestDiscovery();
            return new LolCardPanel(svc);
        }

        public override void Shutdown(int waitMs)
        {
            LolCardPanel.WaitForFileOps(waitMs);
            LolOptimizationService current;
            lock (gate) { current = service; service = null; }
            if (current != null) current.Dispose();
        }
    }
}
