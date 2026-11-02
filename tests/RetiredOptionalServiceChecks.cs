#if PAVISE_RETIRED_OPTIONAL_SERVICES_CHECKS
using System;
using System.Reflection;

namespace PaviseApp
{
    // Compile without SELFTEST so this exercises the same disabled activation branch as production.
    internal static class RetiredOptionalServiceChecks
    {
        private sealed class UnexpectedControl : IOptionalServiceControl, IOptionalServiceLedger
        {
            internal int Calls;
            private Exception Unexpected() { Calls++; return new InvalidOperationException("Retired activation touched a service or ledger"); }
            public bool TryGetBootIdentity(out string identity) { identity = null; throw Unexpected(); }
            public bool TryQuery(string name, out OptionalServiceSnapshot snapshot) { snapshot = null; throw Unexpected(); }
            public bool IsPrintingIdle() { throw Unexpected(); }
            public bool TryStop(string name, OptionalServiceSnapshot expected, Func<bool> mayContinue) { throw Unexpected(); }
            public OptionalServiceStartResult TryStart(string name, string expectedConfiguration) { throw Unexpected(); }
            public bool TryRead(out string value) { value = null; throw Unexpected(); }
            public bool TryWrite(string value) { throw Unexpected(); }
        }
        private static int Main()
        {
            try
            {
                var control = new UnexpectedControl();
                var engine = new OptionalServicePauseEngine(control, control);
                int predicates = 0;
                Func<bool> allowed = delegate { predicates++; return true; };
                if (engine.Activate() || engine.Activate(allowed) || engine.Activate(allowed, true)
                    || engine.Activate(allowed, false, true) || engine.Activate(allowed, true, true)
                    || engine.Activate(allowed, false, false))
                    throw new InvalidOperationException("Retired production engine accepted activation");
                if (control.Calls != 0 || predicates != 0 || engine.Active)
                    throw new InvalidOperationException("Retired production activation performed work");
                if (typeof(OptionalServicePause).GetMethod("Activate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null)
                    throw new InvalidOperationException("Production service-pause activation facade remains");
                Console.WriteLine("PASS retired service activation: production_branch=true overloads=6 service_calls=0 ledger_calls=0 runtime_started=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
