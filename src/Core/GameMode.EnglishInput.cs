// File purpose Input language consent is captured only at entry, a mid-match change cancels pending work
// but must not arm a second request within the same game run
using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private volatile bool englishInputOn;
        private int englishInputGeneration;
        private long englishInputCreationFloor;
        private EnglishInputOnce englishInputOnce;

        private void InitializeEnglishInput()
        {
            // Restarting Pavise must not treat a game already running with Chinese input
            // as a fresh entry, this is the OS FILETIME, same as RendererCreation
            Interlocked.Exchange(ref englishInputCreationFloor, DateTime.UtcNow.ToFileTimeUtc());
            englishInputOn = Settings.Load(PolicyCatalog.KeyEnglishInput, false);
            englishInputOnce = new EnglishInputOnce(new GameInputLanguage());
        }

        public bool EnglishInputEnabled
        {
            get { return englishInputOn; }
            set
            {
                lock (sync)
                {
                    if (stopping) return;
                    InvalidateEnglishInputWork();
                    // A failed save never grants new enable eligibility, when turning off
                    // pending work must stop even if the save fails
                    bool saved = Settings.Save(PolicyCatalog.KeyEnglishInput, value);
                    englishInputOn = value && saved;
                }
                RequestPolicyApply();
            }
        }

        private bool LiveEnglishInputPreference()
        {
            // Caller holds sync, IsGlobal also covers profiles without any override
            return LiveBoolPreferenceLocked(PolicyCatalog.KeyEnglishInput, englishInputOn);
        }

        private Func<bool> CaptureEnglishInputAdmission(bool entry, out string profileId,
            out string profileName, out GameInputProcess process, out bool wanted)
        {
            int generation;
            long creationFloor;
            string id;
            bool preference;
            GameInputProcess renderer;
            lock (sync)
            {
                generation = Volatile.Read(ref englishInputGeneration);
                creationFloor = Volatile.Read(ref englishInputCreationFloor);
                GameDetection detection = activeDetection;
                profileId = id = detection != null && detection.Profile != null ? detection.Profile.Id : null;
                profileName = detection != null && detection.Profile != null ? detection.Profile.Name : "";
                bool selected = detection != null && detection.RendererCandidateSelected
                    && !detection.RendererSafetyOnly && !detection.RequiresGpuConfirm;
                process = renderer = selected ? new GameInputProcess(detection.RendererPid, detection.RendererCreation)
                    : new GameInputProcess();
                PolicySnapshot snapshot = sessionPolicy;
                wanted = preference = LiveEnglishInputPreference() && !string.IsNullOrEmpty(id)
                    && creationFloor > 0 && (!renderer.IsValid || renderer.Creation >= creationFloor)
                    && (snapshot == null || string.IsNullOrEmpty(snapshot.ProfileId)
                        || string.Equals(snapshot.ProfileId, id, StringComparison.OrdinalIgnoreCase));
            }
            return delegate
            {
                if (!preference || creationFloor != Volatile.Read(ref englishInputCreationFloor)
                    || generation != Volatile.Read(ref englishInputGeneration) || !Volatile.Read(ref active)
                    || !enabled || stopping || panicReq || Volatile.Read(ref standbyCleanerRestorePending) != 0
                    || ProfileStoreSaveFailed || Volatile.Read(ref stickyGraceOnly)
                    || Volatile.Read(ref gameGoneSinceTicks) != 0) return false;
                GameDetection detection = Volatile.Read(ref activeDetection);
                if (detection == null || detection.Profile == null
                    || !string.Equals(detection.Profile.Id, id, StringComparison.OrdinalIgnoreCase)) return false;
                bool selected = detection.RendererCandidateSelected && !detection.RendererSafetyOnly
                    && !detection.RequiresGpuConfirm;
                // The renderer can only be selected after Begin, apply the creation floor again
                // at request admission so a late discovery can't bypass it
                if (selected && detection.RendererCreation > 0 && detection.RendererCreation < creationFloor)
                    return false;
                // The entry token follows the handover of this match's verified renderer
                // a single request additionally pins its own renderer process
                return entry || (selected && renderer.IsValid && renderer.Creation >= creationFloor
                    && detection.RendererPid == renderer.Pid
                    && detection.RendererCreation == renderer.Creation);
            };
        }

        private void BeginEnglishInputSession()
        {
            EnglishInputOnce runner = englishInputOnce;
            if (runner == null) return;
            string profileId, profileName;
            GameInputProcess process;
            bool wanted;
            Func<bool> admission = CaptureEnglishInputAdmission(true, out profileId, out profileName, out process, out wanted);
            runner.Begin(profileId, process, wanted, admission);
        }

        private void StepEnglishInputSession()
        {
            EnglishInputOnce runner = englishInputOnce;
            if (runner == null) return;
            string profileId, profileName;
            GameInputProcess process;
            bool wanted;
            Func<bool> admission = CaptureEnglishInputAdmission(false, out profileId, out profileName, out process, out wanted);
            // Never Begin here, enabling mid-match expresses intent for the next entry
            EnglishInputOutcome result = runner.Step(profileId, process, admission);
            if (result == null) return;
            string key;
            switch (result.Result)
            {
                case EnglishInputResult.Changed: key = "englishinput.changed"; break;
                case EnglishInputResult.AlreadyEnglish: key = "englishinput.already"; break;
                case EnglishInputResult.Unconfirmed: key = "englishinput.unconfirmed"; break;
                case EnglishInputResult.Denied: key = "englishinput.denied"; break;
                case EnglishInputResult.NoEnglishLayout: key = "englishinput.nolayout"; break;
                case EnglishInputResult.TargetChanged: key = "englishinput.targetchanged"; break;
                case EnglishInputResult.Expired: key = "englishinput.expired"; break;
                case EnglishInputResult.Canceled: key = "englishinput.canceled"; break;
                case EnglishInputResult.HistoryFull: key = "englishinput.historyfull"; break;
                default: key = "englishinput.unavailable"; break;
            }
            Logger.Log(Lang.F("log.englishinput.result", profileName, Lang.T(key), result.Error));
        }

        private void InvalidateEnglishInputWork()
        {
            Interlocked.Increment(ref englishInputGeneration);
            EnglishInputOnce runner = englishInputOnce;
            if (runner != null) runner.Cancel();
        }

        private void EndEnglishInputSession()
        {
            InvalidateEnglishInputWork();
            EnglishInputOnce runner = englishInputOnce;
            if (runner != null) runner.End();
        }

        private bool DrainEnglishInput(int timeoutMs)
        {
            EnglishInputOnce runner = englishInputOnce;
            return runner == null || runner.Drain(timeoutMs);
        }
    }
}
