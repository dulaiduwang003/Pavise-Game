using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class AutoAddAttempt
        {
            internal int Pid;

            internal int Misses;

            internal int Confirmations;

            internal long Creation;

            internal long EvidenceAt;

            internal long LastSeen;

            internal long NoteAt;

            internal string Note;
        }

        private enum ExclusiveWriteBack
        {
            Written,
            Busy,
            Deferred,
            Failed
        }

        private struct AutomationLastInput
        {
            internal uint Size;

            internal uint Time;
        }

        private sealed class BoostPass
        {
            public bool CpuBoost = true;

            public bool IoBoost = true;

            public bool GpuBoost = true;

            public bool NvMaxPerf;

            public string NvLowLat;

            public bool NvSmooth;

            public bool NvShader;

            public bool NvRebar;

            public string NvDlss;

            public bool NvVrr;

            public bool Exclusive;

            public ulong DesiredMask;

            public int RendererPid;

            public long RendererCreation;

            public string RendererName;

            public string ActionTarget;

            public bool WriteDenied;

            public bool LaneAllowed;

            public bool LaneRestorationReady = true;

            public uint PriorityTarget = Native.HIGH_PRIORITY_CLASS;

            public long IdentityGeneration;

            public string PlacementStage;

            public int PlacementError;

            // 本次在登记恢复信息和写入前停止，无待结清改动。
            public bool PlacementUntouched;
        }

        private enum HardPinHandleState
        {
            Match,
            Gone,
            Mismatch,
            Unknown
        }

        private enum BoostTarget
        {
            Alive,
            Gone,
            Unopenable
        }

        private sealed class CriticalThreadMint
        {
            internal readonly int Pid;

            internal readonly long Birth;

            internal readonly long Epoch;

            internal readonly long Serial;

            internal readonly string Path;

            internal readonly string Rule;

            internal readonly ulong Allowed;

            internal CriticalThreadMint(int pid, long birth, string path, ulong allowed, long epoch, long serial, string rule)
            {
                Pid = pid;
                Birth = birth;
                Path = path;
                Allowed = allowed;
                Epoch = epoch;
                Serial = serial;
                Rule = rule;
            }
        }

        private sealed class CriticalProcessPin : IDisposable
        {
            private object held;

            private readonly int thread = Thread.CurrentThread.ManagedThreadId;

            internal CriticalProcessPin(object gate)
            {
                if (!Monitor.TryEnter(gate, 3000))
                {
                    throw new TimeoutException("Process/thread placement is still changing");
                }
                held = gate;
            }

            public void Dispose()
            {
                if (held != null)
                {
                    if (thread != Thread.CurrentThread.ManagedThreadId)
                    {
                        throw new InvalidOperationException("Placement pin must close on its original thread");
                    }
                    object heldSnapshot = held;
                    held = null;
                    Monitor.Exit(heldSnapshot);
                }
            }
        }

        internal sealed class CriticalThreadTargetLease
        {
            private readonly GameMode owner;

            private readonly long epoch;

            private readonly long gameSerial;

            private readonly string store;

            private readonly string rule;

            internal string OriginalRule { get { return rule; } }

            private readonly bool priorityOnly;

            private int revoked;

            internal readonly Guid SessionId;

            internal readonly int ProcessId;

            internal readonly long ProcessCreation;

            internal readonly string ExecutablePath;

            internal readonly ulong AllowedMask;

            internal readonly bool WorkerAvoidanceAllowed;

            internal string CanonicalStore
            {
                get
                {
                    return store;
                }
            }

            internal bool Revoked
            {
                get
                {
                    return Volatile.Read(ref revoked) != 0;
                }
            }

            private CriticalThreadTargetLease(GameMode originalOwner, int pid, long birth, string executable, ulong allowed, long originalEpoch, long originalGameSerial, string originalRule)
            {
                owner = originalOwner;
                ProcessId = pid;
                ProcessCreation = birth;
                ExecutablePath = Path.GetFullPath(executable);
                AllowedMask = allowed;
                epoch = originalEpoch;
                gameSerial = originalGameSerial;
                rule = originalRule;
                // 授权跟随原规则冻结；纯优先级不授予候选固定或其他线程避让权限。
                CriticalThreadRule originalStrategy = CriticalThreadRule.ParseForRuntime(originalRule);
                WorkerAvoidanceAllowed = originalStrategy.WritesOtherThreadRanges;
                priorityOnly = originalStrategy.IsDependencyBoost;
                store = owner.CriticalThreadStoreRoot;
                SessionId = Guid.NewGuid();
            }

            internal static CriticalThreadTargetLease Issue(GameMode originalOwner, object originalMint)
            {
                CriticalThreadMint criticalThreadMint = originalMint as CriticalThreadMint;
                if (originalOwner == null || criticalThreadMint == null || !Monitor.IsEntered(originalOwner.criticalProcessGate) || !object.ReferenceEquals(originalOwner.criticalMintInFlight, criticalThreadMint) || Interlocked.Read(ref originalOwner.criticalIntentEpoch) != criticalThreadMint.Epoch)
                {
                    throw new InvalidOperationException("Only the original GameMode renderer factory can issue a target lease");
                }
                originalOwner.criticalMintInFlight = null;
                return new CriticalThreadTargetLease(originalOwner, criticalThreadMint.Pid, criticalThreadMint.Birth, criticalThreadMint.Path, criticalThreadMint.Allowed, criticalThreadMint.Epoch, criticalThreadMint.Serial, criticalThreadMint.Rule);
            }

            internal IDisposable EnterUse()
            {
                return new CriticalProcessPin(owner.criticalProcessGate);
            }

            internal bool RegisterGuardianActor(HardThreadGuardianActor actor)
            {
                RequireActive();
                if (actor == null)
                {
                    return false;
                }
                owner.criticalGuardianActor = actor;
                return owner.ReleaseCriticalGuardianBackground();
            }

            internal bool UnregisterGuardianActor(HardThreadGuardianActor actor)
            {
                if (actor == null || !Monitor.TryEnter(owner.criticalProcessGate, 0))
                {
                    return false;
                }
                try
                {
                    RequireRecovery();
                    HardThreadGuardianActor criticalGuardianActor = owner.criticalGuardianActor;
                    if (criticalGuardianActor != null && (object.ReferenceEquals(criticalGuardianActor, actor) || criticalGuardianActor.Same(actor)))
                    {
                        owner.criticalGuardianActor = null;
                    }
                    return true;
                }
                finally
                {
                    Monitor.Exit(owner.criticalProcessGate);
                }
            }

            internal void RequireActive()
            {
                RequireRecovery();
                string message = ((Volatile.Read(ref revoked) != 0) ? "revoked" : ((Interlocked.Read(ref owner.criticalIntentEpoch) != epoch) ? "intent changed" : ((!object.ReferenceEquals(owner.criticalOriginalTarget, this)) ? "superseded" : ((!owner.enabled || owner.stopping || owner.panicReq) ? "game mode stopped" : ((!Volatile.Read(ref owner.active)) ? "session ended" : ((Interlocked.Read(ref owner.gameSessionStarts) != gameSerial) ? "another session" : (owner.ProfileStoreSaveFailed ? "profile store unavailable" : null)))))));
                if (message != null)
                {
                    throw new OperationCanceledException("The original game-thread intent is no longer active: " + message);
                }
            }

            internal void RequireRecovery()
            {
                if (!Monitor.IsEntered(owner.criticalProcessGate) || SessionId == Guid.Empty || ProcessId <= 0 || ProcessCreation <= 0
                    || string.IsNullOrEmpty(rule) || AllowedMask == 0 && !priorityOnly
                    || store != owner.CriticalThreadStoreRoot)
                {
                    throw new InvalidOperationException("No original game-thread restoration identity");
                }
            }

            internal void Revoke()
            {
                Interlocked.Exchange(ref revoked, 1);
            }
        }

        private sealed class CriticalRuntimeSource
        {
            internal readonly Guid Token = Guid.NewGuid();

            internal readonly string Profile;

            internal readonly string RuleText;

            internal readonly string Path;

            internal readonly string Name;

            internal readonly long Serial;

            internal readonly long Epoch;

            internal readonly long Birth;

            internal readonly int Pid;

            internal readonly CriticalThreadRule Rule;

            internal CriticalRuntimeSource(GameDetection detection, string raw, long serial, long epoch)
            {
                Profile = detection.Profile.Id;
                RuleText = raw;
                Rule = CriticalThreadRule.ParseForRuntime(raw);
                Path = detection.RendererPath;
                Name = detection.RendererName;
                Pid = detection.RendererPid;
                Birth = detection.RendererCreation;
                Serial = serial;
                Epoch = epoch;
            }

            internal bool Same(CriticalRuntimeSource other)
            {
                if (other != null && Profile == other.Profile && RuleText == other.RuleText && Serial == other.Serial && Epoch == other.Epoch && Pid == other.Pid && Birth == other.Birth)
                {
                    return string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }

            internal string Difference(CriticalRuntimeSource other)
            {
                if (other != null)
                {
                    if (Pid == other.Pid && Birth == other.Birth && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Profile == other.Profile && RuleText == other.RuleText)
                        {
                            if (Serial == other.Serial)
                            {
                                if (Epoch == other.Epoch)
                                {
                                    return "source changed";
                                }
                                return "intent changed";
                            }
                            return "another session";
                        }
                        return "rule changed";
                    }
                    return "renderer changed";
                }
                return "source missing";
            }
        }

        private struct Snap
        {
            public uint Pri;

            public ulong Aff;

            public int Io;

            public int Pg;

            public string Name;

            public long Creation;

            public uint[] CpuSets;

            public int QoSControl;

            public int QoSState;

            public bool PlacementYielded;
        }

        private sealed class HardPin
        {
            public int Pid;

            public long Creation;

            public ulong OriginalAffinity;

            public ulong AppliedAffinity;

            public IntPtr RestoreHandle;

            public bool JobBound;

            public bool JobRecoveryOnly;

            public string JobFallbackReason;

            public bool RestoreRequested, RestoreInFlight;

            public long NextRestore;

            public int RestoreDelay;

            public int RestoreAttempts;

            public bool RestoreExitPhase;

            public bool RestoreTargetGone;
        }

        private struct GameId
        {
            public string Name;

            public long Creation;
        }

        private sealed class LaneFallbackTarget
        {
            internal readonly int Pid;

            internal readonly long Birth;

            internal LaneFallbackTarget(int pid, long birth)
            {
                Pid = pid;
                Birth = birth;
            }
        }

        private sealed class AutomaticFsoEntry
        {
            internal bool Disable;

            internal bool Configured;

            internal int Failures;

            internal long CheckAt;
        }

        private sealed class BackgroundRequest
        {
            public int Pid;

            public string Name;

            public long Creation;

            public long Cpu;

            public SuppressionLevel Desired;

            public SuppressionLevel Previous;

            public bool HadBackgroundReason;

            public AcquireResult Result;

            public string FailureDetail;
        }

        private sealed class WhitelistProcessInfo
        {
            public int Pid;

            public int Session = -1;

            public int Parent;

            public string Name;

            public string Path;

            public long Creation;

            public long Cpu;

            public ulong Io;
        }

        private sealed class WhitelistEvaluation
        {
            public readonly HashSet<int> Protected = new HashSet<int>();

            public Dictionary<int, PlatformProcessOwner> PlatformProcesses;

            public readonly HashSet<int> RuntimeProtected = new HashSet<int>();

            public readonly Dictionary<string, HashSet<int>> ByRule = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            public readonly Dictionary<int, WhitelistProcessInfo> Processes = new Dictionary<int, WhitelistProcessInfo>();

            public readonly Dictionary<int, int> Parents = new Dictionary<int, int>();

            public readonly Dictionary<int, string> Names = new Dictionary<int, string>();

            public readonly Dictionary<int, long> Creations = new Dictionary<int, long>();
        }

        private sealed class WhitelistMatchIndex
        {
            private readonly Dictionary<string, List<WhitelistProcessInfo>> names = new Dictionary<string, List<WhitelistProcessInfo>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<WhitelistProcessInfo>> paths = new Dictionary<string, List<WhitelistProcessInfo>>(StringComparer.OrdinalIgnoreCase);
            private static readonly List<WhitelistProcessInfo> empty = new List<WhitelistProcessInfo>();

            internal WhitelistMatchIndex(Dictionary<int, WhitelistProcessInfo> processes, IList<WhitelistRule> rules)
            {
                foreach (WhitelistRule rule in rules)
                {
                    if (rule == null || rule.Value == null || (rule.Kind != 0 && rule.Value.Length == 0)) continue;
                    (rule.Kind == 0 ? names : paths)[rule.Value] = null;
                }
                foreach (WhitelistProcessInfo process in processes.Values)
                {
                    if (process.Name != null) Add(names, process.Name, process);
                    if (!string.IsNullOrEmpty(process.Path)) Add(paths, process.Path, process);
                }
            }

            private static void Add(Dictionary<string, List<WhitelistProcessInfo>> index, string key, WhitelistProcessInfo process)
            {
                List<WhitelistProcessInfo> matches;
                if (!index.TryGetValue(key, out matches)) return;
                if (matches == null) index[key] = matches = new List<WhitelistProcessInfo>();
                matches.Add(process);
            }

            internal List<WhitelistProcessInfo> Candidates(WhitelistRule rule)
            {
                List<WhitelistProcessInfo> matches;
                return (rule.Kind == 0 ? names : paths).TryGetValue(rule.Value, out matches) && matches != null ? matches : empty;
            }
        }

    }
}
