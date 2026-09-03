// @author bdth 2074055628@qq.com
// 文件用途 可选游戏扩展模块槽 模块不在编译里时所有入口静默返回空 卡片和运行时都不变
using System;
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
        public virtual bool AppliesTo(GameProfile profile) { return false; }
        public virtual GameCardExtension CreateCard(GameProfile profile) { return null; }
        public virtual void Shutdown(int waitMs) { }
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

        public static GameCardExtension CreateCard(GameProfile profile)
        {
            try
            {
                if (Current == null || profile == null || !Current.AppliesTo(profile)) return null;
                return Current.CreateCard(profile);
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
