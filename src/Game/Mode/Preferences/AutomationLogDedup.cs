namespace PaviseApp
{
    // 自动化日志里只决定写不写 以及会话日志显示什么名字 不参与执行状态判断
    // 没有自己的锁 读写都在游戏模式的 sync 里 实例归 GameMode 所有
    internal sealed class AutomationLogDedup
    {
        private string sessionName = "";
        private string powerFailureSignature;
        private string timerFailureSignature;

        // 会话开始时记下 结束那条日志沿用同一个名字
        internal string SessionName
        {
            get { return sessionName; }
            set { sessionName = value; }
        }

        // 状态没变但失败详情变了也要再写一条 签名为空表示这次没有详情
        internal bool PowerFailureChanged(string signature)
        {
            return signature.Length > 0 && powerFailureSignature != signature;
        }

        internal bool TimerFailureChanged(string signature)
        {
            return signature.Length > 0 && timerFailureSignature != signature;
        }

        internal void RememberPowerFailure(string signature)
        {
            powerFailureSignature = signature;
        }

        internal void RememberTimerFailure(string signature)
        {
            timerFailureSignature = signature;
        }
    }
}
