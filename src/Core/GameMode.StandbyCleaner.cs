// Opt-in ISLC memory rules, confined to the current verified game session.
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
            // Retired GmStandbyGuard/GmStandbySweep values never grant consent.
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
                // Preserve malformed parameters for inspection, but never run
                // with silently substituted thresholds after a corrupt save.
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
                    // An unsuccessful enable is not consent; an unsuccessful
                    // disable must still stop the live worker immediately.
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
                    // The original attempt to persist a corrupt-config lockout
                    // may have failed. Commit that lockout BEFORE valid options,
                    // or a restart could revive an old per-game opt-in silently.
                    envFused.Add("standby");
                    standbyCleanerOn = false;
                    if (!Settings.Save("EnvFuse_standby", true)
                        || !Settings.Save(PolicyCatalog.KeyStandbyCleaner, false)) return false;
                }
                // One registry value is the commit boundary for all three
                // parameters. Cancel/failed writes never partially publish them.
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
            // Called while holding sync, before entering any native/runner gate.
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
            // Native admission only reads atomics/immutable identity. In particular
            // it must never take sync while holding the native or IRQ mutation gate.
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
