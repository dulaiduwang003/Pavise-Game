// @author bdth 2074055628@qq.com
// 文件用途 可选游戏扩展模块槽 模块不在编译里时所有入口静默返回空 卡片和运行时都不变
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
        // 对局开始 渲染进程更替 对局结束 都从这里进来 profile 为空表示没有对局
        public virtual void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active) { }
        public virtual bool AppliesTo(GameProfile profile) { return false; }
        public virtual GameCardExtension CreateCard(GameProfile profile) { return null; }
        public virtual void Shutdown(int waitMs) { }
    }

    // 多个模块串在一个槽里 卡片给第一个认领的模块 其余入口全部广播
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

        // 谁先认领归谁 英雄联盟排在通用 WeGame 前面 它的卡片更全
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
            ScalingService.Start();
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
            ScalingService.NotifySession(profile, rendererPid, rendererCreation, active);
            try { if (Current != null) Current.NotifySession(profile, rendererPid, rendererCreation, active); }
            catch { }
        }

        public static GameCardExtension CreateCard(GameProfile profile)
        {
            try
            {
                if (profile == null) return null;
                GameCardExtension existing = Current != null && Current.AppliesTo(profile) ? Current.CreateCard(profile) : null;
                return new ScalingCardPanel(profile, existing);
            }
            catch (Exception ex) { Logger.Log("扩展卡片创建失败 " + ex.Message); return null; }
        }

        public static void Shutdown(int waitMs)
        {
            ScalingService.Shutdown(waitMs);
            try { if (Current != null) Current.Shutdown(waitMs); }
            catch { }
        }
    }
}
