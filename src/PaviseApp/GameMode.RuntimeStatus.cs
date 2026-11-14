using System;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {

        internal PolicySnapshot AutomationPolicySnapshot
        {
            get
            {
                return sessionPolicy ?? PolicyResolver.Global();
            }
        }

        public string AutomationStatus
        {
            get
            {
                lock (sync)
                {
                    string textResult = ((!enabled) ? PerformancePolicy.Text("守护已暂停", "Guard paused") : (active ? (PerformancePolicy.Text("正在守护 · ", "Guarding · ") + activeGame) : PerformancePolicy.Text("自动策略就绪 · 等待游戏", "Automatic policy ready · no active game")));
                    return textResult + ((envFused.Count == 0) ? "" : PerformancePolicy.Text(" · 有受限动作，查看记录", " · actions blocked; see records"));
                }
            }
        }

        internal string AutomationSession
        {
            get
            {
                lock (sync)
                {
                    return automationSession;
                }
            }
        }

        internal long AutomaticSessionSerial { get { return Interlocked.Read(ref gameSessionStarts); } }

        internal string GlobalTimerResolutionDetail
        {
            get
            {
                bool verified;
                uint actual;
                uint finest;
                if (!timerLease.Readback(out verified, out actual, out finest))
                {
                    return "";
                }
                if (!verified)
                {
                    return Lang.T("env.gtimer.query-unverified");
                }
                return Lang.F("env.gtimer.readback", 0.5.ToString("F4", CultureInfo.InvariantCulture), ((double)actual / 10000.0).ToString("F4", CultureInfo.InvariantCulture), ((double)finest / 10000.0).ToString("F4", CultureInfo.InvariantCulture));
            }
        }

        public string BackgroundGovernanceStatus
        {
            get
            {
                if (!SuppressBackground) return Lang.T("background.governance.status.off");
                return Lang.T(BackgroundGovernancePolicy.Load() == BackgroundGovernancePolicy.Aggressive
                    ? "background.governance.status.aggressive" : "background.governance.status.normal");
            }
        }

        private bool NvGameResidueNeedsRestore
        {
            get
            {
                return NvGameResidueGate(NvDrsTweaks.HasGameResidue);
            }
        }

        private GameFamilyHistory FamilyHistory
        {
            get
            {
                if (gameFamilyHistory == null)
                {
                    Interlocked.CompareExchange(ref gameFamilyHistory, new GameFamilyHistory(), null);
                }
                return gameFamilyHistory;
            }
        }

        private GameFamilyEvidence FamilyEvidence
        {
            get
            {
                return Volatile.Read(ref gameFamilyEvidence) ?? GameFamilyEvidence.Empty;
            }
        }

        internal int FamilyPolicyEpoch
        {
            get
            {
                return Volatile.Read(ref familyPolicyEpoch);
            }
        }

        internal bool SmartSceneSwitchedOut
        {
            get
            {
                return smartSwitchedOut;
            }
        }

        internal bool SessionSwitchedOut
        {
            get
            {
                return sessionSwitchedOut;
            }
        }

        internal SessionForegroundState SessionForeground
        {
            get
            {
                return sessionForeground.State;
            }
        }

        private long SwitchOutDebounceTicks
        {
            get
            {
                int scanInterval = ProcessScanIntervalMs(ProcessEventsAvailable, true);
                return (long)Math.Max(5000, scanInterval) * 10000L;
            }
        }

        internal bool SessionServicesSuspended
        {
            get
            {
                return SessionServiceSuspended("services", DateTime.UtcNow.Ticks);
            }
        }

        internal string SwitchOutReleaseText
        {
            get
            {
                return PerformancePolicy.Text("智能切出，释放后台压制", "Smart switch-out: releasing background suppression");
            }
        }

        internal bool GameNvDeliveryPending
        {
            get
            {
                return Interlocked.CompareExchange(ref gameNvPending, 0, 0) != 0;
            }
        }

        public string WhitelistLastError
        {
            get
            {
                lock (sync)
                {
                    return whitelistLastError;
                }
            }
        }

        internal bool ShutdownWritersStopped
        {
            get
            {
                return shutdownWritersStopped;
            }
        }

        public bool ProcessEventsAvailable
        {
            get
            {
                return Interlocked.CompareExchange(ref processEventsAvailable, 0, 0) != 0;
            }
            set
            {
                Interlocked.Exchange(ref processEventsAvailable, value ? 1 : 0);
                RequestFullGameDetection();
                try
                {
                    kick.Set();
                }
                catch
                {
                }
            }
        }

        internal long ProcessScanCount
        {
            get
            {
                return Interlocked.Read(ref processScanCount);
            }
        }

        public bool ProfileStoreSaveFailed
        {
            get
            {
                if (!profileStore.SaveFailed)
                {
                    return Interlocked.CompareExchange(ref profileSaveFailureSignaled, 0, 0) != 0;
                }
                return true;
            }
        }

        public string StatusText
        {
            get
            {
                if (!enabled)
                {
                    return Lang.T("st.off");
                }
                lock (sync)
                {
                    if (!active)
                    {
                        string armedGameNameSnapshot = armedGameName;
                        return (armedGameNameSnapshot != null) ? Lang.F("st.armed", armedGameNameSnapshot) : Lang.T("st.mon");
                    }
                    long gameGoneSinceTicksSnapshot = gameGoneSinceTicks;
                    if (gameGoneSinceTicksSnapshot != 0)
                    {
                        int remainingExitGraceSeconds = ExitGraceSeconds - (int)((DateTime.UtcNow.Ticks - gameGoneSinceTicksSnapshot) / 10000000);
                        if (remainingExitGraceSeconds < 0)
                        {
                            remainingExitGraceSeconds = 0;
                        }
                        return Lang.F("st.grace", activeGame, remainingExitGraceSeconds);
                    }
                    int throttledCountCachedResult = core.ThrottledCountCached() + windowsBackground.ActiveCount;
                    int count = boostStateVerified.Count;
                    string stActiveText = Lang.F("st.active", activeGame, throttledCountCachedResult);
                    return stActiveText + Lang.T("st.sep") + Lang.F("st.boost", count, PowerPlanStatusText);
                }
            }
        }

        public bool BoostStateVerified
        {
            get
            {
                lock (sync)
                {
                    return active && activeDetection != null && boostStateVerified.Contains(activeDetection.RendererPid);
                }
            }
        }

        public bool BoostHandleProtected
        {
            get
            {
                lock (sync)
                {
                    return active && activeDetection != null && (boostHandleStripped.Contains(activeDetection.RendererPid) || boostStateWarned.Contains(activeDetection.RendererPid));
                }
            }
        }

        public string BoostStatusText
        {
            get
            {
                if (!enabled || !EffBoost)
                {
                    return Lang.T("v14.boost.disabled");
                }
                lock (sync)
                {
                    if (!active)
                    {
                        return Lang.T("v14.boost.wait");
                    }
                    if (activeDetection == null || activeDetection.RendererPid <= 0)
                    {
                        return Lang.T("v14.boost.no.renderer");
                    }
                    string rendererNameOffset = activeDetection.RendererName ?? activeGame ?? "Game";
                    if (boostStateVerified.Contains(activeDetection.RendererPid))
                    {
                        return Lang.F("v14.boost.verified", rendererNameOffset);
                    }
                    if (boostHandleStripped.Contains(activeDetection.RendererPid) || boostStateWarned.Contains(activeDetection.RendererPid))
                    {
                        return Lang.F("v14.boost.protected", rendererNameOffset);
                    }
                    return Lang.F("v14.boost.applying", rendererNameOffset);
                }
            }
        }

        private RendererHandoffTracker Handoff
        {
            get
            {
                if (rendererHandoff == null)
                {
                    Interlocked.CompareExchange(ref rendererHandoff, new RendererHandoffTracker(), null);
                }
                return rendererHandoff;
            }
        }

        internal long GameSessionToken
        {
            get
            {
                lock (sync)
                {
                    return (active || activeDetection != null || gameGoneSinceTicks != 0) ? (-1) : gameSessionStarts;
                }
            }
        }

        public string ArmedGame
        {
            get
            {
                lock (sync)
                {
                    return active ? null : armedGameName;
                }
            }
        }

        public static string LastSessionBrief
        {
            get
            {
                return Settings.LoadStr("LastSessionBrief", "");
            }
        }

        public int ExitGraceSeconds
        {
            get
            {
                return exitGraceSeconds;
            }
        }

        private sealed class CriticalAutoTopology
        {
            internal ulong[] Physical;
            internal ulong[] Domains;
            internal ulong Allowed, Reserved, Performance, PreferredDomain;
        }

        private bool CriticalThreadHistoricalRecoveryDebt(bool force)
        {
            long nowTicks = AutoCores.NowTicks;
            if (force || nowTicks >= Interlocked.Read(ref criticalDebtNextRead))
            {
                criticalHistoricalDebt = HardThreadFileStore.CanonicalRootHasRecoveryDebt(CriticalThreadStoreRoot);
                Interlocked.Exchange(ref criticalDebtNextRead, nowTicks + (long)(criticalHistoricalDebt ? 1 : 15) * 10000000L);
            }
            return criticalHistoricalDebt;
        }

    }
}
