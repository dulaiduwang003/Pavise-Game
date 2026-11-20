using System;

namespace PaviseApp
{
    // 实验室只通过这个接口读游戏模式的状态和锁 不直接碰 GameMode 的字段
    internal interface ILaboratoryHost
    {
        // 就是游戏模式自己的 sync 锁 会话创建和关闭标记都在这把锁里
        object SyncGate { get; }
        // 就是游戏模式的电源应用锁 实验室改电源设置时和电源计划互斥
        object PowerApplyGate { get; }
        // 守护开着 对局进行中 没有停止或紧急恢复 档案和忽略名单都可用 只在 SyncGate 里读
        bool AutomaticAdmittedLocked { get; }
        GameDetection ActiveDetection { get; }
        long GameSessionStarts { get; }
    }

    // 对局里的实验室自动会话和对局外的手动写入 实例归 GameMode 所有
    internal sealed class LaboratoryCoordinator
    {
        private readonly ILaboratoryHost host;
        private LaboratorySession session;
        private bool closed;
        private int debtGeneration = -1;
        private bool debt;

        internal LaboratoryCoordinator(ILaboratoryHost host)
        {
            if (host == null) throw new ArgumentNullException("host");
            this.host = host;
        }

        // 自动会话还没把改动恢复干净 环境恢复和退局核验都要等它
        internal bool SessionUnrestored { get { return session != null && !session.IsRestored; } }

        internal void Sync()
        {
            bool match; int pid; long creation;
            lock (host.SyncGate)
            {
                if (closed) return;
                match = host.AutomaticAdmittedLocked;
                pid = match && host.ActiveDetection != null ? host.ActiveDetection.RendererPid : 0;
                creation = match && host.ActiveDetection != null ? host.ActiveDetection.RendererCreation : 0;
                if (session == null)
                {
                    if (!match)
                    {
                        // 没有对局时只为上次留下的恢复记录建会话 设置没变就不重读记录
                        int generation = Settings.MutationGeneration;
                        if (generation != debtGeneration)
                        {
                            debt = LaboratoryPower.HasResidue || LaboratoryGpu.HasResidue;
                            debtGeneration = generation;
                        }
                        if (!debt) return;
                    }
                    session = new LaboratorySession(host.PowerApplyGate);
                }
            }
            session.BeginAutomaticCycle(host.GameSessionStarts);
            LaboratorySelection selection = LaboratorySelection.Read(match, pid, creation);
            lock (host.SyncGate)
            {
                if (closed) return;
                // 守护关闭或对局结束后 过期快照不得重新启用实验室动作
                if (!host.AutomaticAdmittedLocked)
                    selection = new LaboratorySelection();
                session.Queue(selection);
            }
        }

        internal bool Restore()
        {
            if (session != null) return session.WaitForRestore(8000);
#if PAVISE_SELFTEST
            return true;
#else
            lock (host.PowerApplyGate) return LaboratorySession.Recover();
#endif
        }

        // 关闭以后自动会话和手动写入都不准入 已有改动按记录恢复
        internal bool Close(int timeoutMs)
        {
            LaboratorySession current;
            lock (host.SyncGate)
            {
                closed = true;
                current = session;
            }
            bool manual = LaboratoryManualActions.CloseAndRestoreRuntime(timeoutMs);
            bool ordinary = current != null ? current.Close(timeoutMs)
                : !LaboratoryPower.HasResidue && !LaboratoryGpu.HasResidue;
            return manual && ordinary;
        }

        // 程序停止时只等已有的自动会话收尾 手动写入另有关闭流程
        internal bool CloseSession(int timeoutMs)
        {
            return session == null || session.Close(timeoutMs);
        }

        internal string State(string key)
        {
            string fault = session == null ? "" : session.Failure;
            if (!string.IsNullOrEmpty(fault)) return fault;
            if (session == null || session.IsRestored)
            {
                string receiptKey = key == LaboratoryGpu.PowerKey ? LaboratoryGpu.PowerReceipt
                    : key == LaboratoryGpu.ClockKey ? LaboratoryGpu.ClockReceipt
                    : key == LaboratoryPower.EppKey || key == LaboratoryPower.IdleKey ? LaboratoryPower.ReceiptKey : null;
                string receipt;
                if (receiptKey != null && (!Settings.TryLoadStr(receiptKey, out receipt) || !string.IsNullOrEmpty(receipt)))
                    return PerformancePolicy.Text("恢复记录尚未结清", "Recovery record is still pending");
            }
            bool selected = key == LaboratoryPower.IdleKey
                ? LaboratorySelection.CanonicalIdle(Settings.LoadStr(key, "default")) != "default" : Settings.Load(key, false);
            if (session == null || session.IsRestored)
                return PerformancePolicy.Text(selected ? "已选择 · 等待对局按条件执行" : "已关闭", selected ? "Selected · waits for an eligible match" : "Off");
            return session.State(key);
        }
    }
}
