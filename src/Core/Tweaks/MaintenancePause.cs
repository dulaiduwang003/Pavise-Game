// @author bdth 2074055628@qq.com
// 文件用途 对局期间关掉 Windows 自动维护 退局写回 碎片整理 NGEN 计划扫描不再挑对局中的空闲判定起跑
using Microsoft.Win32;

namespace PaviseApp
{
    // 自动维护在"空闲判定"之后启动 挂机 过场 加载屏都可能被判成空闲
    //   MaintenanceDisabled 只挡自动触发 用户手动运行维护不受影响 退局按快照写回
    internal static class MaintenancePause
    {
        // 会话日记键由恢复完成判定共同引用 改名必须两边一起
        internal const string JournalKey = "PrevMaintDisabled";
        private static readonly ReversibleReg Disabled = new ReversibleReg(
            Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance",
            "MaintenanceDisabled", RegistryValueKind.DWord, JournalKey);
        private static readonly object lk = new object();
        private static bool active;

        public static bool HasResidue
        {
            get { lock (lk) { try { return Disabled.HasBackup; } catch { return true; } } }
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                if (!Native.IsElevated()) return false;
                if (!Disabled.Apply(1))
                {
                    Logger.Log(Lang.T("log.maint.1"));
                    return false;
                }
                active = true;
                Logger.Log(Lang.T("log.maint.2"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool had = Disabled.HasBackup;
                bool ok = !had || Disabled.Restore();
                active = false;
                if (had) Logger.Log(Lang.T(ok ? "log.maint.3" : "log.maint.4"));
                return ok;
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue) Restore();
        }
    }
}
