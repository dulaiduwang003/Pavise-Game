// @author bdth 2074055628@qq.com
// 文件用途 英雄联盟增强接进扩展模块槽 游戏库识别到英雄联盟条目时给卡片挂增强区
using System;
using System.Threading;

namespace PaviseApp
{
    internal static partial class GameExtension
    {
        static partial void Create(ref GameExtensionModule module)
        {
            // 英雄联盟在前 它的卡片更全 其余 WeGame 游戏归通用脱壳
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
