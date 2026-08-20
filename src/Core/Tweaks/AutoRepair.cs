// @author bdth 2074055628@qq.com
// 文件用途 开机把被优化教程改坏的系统默认值自动改回去 不给开关
//
// 为什么要自动做
//   这些项原本只挂在体检页等用户点"修复"。实测证据是：开发机上
//   NetworkThrottlingIndex 长期停在 0xffffffff 这个被教程改坏的值，
//   判据 (<1 || >70) 早就命中，功能却从未生效过——因为没人会去点那个按钮。
//   把值改回系统默认对任何机器都不构成权衡，不该由用户来决定做不做。
//
// 收哪些项由 RepairCatalog 的 AutoApply 决定 门槛与排除理由都写在那个文件里
// 逃生舱 自动修过的项在体检页依旧显示且可撤销 快照走 ReversibleReg 卸载时一并还原
namespace PaviseApp
{
    internal static class AutoRepair
    {
        public static int Run()
        {
            int done = 0;
            foreach (RepairTweak t in RepairCatalog.Auto())
            {
                try
                {
                    // 每项只自动动手一次 动过就交回给用户
                    // 否则用户在体检页撤销之后 下次开机又会被改回去 而且不会有任何提示
                    string onceKey = "AutoRepairDone_" + t.Key;
                    if (Settings.Load(onceKey, false)) continue;
                    if (!t.NeedsRepair()) continue;
                    string what = t.Describe();
                    if (!t.Repair())
                    {
                        Logger.Log(Lang.T("log.autorepair.2") + what);
                        continue;
                    }
                    Settings.Save(onceKey, true);
                    Logger.Log(Lang.T("log.autorepair.1") + what);
                    done++;
                }
                catch { }
            }
            return done;
        }
    }
}
