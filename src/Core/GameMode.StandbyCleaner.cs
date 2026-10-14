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
                standbyCleanerOn = false;
                Logger.Log(Lang.T("standbycleaner.config.invalid"));
            }
            standbyCleaner = new StandbyCleanerRunner(
                new StandbyCleanerEngine(new StandbyMemoryControl()),
                OnStandbyCleanerFault);
        }

        public bool StandbyCleanerEnabled
        {
            get { return standbyCleanerOn; }
            set { TrySetStandbyCleanerEnabled(value); }
        }

        internal bool TrySetStandbyCleanerEnabled(bool value)
        {
            bool saved;
            lock (sync)
            {
                if (stopping) return false;
                InvalidateCacheWarm();
                InvalidateStandbyCleanerWork();
                if (value && !standbyCleanerOptionsValid) return false;
                saved = Settings.Save(PolicyCatalog.KeyStandbyCleaner, value);
                standbyCleanerOn = value && saved;
                if (standbyCleanerOn) ClearEnvFuse("standby");
            }
            RequestPolicyApply();
            return saved;
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
                    standbyCleanerOn = false;
                }
                // 三个清理参数共用一个注册表值，取消或失败不能部分发布。
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
            if (AutomaticPolicy.Enabled) return standbyCleanerOn && AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyStandbyCleaner);
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
                    && !ActionFailureStore.IsBlocked(PolicyCatalog.KeyStandbyCleaner, "system") && !string.IsNullOrEmpty(profileId)
                    && (snapshot == null || string.IsNullOrEmpty(snapshot.ProfileId)
                        || string.Equals(snapshot.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
            }
            // 原生准入只读原子状态，持有原生锁时不能获取 sync。
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
                ActionFailureStore.Trip(PolicyCatalog.KeyStandbyCleaner, "system",
                    result + " | 0x" + nativeStatus.ToString("X8", CultureInfo.InvariantCulture));
            }
            Logger.Warn(Lang.T("log.gamemodeenv.9") + EnvLabel("standby")
                + Lang.T("log.gamemodeenv.10")
                + "1" + Lang.T("log.gamemodeenv.11")
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
