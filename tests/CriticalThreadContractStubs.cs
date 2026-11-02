#if PAVISE_HARD_THREAD_CONTRACT_STUB
using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal sealed class CriticalThreadTargetLease
        {
            internal readonly Guid SessionId;
            internal readonly int ProcessId;
            internal readonly long ProcessCreation;
            internal readonly string ExecutablePath;
            internal readonly ulong AllowedMask;
            internal readonly bool WorkerAvoidanceAllowed;
            private CriticalThreadTargetLease() { throw new InvalidOperationException("contract stubs cannot mint an original target"); }
            internal string CanonicalStore { get { throw new InvalidOperationException("contract stubs have no original store"); } }
            internal IDisposable EnterUse() { throw new InvalidOperationException("contract stubs have no original authority"); }
            internal void RequireActive() { throw new InvalidOperationException("contract stubs cannot authorize native writes"); }
            internal void RequireRecovery() { throw new InvalidOperationException("contract stubs cannot authorize native recovery"); }
            internal bool RegisterGuardianActor(HardThreadGuardianActor actor) { throw new InvalidOperationException("contract stubs cannot register native helpers"); }
            internal bool UnregisterGuardianActor(HardThreadGuardianActor actor) { throw new InvalidOperationException("contract stubs cannot unregister native helpers"); }
        }
    }
    internal static class TaskHelper
    {
        internal const string AutostartDataArgument = "--autostart-data";
    }
    internal static class BootSessionIdentity
    {
        internal static string Read() { throw new InvalidOperationException("contract stubs have no original Windows kernel domain"); }
    }
}
#endif
