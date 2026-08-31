// 文件用途 双阈值待机内存清理 需用户开启 只在已核实的当前对局内生效
using System;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal const string StandbyCleanerOptionsKey = "GmStandbyCleanerOptionsV1";
        private volatile bool standbyCleanerOn;
        private volatile bool standbyCleanerOptionsValid;
        private volatile StandbyCleanerOptions standbyCleanerOptions = StandbyCleanerOptions.Default;
        private int standbyCleanerGeneration;
        private int standbyCleanerRestorePending;
        private StandbyCleanerRunner standbyCleaner;

        private void InitializeStandbyCleaner()
        {
            // 已退役的 GmStandbyGuard 和 GmStandbySweep 取值 一律不视为同意
            standbyCleanerOn = Settings.Load(PolicyCatalog.KeyStandbyCleaner, false);
            string raw;
            StandbyCleanerOptions parsed;
            bool readable = Settings.TryLoadStr(StandbyCleanerOptionsKey, out raw);
            standbyCleanerOptionsValid = readable && (raw.Length == 0
                || StandbyCleanerOptions.TryParse(raw, out parsed));
            if (readable && raw.Length != 0 && StandbyCleanerOptions.TryParse(raw, out parsed))
                standbyCleanerOptions = parsed;
            if (!standbyCleanerOptionsValid)
            {
                // 参数损坏时原样留着供排查 但绝不能在保存损坏之后
                // 拿悄悄替换过的阈值继续跑
                standbyCleanerOn = false;
                envFused.Add("standby");
                Settings.Save("EnvFuse_standby", true);
                Settings.Save(PolicyCatalog.KeyStandbyCleaner, false);
                Logger.Log(Lang.T("standbycleaner.config.invalid"));
            }
            standbyCleaner = new StandbyCleanerRunner(
                new StandbyCleanerEngine(new StandbyMemoryControl(), RunIrqIsolatedMutation),
                OnStandbyCleanerFault);
        }

        public bool StandbyCleanerEnabled
        {
            get { return standbyCleanerOn; }
            set
            {
                lock (sync)
                {
                    if (stopping) return;
                    InvalidateStandbyCleanerWork();
                    if (value && !standbyCleanerOptionsValid) return;
                    bool saved = Settings.Save(PolicyCatalog.KeyStandbyCleaner, value);
                    // 开启失败不算同意 关闭失败也必须立刻
                    // 把正在跑的工作线程停掉
                    standbyCleanerOn = value && saved;
                    if (standbyCleanerOn) ClearEnvFuse("standby");
                }
                RequestPolicyApply();
            }
        }

        public StandbyCleanerOptions StandbyCleaningOptions { get { return standbyCleanerOptions; } }
        public bool StandbyCleaningOptionsValid { get { return standbyCleanerOptionsValid; } }

        public bool TrySetStandbyCleanerOptions(StandbyCleanerOptions value)
        {
            if (value == null || !value.IsValid) return false;
            bool saved;
            lock (sync)
            {
                if (stopping) return false;
                InvalidateStandbyCleanerWork();
                if (!standbyCleanerOptionsValid)
                {
                    // 最初那次把配置损坏锁定落盘的尝试可能已经失败
                    // 所以要在写有效参数之前先提交这个锁定
                    // 否则重启后可能悄悄复活一条旧的逐游戏开关
                    envFused.Add("standby");
                    standbyCleanerOn = false;
                    if (!Settings.Save("EnvFuse_standby", true)
                        || !Settings.Save(PolicyCatalog.KeyStandbyCleaner, false)) return false;
                }
                // 三个参数共用一个注册表值作为提交边界
                // 取消或者写失败 都不会让它们部分发布出去
                saved = Settings.SaveStr(StandbyCleanerOptionsKey, value.Serialize());
                if (saved)
                {
                    standbyCleanerOptions = value;
                    standbyCleanerOptionsValid = true;
                }
            }
            RequestPolicyApply();
            return saved;
        }

        private void InvalidateStandbyCleanerWork()
        {
            Interlocked.Increment(ref standbyCleanerGeneration);
            StandbyCleanerRunner runner = standbyCleaner;
            if (runner != null) runner.Pause();
        }

        private void StandbyCleanerPolicyChanged(bool enabledValue)
        {
            if (enabledValue) ClearEnvFuse("standby");
            RequestPolicyApply();
        }

        private bool LiveStandbyCleanerPreference()
        {
            // 持有 sync 时调用 且在进入任何原生或 runner 闸门之前
            return LiveBoolPreferenceLocked(PolicyCatalog.KeyStandbyCleaner, standbyCleanerOn);
        }

        private Func<bool> CaptureStandbyCleanerAdmission(
            out StandbyCleanerOptions options, out int generation)
        {
            bool wanted;
            string profileId;
            int capturedGeneration;
            lock (sync)
            {
                generation = capturedGeneration = Volatile.Read(ref standbyCleanerGeneration);
                options = standbyCleanerOptions;
                GameDetection detection = activeDetection;
                profileId = detection != null && detection.Profile != null ? detection.Profile.Id : null;
                PolicySnapshot snapshot = sessionPolicy;
                wanted = standbyCleanerOptionsValid && LiveStandbyCleanerPreference()
                    && !envFused.Contains("standby") && !string.IsNullOrEmpty(profileId)
                    && (snapshot == null || string.IsNullOrEmpty(snapshot.ProfileId)
                        || string.Equals(snapshot.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
            }
            // 原生准入只读原子量和不可变身份 特别注意
            // 它绝不能在持有原生闸或中断改动闸的时候再去拿 sync
            return delegate
            {
                if (!wanted || capturedGeneration != Volatile.Read(ref standbyCleanerGeneration)
                    || !Volatile.Read(ref active) || !enabled || stopping || panicReq
                    || Volatile.Read(ref standbyCleanerRestorePending) != 0
                    || ProfileStoreSaveFailed || Volatile.Read(ref stickyGraceOnly)
                    || Volatile.Read(ref gameGoneSinceTicks) != 0) return false;
                GameDetection detection = Volatile.Read(ref activeDetection);
                return detection != null && detection.Profile != null
                    && string.Equals(detection.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase);
            };
        }

        private void ApplyStandbyCleanerPolicy()
        {
            StandbyCleanerRunner runner = standbyCleaner;
            if (runner == null) return;
            StandbyCleanerOptions options;
            int generation;
            Func<bool> mayContinue = CaptureStandbyCleanerAdmission(out options, out generation);
            if (mayContinue()) runner.Update(generation, options, mayContinue);
            else runner.Pause();
        }

        private void OnStandbyCleanerFault(int generation, StandbyCleanerResult result, int nativeStatus)
        {
            lock (sync)
            {
                if (generation != Volatile.Read(ref standbyCleanerGeneration) || stopping
                    || !active || !enabled || panicReq || ProfileStoreSaveFailed) return;
                if (!envFused.Add("standby")) return;
                Settings.Save("EnvFuse_standby", true);
                DisableEnvSwitch("standby");
            }
            Logger.Log(Lang.T("log.gamemodeenv.9") + EnvLabel("standby")
                + Lang.T("log.gamemodeenv.10")
                + (result == StandbyCleanerResult.Purged ? "1" : "2") + Lang.T("log.gamemodeenv.11")
                + " [" + result + ", 0x" + nativeStatus.ToString("X8", CultureInfo.InvariantCulture) + "]");
            RequestPolicyApply();
        }

#if PAVISE_SELFTEST
        internal Func<bool> CaptureStandbyCleanerAdmissionForTest(
            out StandbyCleanerOptions options, out int generation)
        {
            return CaptureStandbyCleanerAdmission(out options, out generation);
        }

        internal void StepStandbyCleanerForTest() { ApplyStandbyCleanerPolicy(); }
#endif
    }
}
