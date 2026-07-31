// Deterministic identity tests for delayed WMI process-start events.

namespace AegisApp
{
    internal static partial class SelfTests
    {
        private static void TestProcNotifyParentIdentity()
        {

            Eq(42, ProcNotify.ResolveVerifiedParentPid(42, 42));

            Eq(42, ProcNotify.ResolveVerifiedParentPid(0, 42));

            Eq(0, ProcNotify.ResolveVerifiedParentPid(42, 0));

            Eq(0, ProcNotify.ResolveVerifiedParentPid(42, 99));

            Eq(7, ProcNotify.ResolveVerifiedSessionId(7, true, 7));
            Eq(-1, ProcNotify.ResolveVerifiedSessionId(7, false, 7));
            Eq(-1, ProcNotify.ResolveVerifiedSessionId(7, true, 8));
            Eq(-1, ProcNotify.ResolveVerifiedSessionId(-1, true, 7));
            Eq(-1, ProcNotify.ResolveVerifiedSessionId(7, true, -1));

            Eq("CurrentWorker", ProcNotify.ResolveCurrentProcessName(
                @"C:\Other\CurrentWorker.exe"));
            Eq("", ProcNotify.ResolveCurrentProcessName(null));
            Eq("", ProcNotify.ResolveCurrentProcessName(@"C:\Other\"));
        }
    }
}
