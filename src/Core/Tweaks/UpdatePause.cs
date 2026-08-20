// @author bdth 2074055628@qq.com
// 文件用途 对局期间暂停 Windows 更新相关服务并在结束后恢复 账目逻辑走 ServicePauser
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class UpdatePause
    {
        private static readonly string[] Names = { "wuauserv", "UsoSvc" };
        private const string Flag = "PrevUpdatePaused";

        // 更新服务停着就别碰 只处理正在跑的
        private static readonly ServicePauser pauser = new ServicePauser(Names, Flag, true);

        public static bool Activate()
        {
            List<string> justStopped, confirmed;
            bool ledgerLost;
            bool ok = pauser.Activate(out justStopped, out confirmed, out ledgerLost);
            if (ledgerLost)
            {
                Logger.Log(Lang.T("log.updatepause.1"));
                return false;
            }
            if (justStopped.Count > 0)
                Logger.Log(Lang.T("log.updatepause.2") + string.Join(" + ", justStopped.ToArray()));
            return ok;
        }

        public static bool Restore()
        {
            bool had = pauser.HadLedger;
            List<string> remain;
            bool ok = pauser.Restore(out remain);
            if (had)
            {
                if (remain.Count == 0) Logger.Log(Lang.T("log.updatepause.3"));
                else Logger.Log(Lang.T("log.updatepause.4") + string.Join(",", remain.ToArray()) + Lang.T("log.svcpause.6"));
            }
            return ok;
        }

        public static void HealFromCrash() { if (pauser.HasResidue) Restore(); }
    }
}
