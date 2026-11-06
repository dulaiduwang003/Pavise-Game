namespace PaviseApp
{
    internal partial class GameMode
    {
        private LaboratorySession laboratory;
        private bool laboratoryClosed;
        private int laboratoryDebtGeneration = -1;
        private bool laboratoryDebt;
        private void SyncLaboratory()
        {
            bool match; int pid; long creation;
            lock (sync)
            {
                if (laboratoryClosed) return;
                match = enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed && !autoIgnoreLoadFailed
                    && (libraryIgnoreTransaction == null || !libraryIgnoreTransaction.RecoveryPending);
                pid = match && activeDetection != null ? activeDetection.RendererPid : 0;
                creation = match && activeDetection != null ? activeDetection.RendererCreation : 0;
                if (laboratory == null)
                {
                    if (!match)
                    {
                        int generation = Settings.MutationGeneration;
                        if (generation != laboratoryDebtGeneration)
                        {
                            laboratoryDebt = LaboratoryPower.HasResidue || LaboratoryGpu.HasResidue;
                            laboratoryDebtGeneration = generation;
                        }
                        if (!laboratoryDebt) return;
                    }
                    laboratory = new LaboratorySession(powerApplyGate);
                }
            }
            laboratory.BeginAutomaticCycle(System.Threading.Interlocked.Read(ref gameSessionStarts));
            LaboratorySelection selection = LaboratorySelection.Read(match, pid, creation);
            lock (sync)
            {
                if (laboratoryClosed) return;
                // 守护关闭或对局结束后，过期快照不得重新启用实验室动作。
                if (!enabled || !active || stopping || panicReq || ProfileStoreSaveFailed || autoIgnoreLoadFailed
                    || (libraryIgnoreTransaction != null && libraryIgnoreTransaction.RecoveryPending))
                    selection = new LaboratorySelection();
                laboratory.Queue(selection);
            }
        }
        private bool RestoreLaboratory()
        {
            if (laboratory != null) return laboratory.WaitForRestore(8000);
#if PAVISE_SELFTEST
            return true;
#else
            lock (powerApplyGate) return LaboratorySession.Recover();
#endif
        }
        internal bool CloseLaboratory(int timeoutMs)
        {
            LaboratorySession session;
            lock (sync)
            {
                laboratoryClosed = true;
                session = laboratory;
            }
            bool manual = LaboratoryManualActions.CloseAndRestoreRuntime(timeoutMs);
            bool ordinary = session != null ? session.Close(timeoutMs)
                : !LaboratoryPower.HasResidue && !LaboratoryGpu.HasResidue;
            return manual && ordinary;
        }
        internal string LaboratoryState(string key)
        {
            string fault = laboratory == null ? "" : laboratory.Failure;
            if (!string.IsNullOrEmpty(fault)) return fault;
            if (laboratory == null || laboratory.IsRestored)
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
            if (laboratory == null || laboratory.IsRestored)
                return PerformancePolicy.Text(selected ? "已选择 · 等待对局按条件执行" : "已关闭", selected ? "Selected · waits for an eligible match" : "Off");
            return laboratory.State(key);
        }
    }
}
