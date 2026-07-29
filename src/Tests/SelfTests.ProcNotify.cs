// Deterministic identity tests for delayed WMI process-start events.

namespace AegisApp
{
    internal static partial class SelfTests
    {
        private static void TestProcNotifyParentIdentity()
        {
            // A current handle confirms the event edge.
            Eq(42, ProcNotify.ResolveVerifiedParentPid(42, 42));

            // Missing event data may be completed from the current handle.
            Eq(42, ProcNotify.ResolveVerifiedParentPid(0, 42));

            // If the current parent cannot be read, no event-only edge survives.
            Eq(0, ProcNotify.ResolveVerifiedParentPid(42, 0));

            // A delayed start for a reused PID must never splice the old
            // event parent onto the current process path/creation.
            Eq(0, ProcNotify.ResolveVerifiedParentPid(42, 99));

            // Session is part of the same trusted start identity. A missing
            // lookup or a delayed event from another session must fail closed.
            Eq(7, ProcNotify.ResolveVerifiedSessionId(7, true, 7));
            Eq(-1, ProcNotify.ResolveVerifiedSessionId(7, false, 7));
            Eq(-1, ProcNotify.ResolveVerifiedSessionId(7, true, 8));
            Eq(-1, ProcNotify.ResolveVerifiedSessionId(-1, true, 7));
            Eq(-1, ProcNotify.ResolveVerifiedSessionId(7, true, -1));

            // The event name is not identity evidence after a delayed PID
            // reuse. Consumers must see the name derived from the same current
            // image path, never the old WMI ProcessName.
            Eq("CurrentWorker", ProcNotify.ResolveCurrentProcessName(
                @"C:\Other\CurrentWorker.exe"));
            Eq("", ProcNotify.ResolveCurrentProcessName(null));
            Eq("", ProcNotify.ResolveCurrentProcessName(@"C:\Other\"));
        }
    }
}
