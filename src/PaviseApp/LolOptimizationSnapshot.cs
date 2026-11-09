using System;

namespace PaviseApp
{
    internal sealed class LolOptimizationSnapshot
    {
        public bool Running { get; private set; }

        public bool Enabled { get; private set; }

        public bool CleanupEnabled { get; private set; }

        public bool HeadlessEnabled { get; private set; }

        public string LolRoot { get; private set; }

        public string WeGameRoot { get; private set; }

        public bool InstallationFound { get; private set; }

        public bool WeGameFound { get; private set; }

        public bool ClientRunning { get; private set; }

        public bool GameRunning { get; private set; }

        public bool LcuReady { get; private set; }

        public string Phase { get; private set; }

        public bool HeadlessActive { get; private set; }

        public int WeGameProcessCount { get; private set; }

        public bool WeGameMainRunning { get; internal set; }

        public int CrossProcessCount { get; private set; }

        public int UxProcessCount { get; private set; }

        public int CleanedProcessCount { get; private set; }

        public long ReleasedWorkingSetBytes { get; private set; }

        public string LastAction { get; private set; }

        public string LastError { get; private set; }

        public DateTime UpdatedUtc { get; private set; }

        public DateTime LastActionUtc { get; private set; }

        public bool Discovering { get; private set; }

        internal LolOptimizationSnapshot(bool running, bool enabled, bool cleanupEnabled, bool headlessEnabled, string lolRoot, string weGameRoot, bool installationFound, bool weGameFound, bool clientRunning, bool gameRunning, bool lcuReady, string phase, bool headlessActive, int weGameProcessCount, int crossProcessCount, int uxProcessCount, int cleanedProcessCount, long releasedWorkingSetBytes, string lastAction, string lastError, DateTime updatedUtc, DateTime lastActionUtc, bool discovering)
        {
            Discovering = discovering;
            Running = running;
            Enabled = enabled;
            CleanupEnabled = cleanupEnabled;
            HeadlessEnabled = headlessEnabled;
            LolRoot = lolRoot;
            WeGameRoot = weGameRoot;
            InstallationFound = installationFound;
            WeGameFound = weGameFound;
            ClientRunning = clientRunning;
            GameRunning = gameRunning;
            LcuReady = lcuReady;
            Phase = phase;
            HeadlessActive = headlessActive;
            WeGameProcessCount = weGameProcessCount;
            CrossProcessCount = crossProcessCount;
            UxProcessCount = uxProcessCount;
            CleanedProcessCount = cleanedProcessCount;
            ReleasedWorkingSetBytes = releasedWorkingSetBytes;
            LastAction = lastAction;
            LastError = lastError;
            UpdatedUtc = updatedUtc;
            LastActionUtc = lastActionUtc;
        }
    }
}
