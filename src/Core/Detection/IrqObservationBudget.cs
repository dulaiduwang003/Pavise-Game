// @author bdth 2074055628@qq.com
// 文件用途 观测降频 证据饱和后按预算抽局观测 证据不足或有待验收时全量观测
using System;
using System.Globalization;

namespace PaviseApp
{
    // 对局观测是全家最重的探针 每个 DPC 都要写事件 消费线程整局解码
    //   台账只留 12 局 裁决只看 5 局窗口 证据饱和之后继续每局全程观测
    //   信息增量趋近于零 开销照付 这里按预算抽局 四局观测一局
    //
    // 三种情况永远全量 判据缺一不可
    //   本 boot 的合格局还不够裁决窗口 证据还在攒
    //   自动编排有待重启或验收中的钉核 验收就等着这些局
    //   判定过程任何一步出错 宁可全量观测也不能少证据
    // 重启或拓扑变化后台账合格局自动清零 全量观测自动恢复 不用任何显式复位
    internal static class IrqObservationBudget
    {
        internal const int ObserveEveryN = 4;
        internal const string SkipCountKey = "IrqObserveSkipsV1";

        // 纯判定 隔离测试直接喂参数
        internal static bool ShouldObserve(int bootUsableSessions, bool verificationPending, int skips)
        {
            if (verificationPending) return true;
            if (bootUsableSessions < IrqSessionLedger.VerdictWindow) return true;
            return skips >= ObserveEveryN - 1;
        }

        // 开局只读判定 不动计数 记账推迟到局末
        //   开局就记账的话 闪退和秒退这类不合格局会烧掉观测名额
        //   最坏情况短局与真对局交替相位锁死 观测的全是废局 真对局一直被跳
        public static bool Peek()
        {
            try
            {
                bool pending = false; // 自动编排已下架 不再有待验收钉核拉满观测
                int usable = 0;
                foreach (IrqSessionRecord rec in IrqSessionLedger.Load())
                    if (rec != null && rec.UsableForVerdict) usable++;
                bool observe = ShouldObserve(usable, pending, LoadSkips());
                if (!observe)
                    Logger.Log(Lang.F("log.irqbudget.1",
                        usable.ToString(CultureInfo.InvariantCulture),
                        ObserveEveryN.ToString(CultureInfo.InvariantCulture)));
                return observe;
            }
            catch { return true; }
        }

        // 局末记账 短于合格门槛的局不动计数 无论它观测没观测
        //   跳过的真对局记一笔 观测过的真对局清零
        public static void CommitSession(bool observed, int durationSeconds)
        {
            try
            {
                if (durationSeconds < IrqSessionRecord.MinUsableSeconds) return;
                Settings.SaveStr(SkipCountKey, observed ? "0"
                    : (LoadSkips() + 1).ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static int LoadSkips()
        {
            int skips;
            int.TryParse(Settings.LoadStr(SkipCountKey, "0"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out skips);
            return skips;
        }
    }
}
