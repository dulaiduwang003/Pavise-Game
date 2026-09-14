// @author bdth 2074055628@qq.com
// File purpose Optional game extension module slot, when the module isn't compiled in every entry silently returns empty and neither card nor runtime changes
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal abstract class GameCardExtension : Control
    {
        public abstract int PreferredHeight { get; }
        public virtual void SetSurface(Color surface) { }
    }

    internal abstract class GameExtensionModule
    {
        public virtual bool TryHandleArgs(string[] args) { return false; }
        public virtual void Start() { }
        public virtual bool NeedsProcessIdentity(string name) { return false; }
        public virtual void NotifyProcessChanges(ProcessChangeBatch batch) { }
        // Match start, renderer process handover and match end all come through here, null profile means no match
        public virtual void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active) { }
        public virtual bool AppliesTo(GameProfile profile) { return false; }
        public virtual GameCardExtension CreateCard(GameProfile profile) { return null; }
        public virtual void Shutdown(int waitMs) { }
    }

    // Multiple modules chained in one slot, the card goes to the first module that claims it, every other entry is broadcast
    internal sealed class CompositeGameExtension : GameExtensionModule
    {
        private readonly GameExtensionModule[] modules;

        public CompositeGameExtension(params GameExtensionModule[] parts)
        {
            var list = new List<GameExtensionModule>();
            if (parts != null)
                foreach (GameExtensionModule part in parts)
                    if (part != null) list.Add(part);
            modules = list.ToArray();
        }

        public GameExtensionModule[] Modules { get { return (GameExtensionModule[])modules.Clone(); } }

        public override bool TryHandleArgs(string[] args)
        {
            foreach (GameExtensionModule m in modules)
                try { if (m.TryHandleArgs(args)) return true; } catch { }
            return false;
        }

        public override void Start()
        {
            foreach (GameExtensionModule m in modules)
                try { m.Start(); } catch (Exception ex) { Logger.Log("扩展模块启动失败 " + ex.Message); }
        }

        public override bool NeedsProcessIdentity(string name)
        {
            foreach (GameExtensionModule m in modules)
                try { if (m.NeedsProcessIdentity(name)) return true; } catch { }
            return false;
        }

        public override void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            foreach (GameExtensionModule m in modules)
                try { m.NotifyProcessChanges(batch); } catch { }
        }

        public override void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active)
        {
            foreach (GameExtensionModule m in modules)
                try { m.NotifySession(profile, rendererPid, rendererCreation, active); } catch { }
        }

        public override bool AppliesTo(GameProfile profile)
        {
            return Owner(profile) != null;
        }

        public override GameCardExtension CreateCard(GameProfile profile)
        {
            GameExtensionModule owner = Owner(profile);
            return owner == null ? null : owner.CreateCard(profile);
        }

        // First claim wins, League of Legends is ordered before generic WeGame since its card is more complete
        internal GameExtensionModule Owner(GameProfile profile)
        {
            if (profile == null) return null;
            foreach (GameExtensionModule m in modules)
                try { if (m.AppliesTo(profile)) return m; } catch { }
            return null;
        }

        public override void Shutdown(int waitMs)
        {
            foreach (GameExtensionModule m in modules)
                try { m.Shutdown(waitMs); } catch { }
        }
    }

    internal static partial class GameExtension
    {
        public static readonly GameExtensionModule Current = Load();
        public static bool Present { get { return Current != null; } }

        static partial void Create(ref GameExtensionModule module);

        private static GameExtensionModule Load()
        {
            GameExtensionModule module = null;
            try { Create(ref module); }
            catch { module = null; }
            return module;
        }

        public static bool TryHandleArgs(string[] args)
        {
            try { return Current != null && Current.TryHandleArgs(args); }
            catch { return false; }
        }

        public static void Start()
        {
            try { if (Current != null) Current.Start(); }
            catch (Exception ex) { Logger.Log("扩展模块启动失败 " + ex.Message); }
        }

        public static bool NeedsProcessIdentity(string name)
        {
            try { return Current != null && Current.NeedsProcessIdentity(name); }
            catch { return false; }
        }

        public static void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            try { if (Current != null) Current.NotifyProcessChanges(batch); }
            catch { }
        }

        public static void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active)
        {
            try { if (Current != null) Current.NotifySession(profile, rendererPid, rendererCreation, active); }
            catch { }
        }

        public static GameCardExtension CreateCard(GameProfile profile)
        {
            try
            {
                if (profile == null) return null;
                return Current != null && Current.AppliesTo(profile) ? Current.CreateCard(profile) : null;
            }
            catch (Exception ex) { Logger.Log("扩展卡片创建失败 " + ex.Message); return null; }
        }

        public static void Shutdown(int waitMs)
        {
            try { if (Current != null) Current.Shutdown(waitMs); }
            catch { }
        }
    }
}
